using System;
using System.Collections.Generic;
using System.Linq;

namespace CodexClaudeUsage
{
    /// <summary>Chooses the model-wide weekly bucket used by summaries and insights.</summary>
    internal static class UsageSelection
    {
        /// <summary>
        /// 供應商的代表額度：優先取每週視窗，多個每週額度（Antigravity 的 Gemini 與 3P）
        /// 取已用比例最高者，讓托盤儀表與小工具永遠反映最吃緊的那一條限額。
        /// </summary>
        public static UsageBucket SummaryBucket(ProviderSnapshot provider)
        {
            if (provider == null || !provider.IsAvailable || provider.Buckets == null)
            {
                return null;
            }

            UsageBucket weekly = null;
            foreach (var bucket in provider.Buckets)
            {
                if (bucket == null
                    || string.IsNullOrWhiteSpace(bucket.Label)
                    || bucket.Label.IndexOf("每週", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }
                if (weekly == null || bucket.UsedPercent > weekly.UsedPercent)
                {
                    weekly = bucket;
                }
            }

            return weekly ?? provider.Buckets.FirstOrDefault(bucket => bucket != null);
        }

        public static UsageSnapshot ForDisplay(UsageSnapshot snapshot, bool totalOnly)
        {
            if (snapshot == null || !totalOnly)
            {
                return snapshot;
            }

            return new UsageSnapshot
            {
                UpdatedAt = snapshot.UpdatedAt,
                Codex = SummaryProvider(snapshot.Codex, "Codex"),
                Claude = SummaryProvider(snapshot.Claude, "Claude Code"),
                Antigravity = SummaryProvider(snapshot.Antigravity, "Antigravity")
            };
        }

        public static ProviderSnapshot SummaryProvider(ProviderSnapshot source, string fallbackName)
        {
            if (source == null)
            {
                return new ProviderSnapshot(fallbackName);
            }

            var copy = new ProviderSnapshot(string.IsNullOrWhiteSpace(source.Name) ? fallbackName : source.Name)
            {
                Status = source.Status,
                Error = source.Error,
                IsAvailable = source.IsAvailable,
                UpdatedAt = source.UpdatedAt,
                SourceKind = source.SourceKind
            };
            var summary = SummaryBucket(source);
            if (summary != null)
            {
                copy.Buckets.Add(summary);
            }
            return copy;
        }
    }

    internal sealed class ProviderInsight
    {
        public ProviderInsight()
        {
            Provider = string.Empty;
            Status = "無資料";
        }

        public string Provider { get; set; }
        public double UsedPercent { get; set; }
        public double RemainingPercent { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }
        public double ExpectedUsedPercent { get; set; }
        public double PaceDeltaPercent { get; set; }
        public double DailyAllowancePercent { get; set; }
        public bool IsFresh { get; set; }
        public string Status { get; set; }
    }

    internal sealed class ResetTimelineItem
    {
        public ResetTimelineItem()
        {
            Provider = string.Empty;
            Status = string.Empty;
        }

        public string Provider { get; set; }
        public DateTimeOffset ResetsAt { get; set; }
        public double RemainingPercent { get; set; }
        public bool IsFresh { get; set; }
        public string Status { get; set; }
    }

    internal sealed class UsageInsightReport
    {
        public UsageInsightReport()
        {
            Codex = new ProviderInsight { Provider = "Codex" };
            Claude = new ProviderInsight { Provider = "Claude Code" };
            Antigravity = new ProviderInsight { Provider = "Antigravity" };
            Recommendation = string.Empty;
            Timeline = new List<ResetTimelineItem>();
        }

        public ProviderInsight Codex { get; set; }
        public ProviderInsight Claude { get; set; }
        public ProviderInsight Antigravity { get; set; }
        public string Recommendation { get; set; }
        public List<ResetTimelineItem> Timeline { get; set; }
    }

