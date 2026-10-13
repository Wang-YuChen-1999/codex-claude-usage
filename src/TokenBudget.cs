using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexClaudeUsage
{
    internal sealed class TokenBudgetAlert
    {
        public int Level { get; set; }
        public decimal Used { get; set; }
        public long Budget { get; set; }
    }

    internal sealed class TokenBudgetMonitor
    {
        private readonly string _path;
        private string _date;
        private long _budget;
        private int _level;

        public TokenBudgetMonitor() : this(Path.Combine(AppPaths.LocalData, "token-budget-state.json")) { }

        public TokenBudgetMonitor(string path)
        {
            _path = path;
            try
            {
                if (File.Exists(path))
                {
                    var map = Json.Object(Json.DeserializeObject(File.ReadAllText(path)));
                    _date = Json.String(map, "date", string.Empty);
                    _budget = Json.Long(map, "budget", 0);
                    _level = Json.Int(map, "level", 0);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        public TokenBudgetAlert Evaluate(TokenAnalyticsSnapshot snapshot, UserPreferences preferences,
            DateTimeOffset now, bool notificationsEnabled)
        {
            if (snapshot == null || snapshot.IsIndexing || snapshot.Rows == null
                || preferences == null || !notificationsEnabled || !preferences.NotifyTokenBudget
                || preferences.TokenDailyBudget <= 0 || preferences.IsQuietTime(now)
                || snapshot.CapturedAt > now.AddMinutes(1) || snapshot.CapturedAt < now.AddMinutes(-5))
            {
                return null;
            }
            var today = now.LocalDateTime.Date;
            if (!snapshot.IsComplete && (!snapshot.CompleteFrom.HasValue || snapshot.CompleteFrom.Value.Date > today))
            {
                return null;
            }
            var used = snapshot.Rows.Where(r => r.Date.Date == today).Sum(r => (decimal)r.TotalTokens);
            var budget = preferences.TokenDailyBudget;
            var level = used >= budget ? 100 : used >= budget * 0.8m ? 80 : 0;
            var date = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (level == 0 || (_date == date && _budget == budget && _level >= level))
            {
                return null;
            }
            _date = date;
            _budget = budget;
            _level = level;
            Save();
            return new TokenBudgetAlert { Level = level, Used = used, Budget = budget };
        }

        private void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path)));
                var json = Json.Serialize(new Dictionary<string, object>
                {
                    { "date", _date }, { "budget", _budget }, { "level", _level }
                });
                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (File.Exists(_path)) File.Replace(temporary, _path, null);
                else File.Move(temporary, _path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
