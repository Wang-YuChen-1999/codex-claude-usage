using System;
using System.Collections.Generic;
using System.Globalization;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Codex + Claude + Antigravity Usage")]
[assembly: AssemblyDescription("Windows tray companion for Codex, Claude Code, and Antigravity plan usage")]
[assembly: AssemblyCompany("Local")]
[assembly: AssemblyProduct("Codex + Claude + Antigravity Usage")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace CodexClaudeUsage
{
    internal static class Program
    {
        private const string ShowEventName = "Local\\CodexClaudeUsage.Show.v1";

        [STAThread]
        private static void Main(string[] args)
        {
            Directory.CreateDirectory(AppPaths.LocalData);
            var config = AppConfig.Load(AppPaths.ConfigFile);

            if (args.Length >= 2 && string.Equals(args[0], "--snapshot", StringComparison.OrdinalIgnoreCase))
            {
                WriteSnapshot(config, args[1]);
                return;
            }
            if (args.Length >= 2 && string.Equals(args[0], "--render", StringComparison.OrdinalIgnoreCase))
            {
                WriteRender(config, args[1]);
                return;
            }
            if (args.Length >= 2 && string.Equals(args[0], "--render-widget", StringComparison.OrdinalIgnoreCase))
            {
                WriteWidgetRender(config, args[1]);
                return;
            }
            if (args.Length >= 2 && string.Equals(args[0], "--render-center", StringComparison.OrdinalIgnoreCase))
            {
                int selectedTab;
                if (args.Length < 3 || !int.TryParse(args[2], out selectedTab))
                {
                    selectedTab = 0;
                }
                WriteCenterRender(config, args[1], selectedTab);
                return;
            }

            bool showEventCreated;
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName, out showEventCreated))
            {
                bool createdNew;
                using (var mutex = new Mutex(true, "Local\\CodexClaudeUsage.Tray.v1", out createdNew))
                {
                    if (!createdNew)
                    {
                        showEvent.Set();
                        return;
                    }

                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs eventArgs)
                    {
                        LogError(eventArgs.Exception);
                    };
                    AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs eventArgs)
                    {
                        LogError(eventArgs.ExceptionObject as Exception);
                    };

                    var background = args.Length > 0 && string.Equals(args[0], "--background", StringComparison.OrdinalIgnoreCase);
                    using (var context = new TrayApplication(config, !background, showEvent))
                    {
                        Application.Run(context);
                    }
                }
            }
        }

        private static void WriteSnapshot(AppConfig config, string outputPath)
        {
            try
            {
                var aggregator = new UsageAggregator(config);
                var snapshot = aggregator.ReadAsync().GetAwaiter().GetResult();
                var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(outputPath, Json.Serialize(ToDictionary(snapshot)));
            }
            catch (Exception ex)
            {
                File.WriteAllText(outputPath, Json.Serialize(new Dictionary<string, object>
                {
                    { "error", ex.Message },
                    { "capturedAt", DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture) }
                }));
                Environment.ExitCode = 1;
            }
        }

        private static void WriteRender(AppConfig config, string outputPath)
        {
            var aggregator = new UsageAggregator(config);
            var snapshot = aggregator.ReadAsync().GetAwaiter().GetResult();
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var popup = new UsagePopup())
            {
                popup.SetSnapshot(UsageSelection.ForDisplay(snapshot, UserPreferences.Load().TotalOnly));
                popup.CreateControl();
                foreach (Control control in popup.Controls)
                {
                    control.CreateControl();
                }
                popup.PerformLayout();
                using (var bitmap = new Bitmap(popup.ClientSize.Width, popup.ClientSize.Height))
                {
                    popup.DrawToBitmap(bitmap, new Rectangle(Point.Empty, popup.ClientSize));
                    bitmap.Save(fullPath, ImageFormat.Png);
                }
            }
        }

        private static void WriteCenterRender(AppConfig config, string outputPath, int selectedTab)
        {
            var aggregator = new UsageAggregator(config);
            var snapshot = aggregator.ReadAsync().GetAwaiter().GetResult();
            var preferences = UserPreferences.Load();
            var history = new CycleHistoryStore();
            history.ApplyRetention(preferences.HistoryRetentionDays);
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var center = new UsageCenterForm(config, preferences))
            {
                center.SetSnapshot(
                    snapshot,
                    UsageInsights.Build(snapshot),
                    history.GetCycles(preferences.HistoryRetentionDays),
                    UsageDiagnostics.Evaluate(snapshot),
                    ClaudeBridgeHealth.Check());
                center.CreateControl();
                CreateChildControls(center);
                center.Show();
                center.SelectTab(selectedTab);
                Application.DoEvents();
                center.PerformLayout();
                Application.DoEvents();
                var renderTarget = center.Controls.Count > 0 ? center.Controls[0] : center;
                using (var bitmap = new Bitmap(renderTarget.ClientSize.Width, renderTarget.ClientSize.Height))
                {
                    renderTarget.DrawToBitmap(bitmap, new Rectangle(Point.Empty, renderTarget.ClientSize));
                    bitmap.Save(fullPath, ImageFormat.Png);
                }
                center.Hide();
            }
        }

        private static void CreateChildControls(Control parent)
        {
            foreach (Control control in parent.Controls)
            {
                control.CreateControl();
                CreateChildControls(control);
            }
        }

        private static void WriteWidgetRender(AppConfig config, string outputPath)
        {
            var aggregator = new UsageAggregator(config);
            var snapshot = aggregator.ReadAsync().GetAwaiter().GetResult();
            var fullPath = Path.GetFullPath(outputPath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var widget = new DesktopWidget(new WidgetState { Pinned = true }))
            {
                widget.SetSnapshot(snapshot);
                widget.CreateControl();
                widget.PerformLayout();
                using (var bitmap = new Bitmap(widget.ClientSize.Width, widget.ClientSize.Height))
                {
                    widget.DrawToBitmap(bitmap, new Rectangle(Point.Empty, widget.ClientSize));
                    bitmap.Save(fullPath, ImageFormat.Png);
                }
            }
        }

        private static IDictionary<string, object> ToDictionary(UsageSnapshot snapshot)
        {
            return new Dictionary<string, object>
            {
                { "capturedAt", snapshot.UpdatedAt.ToString("o", CultureInfo.InvariantCulture) },
                { "codex", ProviderDictionary(snapshot.Codex) },
                { "claude", ProviderDictionary(snapshot.Claude) },
                { "antigravity", ProviderDictionary(snapshot.Antigravity) }
            };
        }

        private static IDictionary<string, object> ProviderDictionary(ProviderSnapshot provider)
        {
            var buckets = new List<object>();
            foreach (var bucket in provider.Buckets)
            {
                buckets.Add(new Dictionary<string, object>
                {
                    { "label", bucket.Label },
                    { "detail", bucket.Detail },
                    { "stableId", bucket.StableId },
                    { "windowDurationMinutes", bucket.WindowDurationMinutes },
                    { "usedPercent", bucket.UsedPercent },
                    { "remainingPercent", Math.Max(0, 100 - bucket.UsedPercent) },
                    { "resetsAt", bucket.ResetsAt.HasValue ? bucket.ResetsAt.Value.ToString("o", CultureInfo.InvariantCulture) : null },
                    { "resetEstimate", bucket.ResetEstimate.ToString() },
                    { "projectedExhaustAt", bucket.ProjectedExhaustAt.HasValue ? bucket.ProjectedExhaustAt.Value.ToString("o", CultureInfo.InvariantCulture) : null },
                    { "trendPointCount", bucket.TrendPoints == null ? 0 : bucket.TrendPoints.Length }
                });
            }
            return new Dictionary<string, object>
            {
                { "available", provider.IsAvailable },
                { "source", provider.SourceKind.ToString() },
                { "status", provider.Status },
                { "error", provider.Error },
                { "updatedAt", provider.UpdatedAt.ToString("o", CultureInfo.InvariantCulture) },
                { "buckets", buckets }
            };
        }

        internal static void LogError(Exception exception)
        {
            try
            {
                var logPath = Path.Combine(AppPaths.LocalData, "error.log");
                var oldPath = logPath + ".old";
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 256 * 1024)
                {
                    if (File.Exists(oldPath))
                    {
                        File.Delete(oldPath);
                    }
                    File.Move(logPath, oldPath);
                }

                var message = exception == null
                    ? "Unknown error"
                    : exception.GetType().Name + ": " + exception.Message;
                message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
                message = Regex.Replace(message, @"[A-Za-z0-9_\-]{32,}", "[redacted]");
                message = Regex.Replace(message, @"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", "[redacted-email]");
                if (message.Length > 800)
                {
                    message = message.Substring(0, 800) + "…";
                }
                File.AppendAllText(logPath, DateTimeOffset.Now.ToString("o", CultureInfo.InvariantCulture) + " " + message + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
