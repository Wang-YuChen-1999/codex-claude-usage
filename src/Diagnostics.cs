using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexClaudeUsage
{
    /// <summary>A compact, non-sensitive health entry for one usage provider.</summary>
    internal sealed class SourceHealth
    {
        public SourceHealth()
        {
            Provider = string.Empty;
            Source = UsageSourceKind.Unknown.ToString();
            State = "unknown";
            Status = string.Empty;
            Error = string.Empty;
            AgeSeconds = -1;
        }

        public string Provider { get; set; }
        public string Source { get; set; }
        public string State { get; set; }
        public long AgeSeconds { get; set; }
        public string Status { get; set; }
        public string Error { get; set; }
        public double? ModelTotalUsedPercent { get; set; }
        public DateTimeOffset? ModelTotalResetsAt { get; set; }
    }

    /// <summary>Builds support diagnostics without exporting account or machine secrets.</summary>
    internal static class UsageDiagnostics
    {
        private static readonly Regex EmailPattern = new Regex(@"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
        private static readonly Regex IpPattern = new Regex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled);
        private static readonly Regex WindowsPathPattern = new Regex(@"(?i)(?:\\\\[^\s""']+|\b[A-Z]:\\[^\r\n""']+)", RegexOptions.Compiled);
        private static readonly Regex UnixPathPattern = new Regex(@"(?i)(?<![A-Za-z0-9_])/(?:users|home|private|var|tmp|opt|mnt|etc|appdata)(?:/[^\s""']*)?", RegexOptions.Compiled);
        private static readonly Regex UrlPattern = new Regex(@"(?i)https?://[^\s""']+", RegexOptions.Compiled);
        private static readonly Regex SensitiveValuePattern = new Regex(
            @"(?i)\b(?:prompt|response|token|credential|password|secret|api[_ -]?key|authorization)\b\s*(?:[:=]|is)\s*[^,;\r\n]+",
            RegexOptions.Compiled);
        private static readonly Regex OpaqueValuePattern = new Regex(@"\b[A-Za-z0-9_\-]{28,}\b", RegexOptions.Compiled);

        public static List<SourceHealth> Evaluate(UsageSnapshot snapshot)
        {
            var result = new List<SourceHealth>();
            if (snapshot == null)
            {
                return result;
            }

            result.Add(EvaluateProvider(snapshot.Codex, "Codex"));
            result.Add(EvaluateProvider(snapshot.Claude, "Claude Code"));
            result.Add(EvaluateProvider(snapshot.Antigravity, "Antigravity"));
            return result;
        }

        public static string BuildRedactedJson(UsageSnapshot snapshot, UserPreferences preferences, AppConfig config)
        {
            var sources = new List<object>();
            foreach (var health in Evaluate(snapshot))
            {
                sources.Add(new Dictionary<string, object>
                {
                    { "provider", health.Provider },
                    { "source", health.Source },
                    { "state", health.State },
                    { "ageSeconds", health.AgeSeconds },
                    { "status", health.Status },
                    { "error", health.Error },
                    { "modelTotal", new Dictionary<string, object>
                        {
                            { "usedPercent", health.ModelTotalUsedPercent },
                            { "resetsAt", health.ModelTotalResetsAt.HasValue
                                ? health.ModelTotalResetsAt.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                                : null }
                        }
                    }
                });
            }

            var payload = new Dictionary<string, object>
            {
                { "schemaVersion", 1 },
                { "capturedAt", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture) },
                { "app", new Dictionary<string, object>
                    {
                        { "version", AppVersion() },
                        { "os", Sanitize(Environment.OSVersion.VersionString, 120) },
                        { "dpi", CurrentDpi() }
                    }
                },
                { "config", SafeConfig(config) },
                { "preferences", SafePreferences(preferences) },
                { "sources", sources }
            };
            return Json.Serialize(payload);
        }

        internal static string Sanitize(string value, int maximumLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var sanitized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            sanitized = SensitiveValuePattern.Replace(sanitized, "[redacted]");
            sanitized = UrlPattern.Replace(sanitized, "[redacted-url]");
            sanitized = WindowsPathPattern.Replace(sanitized, "[redacted-path]");
            sanitized = UnixPathPattern.Replace(sanitized, "[redacted-path]");
            sanitized = EmailPattern.Replace(sanitized, "[redacted-email]");
            sanitized = IpPattern.Replace(sanitized, "[redacted-ip]");
            sanitized = OpaqueValuePattern.Replace(sanitized, "[redacted]");
            sanitized = Regex.Replace(sanitized, @"\s+", " ").Trim();
            if (maximumLength > 0 && sanitized.Length > maximumLength)
            {
                return sanitized.Substring(0, maximumLength);
            }
            return sanitized;
        }

        private static SourceHealth EvaluateProvider(ProviderSnapshot provider, string fallbackName)
        {
            var health = new SourceHealth { Provider = fallbackName };
            if (provider == null)
            {
                health.State = "missing";
                health.Error = "No snapshot was collected.";
                return health;
            }

            health.Provider = Sanitize(provider.Name, 48);
            health.Source = provider.SourceKind.ToString();
            health.Status = Sanitize(provider.Status, 240);
            health.Error = Sanitize(provider.Error, 320);
            if (provider.UpdatedAt != DateTimeOffset.MinValue)
            {
                var age = DateTimeOffset.UtcNow - provider.UpdatedAt.ToUniversalTime();
                health.AgeSeconds = Math.Max(0, (long)Math.Floor(age.TotalSeconds));
            }

            if (!provider.IsAvailable)
            {
                health.State = "unavailable";
            }
            else if (health.AgeSeconds < 0)
            {
                health.State = "unknown-age";
            }
            else if (health.AgeSeconds > 60 * 60)
            {
                health.State = provider.SourceKind == UsageSourceKind.ClaudeDesktop
                    ? "waiting-sync"
                    : "stale";
            }
            else
            {
                health.State = "healthy";
            }

            var total = ModelTotalBucket(provider);
            if (total != null)
            {
                health.ModelTotalUsedPercent = ClampPercent(total.UsedPercent);
                health.ModelTotalResetsAt = total.ResetsAt;
            }
            return health;
        }

        private static UsageBucket ModelTotalBucket(ProviderSnapshot provider)
        {
            if (provider == null || provider.Buckets == null || provider.Buckets.Count == 0)
            {
                return null;
            }

            var named = provider.Buckets.FirstOrDefault(delegate(UsageBucket bucket)
            {
                var label = bucket == null ? string.Empty : bucket.Label ?? string.Empty;
                return label.IndexOf("所有模型", StringComparison.OrdinalIgnoreCase) >= 0
                    || label.IndexOf("model total", StringComparison.OrdinalIgnoreCase) >= 0
                    || string.Equals(label, "每週額度", StringComparison.OrdinalIgnoreCase);
            });
            return named ?? provider.Buckets
                .Where(item => item != null)
                .OrderByDescending(item => item.WindowDurationMinutes)
                .FirstOrDefault();
        }

        private static IDictionary<string, object> SafeConfig(AppConfig config)
        {
            return new Dictionary<string, object>
            {
                { "refreshSeconds", config == null ? 0 : config.RefreshSeconds },
                { "warningPercent", config == null ? 0 : config.WarningPercent },
                { "criticalPercent", config == null ? 0 : config.CriticalPercent },
                { "notificationsEnabled", config != null && config.NotificationsEnabled }
            };
        }

        private static IDictionary<string, object> SafePreferences(UserPreferences preferences)
        {
            return new Dictionary<string, object>
            {
                { "totalOnly", preferences != null && preferences.TotalOnly },
                { "notifyThresholds", preferences != null && preferences.NotifyThresholds },
                { "notifyExhaustion", preferences != null && preferences.NotifyExhaustion },
                { "notifyReset", preferences != null && preferences.NotifyReset },
                { "notifyAntigravity", preferences != null && preferences.NotifyAntigravity },
                { "globalHotkeysEnabled", preferences != null && preferences.GlobalHotkeysEnabled },
                { "historyRetentionDays", preferences == null ? 0 : preferences.HistoryRetentionDays }
            };
        }

        private static string AppVersion()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null ? "unknown" : version.ToString();
        }

        private static int CurrentDpi()
        {
            try
            {
                using (var bitmap = new Bitmap(1, 1))
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    return Math.Max(1, (int)Math.Round(graphics.DpiX, MidpointRounding.AwayFromZero));
                }
            }
            catch
            {
                return 96;
            }
        }

        private static double ClampPercent(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? 0
                : Math.Max(0, Math.Min(100, value));
        }
    }

    internal enum ClaudeBridgeState
    {
        Ready,
        WaitingForData,
        MissingSettings,
        MissingStatusLine,
        MissingBridge,
        CustomStatusLine,
        InvalidSettings,
        Error
    }

    internal sealed class ClaudeBridgeHealthResult
    {
        public ClaudeBridgeState State { get; set; }
        public string Message { get; set; }
        public bool CustomStatusLine { get; set; }
    }

    /// <summary>Safely verifies and repairs only this application's Claude Code status-line bridge.</summary>
    internal static class ClaudeBridgeHealth
    {
        private const string BridgeFileName = "claude_status_bridge.ps1";
        private const string BridgeScript = @"$ErrorActionPreference = 'SilentlyContinue'
$raw = [Console]::In.ReadToEnd()
$payload = $raw | ConvertFrom-Json
$rateLimits = $payload.rate_limits
if ($rateLimits) {
    $cache = if ($env:CODEX_CLAUDE_USAGE_CACHE_PATH) { [IO.Path]::GetFullPath($env:CODEX_CLAUDE_USAGE_CACHE_PATH) } else { Join-Path $env:LOCALAPPDATA 'CodexClaudeUsage\claude-status.json' }
    $directory = Split-Path -Parent $cache
    $temporary = $cache + '.tmp'
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $record = [ordered]@{ version = 1; capturedAt = [DateTimeOffset]::UtcNow.ToString('o'); source = 'claude-code-statusline'; rate_limits = $rateLimits }
    [IO.File]::WriteAllText($temporary, ($record | ConvertTo-Json -Depth 12 -Compress), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $cache -Force
}
$parts = [Collections.Generic.List[string]]::new()
if ($payload.model.display_name) { $parts.Add('Claude ' + $payload.model.display_name) }
if ($rateLimits.five_hour) { $parts.Add(('5h {0:0}%' -f $rateLimits.five_hour.used_percentage)) }
if ($rateLimits.seven_day) { $parts.Add(('7d {0:0}%' -f $rateLimits.seven_day.used_percentage)) }
if ($parts.Count -gt 0) { [Console]::Write(($parts -join ' | ')) }
";

        public static ClaudeBridgeHealthResult Check()
        {
            try
            {
                var settingsPath = SettingsPath();
                if (!File.Exists(settingsPath))
                {
                    return Result(ClaudeBridgeState.MissingSettings, "Claude Code settings were not found.", false);
                }

                var settings = ReadSettings(settingsPath);
                if (settings == null)
                {
                    return Result(ClaudeBridgeState.InvalidSettings, "Claude Code settings could not be read.", false);
                }

                object rawStatusLine;
                if (!settings.TryGetValue("statusLine", out rawStatusLine) || rawStatusLine == null)
                {
                    return Result(ClaudeBridgeState.MissingStatusLine, "Claude Code status line is not configured.", false);
                }

                if (!ReferencesManagedBridge(rawStatusLine))
                {
                    return Result(ClaudeBridgeState.CustomStatusLine, "A custom Claude Code status line is preserved.", true);
                }

                if (!File.Exists(BridgePath()))
                {
                    return Result(ClaudeBridgeState.MissingBridge, "Claude Code usage bridge is missing.", false);
                }
                return HasRecentBridgeData()
                    ? Result(ClaudeBridgeState.Ready, "Claude Code usage bridge is ready.", false)
                    : Result(ClaudeBridgeState.WaitingForData, "Claude Code usage bridge is configured and waiting for data.", false);
            }
            catch
            {
                return Result(ClaudeBridgeState.Error, "Claude Code bridge status could not be checked.", false);
            }
        }

        public static ClaudeBridgeHealthResult Repair()
        {
            try
            {
                var settingsPath = SettingsPath();
                IDictionary<string, object> settings;
                if (File.Exists(settingsPath))
                {
                    settings = ReadSettings(settingsPath);
                    if (settings == null)
                    {
                        return Result(ClaudeBridgeState.InvalidSettings, "Claude Code settings could not be read.", false);
                    }
                }
                else
                {
                    settings = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                }

                object existing;
                var hasStatusLine = settings.TryGetValue("statusLine", out existing) && existing != null;
                if (hasStatusLine && !ReferencesManagedBridge(existing))
                {
                    return Result(ClaudeBridgeState.CustomStatusLine, "A custom Claude Code status line was not changed.", true);
                }

                var bridgePath = BridgePath();
                Directory.CreateDirectory(Path.GetDirectoryName(bridgePath));
                File.WriteAllText(bridgePath, BridgeScript, new UTF8Encoding(false));

                var expectedCommand = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"" + bridgePath + "\"";
                var needsSettingsWrite = !hasStatusLine || !string.Equals(StatusCommand(existing), expectedCommand, StringComparison.Ordinal);
                if (needsSettingsWrite)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                    if (File.Exists(settingsPath))
                    {
                        BackupSettings(settingsPath);
                    }
                    settings["statusLine"] = new Dictionary<string, object>
                    {
                        { "type", "command" },
                        { "command", expectedCommand },
                        { "padding", 0 }
                    };
                    WriteSettings(settingsPath, settings);
                }
                return HasRecentBridgeData()
                    ? Result(ClaudeBridgeState.Ready, "Claude Code usage bridge is ready.", false)
                    : Result(ClaudeBridgeState.WaitingForData, "Claude Code usage bridge is configured and waiting for data.", false);
            }
            catch
            {
                return Result(ClaudeBridgeState.Error, "Claude Code bridge could not be repaired.", false);
            }
        }

        private static ClaudeBridgeHealthResult Result(ClaudeBridgeState state, string message, bool custom)
        {
            return new ClaudeBridgeHealthResult { State = state, Message = message, CustomStatusLine = custom };
        }

        private static bool HasRecentBridgeData()
        {
            try
            {
                if (!File.Exists(AppPaths.ClaudeStatusCache)) return false;
                var record = Json.Object(Json.DeserializeObject(File.ReadAllText(AppPaths.ClaudeStatusCache)));
                if (record == null || (Json.Map(record, "rate_limits") ?? Json.Map(record, "rateLimits")) == null)
                {
                    return false;
                }
                var captured = TimeUtil.ParseReset(Json.Get(record, "capturedAt"));
                if (!captured.HasValue) return false;
                var age = DateTimeOffset.UtcNow - captured.Value.ToUniversalTime();
                return age >= TimeSpan.FromMinutes(-5) && age <= TimeSpan.FromHours(24);
            }
            catch
            {
                return false;
            }
        }

        private static IDictionary<string, object> ReadSettings(string path)
        {
            var raw = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            }
            var parsed = Json.Object(Json.DeserializeObject(raw));
            return parsed == null ? null : new Dictionary<string, object>(parsed, StringComparer.OrdinalIgnoreCase);
        }

        private static void WriteSettings(string path, IDictionary<string, object> settings)
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, Json.Serialize(settings), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Delete(path);
            }
            File.Move(temporary, path);
        }

        private static void BackupSettings(string path)
        {
            var backup = path + ".backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture);
            var suffix = 0;
            while (File.Exists(backup))
            {
                suffix++;
                backup = path + ".backup-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture) + "-" + suffix;
            }
            File.Copy(path, backup, false);
        }

        private static bool ReferencesManagedBridge(object statusLine)
        {
            var command = StatusCommand(statusLine);
            return !string.IsNullOrWhiteSpace(command)
                && (command.IndexOf(BridgeFileName, StringComparison.OrdinalIgnoreCase) >= 0
                    || command.IndexOf(BridgePath(), StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string StatusCommand(object statusLine)
        {
            var map = Json.Object(statusLine);
            return map == null ? string.Empty : Json.String(map, "command", string.Empty);
        }

        private static string SettingsPath()
        {
            var overridePath = Environment.GetEnvironmentVariable("CODEX_CLAUDE_USAGE_SETTINGS_PATH");
            return string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json")
                : Path.GetFullPath(overridePath);
        }

        private static string BridgePath()
        {
            var overridePath = Environment.GetEnvironmentVariable("CODEX_CLAUDE_USAGE_BRIDGE_INSTALL_PATH");
            return string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "CodexClaudeUsage", BridgeFileName)
                : Path.GetFullPath(overridePath);
        }
    }

    internal sealed class LocalDataFile
    {
        public string Name { get; set; }
        public string Category { get; set; }
        public long Bytes { get; set; }
        public DateTimeOffset ModifiedAt { get; set; }
    }

    /// <summary>Manages only files created by this application under its local data directory.</summary>
    internal static class LocalDataManager
    {
        private static readonly HashSet<string> OperationalFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "codex-usage-history.json", "codex-reset-state.json", "claude-status.json",
            "notification-state.json", "error.log", "error.log.old", "cycle-history.json",
            "codex-cycle-history.json", "claude-cycle-history.json"
        };
        private static readonly Regex TimestampedHistoryName = new Regex(
            @"^(?:codex|claude|usage|cycle|reset)[-_].*(?:history|cycle|reset).*\.(?:json|log)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static List<LocalDataFile> ListOwnedFiles()
        {
            var result = new List<LocalDataFile>();
            var root = LocalDataPath();
            if (!Directory.Exists(root))
            {
                return result;
            }
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (!IsOwnedName(name))
                {
                    continue;
                }
                var info = new FileInfo(file);
                result.Add(new LocalDataFile
                {
                    Name = name,
                    Category = Category(name),
                    Bytes = info.Length,
                    ModifiedAt = info.LastWriteTimeUtc
                });
            }
            return result.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>Deletes operational data only; callers own confirmation and UI messaging.</summary>
        public static int ClearOperationalData()
        {
            var removed = 0;
            var root = LocalDataPath();
            if (!Directory.Exists(root))
            {
                return removed;
            }
            foreach (var name in OperationalFiles)
            {
                var candidate = Path.Combine(root, name);
                if (File.Exists(candidate))
                {
                    File.Delete(candidate);
                    removed++;
                }
            }
            return removed;
        }

        /// <summary>Removes old, timestamped app history files. Official Claude Desktop data is never enumerated.</summary>
        public static int TrimTimestampedHistory(int retentionDays)
        {
            var days = Math.Max(1, Math.Min(3650, retentionDays));
            var cutoff = DateTime.UtcNow.AddDays(-days);
            var removed = 0;
            var root = LocalDataPath();
            if (!Directory.Exists(root))
            {
                return removed;
            }
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(path);
                if (!TimestampedHistoryName.IsMatch(name) || new FileInfo(path).LastWriteTimeUtc >= cutoff)
                {
                    continue;
                }
                File.Delete(path);
                removed++;
            }
            return removed;
        }

        private static bool IsOwnedName(string name)
        {
            return OperationalFiles.Contains(name)
                || string.Equals(name, "widget-state.json", StringComparison.OrdinalIgnoreCase)
                || TimestampedHistoryName.IsMatch(name);
        }

        private static string Category(string name)
        {
            if (name.IndexOf("history", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "history";
            }
            if (name.IndexOf("reset", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("cycle", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "reset";
            }
            if (name.IndexOf("notification", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "notification";
            }
            if (name.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "error";
            }
            return "state";
        }

        private static string LocalDataPath()
        {
            var overridePath = Environment.GetEnvironmentVariable("CODEX_CLAUDE_USAGE_LOCAL_DATA_DIR");
            return string.IsNullOrWhiteSpace(overridePath)
                ? AppPaths.LocalData
                : Path.GetFullPath(overridePath);
        }
    }
}