    internal static class UsageInsights
    {
        private static readonly TimeSpan FreshnessLimit = TimeSpan.FromMinutes(60);
        private static readonly TimeSpan FutureTolerance = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan DefaultWeeklyDuration = TimeSpan.FromDays(7);

        public static UsageInsightReport Build(UsageSnapshot snapshot, DateTimeOffset now)
        {
            var report = new UsageInsightReport();
            if (snapshot == null)
            {
                return report;
            }

            var utcNow = now.ToUniversalTime();
            report.Codex = BuildProvider(snapshot.Codex, utcNow, "Codex");
            report.Claude = BuildProvider(snapshot.Claude, utcNow, "Claude Code");
            report.Antigravity = BuildProvider(snapshot.Antigravity, utcNow, "Antigravity");
            report.Recommendation = Recommend(report.Codex, report.Claude, report.Antigravity);
            report.Timeline = BuildTimeline(snapshot, utcNow);
            return report;
        }

        public static UsageInsightReport Build(UsageSnapshot snapshot)
        {
            return Build(snapshot, DateTimeOffset.UtcNow);
        }

        private static ProviderInsight BuildProvider(ProviderSnapshot provider, DateTimeOffset now, string fallbackName)
        {
            var result = new ProviderInsight
            {
                Provider = provider == null || string.IsNullOrWhiteSpace(provider.Name) ? fallbackName : provider.Name
            };
            var bucket = UsageSelection.SummaryBucket(provider);
            if (provider == null || !provider.IsAvailable)
            {
                result.Status = "無資料";
                return result;
            }
            if (bucket == null)
            {
                result.Status = "沒有可用量";
                return result;
            }

            result.UsedPercent = Clamp(bucket.UsedPercent);
            result.RemainingPercent = 100 - result.UsedPercent;
            result.ResetsAt = bucket.ResetsAt.HasValue ? bucket.ResetsAt.Value.ToUniversalTime() : (DateTimeOffset?)null;
            result.IsFresh = IsFresh(provider.UpdatedAt, now);
            if (!result.IsFresh)
            {
                result.Status = provider.SourceKind == UsageSourceKind.ClaudeDesktop
                    ? "等待 Claude 同步"
                    : "資料過期";
                return result;
            }

            if (!result.ResetsAt.HasValue)
            {
                result.Status = "重設時間未知";
                return result;
            }

            var duration = BucketDuration(bucket);
            var cycleStart = result.ResetsAt.Value - duration;
            var elapsed = now - cycleStart;
            result.ExpectedUsedPercent = Clamp(100 * elapsed.TotalMilliseconds / duration.TotalMilliseconds);
            result.PaceDeltaPercent = result.UsedPercent - result.ExpectedUsedPercent;
            var remainingTime = result.ResetsAt.Value - now;
            if (remainingTime.TotalDays > 0)
            {
                result.DailyAllowancePercent = result.RemainingPercent / remainingTime.TotalDays;
            }

            result.Status = result.PaceDeltaPercent > 5
                ? "消耗偏快"
                : (result.PaceDeltaPercent < -5 ? "節奏保守" : "節奏正常");
            return result;
        }

        /// <summary>
        /// 重設時間軸逐一列出每個額度桶（Antigravity 的 Gemini／3P 各有每週與 5 小時兩條限額），
        /// 依重設時刻排序，讓使用者一眼看出下一個補血的是哪一條。
        /// </summary>
        private static List<ResetTimelineItem> BuildTimeline(UsageSnapshot snapshot, DateTimeOffset now)
        {
            var timeline = new List<ResetTimelineItem>();
            if (snapshot == null)
            {
                return timeline;
            }
            AddProviderTimeline(timeline, snapshot.Codex, "Codex", now);
            AddProviderTimeline(timeline, snapshot.Claude, "Claude Code", now);
            AddProviderTimeline(timeline, snapshot.Antigravity, "Antigravity", now);
            return timeline.OrderBy(item => item.ResetsAt).ToList();
        }

