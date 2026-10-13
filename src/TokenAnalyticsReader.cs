using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CodexClaudeUsage
{
    internal sealed class TokenAnalyticsReader
    {
        private const int CacheVersion = 4;
        private const int MaxFiles = 20000;
        private const int MaxSamples = 500000;
        private const int MaxLineBytes = 8 * 1024 * 1024;
        private const long ReadBudget = 128L * 1024 * 1024;
        private const long MaxCacheBytes = 128L * 1024 * 1024;
        private readonly object _sync = new object();
        private readonly string _codexRoot;
        private readonly string _claudeRoot;
        private readonly string _cachePath;
        private readonly string _rootKey;
        private readonly Dictionary<string, FileIndex> _files = new Dictionary<string, FileIndex>(StringComparer.Ordinal);
        private readonly byte[] _buffer = new byte[65536];
        private DateTime _lastSaveUtc;
        private DateTime _lastPruneDate;
        private bool _dirty;
        private bool _recoveredCache;

        public TokenAnalyticsReader()
            : this(Path.Combine(CodexHome(), "sessions"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects"),
                Path.Combine(AppPaths.LocalData, "token-analytics-index.bin")) { }

        public TokenAnalyticsReader(string codexRoot, string claudeRoot) : this(codexRoot, claudeRoot, null) { }

        public TokenAnalyticsReader(string codexRoot, string claudeRoot, string cachePath)
        {
            _codexRoot = codexRoot;
            _claudeRoot = claudeRoot;
            _cachePath = cachePath;
            _rootKey = Hash((codexRoot ?? string.Empty).ToUpperInvariant() + "\n" + (claudeRoot ?? string.Empty).ToUpperInvariant());
            LoadCache();
        }

        public TokenAnalyticsSnapshot Read() { return Read(CancellationToken.None); }

        public TokenAnalyticsSnapshot Read(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                var now = DateTimeOffset.Now;
                var cutoff = now.LocalDateTime.Date.AddDays(-89);
                var cutoffUtc = new DateTimeOffset(cutoff).UtcTicks;
                var result = new TokenAnalyticsSnapshot { CapturedAt = now, CoverageStart = cutoff, CompleteFrom = cutoff, IsComplete = true };
                if (_lastPruneDate != cutoff)
                {
                    foreach (var state in _files.Values)
                    {
                        var baselines = state.Provider == 0
                            ? state.Samples.Where(s => s.Timestamp < cutoffUtc).GroupBy(s => s.Session).Select(g => g.OrderByDescending(s => s.Timestamp).First()).ToList()
                            : new List<Sample>();
                        state.Samples.RemoveAll(s => s.Timestamp < cutoffUtc);
                        state.Samples.InsertRange(0, baselines);
                        state.MessagePositions = null;
                    }
                    _lastPruneDate = cutoff;
                }
                var candidates = EnumerateFiles(result, cutoff, cancellationToken);
                var alive = new HashSet<string>(candidates.Select(f => f.Key), StringComparer.Ordinal);
                if (result.IsComplete)
                {
                    foreach (var obsolete in _files.Keys.Where(key => !alive.Contains(key)).ToArray())
                    {
                        _files.Remove(obsolete);
                        _dirty = true;
                    }
                }

                long budget = ReadBudget;
                var watch = Stopwatch.StartNew();
                var sampleCount = _files.Values.Sum(f => f.Samples.Count);
                foreach (var candidate in candidates.OrderBy(f => f.Provider).ThenByDescending(f => f.Info.LastWriteTimeUtc))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileIndex state;
                    if (!_files.TryGetValue(candidate.Key, out state))
                    {
                        state = new FileIndex { Key = candidate.Key, Provider = candidate.Provider, Session = candidate.Key,
                            Project = candidate.Provider == 1 ? ClaudeProjectFallback(candidate.Info.FullName) : "未知專案" };
                        _files.Add(candidate.Key, state);
                    }
                    try
                    {
                        state.CutoffUtc = cutoffUtc;
                        var info = candidate.Info;
                        var length = info.Length;
                        var modified = info.LastWriteTimeUtc.Ticks;
                        if (length < state.ObservedLength || (length == state.ObservedLength && modified != state.ModifiedUtc && state.ModifiedUtc != 0))
                        {
                            sampleCount -= state.Samples.Count;
                            state.Reset();
                            if (candidate.Provider == 1) state.Project = ClaudeProjectFallback(candidate.Info.FullName);
                            _dirty = true;
                        }
                        state.ObservedLength = length;
                        state.ModifiedUtc = modified;
                        if (state.Offset < length && budget > 0 && watch.ElapsedMilliseconds < 1400 && sampleCount < MaxSamples)
                        {
                            var before = state.Samples.Count;
                            state.MaxRetained = before + MaxSamples - sampleCount;
                            ReadFile(candidate.Info.FullName, state, ref budget, watch, cancellationToken);
                            sampleCount += state.Samples.Count - before;
                            _dirty = true;
                        }
                        if (state.Offset < length)
                        {
                            result.IsComplete = false;
                            result.CompleteFrom = null;
                            if (sampleCount < MaxSamples) result.IsIndexing = true;
                            result.FilesSkipped++;
                        }
                        else result.FilesScanned++;
                        if (state.Fault || state.Pending.Length > 0 || state.SkipLine)
                        {
                            result.IsComplete = false;
                            if (state.UnknownFault || state.Pending.Length > 0 || state.SkipLine || state.LastCounterFaultUtc == 0)
                                result.CompleteFrom = null;
                            else IncompleteAt(result, state.LastCounterFaultUtc);
                            if (state.Offset >= length) result.FilesSkipped++;
                        }
                    }
                    catch (IOException) { result.IsComplete = false; result.CompleteFrom = null; result.FilesSkipped++; }
                    catch (UnauthorizedAccessException) { result.IsComplete = false; result.CompleteFrom = null; result.FilesSkipped++; }
                }

                Aggregate(result, cutoff, now.LocalDateTime.Date.AddDays(1));
                if (candidates.Count == 0)
                {
                    result.IsComplete = false;
                    result.CompleteFrom = null;
                    result.Status = "尚無可讀取的本機 Token 紀錄";
                }
                else if (result.IsIndexing)
                    result.Status = string.Format(CultureInfo.InvariantCulture, "歷史索引進行中 · {0}/{1} 個檔案完成", result.FilesScanned, candidates.Count);
                else if (!result.IsComplete)
                    result.Status = "部分紀錄尚未完成或無法讀取 · 目前顯示已確認的消耗";
                else if (result.Rows.Count == 0)
                    result.Status = "近 90 天無 Token 消耗紀錄";
                else result.Status = "近 90 天本機紀錄 · 含主工作階段與子代理";
                if (_recoveredCache && result.IsIndexing) result.Status += " · 索引已重新建立";
                if (_dirty && (!result.IsIndexing || DateTime.UtcNow - _lastSaveUtc > TimeSpan.FromSeconds(30))) SaveCache();
                return result;
            }
        }

        private List<Candidate> EnumerateFiles(TokenAnalyticsSnapshot result, DateTime cutoff, CancellationToken cancellationToken)
        {
            var resultFiles = new List<Candidate>();
            AddCandidates(_codexRoot, 0, resultFiles, result, cutoff, cancellationToken);
            if (!string.IsNullOrWhiteSpace(_codexRoot) && string.Equals(Path.GetFileName(_codexRoot.TrimEnd(Path.DirectorySeparatorChar)), "sessions", StringComparison.OrdinalIgnoreCase))
                AddCandidates(Path.Combine(Path.GetDirectoryName(_codexRoot.TrimEnd(Path.DirectorySeparatorChar)), "archived_sessions"), 0, resultFiles, result, cutoff, cancellationToken);
            AddCandidates(_claudeRoot, 1, resultFiles, result, cutoff, cancellationToken);
            return resultFiles;
        }

        private static void AddCandidates(string root, int provider, List<Candidate> files, TokenAnalyticsSnapshot result, DateTime cutoff, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;
            var pending = new Stack<string>();
            pending.Push(root);
            var directories = 0;
            while (pending.Count > 0 && files.Count < MaxFiles && directories++ < MaxFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                try
                {
                    foreach (var path in Directory.EnumerateFiles(directory, "*.jsonl"))
                    {
                        if (files.Count >= MaxFiles) { result.IsComplete = false; result.CompleteFrom = null; break; }
                        if (provider == 1 && Path.GetFileName(path).StartsWith("journal", StringComparison.OrdinalIgnoreCase)) continue;
                        var info = new FileInfo(path);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LastWriteTime < cutoff) continue;
                        files.Add(new Candidate { Key = Hash(info.FullName.ToUpperInvariant()), Info = info, Provider = provider });
                    }
                    foreach (var path in Directory.EnumerateDirectories(directory))
                    {
                        var info = new DirectoryInfo(path);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || string.Equals(info.Name, "journal", StringComparison.OrdinalIgnoreCase)) continue;
                        pending.Push(path);
                    }
                }
                catch (IOException) { result.IsComplete = false; result.CompleteFrom = null; result.FilesSkipped++; }
                catch (UnauthorizedAccessException) { result.IsComplete = false; result.CompleteFrom = null; result.FilesSkipped++; }
            }
            if (pending.Count > 0) { result.IsComplete = false; result.CompleteFrom = null; result.FilesSkipped += pending.Count; }
        }

        private void ReadFile(string path, FileIndex state, ref long budget, Stopwatch watch, CancellationToken cancellationToken)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, _buffer.Length, FileOptions.SequentialScan))
            {
                stream.Seek(state.Offset, SeekOrigin.Begin);
                while (state.Offset < state.ObservedLength && budget > 0 && watch.ElapsedMilliseconds < 1400)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var count = stream.Read(_buffer, 0, (int)Math.Min(_buffer.Length, Math.Min(state.ObservedLength - state.Offset, budget)));
                    if (count <= 0) break;
                    var position = 0;
                    while (position < count)
                    {
                        var newline = Array.IndexOf(_buffer, (byte)'\n', position, count - position);
                        var size = (newline < 0 ? count : newline) - position;
                        if (!state.SkipLine)
                        {
                            if (state.Pending.Length + size > MaxLineBytes)
                            {
                                state.SkipLine = true;
                                state.Fault = true;
                                state.FaultReasons |= 2;
                                state.UnknownFault = true;
                                ClearPending(state);
                            }
                            else
                            {
                                state.Pending.Write(_buffer, position, size);
                                if (state.Provider == 0 && state.Pending.Length >= 1024 && IsIrrelevantCodexPrefix(state.Pending))
                                {
                                    state.SkipLine = true;
                                    ClearPending(state);
                                }
                            }
                        }
                        if (newline < 0) break;
                        if (!state.SkipLine && state.Pending.Length > 0)
                            ParseLine(state, Encoding.UTF8.GetString(state.Pending.GetBuffer(), 0, (int)state.Pending.Length).TrimStart('\uFEFF'));
                        ClearPending(state);
                        state.SkipLine = false;
                        position = newline + 1;
                    }
                    state.Offset += count;
                    budget -= count;
                }
            }
        }

        private static void ClearPending(FileIndex state)
        {
            state.Pending.SetLength(0);
            if (state.Pending.Capacity > 65536) state.Pending.Capacity = 4096;
        }

        private static bool IsIrrelevantCodexPrefix(MemoryStream pending)
        {
            var prefix = Encoding.UTF8.GetString(pending.GetBuffer(), 0, (int)Math.Min(1024, pending.Length));
            var type = RootType(prefix);
            if (type == "event_msg")
            {
                var payloadOffset = RootValueOffset(prefix, "payload");
                if (payloadOffset >= 0 && prefix[payloadOffset] == '{')
                {
                    var payloadType = RootType(prefix.Substring(payloadOffset));
                    if (payloadType != null && payloadType != "token_count") return true;
                }
            }
            return type != null && type != "session_meta" && type != "turn_context" && type != "event_msg";
        }

        private static string RootType(string json)
        {
            var start = RootValueOffset(json, "type");
            if (start < 0 || json[start++] != '"') return null;
            var end = json.IndexOf('"', start);
            return end < 0 ? null : json.Substring(start, end - start);
        }

        private static int RootValueOffset(string json, string property)
        {
            var depth = 0;
            for (var i = 0; i < json.Length; i++)
            {
                var c = json[i];
                if (c == '{' || c == '[') { depth++; continue; }
                if (c == '}' || c == ']') { depth--; continue; }
                if (c != '"') continue;
                var start = ++i;
                for (; i < json.Length; i++)
                {
                    if (json[i] == '\\') { i++; continue; }
                    if (json[i] == '"') break;
                }
                if (depth != 1 || i >= json.Length || i - start != property.Length || string.CompareOrdinal(json, start, property, 0, property.Length) != 0) continue;
                var next = i + 1;
                while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                if (next >= json.Length || json[next++] != ':') continue;
                while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                return next >= json.Length ? -1 : next;
            }
            return -1;
        }

        private static void ParseLine(FileIndex state, string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            if (state.Provider == 0)
            {
                var type = RootType(line);
                if (type != null && type != "session_meta" && type != "turn_context" && type != "event_msg") return;
                if (type == "event_msg" && line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0) return;
            }
            else if (line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0 || line.IndexOf("\"assistant\"", StringComparison.Ordinal) < 0) return;
            try
            {
                var entry = Json.Object(Json.DeserializeObject(line));
                if (entry == null) { UnknownFault(state, 1); return; }
                if (state.Provider == 0) ParseCodex(state, entry);
                else ParseClaude(state, entry);
            }
            catch (ArgumentException) { UnknownFault(state, 1); }
            catch (InvalidOperationException) { UnknownFault(state, 1); }
            catch (OverflowException) { UnknownFault(state, 8); }
        }

        private static void ParseCodex(FileIndex state, IDictionary<string, object> entry)
        {
            var type = Json.String(entry, "type", string.Empty);
            var payload = Json.Map(entry, "payload");
            if (type == "session_meta")
            {
                // 子代理檔會再次包含父對話的 session_meta；只有檔首身分代表其累計串流。
                if (!state.HasSessionIdentity)
                {
                    var id = Json.String(payload, "id", Json.String(payload, "session_id", null));
                    if (!string.IsNullOrWhiteSpace(id))
                    {
                        state.Session = Hash(id);
                        state.HasSessionIdentity = true;
                        var parent = Json.String(payload, "forked_from_id", Json.String(payload, "forked_from", null));
                        if (!string.IsNullOrWhiteSpace(parent)) state.Parent = Hash(parent);
                    }
                }
                var cwd = Json.String(payload, "cwd", null);
                if (!string.IsNullOrWhiteSpace(cwd)) state.Project = ProjectName(cwd);
                state.Model = CleanName(Json.String(payload, "model", state.Model));
                return;
            }
            if (type == "turn_context")
            {
                state.Model = CleanName(Json.String(payload, "model", "未知模型"));
                var cwd = Json.String(payload, "cwd", null);
                if (!string.IsNullOrWhiteSpace(cwd)) state.Project = ProjectName(cwd);
                state.Turn = Hash(Json.String(payload, "turn_id", string.Empty));
                return;
            }
            if (type != "event_msg" || Json.String(payload, "type", null) != "token_count") return;
            var info = Json.Map(payload, "info");
            var usage = Json.Map(info, "total_token_usage");
            if (usage == null) return;
            DateTimeOffset timestamp;
            var sample = new Sample();
            if (!Timestamp(entry, out timestamp)) { UnknownFault(state, 4); return; }
            if (!CodexCounters(usage, sample))
            {
                state.Fault = true; state.FaultReasons |= 8 | CodexCounterFault(usage);
                state.LastCounterFaultUtc = Math.Max(state.LastCounterFaultUtc, timestamp.UtcTicks);
                state.LastCounterGapUtc = Math.Max(state.LastCounterGapUtc, timestamp.UtcTicks);
                state.PendingCounterFault = true;
                return;
            }
            // 缺漏累計紀錄後的差額可能橫跨日期，連下一筆承接差額的日期也排除完整性。
            if (state.PendingCounterFault)
            {
                state.LastCounterFaultUtc = Math.Max(state.LastCounterFaultUtc, timestamp.UtcTicks);
                state.PendingCounterFault = false;
            }
            sample.Timestamp = timestamp.UtcTicks;
            sample.Session = state.Session;
            sample.Parent = state.Parent;
            sample.Model = state.Model;
            sample.Project = state.Project;
            sample.Ordinal = Json.Long(entry, "ordinal", 0);
            sample.Turn = state.Turn;
            var last = new Sample();
            if (CodexCounters(Json.Map(info, "last_token_usage"), last))
            {
                sample.HasLast = true;
                sample.LastInput = last.Input;
                sample.LastRead = last.Read;
                sample.LastOutput = last.Output;
            }
            sample.Id = Hash(sample.Timestamp.ToString(CultureInfo.InvariantCulture) + "|" + sample.Input + "|" + sample.Read + "|" + sample.Output + "|" + sample.Turn);
            // 同一工作階段會在回應結束後重送相同累計值。
            var previous = state.Samples.Count == 0 ? null : state.Samples[state.Samples.Count - 1];
            if (previous != null && previous.Session == sample.Session && SameCounters(previous, sample)) return;
            if (sample.Timestamp < state.CutoffUtc && previous != null && previous.Timestamp < state.CutoffUtc && previous.Session == sample.Session)
            {
                if (sample.Timestamp >= previous.Timestamp) state.Samples[state.Samples.Count - 1] = sample;
                return;
            }
            if (state.Samples.Count >= state.MaxRetained) { UnknownFault(state, 16); return; }
            state.Samples.Add(sample);
        }

        private static void ParseClaude(FileIndex state, IDictionary<string, object> entry)
        {
            if (Json.String(entry, "type", null) != "assistant" || Json.Bool(entry, "isApiErrorMessage", false)) return;
            var message = Json.Map(entry, "message");
            var model = Json.String(message, "model", "未知模型");
            if (model == "<synthetic>") return;
            var usage = Json.Map(message, "usage");
            if (usage == null) return;
            var id = Json.String(message, "id", null);
            DateTimeOffset timestamp;
            var sample = new Sample();
            if (!Timestamp(entry, out timestamp)) { UnknownFault(state, 4); return; }
            if (string.IsNullOrWhiteSpace(id)
                || !Token(usage, "input_tokens", true, out sample.Input)
                || !Token(usage, "cache_read_input_tokens", false, out sample.Read)
                || !Token(usage, "cache_creation_input_tokens", false, out sample.Write)
                || !Token(usage, "output_tokens", true, out sample.Output)
                || !ValidSum(sample))
            {
                state.Fault = true; state.FaultReasons |= 8;
                if (string.IsNullOrWhiteSpace(id)) state.FaultReasons |= 1024;
                if (!ValidSum(sample)) state.FaultReasons |= 2048;
                state.LastCounterFaultUtc = Math.Max(state.LastCounterFaultUtc, timestamp.UtcTicks);
                return;
            }
            sample.Id = Hash(id);
            sample.Timestamp = timestamp.UtcTicks;
            if (sample.Timestamp < state.CutoffUtc) return;
            sample.Model = CleanName(model);
            var cwd = Json.String(entry, "cwd", null);
            sample.Project = string.IsNullOrWhiteSpace(cwd) ? state.Project : ProjectName(cwd);
            sample.Session = state.Session;
            sample.Turn = string.Empty;
            // 主紀錄、子代理與複製的歷史共享 message.id，因此在彙總時全域去重。
            if (state.MessagePositions == null)
            {
                state.MessagePositions = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < state.Samples.Count; i++) state.MessagePositions[state.Samples[i].Id] = i;
            }
            int existing;
            if (state.MessagePositions.TryGetValue(sample.Id, out existing))
            {
                var prior = state.Samples[existing];
                if (SampleTotal(sample) > SampleTotal(prior) || (SampleTotal(sample) == SampleTotal(prior) && sample.Timestamp < prior.Timestamp))
                    state.Samples[existing] = sample;
                return;
            }
            if (state.Samples.Count >= state.MaxRetained) { UnknownFault(state, 16); return; }
            state.MessagePositions[sample.Id] = state.Samples.Count;
            state.Samples.Add(sample);
        }

        private void Aggregate(TokenAnalyticsSnapshot result, DateTime cutoff, DateTime end)
        {
            var rows = new Dictionary<string, TokenUsageEntry>(StringComparer.Ordinal);
            var codex = _files.Values.Where(f => f.Provider == 0).SelectMany(f => f.Samples)
                .GroupBy(s => s.Session, StringComparer.Ordinal);
            var sessionEvents = codex.ToDictionary(g => g.Key, g => new HashSet<string>(g.Select(s => s.Id), StringComparer.Ordinal), StringComparer.Ordinal);
            var sessionGaps = _files.Values.Where(f => f.Provider == 0 && f.LastCounterGapUtc > 0)
                .GroupBy(f => f.Session).ToDictionary(g => g.Key, g => g.Max(f => f.LastCounterGapUtc), StringComparer.Ordinal);
            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var session in codex)
            {
                long gap;
                if (sessionGaps.TryGetValue(session.Key, out gap))
                {
                    var next = session.Where(s => s.Timestamp >= gap).OrderBy(s => s.Timestamp).FirstOrDefault();
                    if (next != null) IncompleteAt(result, next.Timestamp);
                }
                Sample previous = null;
                foreach (var sample in session.OrderBy(s => s.Timestamp).ThenBy(s => s.Ordinal).ThenBy(s => s.Input + s.Output))
                {
                    if (new DateTimeOffset(sample.Timestamp, TimeSpan.Zero).LocalDateTime.Date < cutoff)
                    {
                        previous = sample;
                        continue;
                    }
                    HashSet<string> parentEvents;
                    if (!string.IsNullOrEmpty(sample.Parent) && sessionEvents.TryGetValue(sample.Parent, out parentEvents) && parentEvents.Contains(sample.Id))
                    {
                        previous = sample;
                        continue;
                    }
                    Sample delta;
                    if (previous != null && SameCounters(previous, sample)) continue;
                    if (previous != null && sample.Input >= previous.Input && sample.Read >= previous.Read && sample.Output >= previous.Output)
                    {
                        delta = new Sample { Input = sample.Input - previous.Input, Read = sample.Read - previous.Read, Output = sample.Output - previous.Output };
                        if (delta.Read > delta.Input) { IncompleteAt(result, sample.Timestamp); previous = sample; continue; }
                    }
                    else if (sample.HasLast)
                    {
                        delta = new Sample { Input = sample.LastInput, Read = sample.LastRead, Output = sample.LastOutput };
                        if (previous == null && string.IsNullOrEmpty(sample.Parent) && !SameCounters(delta, sample)) IncompleteAt(result, sample.Timestamp);
                    }
                    else if (previous == null && string.IsNullOrEmpty(sample.Parent))
                    {
                        delta = new Sample { Input = sample.Input, Read = sample.Read, Output = sample.Output };
                    }
                    else
                    {
                        IncompleteAt(result, sample.Timestamp);
                        previous = sample;
                        continue;
                    }
                    previous = sample;
                    if (!emitted.Add(sample.Id)) continue;
                    delta.Input -= delta.Read;
                    AddRow(result, rows, sample, delta, "Codex", cutoff, end);
                }
            }

            var messages = new Dictionary<string, Sample>(StringComparer.Ordinal);
            foreach (var sample in _files.Values.Where(f => f.Provider == 1).SelectMany(f => f.Samples))
            {
                Sample previous;
                if (!messages.TryGetValue(sample.Id, out previous) || SampleTotal(sample) > SampleTotal(previous)
                    || (SampleTotal(sample) == SampleTotal(previous) && sample.Timestamp < previous.Timestamp))
                    messages[sample.Id] = sample;
            }
            foreach (var sample in messages.Values) AddRow(result, rows, sample, sample, "Claude Code", cutoff, end);
            result.Rows = rows.Values.OrderBy(r => r.Date).ThenBy(r => r.Provider).ThenBy(r => r.Model).ThenBy(r => r.Project).ToList();
        }

        private static void AddRow(TokenAnalyticsSnapshot result, Dictionary<string, TokenUsageEntry> rows, Sample sample, Sample delta, string provider, DateTime cutoff, DateTime end)
        {
            var timestamp = new DateTimeOffset(sample.Timestamp, TimeSpan.Zero);
            var date = timestamp.LocalDateTime.Date;
            if (date < cutoff || date >= end) return;
            if (!result.LastRecordAt.HasValue || timestamp > result.LastRecordAt.Value) result.LastRecordAt = timestamp;
            if (!ValidSum(delta)) { IncompleteAt(result, sample.Timestamp); return; }
            if (SampleTotal(delta) == 0) return;
            var key = date.Ticks.ToString(CultureInfo.InvariantCulture) + "|" + provider + "|" + sample.Model + "|" + sample.Project;
            TokenUsageEntry row;
            if (!rows.TryGetValue(key, out row))
            {
                row = new TokenUsageEntry { Date = date, Provider = provider, Model = sample.Model, Project = sample.Project };
                rows.Add(key, row);
            }
            try
            {
                var total = checked(row.TotalTokens + SampleTotal(delta));
                var input = checked(row.InputTokens + delta.Input);
                var read = checked(row.CacheReadTokens + delta.Read);
                var write = checked(row.CacheWriteTokens + delta.Write);
                var output = checked(row.OutputTokens + delta.Output);
                if (total < 0) throw new OverflowException();
                row.InputTokens = input; row.CacheReadTokens = read; row.CacheWriteTokens = write; row.OutputTokens = output;
            }
            catch (OverflowException) { IncompleteAt(result, sample.Timestamp); }
        }

        private static void UnknownFault(FileIndex state, int reason)
        {
            state.Fault = true; state.UnknownFault = true; state.FaultReasons |= reason;
        }

        private static void IncompleteAt(TokenAnalyticsSnapshot result, long timestamp)
        {
            result.IsComplete = false;
            if (!result.CompleteFrom.HasValue) return;
            var affected = new DateTimeOffset(timestamp, TimeSpan.Zero).LocalDateTime.Date;
            if (affected == DateTime.MaxValue.Date) { result.CompleteFrom = null; return; }
            var completeFrom = affected.AddDays(1);
            if (completeFrom > result.CompleteFrom.Value) result.CompleteFrom = completeFrom;
        }

        private static bool CodexCounters(IDictionary<string, object> usage, Sample sample)
        {
            long total;
            return Token(usage, "input_tokens", true, out sample.Input)
                && Token(usage, "cached_input_tokens", false, out sample.Read)
                && Token(usage, "output_tokens", true, out sample.Output)
                && Token(usage, "total_tokens", true, out total)
                && sample.Read <= sample.Input && sample.Input <= long.MaxValue - sample.Output && sample.Input + sample.Output == total;
        }

        private static int CodexCounterFault(IDictionary<string, object> usage)
        {
            long input, cached, output, total;
            var issues = 0;
            if (!Token(usage, "input_tokens", true, out input)) issues |= 32;
            if (!Token(usage, "cached_input_tokens", false, out cached)) issues |= 64;
            if (!Token(usage, "output_tokens", true, out output)) issues |= 128;
            if (!Token(usage, "total_tokens", true, out total)) issues |= 256;
            if (cached > input) issues |= 4096;
            if (input > long.MaxValue - output) issues |= 2048;
            else if (input + output != total) issues |= 512;
            return issues;
        }

        private static bool SameCounters(Sample left, Sample right) { return left.Input == right.Input && left.Read == right.Read && left.Output == right.Output; }
        private static long SampleTotal(Sample value) { return checked(value.Input + value.Read + value.Write + value.Output); }
        private static bool ValidSum(Sample value)
        {
            if (value.Input < 0 || value.Read < 0 || value.Write < 0 || value.Output < 0) return false;
            try { return SampleTotal(value) >= 0; } catch (OverflowException) { return false; }
        }

        private static bool Token(IDictionary<string, object> map, string key, bool required, out long value)
        {
            value = 0;
            var raw = Json.Get(map, key);
            if (raw == null) return !required && map != null;
            return !(raw is bool) && long.TryParse(Convert.ToString(raw, CultureInfo.InvariantCulture), NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static bool Timestamp(IDictionary<string, object> entry, out DateTimeOffset timestamp)
        {
            return DateTimeOffset.TryParse(Json.String(entry, "timestamp", null), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp);
        }

        private static string ProjectName(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "未知專案";
            var normalized = path.Trim().Replace('/', '\\').TrimEnd('\\');
            var separator = normalized.LastIndexOf('\\');
            var label = separator >= 0 ? normalized.Substring(separator + 1) : normalized;
            if (label.Length == 0 || label.EndsWith(":", StringComparison.Ordinal)) label = "根目錄";
            return CleanName(label) + " · " + Hash(normalized.ToUpperInvariant()).Substring(0, 6);
        }

        private string ClaudeProjectFallback(string path)
        {
            var relative = !string.IsNullOrWhiteSpace(_claudeRoot) && path.StartsWith(_claudeRoot, StringComparison.OrdinalIgnoreCase)
                ? path.Substring(_claudeRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : Path.GetDirectoryName(path);
            var components = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return "未命名專案 · " + Hash(components[0].ToUpperInvariant()).Substring(0, 6);
        }

        private static string CleanName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "未知模型";
            var clean = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
            return clean.Length > 96 ? clean.Substring(0, 96) : clean;
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty))).Replace("-", string.Empty);
        }

        private static string CodexHome()
        {
            var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
            return string.IsNullOrWhiteSpace(configured) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : configured;
        }

        private void SaveCache()
        {
            if (string.IsNullOrWhiteSpace(_cachePath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_cachePath)));
                var temporary = _cachePath + ".tmp";
                using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8))
                {
                    writer.Write(0x54414E31); writer.Write(CacheVersion); writer.Write(_rootKey); writer.Write(_files.Count);
                    foreach (var state in _files.Values)
                    {
                        writer.Write(state.Key); writer.Write(state.Provider); writer.Write(state.ObservedLength); writer.Write(state.ModifiedUtc);
                        writer.Write(state.Offset - state.Pending.Length); writer.Write(state.SkipLine); writer.Write(state.Fault); writer.Write(state.FaultReasons);
                        writer.Write(state.UnknownFault); writer.Write(state.PendingCounterFault); writer.Write(state.LastCounterFaultUtc); writer.Write(state.LastCounterGapUtc); writer.Write(state.HasSessionIdentity);
                        writer.Write(state.Session); writer.Write(state.Parent ?? string.Empty); writer.Write(state.Model); writer.Write(state.Project); writer.Write(state.Turn);
                        writer.Write(state.Samples.Count);
                        foreach (var s in state.Samples)
                        {
                            writer.Write(s.Timestamp); writer.Write(s.Ordinal); writer.Write(s.Id); writer.Write(s.Session); writer.Write(s.Parent ?? string.Empty);
                            writer.Write(s.Model); writer.Write(s.Project); writer.Write(s.Turn ?? string.Empty);
                            writer.Write(s.Input); writer.Write(s.Read); writer.Write(s.Write); writer.Write(s.Output);
                            writer.Write(s.HasLast); writer.Write(s.LastInput); writer.Write(s.LastRead); writer.Write(s.LastOutput);
                        }
                        if (stream.Length > MaxCacheBytes) throw new IOException("Token index exceeds storage limit.");
                    }
                    writer.Flush(); stream.Flush(true);
                }
                if (File.Exists(_cachePath)) File.Replace(temporary, _cachePath, null);
                else File.Move(temporary, _cachePath);
                _lastSaveUtc = DateTime.UtcNow;
                _dirty = false;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private void LoadCache()
        {
            if (string.IsNullOrWhiteSpace(_cachePath) || !File.Exists(_cachePath)) return;
            try
            {
                var info = new FileInfo(_cachePath);
                if (info.Length > MaxCacheBytes) throw new InvalidDataException();
                using (var stream = new FileStream(_cachePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var reader = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (reader.ReadInt32() != 0x54414E31 || reader.ReadInt32() != CacheVersion || ReadString(reader) != _rootKey) throw new InvalidDataException();
                    var count = reader.ReadInt32();
                    if (count < 0 || count > MaxFiles) throw new InvalidDataException();
                    var samples = 0;
                    for (var i = 0; i < count; i++)
                    {
                        var state = new FileIndex { Key = ReadString(reader), Provider = reader.ReadInt32(), ObservedLength = reader.ReadInt64(), ModifiedUtc = reader.ReadInt64(),
                            Offset = reader.ReadInt64(), SkipLine = reader.ReadBoolean(), Fault = reader.ReadBoolean(), FaultReasons = reader.ReadInt32(),
                            UnknownFault = reader.ReadBoolean(), PendingCounterFault = reader.ReadBoolean(), LastCounterFaultUtc = reader.ReadInt64(), LastCounterGapUtc = reader.ReadInt64(), HasSessionIdentity = reader.ReadBoolean(), Session = ReadString(reader), Parent = ReadString(reader),
                            Model = ReadString(reader), Project = ReadString(reader), Turn = ReadString(reader) };
                        if (state.Provider < 0 || state.Provider > 1 || state.Offset < 0 || state.Offset > state.ObservedLength
                            || state.LastCounterFaultUtc < 0 || state.LastCounterFaultUtc > DateTimeOffset.MaxValue.UtcTicks
                            || state.LastCounterGapUtc < 0 || state.LastCounterGapUtc > DateTimeOffset.MaxValue.UtcTicks) throw new InvalidDataException();
                        var sampleCount = reader.ReadInt32();
                        if (sampleCount < 0 || sampleCount > MaxSamples - samples) throw new InvalidDataException();
                        samples += sampleCount;
                        for (var j = 0; j < sampleCount; j++)
                        {
                            var sample = new Sample { Timestamp = reader.ReadInt64(), Ordinal = reader.ReadInt64(), Id = ReadString(reader), Session = ReadString(reader),
                                Parent = ReadString(reader), Model = ReadString(reader), Project = ReadString(reader), Turn = ReadString(reader), Input = reader.ReadInt64(),
                                Read = reader.ReadInt64(), Write = reader.ReadInt64(), Output = reader.ReadInt64(), HasLast = reader.ReadBoolean(), LastInput = reader.ReadInt64(),
                                LastRead = reader.ReadInt64(), LastOutput = reader.ReadInt64() };
                            if (sample.Timestamp < DateTimeOffset.MinValue.UtcTicks || sample.Timestamp > DateTimeOffset.MaxValue.UtcTicks
                                || sample.Input < 0 || sample.Read < 0 || sample.Write < 0 || sample.Output < 0
                                || (state.Provider == 1 && !ValidSum(sample))
                                || (state.Provider == 0 && (sample.Read > sample.Input || sample.Input > long.MaxValue - sample.Output))) throw new InvalidDataException();
                            state.Samples.Add(sample);
                        }
                        _files.Add(state.Key, state);
                    }
                    if (stream.Position != stream.Length) throw new InvalidDataException();
                }
            }
            catch (Exception error)
            {
                if (!(error is IOException) && !(error is InvalidDataException) && !(error is UnauthorizedAccessException) && !(error is ArgumentException) && !(error is OverflowException)) throw;
                _files.Clear(); _recoveredCache = true;
            }
        }

        private static string ReadString(BinaryReader reader)
        {
            var length = 0;
            var shift = 0;
            while (true)
            {
                var next = reader.ReadByte();
                if (shift >= 28) throw new InvalidDataException();
                length |= (next & 127) << shift;
                if (length > 2048) throw new InvalidDataException();
                if ((next & 128) == 0) break;
                shift += 7;
            }
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return Encoding.UTF8.GetString(bytes);
        }

        private sealed class Candidate { public string Key; public int Provider; public FileInfo Info; }
        private sealed class Sample
        {
            public long Timestamp, Ordinal, Input, Read, Write, Output, LastInput, LastRead, LastOutput;
            public string Id, Session, Parent, Model, Project, Turn;
            public bool HasLast;
        }
        private sealed class FileIndex
        {
            public string Key, Session, Parent;
            public string Model = "未知模型", Project = "未知專案", Turn = string.Empty;
            public int Provider, MaxRetained = MaxSamples, FaultReasons;
            public long Offset, ObservedLength, ModifiedUtc, CutoffUtc, LastCounterFaultUtc, LastCounterGapUtc;
            public bool SkipLine, Fault, UnknownFault, PendingCounterFault, HasSessionIdentity;
            public readonly MemoryStream Pending = new MemoryStream();
            public readonly List<Sample> Samples = new List<Sample>();
            public Dictionary<string, int> MessagePositions;
            public void Reset()
            {
                Samples.Clear(); Pending.SetLength(0); Offset = 0; SkipLine = false; Fault = false; FaultReasons = 0; LastCounterFaultUtc = 0;
                LastCounterGapUtc = 0;
                UnknownFault = false; PendingCounterFault = false;
                HasSessionIdentity = false;
                MessagePositions = null;
                Session = Key; Parent = null; Model = "未知模型"; Project = "未知專案"; Turn = string.Empty;
            }
        }
    }
}
