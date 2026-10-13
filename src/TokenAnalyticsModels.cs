using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace CodexClaudeUsage
{
    internal sealed class TokenUsageEntry
    {
        public DateTime Date { get; set; }
        public string Provider { get; set; }
        public string Model { get; set; }
        public string Project { get; set; }
        public long InputTokens { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public long OutputTokens { get; set; }
        public long TotalTokens { get { return checked(InputTokens + CacheReadTokens + CacheWriteTokens + OutputTokens); } }
    }

    internal sealed class TokenAnalyticsSnapshot
    {
        public TokenAnalyticsSnapshot()
        {
            Rows = new List<TokenUsageEntry>();
            Status = "等待本機 Token 紀錄";
        }

        public DateTimeOffset CapturedAt { get; set; }
        public DateTimeOffset? LastRecordAt { get; set; }
        public bool IsComplete { get; set; }
        public bool IsIndexing { get; set; }
        public DateTime CoverageStart { get; set; }
        public DateTime? CompleteFrom { get; set; }
        public string Status { get; set; }
        public int FilesScanned { get; set; }
        public int FilesSkipped { get; set; }
        public List<TokenUsageEntry> Rows { get; set; }
    }

    internal static class TokenAnalyticsReport
    {
        public static string ToCsv(IEnumerable<TokenUsageEntry> rows)
        {
            var text = new StringBuilder("Date,Provider,Model,Project,InputTokens,CacheReadTokens,CacheWriteTokens,OutputTokens,TotalTokens\r\n");
            foreach (var row in rows.OrderBy(r => r.Date).ThenBy(r => r.Provider).ThenBy(r => r.Model).ThenBy(r => r.Project))
            {
                text.Append(row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',');
                text.Append(Cell(row.Provider)).Append(',').Append(Cell(row.Model)).Append(',').Append(Cell(row.Project)).Append(',');
                text.Append(row.InputTokens.ToString(CultureInfo.InvariantCulture)).Append(',');
                text.Append(row.CacheReadTokens.ToString(CultureInfo.InvariantCulture)).Append(',');
                text.Append(row.CacheWriteTokens.ToString(CultureInfo.InvariantCulture)).Append(',');
                text.Append(row.OutputTokens.ToString(CultureInfo.InvariantCulture)).Append(',');
                text.Append(row.TotalTokens.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            }
            return text.ToString();
        }

        private static string Cell(string value)
        {
            value = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
            var first = value.TrimStart();
            if (first.Length > 0 && "=+-@".IndexOf(first[0]) >= 0)
            {
                value = "'" + value;
            }
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
