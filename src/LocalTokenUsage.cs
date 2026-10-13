using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CodexClaudeUsage
{
    internal sealed class LocalTokenUsage
    {
        public long InputTokens { get; set; }
        public long OutputTokens { get; set; }
        public long TotalTokens { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal static class LocalTokenUsageReader
    {
        private const int MaxCandidates = 16;
        private const int MaxScannedFiles = 20000;
        private const int CodexTailBytes = 4 * 1024 * 1024;
        private const long ClaudeSessionBytes = 64L * 1024 * 1024;
        private const int MaxJsonLineChars = 8 * 1024 * 1024;
        private static readonly object CacheSync = new object();
        private static readonly Dictionary<string, CachedUsage> CodexCache = new Dictionary<string, CachedUsage>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, CachedUsage> ClaudeCache = new Dictionary<string, CachedUsage>(StringComparer.OrdinalIgnoreCase);

        public static LocalTokenUsage ReadCodexLatestSession(string sessionsRoot)
        {
            LocalTokenUsage latest = null;
            var files = RecentFiles(sessionsRoot, false);
            PruneCache(CodexCache, files);
            foreach (var file in files)
            {
                try
                {
                    var usage = ReadCached(file, CodexCache, ReadCodexFile);
                    if (usage != null && (latest == null || usage.UpdatedAt > latest.UpdatedAt))
                    {
                        latest = usage;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return latest;
        }

        public static LocalTokenUsage ReadClaudeLatestSession(string projectsRoot)
        {
            // 僅統計單一主工作階段；子代理用量不在此數值內。
            LocalTokenUsage latest = null;
            var files = RecentFiles(projectsRoot, true);
            PruneCache(ClaudeCache, files);
            foreach (var file in files)
            {
                try
                {
                    if (file.Length > ClaudeSessionBytes)
                    {
                        if (latest == null) return null;
                        continue;
                    }

                    var usage = ReadCached(file, ClaudeCache, ReadClaudeFile);
                    if (usage != null && (latest == null || usage.UpdatedAt > latest.UpdatedAt))
                    {
                        latest = usage;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return latest;
        }

        private static LocalTokenUsage ReadCached(FileInfo file, Dictionary<string, CachedUsage> cache, Func<FileInfo, LocalTokenUsage> read)
        {
            var length = file.Length;
            var modified = file.LastWriteTimeUtc;
            lock (CacheSync)
            {
                CachedUsage cached;
                if (cache.TryGetValue(file.FullName, out cached) && cached.Length == length && cached.Modified == modified)
                    return Copy(cached.Usage);
            }
            var usage = read(file);
            lock (CacheSync)
            {
                cache[file.FullName] = new CachedUsage { Length = length, Modified = modified, Usage = Copy(usage) };
            }
            return usage;
        }

        private static void PruneCache(Dictionary<string, CachedUsage> cache, List<FileInfo> files)
        {
            var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files) retained.Add(file.FullName);
            lock (CacheSync)
            {
                var obsolete = new List<string>();
                foreach (var path in cache.Keys) if (!retained.Contains(path)) obsolete.Add(path);
                foreach (var path in obsolete) cache.Remove(path);
            }
        }

        private static LocalTokenUsage ReadCodexFile(FileInfo file)
        {
            LocalTokenUsage latest = null;
            using (var stream = OpenShared(file.FullName))
            {
                var length = stream.Length;
                if (length == 0)
                {
                    return null;
                }
                var terminated = LastLineTerminated(stream, length);
                var offset = Math.Max(0, length - CodexTailBytes);
                var skipPartialLine = false;
                if (offset > 0)
                {
                    stream.Seek(offset - 1, SeekOrigin.Begin);
                    skipPartialLine = stream.ReadByte() != '\n';
                }
                stream.Seek(offset, SeekOrigin.Begin);
                using (var snapshot = new SnapshotReadStream(stream, length - offset))
                using (var reader = new StreamReader(snapshot, Encoding.UTF8, true, 4096, true))
                {
                    if (skipPartialLine)
                    {
                        reader.ReadLine();
                    }
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!terminated && reader.EndOfStream)
                        {
                            break;
                        }
                        if (line.Length > MaxJsonLineChars || line.IndexOf("\"token_count\"", StringComparison.Ordinal) < 0)
                        {
                            continue;
                        }
                        try
                        {
                            var entry = Json.Object(Json.DeserializeObject(line));
                            if (!string.Equals(Json.String(entry, "type", null), "event_msg", StringComparison.Ordinal))
                            {
                                continue;
                            }
                            var payload = Json.Map(entry, "payload");
                            if (!string.Equals(Json.String(payload, "type", null), "token_count", StringComparison.Ordinal))
                            {
                                continue;
                            }
                            var totals = Json.Map(Json.Map(payload, "info"), "total_token_usage");
                            long input, output, total, combined;
                            if (!TryToken(totals, "input_tokens", true, out input)
                                || !TryToken(totals, "output_tokens", true, out output)
                                || !TryToken(totals, "total_tokens", true, out total)
                                || !TryAdd(input, output, out combined)
                                || combined != total)
                            {
                                continue;
                            }
                            DateTimeOffset updatedAt;
                            if (!TryTimestamp(entry, out updatedAt))
                            {
                                continue;
                            }
                            if (latest != null && updatedAt < latest.UpdatedAt)
                            {
                                continue;
                            }
                            latest = new LocalTokenUsage
                            {
                                InputTokens = input,
                                OutputTokens = output,
                                TotalTokens = total,
                                UpdatedAt = updatedAt
                            };
                        }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }
                    }
                }
                if (stream.Length < length)
                {
                    return null;
                }
            }
            return latest;
        }

        private static LocalTokenUsage ReadClaudeFile(FileInfo file)
        {
            var messages = new Dictionary<string, LocalTokenUsage>(StringComparer.Ordinal);
            using (var stream = OpenShared(file.FullName))
            {
                var length = stream.Length;
                if (length == 0 || length > ClaudeSessionBytes)
                {
                    return null;
                }
                var terminated = LastLineTerminated(stream, length);
                stream.Seek(0, SeekOrigin.Begin);
                using (var snapshot = new SnapshotReadStream(stream, length))
                using (var reader = new StreamReader(snapshot, Encoding.UTF8, true, 4096, true))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (!terminated && reader.EndOfStream)
                        {
                            break;
                        }
                        if (line.Length > MaxJsonLineChars)
                        {
                            return null;
                        }
                        if (line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0)
                        {
                            continue;
                        }
                        try
                        {
                            var entry = Json.Object(Json.DeserializeObject(line));
                            if (!string.Equals(Json.String(entry, "type", null), "assistant", StringComparison.Ordinal))
                            {
                                continue;
                            }
                            var message = Json.Map(entry, "message");
                            if (Json.Bool(entry, "isApiErrorMessage", false)
                                || string.Equals(Json.String(message, "model", null), "<synthetic>", StringComparison.Ordinal))
                            {
                                continue;
                            }
                            var id = Json.String(message, "id", null);
                            if (string.IsNullOrWhiteSpace(id))
                            {
                                continue;
                            }
                            var usage = Json.Map(message, "usage");
                            long input, created, read, output, combinedInput, total;
                            if (!TryToken(usage, "input_tokens", true, out input)
                                || !TryToken(usage, "cache_creation_input_tokens", false, out created)
                                || !TryToken(usage, "cache_read_input_tokens", false, out read)
                                || !TryToken(usage, "output_tokens", true, out output)
                                || !TryAdd(input, created, out combinedInput)
                                || !TryAdd(combinedInput, read, out combinedInput)
                                || !TryAdd(combinedInput, output, out total))
                            {
                                continue;
                            }
                            DateTimeOffset updatedAt;
                            if (!TryTimestamp(entry, out updatedAt))
                            {
                                return null;
                            }
                            LocalTokenUsage prior;
                            if (messages.TryGetValue(id, out prior) && prior.TotalTokens > total)
                            {
                                continue;
                            }
                            messages[id] = new LocalTokenUsage
                            {
                                InputTokens = combinedInput,
                                OutputTokens = output,
                                TotalTokens = total,
                                UpdatedAt = updatedAt
                            };
                        }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }
                    }
                }
                if (stream.Length < length)
                {
                    return null;
                }
            }

            if (messages.Count == 0)
            {
                return null;
            }
            var result = new LocalTokenUsage { UpdatedAt = DateTimeOffset.MinValue };
            foreach (var message in messages.Values)
            {
                long input, output, total;
                if (!TryAdd(result.InputTokens, message.InputTokens, out input)
                    || !TryAdd(result.OutputTokens, message.OutputTokens, out output)
                    || !TryAdd(result.TotalTokens, message.TotalTokens, out total))
                {
                    return null;
                }
                result.InputTokens = input;
                result.OutputTokens = output;
                result.TotalTokens = total;
                if (message.UpdatedAt > result.UpdatedAt)
                {
                    result.UpdatedAt = message.UpdatedAt;
                }
            }
            return result;
        }

        private static FileStream OpenShared(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        private static bool LastLineTerminated(FileStream stream, long length)
        {
            stream.Seek(length - 1, SeekOrigin.Begin);
            return stream.ReadByte() == '\n';
        }

        private static List<FileInfo> RecentFiles(string root, bool claude)
        {
            var result = new List<FileInfo>();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return result;
            }
            try
            {
                var scanned = 0;
                foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
                {
                    scanned++;
                    if (scanned > MaxScannedFiles)
                    {
                        return new List<FileInfo>();
                    }
                    if (claude && IsExcludedClaudeFile(path))
                    {
                        continue;
                    }
                    FileInfo file;
                    try
                    {
                        file = new FileInfo(path);
                        if (!file.Exists)
                        {
                            continue;
                        }
                    }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }

                    var index = result.FindIndex(delegate(FileInfo prior)
                    {
                        return file.LastWriteTimeUtc > prior.LastWriteTimeUtc;
                    });
                    if (index < 0)
                    {
                        if (result.Count < MaxCandidates)
                        {
                            result.Add(file);
                        }
                    }
                    else
                    {
                        result.Insert(index, file);
                        if (result.Count > MaxCandidates)
                        {
                            result.RemoveAt(result.Count - 1);
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }

        private static bool IsExcludedClaudeFile(string path)
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith("agent-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("subagent-", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("journal", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            foreach (var part in path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                if (string.Equals(part, "subagents", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, "journal", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool TryToken(IDictionary<string, object> map, string key, bool required, out long value)
        {
            value = 0;
            var raw = Json.Get(map, key);
            if (raw == null)
            {
                return !required && map != null;
            }
            if (raw is bool)
            {
                return false;
            }
            var text = Convert.ToString(raw, CultureInfo.InvariantCulture);
            return !string.IsNullOrEmpty(text)
                && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryAdd(long left, long right, out long sum)
        {
            sum = 0;
            if (left < 0 || right < 0 || left > long.MaxValue - right)
            {
                return false;
            }
            sum = left + right;
            return true;
        }

        private static bool TryTimestamp(IDictionary<string, object> entry, out DateTimeOffset parsed)
        {
            return DateTimeOffset.TryParse(Json.String(entry, "timestamp", null), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed)
                && parsed != DateTimeOffset.MinValue;
        }

        private static LocalTokenUsage Copy(LocalTokenUsage value)
        {
            return value == null ? null : new LocalTokenUsage
            {
                InputTokens = value.InputTokens,
                OutputTokens = value.OutputTokens,
                TotalTokens = value.TotalTokens,
                UpdatedAt = value.UpdatedAt
            };
        }

        private sealed class CachedUsage
        {
            public long Length;
            public DateTime Modified;
            public LocalTokenUsage Usage;
        }

        // 只讀取開啟時已存在的完整紀錄，避免對話持續追加時丟棄有效用量或無限追讀。
        private sealed class SnapshotReadStream : Stream
        {
            private readonly Stream _source;
            private long _remaining;

            public SnapshotReadStream(Stream source, long length)
            {
                _source = source;
                _remaining = length;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = _source.Read(buffer, offset, (int)Math.Min(count, _remaining));
                _remaining -= read;
                return read;
            }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }
    }
}
