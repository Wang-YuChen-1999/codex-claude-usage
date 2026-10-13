using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

namespace CodexClaudeUsage
{
    /// <summary>重設時間的來源性質：官方值、推算上界（最晚於此時重設）、或週期推算。</summary>
    internal enum ResetEstimateKind
    {
        None,
        UpperBound,
        Cycle
    }

    internal enum UsageSourceKind
    {
        Unknown,
        CodexAppServer,
        CodexSessionLog,
        ClaudeDesktop,
        AntigravityServer
    }

    internal sealed class UsageBucket
    {
        public UsageBucket()
        {
            Label = string.Empty;
            Detail = string.Empty;
            StableId = string.Empty;
        }

        public string Label { get; set; }
        public string Detail { get; set; }
        public string StableId { get; set; }
        public int WindowDurationMinutes { get; set; }
        public double UsedPercent { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
        public ResetEstimateKind ResetEstimate { get; set; }

        /// <summary>近 7 天用量趨勢（0–100，NaN 表示該時段無資料）；null 則不顯示迷你圖。</summary>
        public double[] TrendPoints { get; set; }

        /// <summary>依近期使用速度預測的耗盡時刻；僅在早於重設時間（或重設未知）時設定。</summary>
        public DateTimeOffset? ProjectedExhaustAt { get; set; }
    }

    internal sealed class ProviderSnapshot
    {
        public ProviderSnapshot(string name)
        {
            Name = name;
            Status = "等待更新";
            Buckets = new List<UsageBucket>();
            UpdatedAt = DateTimeOffset.MinValue;
            SourceKind = UsageSourceKind.Unknown;
        }