        private static void AddProviderTimeline(
            List<ResetTimelineItem> timeline,
            ProviderSnapshot provider,
            string fallbackName,
            DateTimeOffset now)
        {
            if (provider == null || !provider.IsAvailable || provider.Buckets == null || provider.Buckets.Count == 0)
            {
                return;
            }

            var name = string.IsNullOrWhiteSpace(provider.Name) ? fallbackName : provider.Name;
            var fresh = IsFresh(provider.UpdatedAt, now);
            var multiple = provider.Buckets.Count(bucket => bucket != null && bucket.ResetsAt.HasValue) > 1;

            foreach (var bucket in provider.Buckets)
            {
                if (bucket == null || !bucket.ResetsAt.HasValue)
                {
                    continue;
                }
                var used = Clamp(bucket.UsedPercent);
                timeline.Add(new ResetTimelineItem
                {
                    Provider = multiple && !string.IsNullOrWhiteSpace(bucket.Label)
                        ? name + " · " + bucket.Label
                        : name,
                    ResetsAt = bucket.ResetsAt.Value.ToUniversalTime(),
                    RemainingPercent = 100 - used,
                    IsFresh = fresh,
                    Status = fresh
                        ? BucketStatus(bucket, used, now)
                        : (provider.SourceKind == UsageSourceKind.ClaudeDesktop ? "等待 Claude 同步" : "資料過期")
                });
            }
        }

        /// <summary>單一額度桶的節奏標籤；尚未開始計時的滑動視窗（官方回報上限）另行標註。</summary>
        private static string BucketStatus(UsageBucket bucket, double used, DateTimeOffset now)
        {
            if (bucket.ResetEstimate == ResetEstimateKind.UpperBound && used <= 0.05)
            {
                return "尚未開始計時";
            }
            var duration = BucketDuration(bucket);
            if (duration <= TimeSpan.Zero)
            {
                return "節奏未知";
            }
            var cycleStart = bucket.ResetsAt.Value.ToUniversalTime() - duration;
            var expected = Clamp(100 * (now - cycleStart).TotalMilliseconds / duration.TotalMilliseconds);
            var delta = used - expected;
            return delta > 5 ? "消耗偏快" : (delta < -5 ? "節奏保守" : "節奏正常");
        }

        private static string Recommend(ProviderInsight codex, ProviderInsight claude, ProviderInsight antigravity)
        {
            var list = new[] { codex, claude, antigravity }.Where(p => p != null && p.IsFresh).ToList();
            if (list.Count == 0)
            {
                return string.Empty;
            }
            if (list.Count == 1)
            {
                return "建議優先使用 " + list[0].Provider;
            }

            var best = list.OrderByDescending(p => p.ExpectedUsedPercent - p.UsedPercent)
                .ThenByDescending(p => p.DailyAllowancePercent)
                .First();

            var close = list.All(p => Math.Abs((p.ExpectedUsedPercent - p.UsedPercent) - (best.ExpectedUsedPercent - best.UsedPercent)) < 0.05);
            return close ? "各模型節奏相近" : "建議優先使用 " + best.Provider;
        }

        private static bool IsFresh(DateTimeOffset updatedAt, DateTimeOffset now)
        {
            if (updatedAt == DateTimeOffset.MinValue)
            {
                return false;
            }
            var timestamp = updatedAt.ToUniversalTime();
            return timestamp >= now - FreshnessLimit && timestamp <= now + FutureTolerance;
        }

        private static TimeSpan BucketDuration(UsageBucket bucket)
        {
            if (bucket.WindowDurationMinutes > 0)
            {
                return TimeSpan.FromMinutes(bucket.WindowDurationMinutes);
            }
            return bucket.Label != null && bucket.Label.IndexOf("每週", StringComparison.OrdinalIgnoreCase) >= 0
                ? DefaultWeeklyDuration
                : DefaultWeeklyDuration;
        }

        private static double Clamp(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value)
                ? 0
                : Math.Max(0, Math.Min(100, value));
        }
    }
}
