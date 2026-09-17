using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CodexClaudeUsage
{
    internal enum UsageNotificationKind
    {
        Threshold,
        Exhaustion,
        Reset
    }

    internal sealed class UserPreferences
    {
        private const int DefaultHistoryRetentionDays = 30;
        private int _historyRetentionDays;
        private int _quietStartMinutes;
        private int _quietEndMinutes;

        public UserPreferences()
        {
            TotalOnly = true;
            NotifyThresholds = true;
            NotifyExhaustion = true;
            NotifyReset = true;
            NotifyCodex = true;
            NotifyClaude = true;
            NotifyAntigravity = true;
            QuietHoursEnabled = false;
            QuietStartMinutes = 1320;
            QuietEndMinutes = 480;
            HistoryRetentionDays = DefaultHistoryRetentionDays;
            GlobalHotkeysEnabled = true;
        }

        public bool TotalOnly { get; set; }
        public bool NotifyThresholds { get; set; }
        public bool NotifyExhaustion { get; set; }
        public bool NotifyReset { get; set; }
        public bool NotifyCodex { get; set; }
        public bool NotifyClaude { get; set; }
        public bool NotifyAntigravity { get; set; }
        public bool QuietHoursEnabled { get; set; }

        public int QuietStartMinutes
        {
            get { return _quietStartMinutes; }
            set { _quietStartMinutes = NormalizeMinute(value); }
        }

        public int QuietEndMinutes
        {
            get { return _quietEndMinutes; }
            set { _quietEndMinutes = NormalizeMinute(value); }
        }

        public int HistoryRetentionDays
        {
            get { return _historyRetentionDays; }
            set { _historyRetentionDays = IsSupportedRetention(value) ? value : DefaultHistoryRetentionDays; }
        }

        public bool GlobalHotkeysEnabled { get; set; }

        public static UserPreferences Load()
        {
            var preferences = new UserPreferences();
            try
            {
                var path = PreferencesPath;
                if (!File.Exists(path))
                {
                    return preferences;
                }

                var map = Json.Object(Json.DeserializeObject(File.ReadAllText(path)));
                if (map == null)
                {
                    return preferences;
                }

                preferences.TotalOnly = Json.Bool(map, "totalOnly", preferences.TotalOnly);
                preferences.NotifyThresholds = Json.Bool(map, "notifyThresholds", preferences.NotifyThresholds);
                preferences.NotifyExhaustion = Json.Bool(map, "notifyExhaustion", preferences.NotifyExhaustion);
                preferences.NotifyReset = Json.Bool(map, "notifyReset", preferences.NotifyReset);
                preferences.NotifyCodex = Json.Bool(map, "notifyCodex", preferences.NotifyCodex);
                preferences.NotifyClaude = Json.Bool(map, "notifyClaude", preferences.NotifyClaude);
                preferences.NotifyAntigravity = Json.Bool(map, "notifyAntigravity", preferences.NotifyAntigravity);
                preferences.QuietHoursEnabled = Json.Bool(map, "quietHoursEnabled", preferences.QuietHoursEnabled);
                preferences.QuietStartMinutes = Json.Int(map, "quietStartMinutes", preferences.QuietStartMinutes);
                preferences.QuietEndMinutes = Json.Int(map, "quietEndMinutes", preferences.QuietEndMinutes);
                preferences.HistoryRetentionDays = Json.Int(map, "historyRetentionDays", preferences.HistoryRetentionDays);
                preferences.GlobalHotkeysEnabled = Json.Bool(map, "globalHotkeysEnabled", preferences.GlobalHotkeysEnabled);
            }
            catch
            {
                // Preferences are optional; malformed files must not stop the application.
                return new UserPreferences();
            }

            return preferences;
        }

        public void Save()
        {
            var path = PreferencesPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var values = new Dictionary<string, object>
            {
                { "totalOnly", TotalOnly },
                { "notifyThresholds", NotifyThresholds },
                { "notifyExhaustion", NotifyExhaustion },
                { "notifyReset", NotifyReset },
                { "notifyCodex", NotifyCodex },
                { "notifyClaude", NotifyClaude },
                { "notifyAntigravity", NotifyAntigravity },
                { "quietHoursEnabled", QuietHoursEnabled },
                { "quietStartMinutes", QuietStartMinutes },
                { "quietEndMinutes", QuietEndMinutes },
                { "historyRetentionDays", HistoryRetentionDays },
                { "globalHotkeysEnabled", GlobalHotkeysEnabled }
            };

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, Json.Serialize(values), new UTF8Encoding(false));
            if (File.Exists(path))
            {
                File.Replace(temporary, path, null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }

        public bool IsQuietTime(DateTimeOffset now)
        {
            if (!QuietHoursEnabled || QuietStartMinutes == QuietEndMinutes)
            {
                return false;
            }

            var currentMinute = (now.Hour * 60) + now.Minute;
            if (QuietStartMinutes < QuietEndMinutes)
            {
                return currentMinute >= QuietStartMinutes && currentMinute < QuietEndMinutes;
            }

            return currentMinute >= QuietStartMinutes || currentMinute < QuietEndMinutes;
        }

        public bool Allows(UsageNotificationKind kind, string providerName, DateTimeOffset now, bool globalEnabled)
        {
            if (!globalEnabled || IsQuietTime(now) || !AllowsKind(kind))
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(providerName))
            {
                return false;
            }

            if (providerName.IndexOf("codex", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return NotifyCodex;
            }

            if (providerName.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return NotifyClaude;
            }

            if (providerName.IndexOf("antigravity", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return NotifyAntigravity;
            }

            return false;
        }

        private static string PreferencesPath
        {
            get
            {
                var overridePath = Environment.GetEnvironmentVariable("CODEX_CLAUDE_USAGE_PREFERENCES_PATH");
                return string.IsNullOrWhiteSpace(overridePath)
                    ? Path.Combine(AppPaths.LocalData, "preferences.json")
                    : Path.GetFullPath(overridePath);
            }
        }

        private bool AllowsKind(UsageNotificationKind kind)
        {
            switch (kind)
            {
                case UsageNotificationKind.Threshold:
                    return NotifyThresholds;
                case UsageNotificationKind.Exhaustion:
                    return NotifyExhaustion;
                case UsageNotificationKind.Reset:
                    return NotifyReset;
                default:
                    return false;
            }
        }

        private static int NormalizeMinute(int value)
        {
            return Math.Max(0, Math.Min(1439, value));
        }

        private static bool IsSupportedRetention(int value)
        {
            return value == 1 || value == 7 || value == 14 || value == 30;
        }
    }
}