        public string Name { get; private set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public bool IsAvailable { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public UsageSourceKind SourceKind { get; set; }
        public LocalTokenUsage Tokens { get; set; }
        public List<UsageBucket> Buckets { get; private set; }
    }

    internal sealed class UsageSnapshot
    {
        public UsageSnapshot()
        {
            Codex = new ProviderSnapshot("Codex");
            Claude = new ProviderSnapshot("Claude Code");
            Antigravity = new ProviderSnapshot("Antigravity");
            UpdatedAt = DateTimeOffset.Now;
        }

        public ProviderSnapshot Codex { get; set; }
        public ProviderSnapshot Claude { get; set; }
        public ProviderSnapshot Antigravity { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    internal static class TokenUsageText
    {
        public static string Scope(LocalTokenUsage tokens, bool supported)
        {
            if (!supported)
            {
                return "Token 計數";
            }
            if (tokens == null)
            {
                return "本機最近工作階段";
            }
            return tokens.UpdatedAt.ToLocalTime().Date == DateTimeOffset.Now.Date
                ? "本機最近工作階段 · 今日更新"
                : "本機歷史工作階段 · " + DateLabel(tokens);
        }

        public static string Value(LocalTokenUsage tokens, bool supported)
        {
            if (!supported)
            {
                return "來源未提供";
            }
            if (tokens == null)
            {
                return "暫無完整紀錄";
            }
            return tokens.TotalTokens.ToString("N0", CultureInfo.CurrentCulture) + " Token";
        }

        public static string Short(LocalTokenUsage tokens)
        {
            return tokens == null
                ? "Token 未提供"
                : Value(tokens, true) + " · " + Scope(tokens, true);
        }

        public static string Compact(LocalTokenUsage tokens)
        {
            return Scope(tokens, true) + " · " + Value(tokens, true);
        }

        private static string DateLabel(LocalTokenUsage tokens)
        {
            var local = tokens.UpdatedAt.ToLocalTime();
            if (local.Date == DateTimeOffset.Now.Date)
            {
                return "今日";
            }
            return local.Year == DateTimeOffset.Now.Year
                ? local.ToString("MM/dd", CultureInfo.CurrentCulture)
                : local.ToString("yyyy/MM/dd", CultureInfo.CurrentCulture);
        }

        public static string Detail(LocalTokenUsage tokens, bool supported = true)
        {
            if (!supported)
            {
                return "此資料來源未提供可驗證的 Token 計數。";
            }
            if (tokens == null)
            {
                return "本機最近工作階段暫無完整 Token 紀錄。";
            }
            return string.Format(CultureInfo.CurrentCulture,
                "本機最近工作階段：輸入（含快取）{0:N0}、輸出 {1:N0}，合計 {2:N0} Token；紀錄於 {3:yyyy/MM/dd HH:mm}。這是單一工作階段累積值，不是今日或本週消耗；Token 紀錄時間與方案額度更新時間分開計算。不含其他工作階段與裝置使用量，也不代表帳號額度。",
                tokens.InputTokens, tokens.OutputTokens, tokens.TotalTokens, tokens.UpdatedAt.ToLocalTime());
        }
    }

    internal sealed class AppConfig
    {
        public AppConfig()
        {
            RefreshSeconds = 60;
            WarningPercent = 80;
            CriticalPercent = 95;
            NotificationsEnabled = true;
            CodexExecutable = string.Empty;
        }

        public int RefreshSeconds { get; set; }
        public int WarningPercent { get; set; }
        public int CriticalPercent { get; set; }
        public bool NotificationsEnabled { get; set; }
        public string CodexExecutable { get; set; }

        public static AppConfig Load(string path)
        {
            var config = new AppConfig();
            try
            {
                if (File.Exists(path))
                {
                    var parsed = Json.DeserializeObject(File.ReadAllText(path)) as IDictionary<string, object>;
                    if (parsed != null)
                    {
                        config.RefreshSeconds = Json.Int(parsed, "refreshSeconds", config.RefreshSeconds);
                        config.WarningPercent = Json.Int(parsed, "warningPercent", config.WarningPercent);
                        config.CriticalPercent = Json.Int(parsed, "criticalPercent", config.CriticalPercent);
                        config.NotificationsEnabled = Json.Bool(parsed, "notificationsEnabled", config.NotificationsEnabled);
                        config.CodexExecutable = Json.String(parsed, "codexExecutable", config.CodexExecutable);
                    }
                }
            }
            catch
            {
                // Invalid optional settings should not prevent the tray app from starting.
            }

            config.RefreshSeconds = Math.Max(15, Math.Min(3600, config.RefreshSeconds));
            config.WarningPercent = Math.Max(1, Math.Min(99, config.WarningPercent));
            config.CriticalPercent = Math.Max(config.WarningPercent, Math.Min(100, config.CriticalPercent));
            return config;
        }
    }

    internal static class AppPaths
    {
        public static readonly string LocalData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexClaudeUsage");

        public static readonly string NotificationState = Path.Combine(LocalData, "notification-state.json");

        public static string ClaudeStatusCache
        {
            get
            {
                var overridePath = Environment.GetEnvironmentVariable("CODEX_CLAUDE_USAGE_BRIDGE_PATH");
                return string.IsNullOrWhiteSpace(overridePath)
                    ? Path.Combine(LocalData, "claude-status.json")
                    : Path.GetFullPath(overridePath);
            }
        }

        public static string AntigravityStatusCache
        {
            get
            {
                var overridePath = Environment.GetEnvironmentVariable("CODEX_CLAUDE_USAGE_ANTIGRAVITY_PATH");
                return string.IsNullOrWhiteSpace(overridePath)
                    ? Path.Combine(LocalData, "antigravity-status.json")
                    : Path.GetFullPath(overridePath);
            }
        }

        public static string ExecutableDirectory
        {
            get
            {
                var path = System.Reflection.Assembly.GetExecutingAssembly().Location;
                return Path.GetDirectoryName(path) ?? Environment.CurrentDirectory;
            }
        }

        public static string ConfigFile
        {
            get { return Path.Combine(ExecutableDirectory, "config.json"); }
        }
    }

    internal static class Json
    {
        private static readonly ThreadLocal<JavaScriptSerializer> Serializers =
            new ThreadLocal<JavaScriptSerializer>(delegate
            {
                return new JavaScriptSerializer
                {
                    MaxJsonLength = 16 * 1024 * 1024,
                    RecursionLimit = 128
                };
            });

        public static object DeserializeObject(string text)
        {
            return Serializers.Value.DeserializeObject(text);
        }

        public static string Serialize(object value)
        {
            return Serializers.Value.Serialize(value);
        }

        public static IDictionary<string, object> Object(object value)
        {
            return value as IDictionary<string, object>;
        }

        public static object[] Array(object value)
        {
            var array = value as object[];
            if (array != null)
            {
                return array;
            }

            var list = value as System.Collections.ArrayList;
            return list == null ? null : list.ToArray();
        }

        public static object Get(IDictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) ? value : null;
        }

        public static IDictionary<string, object> Map(IDictionary<string, object> map, string key)
        {
            return Object(Get(map, key));
        }

        public static string String(IDictionary<string, object> map, string key, string fallback)
        {
            var value = Get(map, key);
            return value == null ? fallback : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        public static int Int(IDictionary<string, object> map, string key, int fallback)
        {
            var value = Get(map, key);
            int result;
            return value != null && int.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out result)
                ? result
                : fallback;
        }

        public static long Long(IDictionary<string, object> map, string key, long fallback)
        {
            var value = Get(map, key);
            long result;
            return value != null && long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out result)
                ? result
                : fallback;
        }

        public static double Double(IDictionary<string, object> map, string key, double fallback)
        {
            var value = Get(map, key);
            double result;
            return value != null && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out result)
                ? result
                : fallback;
        }

        public static bool TryDouble(IDictionary<string, object> map, string key, out double result)
        {
            result = 0;
            var value = Get(map, key);
            return value != null
                && double.TryParse(
                    Convert.ToString(value, CultureInfo.InvariantCulture),
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out result)
                && !double.IsNaN(result)
                && !double.IsInfinity(result);
        }

        public static bool Bool(IDictionary<string, object> map, string key, bool fallback)
        {
            var value = Get(map, key);
            bool result;
            return value != null && bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out result)
                ? result
                : fallback;
        }
    }

