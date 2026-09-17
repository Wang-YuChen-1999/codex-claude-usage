using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexClaudeUsage
{
    /// <summary>A privacy-preserving record for one provider quota cycle.</summary>
    internal sealed class CycleHistoryRecord
    {
        public CycleHistoryRecord()
        {
            Provider = string.Empty;
        }

        public string Provider { get; set; }
        public DateTimeOffset CycleStartAt { get; set; }
        public DateTimeOffset? CycleEndAt { get; set; }
        public DateTimeOffset? ResetAt { get; set; }
        public DateTimeOffset ObservedAt { get; set; }
        public double PeakUsedPercent { get; set; }
        public double LastUsedPercent { get; set; }
        public bool IsCompleted { get; set; }
    }

    /// <summary>
    /// Stores weekly model-total quota cycles. It intentionally persists no prompts, account
    /// data, device identifiers, bucket labels, or raw provider status messages.
    /// </summary>
    internal sealed class CycleHistoryStore
    {
        private const int MaximumStateBytes = 512 * 1024;
        private const int MaximumRecords = 256;
        private static readonly TimeSpan FreshnessLimit = TimeSpan.FromMinutes(60);
        private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan ResetBoundaryTolerance = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan ResetAdvanceTolerance = TimeSpan.FromMinutes(2);
        private const int DefaultRetentionDays = 30;

        private readonly string _path;
        private readonly List<CycleHistoryRecord> _records;
        private int _retentionDays = DefaultRetentionDays;

        public CycleHistoryStore()
            : this(ResolvePath())
        {
        }

        public CycleHistoryStore(string path)
        {
            _path = string.IsNullOrWhiteSpace(path) ? ResolvePath() : Path.GetFullPath(path);
            _records = new List<CycleHistoryRecord>();
            Load();
        }

        public string StoragePath
        {
            get { return _path; }
        }

        public List<CycleHistoryRecord> Records
        {
            get { return _records; }
        }

        public List<CycleHistoryRecord> Load()
        {
            _records.Clear();
            try
            {
                if (!File.Exists(_path) || new FileInfo(_path).Length > MaximumStateBytes)
                {
                    return _records;
                }
                var root = Json.Object(Json.DeserializeObject(File.ReadAllText(_path)));
                if (root == null || Json.Int(root, "version", 0) != 1)
                {
                    return _records;
                }
                var items = Json.Array(Json.Get(root, "cycles"));
                if (items != null)
                {
                    foreach (var item in items.Take(MaximumRecords))
                    {
                        var parsed = Parse(Json.Object(item));
                        if (parsed != null)
                        {
                            _records.Add(parsed);
                        }
                    }
                }
                Prune(DateTimeOffset.UtcNow, _retentionDays);
            }
            catch
            {
                _records.Clear();
            }
            return _records;
        }

        public void Observe(UsageSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }
            Observe(snapshot.Codex);
            Observe(snapshot.Claude);
        }

        public void Observe(ProviderSnapshot provider)
        {
            Observe(provider, DateTimeOffset.UtcNow);
        }

        public void Observe(ProviderSnapshot provider, DateTimeOffset now)
        {
            if (provider == null || !provider.IsAvailable || !IsFresh(provider.UpdatedAt, now))
            {
                return;
            }
            var bucket = UsageSelection.SummaryBucket(provider);
            if (bucket == null)
            {
                return;
            }

            var observedAt = provider.UpdatedAt.ToUniversalTime();
            var used = Clamp(bucket.UsedPercent);
            var resetAt = bucket.ResetsAt.HasValue ? bucket.ResetsAt.Value.ToUniversalTime() : (DateTimeOffset?)null;
            var current = _records
                .Where(record => !record.IsCompleted && string.Equals(record.Provider, provider.Name, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(record => record.ObservedAt)
                .FirstOrDefault();

            if (current == null)
            {
                _records.Add(NewCurrent(provider.Name, observedAt, used, resetAt, bucket));
            }
            else if (observedAt >= current.ObservedAt)
            {
                if (IsReset(current, used, observedAt, resetAt))
                {
                    current.IsCompleted = true;
                    current.CycleEndAt = observedAt;
                    current.ResetAt = current.ResetAt ?? observedAt;
                    _records.Add(NewCurrent(provider.Name, observedAt, used, resetAt, bucket));
                }
                else
                {
                    current.LastUsedPercent = used;
                    current.PeakUsedPercent = Math.Max(current.PeakUsedPercent, used);
                    current.ObservedAt = observedAt;
                    if (resetAt.HasValue)
                    {
                        current.ResetAt = resetAt;
                    }
                }
            }

            Prune(now.ToUniversalTime(), _retentionDays);
            Save();
        }

        public void ApplyRetention(int days)
        {
            _retentionDays = SupportedRetentionDays(days);
            Prune(DateTimeOffset.UtcNow, _retentionDays);
            Save();
        }

        public List<CycleHistoryRecord> GetCycles(int days)
        {
            return GetCycles(days, DateTimeOffset.UtcNow);
        }

        public List<CycleHistoryRecord> GetCycles(int days, DateTimeOffset now)
        {
            var supportedDays = SupportedRetentionDays(days);
            var threshold = now.ToUniversalTime().AddDays(-supportedDays);
            return _records
                .Where(record => !record.IsCompleted || RecordEnd(record) >= threshold)
                .OrderByDescending(record => record.ObservedAt)
                .ToList();
        }

        public void Clear()
        {
            _records.Clear();
            Save();
        }

        public string ExportJson()
        {
            var exported = new List<object>();
            foreach (var record in _records.OrderBy(record => record.Provider).ThenBy(record => record.CycleStartAt))
            {
                exported.Add(ExportDictionary(record));
            }
            return Json.Serialize(exported);
        }

        public string ExportCsv()
        {
            var output = new StringBuilder();
            output.AppendLine("provider,cycleStartAt,cycleEndAt,observedAt,resetAt,peakUsedPercent,lastUsedPercent");
            foreach (var record in _records.OrderBy(record => record.Provider).ThenBy(record => record.CycleStartAt))
            {
                output.Append(Csv(record.Provider)).Append(',')
                    .Append(Csv(Format(record.CycleStartAt))).Append(',')
                    .Append(Csv(Format(record.CycleEndAt))).Append(',')
                    .Append(Csv(Format(record.ObservedAt))).Append(',')
                    .Append(Csv(Format(record.ResetAt))).Append(',')
                    .Append(record.PeakUsedPercent.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                    .Append(record.LastUsedPercent.ToString("0.##", CultureInfo.InvariantCulture)).AppendLine();
            }
            return output.ToString();
        }

        public void ExportJson(string path)
        {
            WriteExport(path, ExportJson());
        }

        public void ExportCsv(string path)
        {
            WriteExport(path, ExportCsv());
        }

        private static CycleHistoryRecord NewCurrent(
            string provider,
            DateTimeOffset observedAt,
            double used,
            DateTimeOffset? resetAt,
            UsageBucket bucket)
        {
            var duration = bucket.WindowDurationMinutes > 0
                ? TimeSpan.FromMinutes(bucket.WindowDurationMinutes)
                : TimeSpan.FromDays(7);
            return new CycleHistoryRecord
            {
                Provider = Limit(provider, 80),
                CycleStartAt = resetAt.HasValue ? resetAt.Value - duration : observedAt,
                ResetAt = resetAt,
                ObservedAt = observedAt,
                PeakUsedPercent = used,
                LastUsedPercent = used,
                IsCompleted = false
            };
        }

        private static bool IsReset(CycleHistoryRecord previous, double currentUsed, DateTimeOffset observedAt, DateTimeOffset? currentResetAt)
        {
            if (previous.LastUsedPercent - currentUsed <= 2)
            {
                return false;
            }
            var boundaryPassed = previous.ResetAt.HasValue
                && observedAt >= previous.ResetAt.Value - ResetBoundaryTolerance;
            var boundaryAdvanced = previous.ResetAt.HasValue
                && currentResetAt.HasValue
                && currentResetAt.Value > previous.ResetAt.Value + ResetAdvanceTolerance;
            return boundaryPassed || boundaryAdvanced;
        }

        private void Prune(DateTimeOffset now, int retentionDays)
        {
            var threshold = now.AddDays(-SupportedRetentionDays(retentionDays));
            _records.RemoveAll(record => record.IsCompleted && RecordEnd(record) < threshold);
            if (_records.Count > MaximumRecords)
            {
                var current = _records.Where(record => !record.IsCompleted).ToList();
                var completed = _records.Where(record => record.IsCompleted)
                    .OrderByDescending(record => RecordEnd(record))
                    .Take(Math.Max(0, MaximumRecords - current.Count));
                _records.Clear();
                _records.AddRange(current);
                _records.AddRange(completed);
            }
        }

        private static int SupportedRetentionDays(int days)
        {
            return days <= 1 ? 1 : (days <= 7 ? 7 : (days <= 14 ? 14 : 30));
        }

        private void Save()
        {
            string temporary = null;
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (string.IsNullOrEmpty(directory))
                {
                    return;
                }
                Directory.CreateDirectory(directory);
                var cycles = _records.OrderBy(record => record.Provider).ThenBy(record => record.CycleStartAt)
                    .Select(record => (object)StoreDictionary(record)).ToList();
                var root = new Dictionary<string, object>
                {
                    { "version", 1 },
                    { "cycles", cycles }
                };
                temporary = _path + "." + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".tmp";
                File.WriteAllText(temporary, Json.Serialize(root), new UTF8Encoding(false));
                if (File.Exists(_path))
                {
                    try
                    {
                        File.Replace(temporary, _path, null);
                    }
                    catch
                    {
                        File.Copy(temporary, _path, true);
                        File.Delete(temporary);
                    }
                }
                else
                {
                    File.Move(temporary, _path);
                }
            }
            catch
            {
            }
            finally
            {
                if (!string.IsNullOrEmpty(temporary) && File.Exists(temporary))
                {
                    try { File.Delete(temporary); } catch { }
                }
            }
        }

        private static CycleHistoryRecord Parse(IDictionary<string, object> map)
        {
            if (map == null)
            {
                return null;
            }
            var provider = Limit(Json.String(map, "provider", string.Empty), 80);
            var cycleStart = TimeUtil.ParseReset(Json.Get(map, "cycleStartAt"));
            var observed = TimeUtil.ParseReset(Json.Get(map, "observedAt"));
            if (string.IsNullOrEmpty(provider) || !cycleStart.HasValue || !observed.HasValue)
            {
                return null;
            }
            return new CycleHistoryRecord
            {
                Provider = provider,
                CycleStartAt = cycleStart.Value.ToUniversalTime(),
                CycleEndAt = TimeUtil.ParseReset(Json.Get(map, "cycleEndAt")),
                ResetAt = TimeUtil.ParseReset(Json.Get(map, "resetAt")),
                ObservedAt = observed.Value.ToUniversalTime(),
                PeakUsedPercent = Clamp(Json.Double(map, "peakUsedPercent", 0)),
                LastUsedPercent = Clamp(Json.Double(map, "lastUsedPercent", 0)),
                IsCompleted = Json.Bool(map, "isCompleted", false)
            };
        }

        private static IDictionary<string, object> StoreDictionary(CycleHistoryRecord record)
        {
            return new Dictionary<string, object>
            {
                { "provider", record.Provider },
                { "cycleStartAt", Format(record.CycleStartAt) },
                { "cycleEndAt", Format(record.CycleEndAt) },
                { "observedAt", Format(record.ObservedAt) },
                { "resetAt", Format(record.ResetAt) },
                { "peakUsedPercent", record.PeakUsedPercent },
                { "lastUsedPercent", record.LastUsedPercent },
                { "isCompleted", record.IsCompleted }
            };
        }

        private static IDictionary<string, object> ExportDictionary(CycleHistoryRecord record)
        {
            return new Dictionary<string, object>
            {
                { "provider", record.Provider },
                { "cycleStartAt", Format(record.CycleStartAt) },
                { "cycleEndAt", Format(record.CycleEndAt) },
                { "observedAt", Format(record.ObservedAt) },
                { "resetAt", Format(record.ResetAt) },
                { "peakUsedPercent", record.PeakUsedPercent },
                { "lastUsedPercent", record.LastUsedPercent }
            };
        }

        private static void WriteExport(string path, string content)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("An export path is required.", "path");
            }
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
        }

        private static string ResolvePath()
        {
            var overridePath = Environment.GetEnvironmentVariable("CODEX_CLAUDE_USAGE_CYCLE_HISTORY_PATH");
            return string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(AppPaths.LocalData, "cycle-history.json")
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(overridePath));
        }

        private static bool IsFresh(DateTimeOffset updatedAt, DateTimeOffset now)
        {
            if (updatedAt == DateTimeOffset.MinValue)
            {
                return false;
            }
            var timestamp = updatedAt.ToUniversalTime();
            var utcNow = now.ToUniversalTime();
            return timestamp >= utcNow - FreshnessLimit && timestamp <= utcNow + FutureTolerance;
        }

        private static DateTimeOffset RecordEnd(CycleHistoryRecord record)
        {
            return record.CycleEndAt ?? record.ObservedAt;
        }

        private static string Format(DateTimeOffset value)
        {
            return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        private static string Format(DateTimeOffset? value)
        {
            return value.HasValue ? Format(value.Value) : null;
        }

        private static string Csv(string value)
        {
            value = value ?? string.Empty;
            return '"' + value.Replace("\"", "\"\"") + '"';
        }

        private static string Limit(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        private static double Clamp(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? 0
                : Math.Max(0, Math.Min(100, value));
        }
    }
}
