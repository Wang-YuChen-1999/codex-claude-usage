using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace CodexClaudeUsage
{
    internal interface IUsageSource
    {
        Task<ProviderSnapshot> ReadAsync();
    }

    internal sealed class TrendSample
    {
        public long T;
        public double U;
    }

    /// <summary>
    /// 共用趨勢引擎：由 (時間, 已用百分比) 序列產生 7 天迷你趨勢與耗盡時刻預測。
    /// </summary>
    internal static class UsageTrend
    {
        public const int TrendBins = 56; // 7 天 × 每 3 小時一格
        private const long TrendWindowMs = 7L * 24 * 3600 * 1000;
        private const long ProjectionWindowMs = 48L * 3600 * 1000;
        private const long MinProjectionSpanMs = 2L * 3600 * 1000;
        private const long ProjectionHorizonMs = 8L * 24 * 3600 * 1000;
        private const long MaximumProjectionSampleAgeMs = 60L * 60 * 1000;
        private const long FutureSampleToleranceMs = 5L * 60 * 1000;

        /// <summary>將近 7 天樣本重採樣為等距序列；不足兩個有效點回傳 null。</summary>
        public static double[] BuildTrendPoints(List<TrendSample> samples, long nowMs)
        {
            return BuildTrendPoints(samples, nowMs, TrendWindowMs, TrendBins);
        }

        /// <summary>可指定視窗長度與 bin 數的重採樣版本（供 5 小時視窗等短窗趨勢使用）。</summary>
        public static double[] BuildTrendPoints(List<TrendSample> samples, long nowMs, long windowMs, int bins)
        {
            if (samples == null || samples.Count < 2 || windowMs <= 0 || bins < 2)
            {
                return null;
            }

            var start = nowMs - windowMs;
            var points = new double[bins];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = double.NaN;
            }

            var binSize = windowMs / (double)bins;
            foreach (var sample in samples)
            {
                if (sample.T < start || sample.T > nowMs)
                {
                    continue;
                }
                var index = (int)((sample.T - start) / binSize);
                if (index >= bins)
                {
                    index = bins - 1;
                }
                points[index] = Math.Max(0, Math.Min(100, sample.U));
            }

            // 中段空洞以前值填補，開頭保持 NaN（尚無資料的時段不畫）。
            var seen = false;
            var previous = double.NaN;
            var valid = 0;
            for (var i = 0; i < points.Length; i++)
            {
                if (!double.IsNaN(points[i]))
                {
                    seen = true;
                    previous = points[i];
                    valid++;
                }
                else if (seen)
                {
                    points[i] = previous;
                }
            }
            return valid >= 2 ? points : null;
        }

        /// <summary>
        /// 以目前週期段（最後一次重設之後）近 48 小時的平均速度線性外推耗盡時刻；
        /// 速度為零或 8 天內用不完則回傳 null。
        /// </summary>
        public static DateTimeOffset? ProjectExhaustion(List<TrendSample> samples, long nowMs)
        {
            if (samples == null || samples.Count < 2)
            {
                return null;
            }

            var first = samples.Count - 1;
            for (var i = samples.Count - 1; i >= 1; i--)
            {
                if (samples[i - 1].U > samples[i].U + 2)
                {
                    first = i;
                    break;
                }
                first = i - 1;
            }

            var windowStart = nowMs - ProjectionWindowMs;
            var begin = first;
            while (begin < samples.Count - 1 && samples[begin].T < windowStart)
            {
                begin++;
            }

            var head = samples[begin];
            var last = samples[samples.Count - 1];
            if (last.T < nowMs - MaximumProjectionSampleAgeMs
                || last.T > nowMs + FutureSampleToleranceMs
                || last.T - head.T < MinProjectionSpanMs)
            {
                return null;
            }

            var slope = (last.U - head.U) / (last.T - head.T); // %/ms
            if (slope <= 0)
            {
                return null;
            }

            var remaining = 100 - last.U;
            var projectedMs = remaining <= 0 ? nowMs : last.T + (long)(remaining / slope);
            if (projectedMs < nowMs)
            {
                projectedMs = nowMs;
            }
            if (projectedMs > nowMs + ProjectionHorizonMs)
            {
                return null;
            }
            return TimeUtil.FromUnixMilliseconds(projectedMs);
        }
    }

    /// <summary>
    /// Codex 官方 app-server 只回報當下值；此存放區在每次成功讀取時記錄每週用量快照
    /// （10 分鐘去抖、保留 14 天、原子寫入），供趨勢與耗盡預測使用。
    /// </summary>
    internal static class CodexHistoryStore
    {
        private const long RetainMs = 14L * 24 * 3600 * 1000;
        private const long DebounceMs = 10L * 60 * 1000;
        private const int MaxSamples = 2500;

        private static string StorePath
        {
            get
            {
                var overridePath = Environment.GetEnvironmentVariable("CODEX_CLAUDE_USAGE_CODEX_HISTORY_PATH");
                return string.IsNullOrWhiteSpace(overridePath)
                    ? Path.Combine(AppPaths.LocalData, "codex-usage-history.json")
                    : Path.GetFullPath(overridePath);
            }
        }

        /// <summary>記錄一筆快照並回傳目前的完整序列；任何 IO 失敗都不拋出。</summary>
        public static List<TrendSample> Append(double usedPercent)
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var samples = Load();
            var previous = samples.Count > 0 ? samples[samples.Count - 1] : null;
            if (previous != null
                && nowMs - previous.T < DebounceMs
                && Math.Abs(previous.U - usedPercent) < 0.5)
            {
                return samples;
            }

            samples.Add(new TrendSample { T = nowMs, U = usedPercent });
            samples.RemoveAll(delegate(TrendSample item) { return item.T < nowMs - RetainMs; });
            if (samples.Count > MaxSamples)
            {
                samples.RemoveRange(0, samples.Count - MaxSamples);
            }

            try
            {
                var records = new List<object>(samples.Count);
                foreach (var sample in samples)
                {
                    records.Add(new Dictionary<string, object> { { "t", sample.T }, { "u", sample.U } });
                }
                var payload = Json.Serialize(new Dictionary<string, object>
                {
                    { "version", 1 },
                    { "samples", records }
                });
                var path = StorePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, payload);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(temporary, path);
            }
            catch
            {
            }
            return samples;
        }

        public static List<TrendSample> Load()
        {
            var list = new List<TrendSample>();
            try
            {
                var path = StorePath;
                if (!File.Exists(path))
                {
                    return list;
                }
                var root = Json.Object(Json.DeserializeObject(File.ReadAllText(path)));
                var samples = Json.Array(Json.Get(root, "samples"));
                if (samples == null)
                {
                    return list;
                }
                foreach (var item in samples)
                {
                    var sample = Json.Object(item);
                    if (sample == null)
                    {
                        continue;
                    }
                    var timestamp = Json.Long(sample, "t", 0);
                    if (timestamp <= 0)
                    {
                        continue;
                    }
                    list.Add(new TrendSample { T = timestamp, U = Json.Double(sample, "u", 0) });
                }
                list.Sort(delegate(TrendSample left, TrendSample right) { return left.T.CompareTo(right.T); });
            }
            catch
            {
                list.Clear();
            }
            return list;
        }
    }

    internal sealed class UsageAggregator
    {
        private readonly IUsageSource _codex;
        private readonly IUsageSource _claude;
        private readonly IUsageSource _antigravity;

        public UsageAggregator(AppConfig config)
        {
            _codex = new CodexUsageSource(config);
            _claude = new ClaudeUsageSource();
            _antigravity = new AntigravityUsageSource();
        }

        public async Task<UsageSnapshot> ReadAsync()
        {
            var codexTask = _codex.ReadAsync();
            var claudeTask = _claude.ReadAsync();
            var antigravityTask = _antigravity.ReadAsync();
            await Task.WhenAll(codexTask, claudeTask, antigravityTask).ConfigureAwait(false);
            return new UsageSnapshot
            {
                Codex = codexTask.Result,
                Claude = claudeTask.Result,
                Antigravity = antigravityTask.Result,
                UpdatedAt = DateTimeOffset.Now
            };
        }
    }

    internal sealed class CodexUsageSource : IUsageSource
    {
        private readonly AppConfig _config;

        public CodexUsageSource(AppConfig config)
        {
            _config = config;
        }

        public async Task<ProviderSnapshot> ReadAsync()
        {
            string appServerError = null;
            try
            {
                var executable = ResolveExecutable();
                if (!string.IsNullOrWhiteSpace(executable))
                {
                    return await ReadAppServerAsync(executable).ConfigureAwait(false);
                }
                appServerError = "找不到 codex.exe";
            }
            catch (Exception ex)
            {
                appServerError = CompactError(ex.Message);
            }

            try
            {
                var fallback = await Task.Run(new Func<ProviderSnapshot>(ReadLatestSession)).ConfigureAwait(false);
                if (fallback != null)
                {
                    fallback.Status = "本機工作階段 · " + TimeUtil.FreshnessLabel(fallback.UpdatedAt);
                    return fallback;
                }
            }
            catch (Exception ex)
            {
                if (string.IsNullOrEmpty(appServerError))
                {
                    appServerError = CompactError(ex.Message);
                }
            }

            return new ProviderSnapshot("Codex")
            {
                IsAvailable = false,
                Status = "目前無法讀取",
                Error = appServerError ?? "請先登入 Codex"
            };
        }

        private string ResolveExecutable()
        {
            if (!string.IsNullOrWhiteSpace(_config.CodexExecutable) && File.Exists(_config.CodexExecutable))
            {
                return _config.CodexExecutable;
            }

            var localCandidate = Path.Combine(AppPaths.ExecutableDirectory, "codex.exe");
            if (File.Exists(localCandidate))
            {
                return localCandidate;
            }

            return null;
        }

        private async Task<ProviderSnapshot> ReadAppServerAsync(string executable)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "app-server --stdio",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                if (!process.Start())
                {
                    throw new InvalidOperationException("Codex app-server 無法啟動");
                }

                try
                {
                    process.ErrorDataReceived += delegate { };
                    process.BeginErrorReadLine();
                    process.StandardInput.AutoFlush = true;
                    await process.StandardInput.WriteLineAsync(Json.Serialize(new Dictionary<string, object>
                    {
                        { "id", 1 },
                        { "method", "initialize" },
                        { "params", new Dictionary<string, object>
                            {
                                { "clientInfo", new Dictionary<string, object>
                                    {
                                        { "name", "codex-claude-usage" },
                                        { "title", "Codex + Claude Usage" },
                                        { "version", "1.0.0" }
                                    }
                                },
                                { "capabilities", new Dictionary<string, object> { { "experimentalApi", true } } }
                            }
                        }
                    })).ConfigureAwait(false);

                    var deadline = DateTime.UtcNow.AddSeconds(12);
                    while (DateTime.UtcNow < deadline)
                    {
                        var readTask = process.StandardOutput.ReadLineAsync();
                        var remaining = deadline - DateTime.UtcNow;
                        if (remaining.TotalMilliseconds < 1)
                        {
                            remaining = TimeSpan.FromMilliseconds(1);
                        }
                        var finished = await Task.WhenAny(readTask, Task.Delay(remaining)).ConfigureAwait(false);
                        if (finished != readTask)
                        {
                            throw new TimeoutException("Codex app-server 回應逾時");
                        }

                        var line = await readTask.ConfigureAwait(false);
                        if (line == null)
                        {
                            break;
                        }

                        IDictionary<string, object> message;
                        try
                        {
                            message = Json.Object(Json.DeserializeObject(line));
                        }
                        catch
                        {
                            continue;
                        }

                        var id = Json.Int(message, "id", -1);
                        if (id == 1)
                        {
                            await process.StandardInput.WriteLineAsync(Json.Serialize(new Dictionary<string, object>
                            {
                                { "method", "initialized" },
                                { "params", new Dictionary<string, object>() }
                            })).ConfigureAwait(false);
                            await process.StandardInput.WriteLineAsync(Json.Serialize(new Dictionary<string, object>
                            {
                                { "id", 2 },
                                { "method", "account/rateLimits/read" },
                                { "params", new Dictionary<string, object>() }
                            })).ConfigureAwait(false);
                        }
                        else if (id == 2)
                        {
                            var error = Json.Map(message, "error");
                            if (error != null)
                            {
                                throw new InvalidOperationException(Json.String(error, "message", "Codex 回傳錯誤"));
                            }
                            return ParseAppServerResult(Json.Map(message, "result"));
                        }
                    }

                    throw new InvalidOperationException("Codex app-server 未回傳用量");
                }
                finally
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            process.WaitForExit(1000);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        private ProviderSnapshot ParseAppServerResult(IDictionary<string, object> result)
        {
            if (result == null)
            {
                throw new InvalidDataException("Codex 用量格式不正確");
            }

            var snapshot = new ProviderSnapshot("Codex")
            {
                IsAvailable = true,
                UpdatedAt = DateTimeOffset.Now,
                SourceKind = UsageSourceKind.CodexAppServer
            };

            var main = Json.Map(result, "rateLimits");
            var plan = main == null ? string.Empty : Json.String(main, "planType", string.Empty);
            snapshot.Status = string.IsNullOrEmpty(plan)
                ? "官方 app-server"
                : CultureInfo.CurrentCulture.TextInfo.ToTitleCase(plan) + " · 官方 app-server";

            AddLimitBuckets(
                snapshot,
                main,
                main == null ? "codex" : Json.String(main, "limitId", "codex"),
                "主要額度",
                false);

            var byLimit = Json.Map(result, "rateLimitsByLimitId");
            if (byLimit != null)
            {
                foreach (var item in byLimit)
                {
                    if (string.Equals(item.Key, "codex", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    var limit = Json.Object(item.Value);
                    var name = limit == null ? item.Key : Json.String(limit, "limitName", item.Key);
                    AddLimitBuckets(snapshot, limit, item.Key, name, true);
                }
            }

            TrimBuckets(snapshot, 4);
            if (snapshot.Buckets.Count == 0)
            {
                snapshot.IsAvailable = false;
                snapshot.Error = "帳戶尚未回傳限額資料";
            }
            ApplyCodexTrend(snapshot);
            return snapshot;
        }

        /// <summary>記錄每週用量快照，並為每週額度 bucket 填入趨勢與耗盡預測。</summary>
        private static void ApplyCodexTrend(ProviderSnapshot snapshot)
        {
            try
            {
                var weekly = snapshot.Buckets.FirstOrDefault(
                    item => string.Equals(item.Label, "每週額度", StringComparison.Ordinal));
                if (weekly == null)
                {
                    return;
                }

                var samples = CodexHistoryStore.Append(weekly.UsedPercent);
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                weekly.TrendPoints = UsageTrend.BuildTrendPoints(samples, nowMs);
                var projected = UsageTrend.ProjectExhaustion(samples, nowMs);
                if (projected.HasValue
                    && (!weekly.ResetsAt.HasValue || projected.Value < weekly.ResetsAt.Value))
                {
                    weekly.ProjectedExhaustAt = projected;
                }
            }
            catch
            {
                // 趨勢屬輔助資訊，失敗不影響主要用量讀取。
            }
        }

        private static void AddLimitBuckets(
            ProviderSnapshot snapshot,
            IDictionary<string, object> limit,
            string limitId,
            string name,
            bool includeName)
        {
            if (limit == null)
            {
                return;
            }

            AddWindow(snapshot, Json.Map(limit, "primary"), limitId, "primary", name, includeName);
            AddWindow(snapshot, Json.Map(limit, "secondary"), limitId, "secondary", name, includeName);
        }

        private static void AddWindow(
            ProviderSnapshot snapshot,
            IDictionary<string, object> window,
            string limitId,
            string windowRole,
            string name,
            bool includeName)
        {
            if (window == null)
            {
                return;
            }

            double usedPercent;
            if (!Json.TryDouble(window, "usedPercent", out usedPercent))
            {
                return;
            }

            var minutes = Json.Int(window, "windowDurationMins", 0);
            var windowName = WindowName(minutes);
            var label = includeName ? name + " · " + windowName : windowName;
            snapshot.Buckets.Add(new UsageBucket
            {
                Label = label,
                Detail = "已使用",
                StableId = string.Format(
                    CultureInfo.InvariantCulture,
                    "codex-app-server:{0}:{1}:{2}",
                    string.IsNullOrWhiteSpace(limitId) ? "codex" : limitId.Trim(),
                    windowRole,
                    minutes),
                WindowDurationMinutes = minutes,
                UsedPercent = Clamp(usedPercent),
                ResetsAt = TimeUtil.ParseReset(Json.Get(window, "resetsAt"))
            });
        }

        private ProviderSnapshot ReadLatestSession()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");
            if (!Directory.Exists(root))
            {
                return null;
            }

            var files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(info => info.LastWriteTimeUtc)
                .Take(5);

            IDictionary<string, object> latestRateLimits = null;
            DateTimeOffset updatedAt = DateTimeOffset.MinValue;
            foreach (var file in files)
            {
                foreach (var line in ReadTailLines(file.FullName, 2 * 1024 * 1024))
                {
                    try
                    {
                        var entry = Json.Object(Json.DeserializeObject(line));
                        if (!string.Equals(Json.String(entry, "type", string.Empty), "event_msg", StringComparison.Ordinal))
                        {
                            continue;
                        }
                        var payload = Json.Map(entry, "payload");
                        if (!string.Equals(Json.String(payload, "type", string.Empty), "token_count", StringComparison.Ordinal))
                        {
                            continue;
                        }
                        var rateLimits = Json.Map(payload, "rate_limits");
                        if (rateLimits != null)
                        {
                            latestRateLimits = rateLimits;
                            updatedAt = file.LastWriteTime;
                        }
                    }
                    catch
                    {
                    }
                }
                if (latestRateLimits != null)
                {
                    break;
                }
            }

            if (latestRateLimits == null)
            {
                return null;
            }

            var snapshot = new ProviderSnapshot("Codex")
            {
                IsAvailable = true,
                UpdatedAt = updatedAt,
                SourceKind = UsageSourceKind.CodexSessionLog
            };
            AddLegacyWindow(snapshot, Json.Map(latestRateLimits, "primary"), "primary");
            AddLegacyWindow(snapshot, Json.Map(latestRateLimits, "secondary"), "secondary");
            return snapshot;
        }

        private static IEnumerable<string> ReadTailLines(string path, int maximumBytes)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var offset = Math.Max(0, stream.Length - maximumBytes);
                stream.Seek(offset, SeekOrigin.Begin);
                using (var reader = new StreamReader(stream, Encoding.UTF8, true, 4096, false))
                {
                    if (offset > 0)
                    {
                        reader.ReadLine();
                    }
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        yield return line;
                    }
                }
            }
        }

        private static void AddLegacyWindow(
            ProviderSnapshot snapshot,
            IDictionary<string, object> window,
            string windowRole)
        {
            if (window == null)
            {
                return;
            }
            double usedPercent;
            if (!Json.TryDouble(window, "used_percent", out usedPercent))
            {
                return;
            }
            var minutes = Json.Int(window, "window_minutes", 0);
            snapshot.Buckets.Add(new UsageBucket
            {
                Label = WindowName(minutes),
                Detail = "已使用",
                StableId = string.Format(
                    CultureInfo.InvariantCulture,
                    "codex-session:{0}:{1}",
                    windowRole,
                    minutes),
                WindowDurationMinutes = minutes,
                UsedPercent = Clamp(usedPercent),
                ResetsAt = TimeUtil.ParseReset(Json.Get(window, "resets_at"))
            });
        }

        private static string WindowName(int minutes)
        {
            if (minutes >= 10000)
            {
                return "每週額度";
            }
            if (minutes >= 1440)
            {
                return string.Format(CultureInfo.CurrentCulture, "{0} 天視窗", Math.Max(1, minutes / 1440));
            }
            if (minutes >= 60)
            {
                return string.Format(CultureInfo.CurrentCulture, "{0} 小時視窗", Math.Max(1, minutes / 60));
            }
            return minutes > 0 ? minutes + " 分鐘視窗" : "主要額度";
        }

        private static void TrimBuckets(ProviderSnapshot snapshot, int count)
        {
            if (snapshot.Buckets.Count > count)
            {
                snapshot.Buckets.RemoveRange(count, snapshot.Buckets.Count - count);
            }
        }

        internal static double Clamp(double value)
        {
            return Math.Max(0, Math.Min(100, value));
        }

        private static string CompactError(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "未知錯誤";
            }
            value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length > 140 ? value.Substring(0, 140) + "…" : value;
        }
    }

    internal sealed class ClaudeUsageSource : IUsageSource
    {
        private static readonly IDictionary<string, string> CacheLabels = new Dictionary<string, string>
        {
            { "fh", "目前工作階段" },
            { "sd", "所有模型 · 每週" },
            { "so", "Opus · 每週" },
            { "sn", "Sonnet · 每週" },
            { "om", "Fable · 每週" },
            { "oa", "OAuth Apps · 每週" },
            { "cw", "Cowork · 每週" }
        };

        public Task<ProviderSnapshot> ReadAsync()
        {
            return Task.Run(() => Read());
        }

        private ProviderSnapshot Read()
        {
            var snapshot = new ProviderSnapshot("Claude Code")
            {
                SourceKind = UsageSourceKind.ClaudeDesktop
            };
            string historyError = null;
            var historyAvailable = false;
            try
            {
                ApplyDesktopHistory(snapshot);
                historyAvailable = snapshot.Buckets.Count > 0;
            }
            catch (Exception ex)
            {
                historyError = ex.Message;
            }

            var historyTimestamp = snapshot.UpdatedAt;
            IDictionary<string, object> bridge = null;
            try
            {
                bridge = ReadBridge();
                if (bridge != null)
                {
                    var bridgeTimestamp = TimeUtil.ParseReset(Json.Get(bridge, "capturedAt"));
                    var overwriteUsage = bridgeTimestamp.HasValue && bridgeTimestamp.Value >= historyTimestamp;
                    ApplyBridge(snapshot, bridge, overwriteUsage, bridgeTimestamp.Value);
                }
            }
            catch
            {
                bridge = null;
            }

            snapshot.IsAvailable = snapshot.Buckets.Count > 0;
            var sourceLabel = bridge != null && historyAvailable
                ? "Desktop + Code 狀態列"
                : bridge != null ? "Claude Code 狀態列" : "Claude Desktop 快取";
            snapshot.Status = sourceLabel + " · " + TimeUtil.FreshnessLabel(snapshot.UpdatedAt);
            if (!snapshot.IsAvailable)
            {
                snapshot.Status = "目前無法讀取";
                snapshot.Error = string.IsNullOrWhiteSpace(historyError)
                    ? "請先開啟 Claude Desktop 或 Claude Code"
                    : historyError;
            }

            if (snapshot.Buckets.Count > 4)
            {
                snapshot.Buckets.RemoveRange(4, snapshot.Buckets.Count - 4);
            }
            return snapshot;
        }

        private static IDictionary<string, object> ReadBridge()
        {
            if (!File.Exists(AppPaths.ClaudeStatusCache))
            {
                return null;
            }
            var bridge = Json.Object(Json.DeserializeObject(File.ReadAllText(AppPaths.ClaudeStatusCache)));
            var captured = TimeUtil.ParseReset(Json.Get(bridge, "capturedAt"));
            if (!captured.HasValue)
            {
                return null;
            }

            var age = DateTimeOffset.UtcNow - captured.Value.ToUniversalTime();
            if (age < TimeSpan.FromMinutes(-5) || age > TimeSpan.FromHours(24))
            {
                return null;
            }
            return bridge;
        }

        private static void ApplyBridge(
            ProviderSnapshot snapshot,
            IDictionary<string, object> bridge,
            bool overwriteUsage,
            DateTimeOffset capturedAt)
        {
            if (bridge == null)
            {
                return;
            }

            var limits = Json.Map(bridge, "rate_limits") ?? Json.Map(bridge, "rateLimits");
            if (limits == null)
            {
                return;
            }

            AddBridgeBucket(snapshot, "目前工作階段", Json.Map(limits, "five_hour"), overwriteUsage);
            AddBridgeBucket(snapshot, "所有模型 · 每週", Json.Map(limits, "seven_day"), overwriteUsage);
            AddBridgeBucket(snapshot, "Opus · 每週", Json.Map(limits, "seven_day_opus"), overwriteUsage);
            AddBridgeBucket(snapshot, "Sonnet · 每週", Json.Map(limits, "seven_day_sonnet"), overwriteUsage);

            if (capturedAt > snapshot.UpdatedAt)
            {
                snapshot.UpdatedAt = capturedAt;
            }
        }

        private static void AddBridgeBucket(
            ProviderSnapshot snapshot,
            string label,
            IDictionary<string, object> limit,
            bool overwriteUsage)
        {
            if (limit == null)
            {
                return;
            }

            double used;
            var hasUsed = Json.TryDouble(limit, "used_percentage", out used)
                || Json.TryDouble(limit, "usedPercent", out used);
            var reset = Json.Get(limit, "resets_at") ?? Json.Get(limit, "resetsAt");
            var resetAt = TimeUtil.ParseReset(reset);
            if (resetAt.HasValue && resetAt.Value <= DateTimeOffset.Now)
            {
                resetAt = null;
            }

            var existing = snapshot.Buckets.FirstOrDefault(item => string.Equals(item.Label, label, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                if (!hasUsed)
                {
                    return;
                }
                snapshot.Buckets.Add(new UsageBucket
                {
                    Label = label,
                    Detail = "已使用",
                    UsedPercent = CodexUsageSource.Clamp(used),
                    ResetsAt = resetAt
                });
                return;
            }

            if (overwriteUsage && hasUsed)
            {
                existing.UsedPercent = CodexUsageSource.Clamp(used);
            }
            if (resetAt.HasValue)
            {
                // 官方 statusLine 的重設時間覆蓋歷史推算值。
                existing.ResetsAt = resetAt;
                existing.ResetEstimate = ResetEstimateKind.None;
            }
        }

        private static void ApplyDesktopHistory(ProviderSnapshot snapshot)
        {
            var path = FindDesktopHistory();
            if (path == null)
            {
                throw new FileNotFoundException("找不到 Claude Desktop 用量快取");
            }

            var root = Json.Object(Json.DeserializeObject(File.ReadAllText(path)));
            var samples = Json.Array(Json.Get(root, "samples"));
            if (samples == null || samples.Length == 0)
            {
                throw new InvalidDataException("Claude Desktop 尚無用量紀錄");
            }

            IDictionary<string, object> latest = null;
            long latestTimestamp = 0;
            foreach (var item in samples)
            {
                var sample = Json.Object(item);
                var timestamp = Json.Long(sample, "t", 0);
                if (sample != null && timestamp >= latestTimestamp)
                {
                    latest = sample;
                    latestTimestamp = timestamp;
                }
            }

            var usage = Json.Map(latest, "u");
            if (usage == null)
            {
                throw new InvalidDataException("Claude Desktop 用量格式不正確");
            }

            var preferredKeys = new[] { "fh", "sd", "so", "sn", "om", "oa", "cw" };
            foreach (var key in preferredKeys)
            {
                if (!usage.ContainsKey(key))
                {
                    continue;
                }
                var label = CacheLabels.ContainsKey(key) ? CacheLabels[key] : key;
                Upsert(snapshot, new UsageBucket
                {
                    Label = label,
                    Detail = "已使用",
                    UsedPercent = CodexUsageSource.Clamp(Json.Double(usage, key, 0)),
                    ResetsAt = null
                });
            }

            if (latestTimestamp > 0)
            {
                var historyTime = TimeUtil.FromUnixMilliseconds(latestTimestamp);
                if (historyTime > snapshot.UpdatedAt)
                {
                    snapshot.UpdatedAt = historyTime;
                }
            }

            ApplyResetEstimates(snapshot, samples);
        }

        private const long FiveHourMilliseconds = 5L * 3600 * 1000;
        private const long SevenDayMilliseconds = 7L * 24 * 3600 * 1000;

        private sealed class HistoryPoint
        {
            public long T;
            public double FiveHour;
            public double SevenDay;
        }

        /// <summary>
        /// Desktop 快取沒有重設時間；改由歷史樣本推算：
        /// 5 小時視窗取「目前使用段首見樣本 + 5 小時」作為最晚重設時間（上界，必然成立）；
        /// 每週取最近一次用量驟降（重設）邊界，往後以 7 天週期外推。
        /// 官方 statusLine 橋接資料一旦出現，仍會覆蓋這裡的推算值。
        /// </summary>
        private static void ApplyResetEstimates(ProviderSnapshot snapshot, object[] rawSamples)
        {
            var points = ParsePoints(rawSamples);
            if (points.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var fiveHour = EstimateFiveHourReset(points, now);
            if (fiveHour.HasValue)
            {
                SetEstimate(snapshot, "目前工作階段", fiveHour.Value, ResetEstimateKind.UpperBound);
            }

            var weekly = EstimateWeeklyReset(points, now);
            if (weekly.HasValue)
            {
                SetEstimate(snapshot, "所有模型 · 每週", weekly.Value, ResetEstimateKind.Cycle);
            }

            ApplyWeeklyTrend(snapshot, points, now.ToUnixTimeMilliseconds());
            ApplyFiveHourTrend(snapshot, points, now.ToUnixTimeMilliseconds());
        }

        /// <summary>以 Desktop 歷史為 5 小時 bucket 填入短窗趨勢（面板 sparkline 用）。</summary>
        private static void ApplyFiveHourTrend(ProviderSnapshot snapshot, List<HistoryPoint> points, long nowMs)
        {
            var session = snapshot.Buckets.FirstOrDefault(
                item => string.Equals(item.Label, "目前工作階段", StringComparison.OrdinalIgnoreCase));
            if (session == null)
            {
                return;
            }

            var samples = new List<TrendSample>(points.Count);
            foreach (var point in points)
            {
                samples.Add(new TrendSample { T = point.T, U = point.FiveHour });
            }
            session.TrendPoints = UsageTrend.BuildTrendPoints(samples, nowMs, FiveHourMilliseconds, 40);
        }

        /// <summary>以 Desktop 歷史為每週 bucket 填入 7 天趨勢與耗盡預測。</summary>
        private static void ApplyWeeklyTrend(ProviderSnapshot snapshot, List<HistoryPoint> points, long nowMs)
        {
            var weekly = snapshot.Buckets.FirstOrDefault(
                item => string.Equals(item.Label, "所有模型 · 每週", StringComparison.OrdinalIgnoreCase));
            if (weekly == null)
            {
                return;
            }

            var samples = new List<TrendSample>(points.Count);
            foreach (var point in points)
            {
                samples.Add(new TrendSample { T = point.T, U = point.SevenDay });
            }

            weekly.TrendPoints = UsageTrend.BuildTrendPoints(samples, nowMs);
            var projected = UsageTrend.ProjectExhaustion(samples, nowMs);
            if (projected.HasValue
                && (!weekly.ResetsAt.HasValue || projected.Value < weekly.ResetsAt.Value))
            {
                weekly.ProjectedExhaustAt = projected;
            }
        }

        private static List<HistoryPoint> ParsePoints(object[] samples)
        {
            var list = new List<HistoryPoint>(samples.Length);
            foreach (var item in samples)
            {
                var sample = Json.Object(item);
                if (sample == null)
                {
                    continue;
                }
                var timestamp = Json.Long(sample, "t", 0);
                var usage = Json.Map(sample, "u");
                if (timestamp <= 0 || usage == null)
                {
                    continue;
                }
                list.Add(new HistoryPoint
                {
                    T = timestamp,
                    FiveHour = Json.Double(usage, "fh", 0),
                    SevenDay = Json.Double(usage, "sd", 0)
                });
            }
            list.Sort(delegate(HistoryPoint left, HistoryPoint right) { return left.T.CompareTo(right.T); });
            return list;
        }

        private static DateTimeOffset? EstimateFiveHourReset(List<HistoryPoint> points, DateTimeOffset now)
        {
            // 從最新樣本往回找「目前視窗」的首見樣本：遇到用量歸零、下降邊界
            // 或超過 5 小時的取樣空洞（其間必已重設）即停。
            var first = -1;
            for (var i = points.Count - 1; i >= 0; i--)
            {
                if (points[i].FiveHour <= 0.05)
                {
                    break;
                }
                first = i;
                if (i == 0)
                {
                    break;
                }
                var previous = points[i - 1];
                if (points[i].T - previous.T > FiveHourMilliseconds)
                {
                    break;
                }
                if (previous.FiveHour > points[i].FiveHour + 0.5)
                {
                    break;
                }
                if (previous.FiveHour <= 0.05)
                {
                    break;
                }
            }

            if (first < 0)
            {
                return null;
            }
            var resetAtLatest = TimeUtil.FromUnixMilliseconds(points[first].T + FiveHourMilliseconds);
            return resetAtLatest > now ? resetAtLatest : (DateTimeOffset?)null;
        }

        private static DateTimeOffset? EstimateWeeklyReset(List<HistoryPoint> points, DateTimeOffset now)
        {
            const long maxBoundaryGap = 6L * 3600 * 1000;
            for (var i = points.Count - 1; i >= 1; i--)
            {
                var previous = points[i - 1];
                var current = points[i];
                if (previous.SevenDay > current.SevenDay + 2 && current.T - previous.T <= maxBoundaryGap)
                {
                    var anchor = TimeUtil.FromUnixMilliseconds(previous.T + (current.T - previous.T) / 2);
                    if ((now - anchor).TotalDays > 22)
                    {
                        return null; // 錨點太舊，外推可信度不足。
                    }
                    var reset = anchor.AddMilliseconds(SevenDayMilliseconds);
                    while (reset <= now)
                    {
                        reset = reset.AddMilliseconds(SevenDayMilliseconds);
                    }
                    return reset;
                }
            }
            return null;
        }

        private static void SetEstimate(
            ProviderSnapshot snapshot,
            string label,
            DateTimeOffset resetAt,
            ResetEstimateKind kind)
        {
            var bucket = snapshot.Buckets.FirstOrDefault(
                item => string.Equals(item.Label, label, StringComparison.OrdinalIgnoreCase));
            if (bucket != null && !bucket.ResetsAt.HasValue)
            {
                bucket.ResetsAt = resetAt;
                bucket.ResetEstimate = kind;
            }
        }

        private static string FindDesktopHistory()
        {
            var overridePath = Environment.GetEnvironmentVariable("CODEX_CLAUDE_USAGE_CLAUDE_HISTORY_PATH");
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                var fullPath = Path.GetFullPath(overridePath);
                return File.Exists(fullPath) ? fullPath : null;
            }

            var packages = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages");
            if (!Directory.Exists(packages))
            {
                return null;
            }

            try
            {
                return Directory.EnumerateDirectories(packages, "Claude_*")
                    .Select(path => Path.Combine(path, "LocalCache", "Roaming", "Claude", "plan-usage-history.json"))
                    .Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static void Upsert(ProviderSnapshot snapshot, UsageBucket bucket)
        {
            var existing = snapshot.Buckets.FirstOrDefault(item => string.Equals(item.Label, bucket.Label, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                snapshot.Buckets.Add(bucket);
                return;
            }

            existing.UsedPercent = bucket.UsedPercent;
            existing.ResetsAt = bucket.ResetsAt ?? existing.ResetsAt;
        }
    }

    /// <summary>
    /// Antigravity 用量歷史持久化：每個額度桶各自保存一條取樣序列，
    /// 供趨勢線、耗盡預測與離線快取重建使用。
    /// 檔案格式 v2：{ "version": 2, "series": { "&lt;bucketId&gt;": [ { "t": ms, "u": pct } ] } }；
    /// v1（單一 samples 陣列）載入時自動遷移為 gemini-weekly 序列。
    /// </summary>
    internal sealed class AntigravityHistory
    {
        private const long RetainMs = 14L * 24 * 3600 * 1000;
        private const long DebounceMs = 10L * 60 * 1000;
        private const double DebounceDelta = 0.5;
        private const int MaxSamplesPerSeries = 1200;
        private const string LegacySeriesKey = "gemini-weekly";

        private static readonly object Gate = new object();

        private readonly Dictionary<string, List<TrendSample>> _series;
        private bool _dirty;

        private AntigravityHistory(Dictionary<string, List<TrendSample>> series)
        {
            _series = series;
        }

        private static string StorePath
        {
            get
            {
                var overridePath = Environment.GetEnvironmentVariable("CODEX_CLAUDE_USAGE_ANTIGRAVITY_HISTORY_PATH");
                return string.IsNullOrWhiteSpace(overridePath)
                    ? Path.Combine(AppPaths.LocalData, "antigravity-usage-history.json")
                    : Path.GetFullPath(overridePath);
            }
        }

        public static AntigravityHistory Load()
        {
            var series = new Dictionary<string, List<TrendSample>>(StringComparer.OrdinalIgnoreCase);
            lock (Gate)
            {
                try
                {
                    var path = StorePath;
                    if (File.Exists(path))
                    {
                        var map = Json.Object(Json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)));
                        if (map != null)
                        {
                            var seriesMap = Json.Map(map, "series");
                            if (seriesMap != null)
                            {
                                foreach (var pair in seriesMap)
                                {
                                    var list = ParseSamples(Json.Array(pair.Value));
                                    if (list.Count > 0)
                                    {
                                        series[pair.Key] = list;
                                    }
                                }
                            }
                            else
                            {
                                // v1 相容：單一 samples 陣列即 Gemini 每週序列。
                                var legacy = ParseSamples(Json.Array(Json.Get(map, "samples")));
                                if (legacy.Count > 0)
                                {
                                    series[LegacySeriesKey] = legacy;
                                }
                            }
                        }
                    }
                }
                catch
                {
                    series.Clear();
                }
            }
            return new AntigravityHistory(series);
        }

        private static List<TrendSample> ParseSamples(object[] array)
        {
            var list = new List<TrendSample>();
            if (array == null)
            {
                return list;
            }
            foreach (var item in array)
            {
                var record = Json.Object(item);
                if (record == null)
                {
                    continue;
                }
                var t = Json.Long(record, "t", 0);
                var u = Json.Double(record, "u", double.NaN);
                if (t > 0 && !double.IsNaN(u))
                {
                    list.Add(new TrendSample { T = t, U = Math.Max(0, Math.Min(100, u)) });
                }
            }
            list.Sort(delegate(TrendSample left, TrendSample right) { return left.T.CompareTo(right.T); });
            return list;
        }

        /// <summary>取得某個額度桶的取樣序列（唯讀用途，不存在時回傳空序列）。</summary>
        public List<TrendSample> Series(string bucketId)
        {
            List<TrendSample> list;
            var key = NormalizeKey(bucketId);
            return _series.TryGetValue(key, out list) ? list : new List<TrendSample>();
        }

        /// <summary>
        /// 追加一筆取樣：與前一筆間隔不足 10 分鐘且變化小於 0.5% 時視為雜訊略過，
        /// 用量回落（重設）一律記錄，確保耗盡預測能辨識新週期。
        /// </summary>
        public List<TrendSample> Append(string bucketId, double usedPercent, long nowMs)
        {
            var key = NormalizeKey(bucketId);
            List<TrendSample> samples;
            if (!_series.TryGetValue(key, out samples))
            {
                samples = new List<TrendSample>();
                _series[key] = samples;
            }

            var value = Math.Max(0, Math.Min(100, usedPercent));
            var previous = samples.Count > 0 ? samples[samples.Count - 1] : null;
            var reset = previous != null && value + 2 < previous.U;
            if (previous != null
                && !reset
                && nowMs - previous.T < DebounceMs
                && Math.Abs(previous.U - value) < DebounceDelta)
            {
                return samples;
            }

            samples.Add(new TrendSample { T = nowMs, U = value });
            samples.RemoveAll(delegate(TrendSample item) { return item.T < nowMs - RetainMs; });
            if (samples.Count > MaxSamplesPerSeries)
            {
                samples.RemoveRange(0, samples.Count - MaxSamplesPerSeries);
            }
            _dirty = true;
            return samples;
        }

        /// <summary>將本輪所有變更一次寫回磁碟（無變更時不做 I/O）。</summary>
        public void Save()
        {
            if (!_dirty)
            {
                return;
            }
            lock (Gate)
            {
                try
                {
                    var seriesMap = new Dictionary<string, object>();
                    foreach (var pair in _series)
                    {
                        var records = new List<object>(pair.Value.Count);
                        foreach (var sample in pair.Value)
                        {
                            records.Add(new Dictionary<string, object> { { "t", sample.T }, { "u", sample.U } });
                        }
                        seriesMap[pair.Key] = records;
                    }

                    var payload = Json.Serialize(new Dictionary<string, object>
                    {
                        { "version", 2 },
                        { "series", seriesMap }
                    });

                    var path = StorePath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    var temporary = path + ".tmp";
                    File.WriteAllText(temporary, payload, Encoding.UTF8);
                    if (File.Exists(path))
                    {
                        File.Replace(temporary, path, null);
                    }
                    else
                    {
                        File.Move(temporary, path);
                    }
                    _dirty = false;
                }
                catch
                {
                }
            }
        }

        private static string NormalizeKey(string bucketId)
        {
            if (string.IsNullOrWhiteSpace(bucketId))
            {
                return "unknown";
            }
            var key = bucketId.Trim();
            const string prefix = "antigravity:";
            return key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? key.Substring(prefix.Length)
                : key;
        }
    }

    /// <summary>
    /// Antigravity 用量來源：自動探測本機運行的 Antigravity 語言伺服器，
    /// 透過 Connect-RPC (HTTP POST) 讀取 Gemini 與第三方模型的配額與重設時間。
    /// 連線參數（埠號、CSRF）成功後快取於記憶體，僅在請求失敗時重新探測；
    /// 四個額度桶各自累積取樣序列，提供趨勢線、耗盡預測與離線重建。
    /// </summary>
    internal sealed class AntigravityUsageSource : IUsageSource
    {
        private const int WeeklyWindowMinutes = 7 * 24 * 60;
        private const int ShortWindowMinutes = 300;
        private const long FiveHourWindowMs = 5L * 3600 * 1000;
        private const int FiveHourBins = 40;
        private const double IdleUsedThreshold = 0.05;
        private const long SlidingResetToleranceMs = 5L * 60 * 1000;
        private const double TierCacheMinutes = 30.0;
        private const double CacheStaleHours = 6.0;
        private const double CacheExpiryHours = 72.0;
        private const int LogTailBytes = 256 * 1024;
        private const int LogTailLines = 400;
        private const int RequestTimeoutMs = 2500;

        private static readonly object CredentialGate = new object();
        private static int _cachedPort;
        private static string _cachedCsrf;
        private static string _cachedTierName;
        private static DateTime _tierCachedUtc = DateTime.MinValue;

        public Task<ProviderSnapshot> ReadAsync()
        {
            return Task.Run(() => Read());
        }

        private ProviderSnapshot Read()
        {
            var snapshot = new ProviderSnapshot("Antigravity")
            {
                SourceKind = UsageSourceKind.AntigravityServer
            };

            // 1. 測試環境掛鉤：直接套用指定的快照檔。
            var customCache = Environment.GetEnvironmentVariable("CODEX_CLAUDE_USAGE_ANTIGRAVITY_PATH");
            if (!string.IsNullOrWhiteSpace(customCache) && File.Exists(customCache))
            {
                if (ApplyCacheFile(snapshot, customCache))
                {
                    return snapshot;
                }
            }

            string error = null;

            // 2. 先用上一次成功的連線參數（省去每輪掃描記錄檔）。
            int port;
            string csrf;
            if (TryGetCachedCredentials(out port, out csrf))
            {
                try
                {
                    if (QueryServer(snapshot, port, csrf))
                    {
                        SaveCache(snapshot);
                        return snapshot;
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
                InvalidateCredentials();
            }

            // 3. 重新探測本機語言伺服器連線參數。
            if (TryDiscoverCredentials(out port, out csrf, out error))
            {
                try
                {
                    if (QueryServer(snapshot, port, csrf))
                    {
                        CacheCredentials(port, csrf);
                        SaveCache(snapshot);
                        return snapshot;
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }
            }

            // 4. 即時連線失敗時回退到上一次成功的本地快取。
            if (TryApplyCache(snapshot))
            {
                return snapshot;
            }

            snapshot.IsAvailable = false;
            snapshot.Status = "目前無法讀取";
            snapshot.Error = string.IsNullOrWhiteSpace(error)
                ? "請先啟動 Antigravity"
                : error;
            return snapshot;
        }

        private static bool TryGetCachedCredentials(out int port, out string csrf)
        {
            lock (CredentialGate)
            {
                port = _cachedPort;
                csrf = _cachedCsrf;
            }
            return port > 0 && !string.IsNullOrEmpty(csrf);
        }

        private static void CacheCredentials(int port, string csrf)
        {
            lock (CredentialGate)
            {
                _cachedPort = port;
                _cachedCsrf = csrf;
            }
        }

        private static void InvalidateCredentials()
        {
            lock (CredentialGate)
            {
                _cachedPort = 0;
                _cachedCsrf = null;
            }
        }

        private static bool TryDiscoverCredentials(out int port, out string csrf, out string error)
        {
            port = 0;
            csrf = null;
            error = null;

            // 優先檢查環境變數（測試與手動覆寫）。
            var envPort = Environment.GetEnvironmentVariable("ANTIGRAVITY_PORT");
            if (string.IsNullOrWhiteSpace(envPort))
            {
                envPort = Environment.GetEnvironmentVariable("ANTIGRAVITY_LS_PORT");
            }
            var envCsrf = Environment.GetEnvironmentVariable("ANTIGRAVITY_CSRF_TOKEN");
            if (!string.IsNullOrWhiteSpace(envPort) && !string.IsNullOrWhiteSpace(envCsrf))
            {
                int p;
                if (int.TryParse(envPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out p) && p > 0)
                {
                    port = p;
                    csrf = envCsrf.Trim();
                    return true;
                }
            }

            var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appdata))
            {
                error = "無法取得 APPDATA 路徑";
                return false;
            }

            var logDirectory = Path.Combine(appdata, "Antigravity", "logs");
            var mainLog = Path.Combine(logDirectory, "main.log");
            var lsLog = Path.Combine(logDirectory, "language_server.log");

            if (!File.Exists(mainLog))
            {
                error = "找不到 Antigravity 記錄檔 (請確認已安裝並執行)";
                return false;
            }

            int fallbackPort = 0;

            try
            {
                // 先掃描記錄檔尾端，找不到憑證時才回頭整檔掃描（啟動列可能距今很遠）。
                ScanMainLog(ReadTailLines(mainLog, LogTailBytes, LogTailLines), ref csrf, ref fallbackPort);
                if (string.IsNullOrEmpty(csrf) || fallbackPort <= 0)
                {
                    ScanMainLog(ReadAllLines(mainLog, LogTailLines * 8), ref csrf, ref fallbackPort);
                }
            }
            catch (Exception ex)
            {
                error = "讀取 main.log 失敗: " + ex.Message;
            }

            var httpPort = 0;
            if (File.Exists(lsLog))
            {
                try
                {
                    httpPort = ScanLanguageServerLog(ReadTailLines(lsLog, LogTailBytes, LogTailLines));
                    if (httpPort <= 0)
                    {
                        httpPort = ScanLanguageServerLog(ReadAllLines(lsLog, LogTailLines * 8));
                    }
                }
                catch
                {
                }
            }

            port = httpPort > 0 ? httpPort : (fallbackPort > 0 ? fallbackPort + 1 : 0);
            if (port <= 0 || string.IsNullOrEmpty(csrf))
            {
                if (string.IsNullOrEmpty(error))
                {
                    error = "尚未偵測到執行的 Antigravity 語言伺服器";
                }
                return false;
            }

            return true;
        }

        private static void ScanMainLog(List<string> lines, ref string csrf, ref int fallbackPort)
        {
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var text = lines[i];
                if (string.IsNullOrEmpty(csrf))
                {
                    var match = Regex.Match(text, @"--csrf_token\s+([a-f0-9\-]+)", RegexOptions.IgnoreCase);
                    if (match.Success)
                    {
                        csrf = match.Groups[1].Value;
                    }
                }
                if (fallbackPort <= 0)
                {
                    var portMatch = Regex.Match(text, @"Port changed! Reloading all windows with URL: https?://127\.0\.0\.1:(\d+)/", RegexOptions.IgnoreCase);
                    if (portMatch.Success)
                    {
                        int parsed;
                        if (int.TryParse(portMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                        {
                            fallbackPort = parsed;
                        }
                    }
                }
                if (!string.IsNullOrEmpty(csrf) && fallbackPort > 0)
                {
                    return;
                }
            }
        }

        private static int ScanLanguageServerLog(List<string> lines)
        {
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var match = Regex.Match(lines[i], @"listening on random port at (\d+) for HTTP\b", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    int parsed;
                    if (int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    {
                        return parsed;
                    }
                }
            }
            return 0;
        }

        /// <summary>只讀取記錄檔尾端指定位元組，避免每輪掃描數十 MB 的整份記錄。</summary>
        private static List<string> ReadTailLines(string path, int maxBytes, int maxLines)
        {
            var lines = new List<string>(Math.Min(maxLines, 512));
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                var length = stream.Length;
                var start = length > maxBytes ? length - maxBytes : 0;
                if (start > 0)
                {
                    stream.Seek(start, SeekOrigin.Begin);
                }
                using (var reader = new StreamReader(stream, Encoding.UTF8, false, 8192, true))
                {
                    if (start > 0)
                    {
                        reader.ReadLine(); // 丟棄可能被截斷的首行。
                    }
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (lines.Count >= maxLines)
                        {
                            lines.RemoveAt(0);
                        }
                        lines.Add(line);
                    }
                }
            }
            return lines;
        }

        private static List<string> ReadAllLines(string path, int maxLines)
        {
            var lines = new List<string>(Math.Min(maxLines, 1024));
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (lines.Count >= maxLines)
                    {
                        lines.RemoveAt(0);
                    }
                    lines.Add(line);
                }
            }
            return lines;
        }

        private static bool QueryServer(ProviderSnapshot snapshot, int port, string csrf)
        {
            var quotaJson = HttpPostJson(port, csrf, "RetrieveUserQuotaSummary");
            if (string.IsNullOrWhiteSpace(quotaJson))
            {
                return false;
            }

            var root = Json.Object(Json.DeserializeObject(quotaJson));
            var response = Json.Map(root, "response") ?? root;
            var groups = Json.Array(Json.Get(response, "groups"));
            if (groups == null || groups.Length == 0)
            {
                return false;
            }

            var tierName = ResolveTierName(port, csrf);

            snapshot.Buckets.Clear();
            var now = DateTimeOffset.Now;
            var nowMs = now.ToUniversalTime().ToUnixTimeMilliseconds();
            var history = AntigravityHistory.Load();

            foreach (var groupItem in groups)
            {
                var groupMap = Json.Object(groupItem);
                if (groupMap == null) continue;

                var groupTitle = Json.String(groupMap, "displayName", "");
                var isGemini = groupTitle.IndexOf("gemini", StringComparison.OrdinalIgnoreCase) >= 0;
                var bucketsArray = Json.Array(Json.Get(groupMap, "buckets"));
                if (bucketsArray == null) continue;

                foreach (var bucketItem in bucketsArray)
                {
                    var bMap = Json.Object(bucketItem);
                    if (bMap == null) continue;

                    var bucketId = Json.String(bMap, "bucketId", "");
                    var window = Json.String(bMap, "window", "");
                    var remVal = Json.Double(bMap, "remainingFraction", 1.0);
                    var resetStr = Json.String(bMap, "resetTime", null);
                    var usedPct = Math.Max(0.0, Math.Min(100.0, Math.Round((1.0 - remVal) * 100.0, 1)));

                    var isWeekly = window.Equals("weekly", StringComparison.OrdinalIgnoreCase)
                                   || bucketId.IndexOf("weekly", StringComparison.OrdinalIgnoreCase) >= 0;
                    var windowMinutes = isWeekly ? WeeklyWindowMinutes : ShortWindowMinutes;
                    var resetsAt = TimeUtil.ParseReset(resetStr);

                    var bucket = new UsageBucket
                    {
                        StableId = "antigravity:" + bucketId,
                        WindowDurationMinutes = windowMinutes,
                        UsedPercent = usedPct,
                        ResetsAt = resetsAt,
                        ResetEstimate = ClassifyReset(resetsAt, usedPct, windowMinutes, now)
                    };

                    if (isGemini)
                    {
                        bucket.Label = isWeekly ? "Gemini 每週額度" : "Gemini 5 小時額度";
                        bucket.Detail = "Gemini Flash, Gemini Pro";
                    }
                    else
                    {
                        bucket.Label = isWeekly ? "3P 模型每週額度" : "3P 模型 5 小時額度";
                        bucket.Detail = "Claude Opus/Sonnet, GPT-OSS";
                    }

                    // 四個額度桶各自累積序列：每週用 7 天視窗，5 小時用短視窗。
                    var samples = history.Append(bucketId, usedPct, nowMs);
                    ApplyTrend(bucket, samples, nowMs, isWeekly);

                    snapshot.Buckets.Add(bucket);
                }
            }

            history.Save();

            snapshot.Buckets.Sort((a, b) =>
            {
                var aGemini = a.Label.StartsWith("Gemini", StringComparison.Ordinal);
                var bGemini = b.Label.StartsWith("Gemini", StringComparison.Ordinal);
                if (aGemini != bGemini) return aGemini ? -1 : 1;
                var aWeekly = a.Label.IndexOf("每週", StringComparison.Ordinal) >= 0;
                var bWeekly = b.Label.IndexOf("每週", StringComparison.Ordinal) >= 0;
                if (aWeekly != bWeekly) return aWeekly ? -1 : 1;
                return string.Compare(a.Label, b.Label, StringComparison.Ordinal);
            });

            snapshot.UpdatedAt = now;
            snapshot.IsAvailable = snapshot.Buckets.Count > 0;
            var prefix = string.IsNullOrWhiteSpace(tierName) ? "" : tierName + " · ";
            snapshot.Status = prefix + "官方即時資料 · " + TimeUtil.FreshnessLabel(snapshot.UpdatedAt);
            return snapshot.IsAvailable;
        }

        /// <summary>
        /// 官方在額度尚未使用時回報「現在起算的完整視窗結束點」，每次採樣都會往後滑動；
        /// 這種情況標示為推估上限，避免面板顯示一個永遠不會到來的重設時刻。
        /// </summary>
        private static ResetEstimateKind ClassifyReset(DateTimeOffset? resetsAt, double usedPercent, int windowMinutes, DateTimeOffset now)
        {
            if (!resetsAt.HasValue || usedPercent > IdleUsedThreshold)
            {
                return ResetEstimateKind.None;
            }
            var expected = now.AddMinutes(windowMinutes);
            var drift = Math.Abs((resetsAt.Value - expected).TotalMilliseconds);
            return drift <= SlidingResetToleranceMs ? ResetEstimateKind.UpperBound : ResetEstimateKind.None;
        }

        private static void ApplyTrend(UsageBucket bucket, List<TrendSample> samples, long nowMs, bool isWeekly)
        {
            if (samples == null || samples.Count < 2)
            {
                return;
            }

            bucket.TrendPoints = isWeekly
                ? UsageTrend.BuildTrendPoints(samples, nowMs)
                : UsageTrend.BuildTrendPoints(samples, nowMs, FiveHourWindowMs, FiveHourBins);

            var projected = UsageTrend.ProjectExhaustion(samples, nowMs);
            // 預測若落在本視窗重設之後即無意義（額度會先被重設補滿）。
            if (projected.HasValue && bucket.ResetsAt.HasValue && projected.Value > bucket.ResetsAt.Value)
            {
                projected = null;
            }
            bucket.ProjectedExhaustAt = projected;
        }

        /// <summary>方案層級（tier）幾乎不變動，快取 30 分鐘以省下每輪一次 RPC。</summary>
        private static string ResolveTierName(int port, string csrf)
        {
            lock (CredentialGate)
            {
                if (!string.IsNullOrEmpty(_cachedTierName)
                    && (DateTime.UtcNow - _tierCachedUtc).TotalMinutes < TierCacheMinutes)
                {
                    return _cachedTierName;
                }
            }

            string tierName = null;
            try
            {
                var userJson = HttpPostJson(port, csrf, "GetUserStatus");
                if (!string.IsNullOrWhiteSpace(userJson))
                {
                    var userRoot = Json.Object(Json.DeserializeObject(userJson));
                    var userResp = Json.Map(userRoot, "response") ?? userRoot;
                    var tierMap = Json.Map(userResp, "userTier");
                    if (tierMap != null)
                    {
                        tierName = Json.String(tierMap, "name", null);
                    }
                }
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(tierName))
            {
                lock (CredentialGate)
                {
                    _cachedTierName = tierName;
                    _tierCachedUtc = DateTime.UtcNow;
                }
            }
            return tierName;
        }

        private static string HttpPostJson(int port, string csrf, string method)
        {
            var url = string.Format(CultureInfo.InvariantCulture, "http://127.0.0.1:{0}/exa.language_server_pb.LanguageServerService/{1}", port, method);
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Headers.Add("x-codeium-csrf-token", csrf);
            req.Timeout = RequestTimeoutMs;
            req.ReadWriteTimeout = RequestTimeoutMs;
            req.KeepAlive = false;
            req.Proxy = null; // 迴圈位址不走系統 Proxy，避免代理探測造成數秒延遲。

            var body = Encoding.UTF8.GetBytes("{}");
            req.ContentLength = body.Length;
            using (var reqStream = req.GetRequestStream())
            {
                reqStream.Write(body, 0, body.Length);
            }

            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
            {
                return reader.ReadToEnd();
            }
        }

        private static void SaveCache(ProviderSnapshot snapshot)
        {
            try
            {
                var buckets = new List<object>();
                foreach (var b in snapshot.Buckets)
                {
                    buckets.Add(new Dictionary<string, object>
                    {
                        { "label", b.Label },
                        { "detail", b.Detail },
                        { "stableId", b.StableId },
                        { "windowDurationMinutes", b.WindowDurationMinutes },
                        { "usedPercent", b.UsedPercent },
                        { "resetEstimate", b.ResetEstimate.ToString() },
                        { "resetsAt", b.ResetsAt.HasValue ? b.ResetsAt.Value.ToString("o", CultureInfo.InvariantCulture) : null }
                    });
                }
                var map = new Dictionary<string, object>
                {
                    { "capturedAt", snapshot.UpdatedAt.ToString("o", CultureInfo.InvariantCulture) },
                    { "status", snapshot.Status },
                    { "buckets", buckets }
                };
                var path = AppPaths.AntigravityStatusCache;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, Json.Serialize(map), Encoding.UTF8);
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null);
                }
                else
                {
                    File.Move(temporary, path);
                }
            }
            catch
            {
            }
        }

        private static bool TryApplyCache(ProviderSnapshot snapshot)
        {
            var path = AppPaths.AntigravityStatusCache;
            return ApplyCacheFile(snapshot, path);
        }

        private static bool ApplyCacheFile(ProviderSnapshot snapshot, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            try
            {
                var map = Json.Object(Json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8)));
                if (map == null) return false;

                var captured = TimeUtil.ParseReset(Json.Get(map, "capturedAt"));
                var bucketsArray = Json.Array(Json.Get(map, "buckets"));
                if (bucketsArray == null || bucketsArray.Length == 0) return false;

                var updatedAt = captured ?? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
                var age = DateTimeOffset.Now - updatedAt.ToLocalTime();
                if (age.TotalHours > CacheExpiryHours)
                {
                    // 過期太久的快取不再冒充可用資料。
                    return false;
                }

                var history = AntigravityHistory.Load();
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                snapshot.Buckets.Clear();
                foreach (var item in bucketsArray)
                {
                    var bMap = Json.Object(item);
                    if (bMap == null) continue;

                    var windowMinutes = Json.Int(bMap, "windowDurationMinutes", ShortWindowMinutes);
                    var bucket = new UsageBucket
                    {
                        Label = Json.String(bMap, "label", "額度"),
                        Detail = Json.String(bMap, "detail", ""),
                        StableId = Json.String(bMap, "stableId", ""),
                        WindowDurationMinutes = windowMinutes,
                        UsedPercent = Json.Double(bMap, "usedPercent", 0),
                        ResetsAt = TimeUtil.ParseReset(Json.Get(bMap, "resetsAt")),
                        ResetEstimate = ParseResetEstimate(Json.String(bMap, "resetEstimate", null))
                    };

                    // 離線時仍以本機歷史重建趨勢線，面板不會突然變成空白。
                    var samples = history.Series(bucket.StableId);
                    ApplyTrend(bucket, samples, nowMs, windowMinutes >= WeeklyWindowMinutes);

                    snapshot.Buckets.Add(bucket);
                }

                snapshot.UpdatedAt = updatedAt;
                snapshot.IsAvailable = snapshot.Buckets.Count > 0;
                snapshot.Status = (age.TotalHours >= CacheStaleHours ? "Antigravity 未執行 · 快取 " : "快取資料 · ")
                    + TimeUtil.FreshnessLabel(snapshot.UpdatedAt);
                return snapshot.IsAvailable;
            }
            catch
            {
                return false;
            }
        }

        private static ResetEstimateKind ParseResetEstimate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return ResetEstimateKind.None;
            }
            if (text.Equals("UpperBound", StringComparison.OrdinalIgnoreCase))
            {
                return ResetEstimateKind.UpperBound;
            }
            if (text.Equals("Cycle", StringComparison.OrdinalIgnoreCase))
            {
                return ResetEstimateKind.Cycle;
            }
            return ResetEstimateKind.None;
        }
    }
}
