using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace CodexClaudeUsage
{
    internal sealed class CodexResetEvent
    {
        public CodexResetEvent()
        {
            BucketLabel = string.Empty;
        }

        public string BucketLabel { get; set; }
        public DateTimeOffset ResetAt { get; set; }
        public DateTimeOffset DetectedAt { get; set; }
        public double PreviousUsedPercent { get; set; }
        public double CurrentUsedPercent { get; set; }
        public DateTimeOffset? NextResetAt { get; set; }
    }

    internal sealed class CodexResetObservation
    {
        public string BucketId { get; set; }
        public string BucketLabel { get; set; }
        public int WindowDurationMinutes { get; set; }
        public DateTimeOffset ObservedAt { get; set; }
        public double UsedPercent { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
    }

    /// <summary>
    /// Detects Codex quota resets from fresh official snapshots. A reset requires both a real
    /// usage drop and evidence that the previous reset boundary passed or moved forward.
    /// </summary>
    internal sealed class CodexResetDetector
    {
        private const int MaximumStateBytes = 256 * 1024;
        private const int MaximumBuckets = 24;
        private static readonly TimeSpan FreshSnapshotAge = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan BoundaryTolerance = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan MinimumScheduleAdvanceThreshold = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan MaximumObservationGap = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan ObservationRetention = TimeSpan.FromDays(35);

        private readonly string _statePath;
        private readonly Dictionary<string, CodexResetObservation> _observations;
        private CodexResetEvent _latest;

        public CodexResetDetector()
        {
            _statePath = ResolveStatePath();
            _observations = new Dictionary<string, CodexResetObservation>(StringComparer.OrdinalIgnoreCase);
            Load();
        }

        public CodexResetEvent Latest
        {
            get { return _latest; }
        }

        public List<CodexResetEvent> Observe(ProviderSnapshot provider)
        {
            var events = new List<CodexResetEvent>();
            if (!IsReliableOfficialSnapshot(provider))
            {
                return events;
            }

            var now = DateTimeOffset.UtcNow;
            var observedAt = provider.UpdatedAt.ToUniversalTime();
            if (observedAt < now - FreshSnapshotAge || observedAt > now + FutureTolerance)
            {
                return events;
            }

            foreach (var bucket in provider.Buckets.Take(MaximumBuckets))
            {
                if (bucket == null
                    || string.IsNullOrWhiteSpace(bucket.Label)
                    || string.IsNullOrWhiteSpace(bucket.StableId)
                    || !bucket.ResetsAt.HasValue
                    || bucket.ResetEstimate != ResetEstimateKind.None
                    || double.IsNaN(bucket.UsedPercent)
                    || double.IsInfinity(bucket.UsedPercent)
                    || bucket.UsedPercent < 0
                    || bucket.UsedPercent > 100)
                {
                    continue;
                }

                var label = Limit(bucket.Label, 120);
                var bucketId = Limit(bucket.StableId, 180);
                var currentUsed = bucket.UsedPercent;
                var currentReset = bucket.ResetsAt.Value.ToUniversalTime();
                CodexResetObservation previous;
                if (_observations.TryGetValue(bucketId, out previous)
                    && observedAt > previous.ObservedAt
                    && IsReset(
                        previous,
                        observedAt,
                        currentUsed,
                        currentReset,
                        bucket.WindowDurationMinutes))
                {
                    var detected = new CodexResetEvent
                    {
                        BucketLabel = label,
                        ResetAt = previous.ResetsAt.HasValue
                            ? previous.ResetsAt.Value.ToUniversalTime()
                            : observedAt,
                        DetectedAt = now,
                        PreviousUsedPercent = previous.UsedPercent,
                        CurrentUsedPercent = currentUsed,
                        NextResetAt = currentReset
                    };
                    events.Add(detected);
                    _latest = detected;
                }

                if (previous == null || observedAt >= previous.ObservedAt)
                {
                    _observations[bucketId] = new CodexResetObservation
                    {
                        BucketId = bucketId,
                        BucketLabel = label,
                        WindowDurationMinutes = bucket.WindowDurationMinutes,
                        ObservedAt = observedAt,
                        UsedPercent = currentUsed,
                        ResetsAt = currentReset
                    };
                }
            }

            RemoveStaleObservations(now);
            Save();
            return events;
        }

        private static bool IsReliableOfficialSnapshot(ProviderSnapshot provider)
        {
            return provider != null
                && provider.IsAvailable
                && provider.SourceKind == UsageSourceKind.CodexAppServer
                && provider.UpdatedAt != DateTimeOffset.MinValue
                && provider.Buckets.Count > 0;
        }

        private static bool IsReset(
            CodexResetObservation previous,
            DateTimeOffset observedAt,
            double currentUsed,
            DateTimeOffset currentReset,
            int currentWindowDurationMinutes)
        {
            if (previous == null
                || observedAt - previous.ObservedAt > MaximumObservationGap
                || previous.UsedPercent < 1
                || previous.UsedPercent - currentUsed < 0.5)
            {
                return false;
            }

            if (!previous.ResetsAt.HasValue || currentReset < observedAt - BoundaryTolerance)
            {
                return false;
            }

            var previousReset = previous.ResetsAt.Value.ToUniversalTime();
            var previousBoundaryPassed = observedAt >= previousReset - BoundaryTolerance;
            var durationMinutes = currentWindowDurationMinutes > 0
                ? currentWindowDurationMinutes
                : previous.WindowDurationMinutes;
            var minimumAdvance = durationMinutes > 0
                ? TimeSpan.FromMinutes(Math.Max(
                    MinimumScheduleAdvanceThreshold.TotalMinutes,
                    durationMinutes * 0.5))
                : MinimumScheduleAdvanceThreshold;
            var scheduleAdvanced = currentReset - previousReset >= minimumAdvance;
            var plausibleNextBoundary = durationMinutes <= 0
                || currentReset <= observedAt.AddMinutes(durationMinutes).Add(BoundaryTolerance);
            return previousBoundaryPassed && scheduleAdvanced && plausibleNextBoundary;
        }

        private void RemoveStaleObservations(DateTimeOffset now)
        {
            var stale = _observations
                .Where(item => now - item.Value.ObservedAt > ObservationRetention)
                .Select(item => item.Key)
                .ToArray();
            foreach (var key in stale)
            {
                _observations.Remove(key);
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_statePath) || new FileInfo(_statePath).Length > MaximumStateBytes)
                {
                    return;
                }

                var root = Json.Object(Json.DeserializeObject(File.ReadAllText(_statePath)));
                if (root == null)
                {
                    return;
                }

                var version = Json.Int(root, "version", 0);
                if (version != 1 && version != 2)
                {
                    return;
                }

                var observations = Json.Array(Json.Get(root, "observations"));
                if (observations != null)
                {
                    foreach (var item in observations.Take(MaximumBuckets))
                    {
                        var observation = ParseObservation(Json.Object(item));
                        if (observation != null)
                        {
                            _observations[observation.BucketId] = observation;
                        }
                    }
                }
                _latest = ParseEvent(Json.Map(root, "latestEvent"));
            }
            catch
            {
                _observations.Clear();
                _latest = null;
            }
        }

        private void Save()
        {
            string temporary = null;
            try
            {
                var directory = Path.GetDirectoryName(_statePath);
                if (string.IsNullOrEmpty(directory))
                {
                    return;
                }
                Directory.CreateDirectory(directory);

                var observations = new List<object>();
                foreach (var item in _observations.Values
                    .OrderByDescending(value => value.ObservedAt)
                    .Take(MaximumBuckets))
                {
                    observations.Add(ObservationDictionary(item));
                }

                var root = new Dictionary<string, object>
                {
                    { "version", 2 },
                    { "observations", observations },
                    { "latestEvent", _latest == null ? null : EventDictionary(_latest) }
                };
                temporary = _statePath + "." + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".tmp";
                File.WriteAllText(temporary, Json.Serialize(root), new UTF8Encoding(false));
                if (File.Exists(_statePath))
                {
                    try
                    {
                        File.Replace(temporary, _statePath, null);
                    }
                    catch
                    {
                        File.Copy(temporary, _statePath, true);
                        File.Delete(temporary);
                    }
                }
                else
                {
                    File.Move(temporary, _statePath);
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

        private static CodexResetObservation ParseObservation(IDictionary<string, object> map)
        {
            if (map == null)
            {
                return null;
            }
            var bucketId = Limit(Json.String(map, "bucketId", string.Empty), 180);
            var label = Limit(Json.String(map, "bucketLabel", string.Empty), 120);
            var observedAt = TimeUtil.ParseReset(Json.Get(map, "observedAt"));
            var resetsAt = TimeUtil.ParseReset(Json.Get(map, "resetsAt"));
            if (string.IsNullOrEmpty(bucketId))
            {
                bucketId = label;
            }
            if (string.IsNullOrEmpty(bucketId)
                || string.IsNullOrEmpty(label)
                || !observedAt.HasValue
                || !resetsAt.HasValue)
            {
                return null;
            }
            return new CodexResetObservation
            {
                BucketId = bucketId,
                BucketLabel = label,
                WindowDurationMinutes = Math.Max(0, Json.Int(map, "windowDurationMinutes", 0)),
                ObservedAt = observedAt.Value.ToUniversalTime(),
                UsedPercent = Clamp(Json.Double(map, "usedPercent", 0)),
                ResetsAt = resetsAt.Value.ToUniversalTime()
            };
        }

        private static CodexResetEvent ParseEvent(IDictionary<string, object> map)
        {
            if (map == null)
            {
                return null;
            }
            var label = Limit(Json.String(map, "bucketLabel", string.Empty), 120);
            var resetAt = TimeUtil.ParseReset(Json.Get(map, "resetAt"));
            var detectedAt = TimeUtil.ParseReset(Json.Get(map, "detectedAt"));
            if (string.IsNullOrEmpty(label) || !resetAt.HasValue || !detectedAt.HasValue)
            {
                return null;
            }
            return new CodexResetEvent
            {
                BucketLabel = label,
                ResetAt = resetAt.Value.ToUniversalTime(),
                DetectedAt = detectedAt.Value.ToUniversalTime(),
                PreviousUsedPercent = Clamp(Json.Double(map, "previousUsedPercent", 0)),
                CurrentUsedPercent = Clamp(Json.Double(map, "currentUsedPercent", 0)),
                NextResetAt = TimeUtil.ParseReset(Json.Get(map, "nextResetAt"))
            };
        }

        private static IDictionary<string, object> ObservationDictionary(CodexResetObservation observation)
        {
            return new Dictionary<string, object>
            {
                { "bucketId", observation.BucketId },
                { "bucketLabel", observation.BucketLabel },
                { "windowDurationMinutes", observation.WindowDurationMinutes },
                { "observedAt", observation.ObservedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) },
                { "usedPercent", observation.UsedPercent },
                { "resetsAt", observation.ResetsAt.HasValue
                    ? observation.ResetsAt.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                    : null }
            };
        }

        private static IDictionary<string, object> EventDictionary(CodexResetEvent detected)
        {
            return new Dictionary<string, object>
            {
                { "bucketLabel", detected.BucketLabel },
                { "resetAt", detected.ResetAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) },
                { "detectedAt", detected.DetectedAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture) },
                { "previousUsedPercent", detected.PreviousUsedPercent },
                { "currentUsedPercent", detected.CurrentUsedPercent },
                { "nextResetAt", detected.NextResetAt.HasValue
                    ? detected.NextResetAt.Value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)
                    : null }
            };
        }

        private static string ResolveStatePath()
        {
            var overridePath = Environment.GetEnvironmentVariable("CODEX_CLAUDE_USAGE_CODEX_RESET_STATE_PATH");
            return string.IsNullOrWhiteSpace(overridePath)
                ? Path.Combine(AppPaths.LocalData, "codex-reset-state.json")
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(overridePath));
        }

        private static string Limit(string value, int length)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }
            value = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return value.Length <= length ? value : value.Substring(0, length);
        }

        private static double Clamp(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? 0
                : Math.Max(0, Math.Min(100, value));
        }
    }
}