    internal static class TimeUtil
    {
        private static readonly DateTimeOffset UnixEpoch = new DateTimeOffset(
            1970, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public static DateTimeOffset FromUnixSeconds(long seconds)
        {
            return UnixEpoch.AddSeconds(seconds);
        }

        public static DateTimeOffset FromUnixMilliseconds(long milliseconds)
        {
            return UnixEpoch.AddMilliseconds(milliseconds);
        }

        public static DateTimeOffset? ParseReset(object value)
        {
            if (value == null)
            {
                return null;
            }

            long numeric;
            var text = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out numeric))
            {
                if (numeric > 9999999999L)
                {
                    return FromUnixMilliseconds(numeric);
                }
                return FromUnixSeconds(numeric);
            }

            DateTimeOffset parsed;
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out parsed)
                ? parsed
                : (DateTimeOffset?)null;
        }

        /// <summary>
        /// 依 bucket 的重設資訊產生標籤：官方值沿用原格式；推算值加註「推估」；
        /// 無重設時間且無用量時顯示「目前沒有進行中的視窗」。
        /// </summary>
        public static string ResetLabel(UsageBucket bucket)
        {
            if (bucket == null)
            {
                return "重設時間未知";
            }
            if (!bucket.ResetsAt.HasValue)
            {
                return bucket.UsedPercent <= 0.05 ? "目前沒有進行中的視窗" : "重設時間未知";
            }
            if (bucket.ResetEstimate == ResetEstimateKind.UpperBound)
            {
                if (bucket.UsedPercent <= 0.05) return "尚未開始計時";
                if (bucket.ResetsAt.Value <= DateTimeOffset.Now)
                    return "推估時間已到 · 等待同步";
                return ResetClock(bucket.ResetsAt.Value) + " 前重設 · 推估";
            }
            var label = ResetLabel(bucket.ResetsAt);
            return bucket.ResetEstimate == ResetEstimateKind.Cycle ? label + " · 推估" : label;
        }

        public static string ResetLabel(DateTimeOffset? reset)
        {
            if (!reset.HasValue)
            {
                return "重設時間未知";
            }

            var local = reset.Value.ToLocalTime();
            var now = DateTimeOffset.Now;
            var delta = local - now;
            string relative;
            if (delta.TotalMinutes <= 0)
            {
                relative = "已到時間 · 等待同步";
            }
            else if (delta.TotalHours < 1)
            {
                relative = string.Format(CultureInfo.CurrentCulture, "約 {0} 分鐘後", Math.Max(1, (int)Math.Ceiling(delta.TotalMinutes)));
            }
            else if (delta.TotalDays < 1)
            {
                relative = string.Format(CultureInfo.CurrentCulture, "約 {0} 小時後", (int)Math.Ceiling(delta.TotalHours));
            }
            else
            {
                relative = string.Format(CultureInfo.CurrentCulture, "約 {0} 天後", (int)Math.Ceiling(delta.TotalDays));
            }

            return ResetClock(local) + " · " + relative;
        }

        public static string ResetDetail(UsageBucket bucket)
        {
            if (bucket == null || !bucket.ResetsAt.HasValue)
                return "來源未提供有效的重設時間，等待下一次資料同步；不代表額度已重設。";
            var time = bucket.ResetsAt.Value.ToLocalTime().ToString("yyyy/MM/dd ddd HH:mm", CultureInfo.CurrentCulture);
            if (bucket.ResetEstimate == ResetEstimateKind.UpperBound && bucket.UsedPercent <= 0.05)
                return "尚未開始計時；來源回報的推估上界為 " + time + "，不是已啟動的固定倒數。";
            if (bucket.ResetsAt.Value <= DateTimeOffset.Now)
                return "原" + (bucket.ResetEstimate == ResetEstimateKind.None ? "回報" : "推估") + "重設時間 " + time
                    + " 已到，等待來源同步確認；目前仍保留最後回報的用量。";
            if (bucket.ResetEstimate == ResetEstimateKind.UpperBound)
                return "最晚於 " + time + " 重設（推估上界，並非確定時間）。";
            return "重設於 " + time + (bucket.ResetEstimate == ResetEstimateKind.Cycle ? "（依週期推估）。" : "（來源回報）。");
        }

