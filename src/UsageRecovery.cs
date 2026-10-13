using System;

namespace CodexClaudeUsage
{
    internal sealed class UsageRecoveryPolicy
    {
        private static readonly int[] RetrySeconds = { 5, 15, 30, 60, 120, 300 };

        public int ConsecutiveFailures { get; private set; }
        public DateTimeOffset? LastSuccessAt { get; private set; }
        public DateTimeOffset? NextRetryAt { get; private set; }
        public TimeSpan NextDelay { get { return CalculateDelay(ConsecutiveFailures); } }

        public void ReportSuccess(DateTimeOffset now)
        {
            ConsecutiveFailures = 0;
            LastSuccessAt = now;
            NextRetryAt = null;
        }

        public void ReportFailure(DateTimeOffset now)
        {
            if (ConsecutiveFailures < int.MaxValue)
            {
                ConsecutiveFailures++;
            }
            try
            {
                NextRetryAt = now.Add(NextDelay);
            }
            catch (ArgumentOutOfRangeException)
            {
                NextRetryAt = DateTimeOffset.MaxValue;
            }
        }

        public static TimeSpan CalculateDelay(int consecutiveFailures)
        {
            if (consecutiveFailures <= 0)
            {
                return TimeSpan.Zero;
            }
            return TimeSpan.FromSeconds(RetrySeconds[Math.Min(consecutiveFailures, RetrySeconds.Length) - 1]);
        }
    }
}