        private static string ResetClock(DateTimeOffset timestamp)
        {
            var local = timestamp.ToLocalTime();
            var format = local.Year == DateTimeOffset.Now.Year ? "MM/dd ddd HH:mm" : "yyyy/MM/dd ddd HH:mm";
            return local.ToString(format, CultureInfo.CurrentCulture);
        }

        public static string FreshnessLabel(DateTimeOffset timestamp)
        {
            if (timestamp == DateTimeOffset.MinValue)
            {
                return "時間未知";
            }

            var age = DateTimeOffset.Now - timestamp.ToLocalTime();
            if (age.TotalMinutes < 2)
            {
                return "剛剛";
            }
            if (age.TotalMinutes < 60)
            {
                return string.Format(CultureInfo.CurrentCulture, "{0} 分前", Math.Max(2, (int)Math.Floor(age.TotalMinutes)));
            }
            if (age.TotalHours < 24)
            {
                return string.Format(CultureInfo.CurrentCulture, "{0} 小時前", Math.Max(1, (int)Math.Floor(age.TotalHours)));
            }
            return string.Format(CultureInfo.CurrentCulture, "舊資料 {0:MM/dd HH:mm}", timestamp.ToLocalTime());
        }
    }

    /// <summary>桌面小工具狀態：是否顯示、是否保持最上層、記住的位置。</summary>
    internal sealed class WidgetState
    {
        public WidgetState()
        {
            Enabled = true;
            TopMost = true;
            Opacity = 0.96;
            X = int.MinValue;
            Y = int.MinValue;
        }

        public bool Enabled { get; set; }
        public bool TopMost { get; set; }
        public bool Pinned { get; set; }
        public double Opacity { get; set; } // 小工具靜止不透明度（0.5–1.0）。
        public int X { get; set; }
        public int Y { get; set; }

        public bool HasPosition
        {
            get { return X != int.MinValue && Y != int.MinValue; }
        }

        private static string StatePath
        {
            get { return Path.Combine(AppPaths.LocalData, "widget-state.json"); }
        }

        public static WidgetState Load()
        {
            var state = new WidgetState();
            try
            {
                if (File.Exists(StatePath))
                {
                    var parsed = Json.Object(Json.DeserializeObject(File.ReadAllText(StatePath)));
                    if (parsed != null)
                    {
                        state.Enabled = Json.Bool(parsed, "enabled", state.Enabled);
                        state.TopMost = Json.Bool(parsed, "topMost", state.TopMost);
                        state.Pinned = Json.Bool(parsed, "pinned", state.Pinned);
                        state.X = Json.Int(parsed, "x", state.X);
                        state.Y = Json.Int(parsed, "y", state.Y);
                        state.Opacity = Math.Max(0.5, Math.Min(1.0, Json.Double(parsed, "opacity", state.Opacity)));
                    }
                }
            }
            catch
            {
            }
            return state;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppPaths.LocalData);
                var temporary = StatePath + ".tmp";
                File.WriteAllText(temporary, Json.Serialize(new Dictionary<string, object>
                {
                    { "enabled", Enabled },
                    { "topMost", TopMost },
                    { "pinned", Pinned },
                    { "opacity", Opacity },
                    { "x", X },
                    { "y", Y }
                }));
                if (File.Exists(StatePath))
                {
                    File.Delete(StatePath);
                }
                File.Move(temporary, StatePath);
            }
            catch
            {
            }
        }
    }

    internal static class UserState
    {
        public static bool LoadNotifications(bool fallback)
        {
            try
            {
                if (!File.Exists(AppPaths.NotificationState))
                {
                    return fallback;
                }
                var state = Json.Object(Json.DeserializeObject(File.ReadAllText(AppPaths.NotificationState)));
                return Json.Bool(state, "notificationsEnabled", fallback);
            }
            catch
            {
                return fallback;
            }
        }

        public static void SaveNotifications(bool enabled)
        {
            Directory.CreateDirectory(AppPaths.LocalData);
            var temporary = AppPaths.NotificationState + ".tmp";
            File.WriteAllText(temporary, Json.Serialize(new Dictionary<string, object>
            {
                { "notificationsEnabled", enabled }
            }));
            if (File.Exists(AppPaths.NotificationState))
            {
                File.Delete(AppPaths.NotificationState);
            }
            File.Move(temporary, AppPaths.NotificationState);
        }
    }
}
