using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CodexClaudeUsage
{
    internal sealed class TrayApplication : ApplicationContext
    {
        private const string StartupValueName = "CodexClaudeUsage";
        private const int MinimumVisibleRefreshMilliseconds = UsagePopup.RefreshRotationDuration;
        private const int MenuCommandDelayMilliseconds = 100;
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        private readonly AppConfig _config;
        private readonly UsageAggregator _aggregator;
        private CodexResetDetector _resetDetector;
        private readonly UserPreferences _preferences;
        private readonly CycleHistoryStore _cycleHistory;
        private readonly GlobalHotkeyManager _hotkeys;
        private readonly UsagePopup _popup;
        private readonly UsageCenterForm _center;
        private readonly WidgetState _widgetState;
        private readonly DesktopWidget _widget;
        private readonly ToolStripMenuItem _widgetItem;
        private readonly NotifyIcon _trayIcon;
        private Icon _ownedIcon;
        private readonly ContextMenuStrip _menu;
        private readonly System.Windows.Forms.Timer _menuCommandTimer;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly ToolStripMenuItem _openItem;
        private readonly ToolStripMenuItem _centerItem;
        private readonly ToolStripMenuItem _refreshItem;
        private readonly ToolStripMenuItem _detailItem;
        private readonly ToolStripMenuItem _resetStatusItem;
        private readonly ToolStripMenuItem _notificationItem;
        private readonly ToolStripMenuItem _startupItem;
        private readonly Dictionary<string, int> _notificationLevels = new Dictionary<string, int>();
        private readonly HashSet<string> _exhaustWarned = new HashSet<string>();
        private readonly RegisteredWaitHandle _showRegistration;
        private bool _isRefreshing;
        private bool _hasBaseline;
        private bool _syncingPreferences;
        private bool _centerHasBeenShown;
        private MethodInvoker _pendingMenuCommand;
        private UsageSnapshot _latestSnapshot;

        public TrayApplication(AppConfig config, bool showOnStart, EventWaitHandle showEvent)
        {
            _config = config;
            _aggregator = new UsageAggregator(config);
            _resetDetector = new CodexResetDetector();
            _preferences = UserPreferences.Load();
            _cycleHistory = new CycleHistoryStore();
            _cycleHistory.ApplyRetention(_preferences.HistoryRetentionDays);
            _hotkeys = new GlobalHotkeyManager();
            _hotkeys.Pressed += GlobalHotkeyPressed;
            _popup = new UsagePopup();
            // 單一執行個體事件會在首次顯示前呼叫 BeginInvoke，先建立控制代碼以確保可喚出面板。
            _popup.CreateControl();
            _popup.RefreshRequested += delegate { RefreshUsage(); };

            _center = new UsageCenterForm(config, _preferences);
            _center.RefreshRequested += delegate { RefreshUsage(); };
            _center.PreferencesChanged += delegate { ApplyPreferences(); };
            _center.ClearDataRequested += delegate { ClearLocalData(); };
            _center.ExportDiagnosticsRequested += delegate { CopyDiagnostics(); };
            _center.ExportHistoryCsvRequested += delegate { ExportHistory(false); };
            _center.ExportHistoryJsonRequested += delegate { ExportHistory(true); };
            _center.RepairBridgeRequested += delegate { RepairClaudeBridge(); };

            _widgetState = WidgetState.Load();
            _widget = new DesktopWidget(_widgetState);
            _widget.OpenAtRequested += delegate(Point landing) { _popup.ShowLiquidReveal(landing); };
            _widget.RefreshRequested += delegate { RefreshUsage(); };
            _widget.HiddenByUser += delegate
            {
                _widgetState.Enabled = false;
                _widgetState.Save();
                if (_widgetItem != null)
                {
                    _widgetItem.Checked = false;
                }
            };

            _openItem = new ToolStripMenuItem("開啟用量")
            {
                Image = NativeVisuals.CreateMenuGlyph("\uE8A7", Palette.PrimaryText)
            };
            _openItem.Font = new Font(_openItem.Font, FontStyle.Bold);
            _openItem.Click += delegate { DeferMenuCommand(delegate { TogglePopup(true); }); };

            _centerItem = new ToolStripMenuItem("用量中心")
            {
                Image = NativeVisuals.CreateMenuGlyph("\uE80F", Palette.PrimaryText)
            };
            _centerItem.Click += delegate { DeferMenuCommand(ShowUsageCenter); };

            _refreshItem = new ToolStripMenuItem("立即更新")
            {
                Image = NativeVisuals.CreateMenuGlyph("\uE72C", Palette.PrimaryText)
            };
            _refreshItem.Click += delegate { RefreshUsage(); };

            _detailItem = new ToolStripMenuItem("顯示短期視窗")
            {
                CheckOnClick = true,
                Checked = !_preferences.TotalOnly,
                Image = NativeVisuals.CreateMenuGlyph("\uE8A9", Palette.PrimaryText)
            };
            _detailItem.CheckedChanged += delegate
            {
                if (_syncingPreferences)
                {
                    return;
                }
                var totalOnly = !_detailItem.Checked;
                DeferMenuCommand(delegate
                {
                    _preferences.TotalOnly = totalOnly;
                    ApplyPreferences();
                    TogglePopup(true);
                });
            };

            _resetStatusItem = new ToolStripMenuItem("Codex 重置 · 監測中")
            {
                Enabled = false,
                Image = NativeVisuals.CreateMenuGlyph("\uE823", Palette.SecondaryText)
            };

            _notificationItem = new ToolStripMenuItem("用量與重置通知")
            {
                CheckOnClick = true,
                Checked = UserState.LoadNotifications(config.NotificationsEnabled),
                Image = NativeVisuals.CreateMenuGlyph("\uE7ED", Palette.PrimaryText)
            };
            _notificationItem.CheckedChanged += delegate
            {
                try
                {
                    UserState.SaveNotifications(_notificationItem.Checked);
                }
                catch
                {
                }
            };

            _widgetItem = new ToolStripMenuItem("桌面小工具")
            {
                CheckOnClick = true,
                Checked = _widgetState.Enabled,
                Image = NativeVisuals.CreateMenuGlyph("\uE7F4", Palette.PrimaryText)
            };
            _widgetItem.CheckedChanged += delegate
            {
                _widgetState.Enabled = _widgetItem.Checked;
                _widgetState.Save();
                if (_widgetItem.Checked)
                {
                    _widget.ShowWidget();
                }
                else
                {
                    _widget.Hide();
                }
            };

            _startupItem = new ToolStripMenuItem("隨 Windows 啟動")
            {
                CheckOnClick = true,
                Checked = StartupRegistration.IsEnabled(StartupValueName),
                Image = NativeVisuals.CreateMenuGlyph("\uE7E8", Palette.PrimaryText)
            };
            _startupItem.CheckedChanged += StartupChanged;

            var exitItem = new ToolStripMenuItem("結束")
            {
                Image = NativeVisuals.CreateMenuGlyph("\uE8BB", Palette.SecondaryText)
            };
            exitItem.Click += delegate { ExitApplication(); };

            _menu = new ContextMenuStrip
            {
                BackColor = Palette.Surface,
                ForeColor = Palette.PrimaryText,
                Font = NativeVisuals.CreateUiFont(9f, FontStyle.Regular),
                ShowImageMargin = true,
                ImageScalingSize = new Size(18, 18),
                Padding = new Padding(2),
                Renderer = new DarkMenuRenderer()
            };
            _menu.Items.Add(_openItem);
            _menu.Items.Add(_centerItem);
            _menu.Items.Add(_refreshItem);
            _menu.Items.Add(_resetStatusItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(_detailItem);
            _menu.Items.Add(_notificationItem);
            _menu.Items.Add(_widgetItem);
            _menu.Items.Add(_startupItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(exitItem);
            MenuChrome.Attach(_menu);
            _menuCommandTimer = new System.Windows.Forms.Timer { Interval = MenuCommandDelayMilliseconds };
            _menuCommandTimer.Tick += delegate { RunPendingMenuCommand(); };
            _menu.Closed += MenuClosed;

            _ownedIcon = TrayIconFactory.Create(null);
            _trayIcon = new NotifyIcon
            {
                Icon = _ownedIcon,
                Text = "Codex + Claude + Antigravity 用量",
                ContextMenuStrip = _menu,
                Visible = true
            };
            _trayIcon.MouseClick += TrayMouseClick;
            _trayIcon.DoubleClick += delegate { TogglePopup(true); };

            ConfigureGlobalHotkeys(true);

            _timer = new System.Windows.Forms.Timer { Interval = config.RefreshSeconds * 1000 };
            _timer.Tick += delegate { RefreshUsage(); };
            _timer.Start();

            _showRegistration = ThreadPool.RegisterWaitForSingleObject(
                showEvent,
                delegate
                {
                    try
                    {
                        _popup.BeginInvoke((MethodInvoker)delegate { TogglePopup(true); });
                    }
                    catch
                    {
                    }
                },
                null,
                Timeout.Infinite,
                false);

            if (_widgetState.Enabled)
            {
                _widget.ShowWidget();
            }
            if (showOnStart)
            {
                _popup.ShowNearTray();
            }
            RefreshUsage();
        }

        private void TrayMouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                TogglePopup(false);
            }
        }

        private void TogglePopup(bool forceOpen)
        {
            if (_popup.Visible && !forceOpen)
            {
                _popup.Dismiss();
                return;
            }
            _popup.ShowNearTray();
        }

        private void ShowUsageCenter()
        {
            if (_center.IsDisposed)
            {
                return;
            }
            if (!_centerHasBeenShown || !IsReachable(_center.Bounds))
            {
                var workingArea = Screen.FromPoint(Cursor.Position).WorkingArea;
                _center.StartPosition = FormStartPosition.Manual;
                _center.Location = new Point(
                    workingArea.Left + Math.Max(0, (workingArea.Width - _center.Width) / 2),
                    workingArea.Top + Math.Max(0, (workingArea.Height - _center.Height) / 2));
            }
            _center.ShowInTaskbar = true;
            if (!_center.Visible)
            {
                _center.Show();
            }
            _center.WindowState = FormWindowState.Normal;
            _center.TopMost = true;
            _center.BringToFront();
            _center.Activate();
            SetForegroundWindow(_center.Handle);
            _centerHasBeenShown = true;
            _center.BeginInvoke((MethodInvoker)delegate
            {
                if (!_center.IsDisposed)
                {
                    _center.TopMost = false;
                }
            });
        }

        private void DeferMenuCommand(MethodInvoker command)
        {
            if (command == null)
            {
                return;
            }
            _pendingMenuCommand = command;
            if (_menu == null || !_menu.Visible)
            {
                RunPendingMenuCommand();
                return;
            }
            try
            {
                _menu.BeginInvoke((MethodInvoker)delegate
                {
                    if (_menu.Visible)
                    {
                        _menu.Close(ToolStripDropDownCloseReason.ItemClicked);
                    }
                });
            }
            catch (Exception ex)
            {
                ReportMenuCommandError(ex);
            }
        }

        private void MenuClosed(object sender, ToolStripDropDownClosedEventArgs eventArgs)
        {
            if (_pendingMenuCommand == null)
            {
                return;
            }
            _menuCommandTimer.Stop();
            _menuCommandTimer.Start();
        }

        private void RunPendingMenuCommand()
        {
            _menuCommandTimer.Stop();
            var command = _pendingMenuCommand;
            _pendingMenuCommand = null;
            if (command == null)
            {
                return;
            }
            try
            {
                command();
            }
            catch (Exception ex)
            {
                ReportMenuCommandError(ex);
            }
        }

        private void ReportMenuCommandError(Exception exception)
        {
            Program.LogError(exception);
            if (_trayIcon != null)
            {
                _trayIcon.ShowBalloonTip(4000, "無法開啟功能", Compact(exception.Message), ToolTipIcon.Warning);
            }
        }

        private static bool IsReachable(Rectangle bounds)
        {
            foreach (var screen in Screen.AllScreens)
            {
                var visible = Rectangle.Intersect(screen.WorkingArea, bounds);
                if (visible.Width >= 64 && visible.Height >= 64)
                {
                    return true;
                }
            }
            return false;
        }

        private void GlobalHotkeyPressed(GlobalHotkeyAction action)
        {
            if (action == GlobalHotkeyAction.Refresh)
            {
                RefreshUsage();
                return;
            }
            TogglePopup(false);
        }

        private async void RefreshUsage()
        {
            if (_isRefreshing)
            {
                return;
            }

            _isRefreshing = true;
            _refreshItem.Enabled = false;
            _popup.SetLoading(true);
            if (_center.Visible)
            {
                _center.SetStatus("正在更新…");
            }
            var visibleFeedback = _popup.Visible && Motion.IsEnabled;
            var feedbackClock = Stopwatch.StartNew();
            try
            {
                var snapshot = await Task.Run(async delegate
                {
                    return await _aggregator.ReadAsync().ConfigureAwait(false);
                });
                if (visibleFeedback)
                {
                    var remaining = MinimumVisibleRefreshMilliseconds - (int)feedbackClock.ElapsedMilliseconds;
                    if (remaining > 0)
                    {
                        await Task.Delay(remaining);
                    }
                }
                _latestSnapshot = snapshot;
                _cycleHistory.Observe(snapshot);
                _popup.SetSnapshot(UsageSelection.ForDisplay(snapshot, _preferences.TotalOnly));
                _widget.SetSnapshot(snapshot);
                UpdateTrayText(snapshot);
                ProcessNotifications(snapshot);
                UpdateUsageCenter(snapshot);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                _trayIcon.Text = "用量更新失敗";
                if (_popup.Visible)
                {
                    _trayIcon.ShowBalloonTip(4000, "模型用量", Compact(ex.Message), ToolTipIcon.Warning);
                }
                if (_center.Visible)
                {
                    _center.SetStatus("更新失敗：" + Compact(ex.Message));
                }
            }
            finally
            {
                _popup.SetLoading(false);
                _refreshItem.Enabled = true;
                _isRefreshing = false;
            }
        }

        private void UpdateUsageCenter(UsageSnapshot snapshot)
        {
            _center.SetSnapshot(
                snapshot,
                UsageInsights.Build(snapshot),
                _cycleHistory.GetCycles(_preferences.HistoryRetentionDays),
                UsageDiagnostics.Evaluate(snapshot),
                ClaudeBridgeHealth.Check());
        }

        private void UpdateTrayText(UsageSnapshot snapshot)
        {
            var codex = TrayIconFactory.SummaryUsedPercent(snapshot.Codex);
            var claude = TrayIconFactory.SummaryUsedPercent(snapshot.Claude);
            var antigravity = TrayIconFactory.SummaryUsedPercent(snapshot.Antigravity);
            var text = string.Format(
                CultureInfo.CurrentCulture,
                "Codex {0} · Claude {1} · AG {2}",
                codex.HasValue ? codex.Value.ToString("0", CultureInfo.CurrentCulture) + "%" : "--",
                claude.HasValue ? claude.Value.ToString("0", CultureInfo.CurrentCulture) + "%" : "--",
                antigravity.HasValue ? antigravity.Value.ToString("0", CultureInfo.CurrentCulture) + "%" : "--");
            _trayIcon.Text = text.Length <= 63 ? text : text.Substring(0, 63);
            UpdateTrayIcon(snapshot);
        }

        private void UpdateTrayIcon(UsageSnapshot snapshot)
        {
            Icon fresh = null;
            try
            {
                fresh = TrayIconFactory.Create(snapshot);
                _trayIcon.Icon = fresh;
                var stale = _ownedIcon;
                _ownedIcon = fresh;
                if (stale != null)
                {
                    stale.Dispose();
                }
            }
            catch
            {
                // 圖示重繪失敗時保留舊圖示，不影響資料更新。
                if (fresh != null && !ReferenceEquals(fresh, _ownedIcon))
                {
                    fresh.Dispose();
                }
            }
        }

        private void ProcessNotifications(UsageSnapshot snapshot)
        {
            ProcessCodexResetNotifications(snapshot.Codex);
            ProcessExhaustWarnings(snapshot.Codex);
            ProcessExhaustWarnings(snapshot.Claude);
            ProcessExhaustWarnings(snapshot.Antigravity);

            var current = new Dictionary<string, int>();
            CollectNotificationLevels(current, snapshot.Codex);
            CollectNotificationLevels(current, snapshot.Claude);
            CollectNotificationLevels(current, snapshot.Antigravity);

            if (!_hasBaseline)
            {
                foreach (var item in current)
                {
                    _notificationLevels[item.Key] = item.Value;
                }
                _hasBaseline = true;
                return;
            }

            var inactiveKeys = _notificationLevels.Keys
                .Where(key => !current.ContainsKey(key))
                .ToArray();
            foreach (var key in inactiveKeys)
            {
                _notificationLevels.Remove(key);
            }

            foreach (var item in current)
            {
                int previous;
                _notificationLevels.TryGetValue(item.Key, out previous);
                var providerName = item.Key.StartsWith("Codex", StringComparison.OrdinalIgnoreCase)
                    ? "Codex"
                    : (item.Key.StartsWith("Claude", StringComparison.OrdinalIgnoreCase) ? "Claude Code" : "Antigravity");
                if (_preferences.Allows(
                        UsageNotificationKind.Threshold,
                        providerName,
                        DateTimeOffset.Now,
                        _notificationItem.Checked)
                    && item.Value > previous
                    && item.Value > 0)
                {
                    var levelName = item.Value >= 2 ? "接近上限" : "用量偏高";
                    _trayIcon.ShowBalloonTip(
                        6000,
                        "模型用量" + levelName,
                        item.Key,
                        item.Value >= 2 ? ToolTipIcon.Warning : ToolTipIcon.Info);
                }
                _notificationLevels[item.Key] = item.Value;
            }
        }

        private void ProcessCodexResetNotifications(ProviderSnapshot provider)
        {
            var totalProvider = UsageSelection.SummaryProvider(provider, "Codex");
            var detected = _resetDetector.Observe(totalProvider);
            var latest = _resetDetector.Latest;
            var summary = UsageSelection.SummaryBucket(provider);
            if (latest != null && summary != null
                && !string.Equals(latest.BucketLabel, summary.Label, StringComparison.OrdinalIgnoreCase))
            {
                latest = null;
            }
            if (latest == null)
            {
                var official = provider != null
                    && provider.IsAvailable
                    && provider.SourceKind == UsageSourceKind.CodexAppServer;
                _resetStatusItem.Text = official
                    ? "Codex 重置 · 監測中"
                    : provider != null && provider.IsAvailable
                        ? "Codex 重置 · 等待官方資料"
                        : "Codex 重置 · 等待資料";
                _resetStatusItem.ToolTipText = official
                    ? "尚未偵測到 Codex 額度重置"
                    : "重置偵測只使用 Codex 官方 app-server 資料";
            }
            else
            {
                var label = latest.BucketLabel.Length > 24
                    ? latest.BucketLabel.Substring(0, 24) + "…"
                    : latest.BucketLabel;
                _resetStatusItem.Text = "最近重置 · " + label + " · " + TimeUtil.FreshnessLabel(latest.DetectedAt);
                _resetStatusItem.ToolTipText = string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}\n重置時間：{1:yyyy/MM/dd HH:mm}\n用量：{2:0.#}% → {3:0.#}%",
                    latest.BucketLabel,
                    latest.ResetAt.ToLocalTime(),
                    latest.PreviousUsedPercent,
                    latest.CurrentUsedPercent);
            }

            if (!_preferences.Allows(
                    UsageNotificationKind.Reset,
                    "Codex",
                    DateTimeOffset.Now,
                    _notificationItem.Checked)
                || detected.Count == 0)
            {
                return;
            }

            var lines = new List<string>();
            foreach (var item in detected.Take(3))
            {
                var remaining = Math.Max(0, 100 - item.CurrentUsedPercent);
                var next = item.NextResetAt.HasValue
                    ? string.Format(CultureInfo.CurrentCulture, "；下次 {0:MM/dd HH:mm}", item.NextResetAt.Value.ToLocalTime())
                    : string.Empty;
                lines.Add(string.Format(
                    CultureInfo.CurrentCulture,
                    "{0}：目前可用 {1:0.#}%{2}",
                    item.BucketLabel,
                    remaining,
                    next));
            }
            _trayIcon.ShowBalloonTip(
                7000,
                "Codex 額度已重置",
                string.Join(Environment.NewLine, lines),
                ToolTipIcon.Info);
        }

        /// <summary>
        /// 耗盡預警通知：bucket 進入「預計早於重設時間用罄」狀態時通知一次，
        /// 離開該狀態後重置，避免每次更新重複轟炸。
        /// </summary>
        private void ProcessExhaustWarnings(ProviderSnapshot provider)
        {
            if (provider == null)
            {
                return;
            }

            var prefix = provider.Name + " · ";
            if (!IsFreshForNotifications(provider))
            {
                _exhaustWarned.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
                return;
            }

            var activeKeys = new HashSet<string>(StringComparer.Ordinal);
            // 掃描該供應商的每一條限額（Antigravity 有四條），取最早用罄者發出單一預警，
            // 避免只盯代表額度而漏掉 5 小時視窗，也不會一次跳出多則氣球。
            var bucket = EarliestExhaustBucket(provider) ?? UsageSelection.SummaryBucket(provider);
            if (bucket != null)
            {
                var key = prefix + bucket.Label;
                activeKeys.Add(key);
                if (bucket.ProjectedExhaustAt.HasValue)
                {
                    if (_preferences.Allows(
                            UsageNotificationKind.Exhaustion,
                            provider.Name,
                            DateTimeOffset.Now,
                            _notificationItem.Checked)
                        && !_exhaustWarned.Contains(key))
                    {
                        _exhaustWarned.Add(key);
                        _trayIcon.ShowBalloonTip(
                            6000,
                            "用量耗盡預警",
                            string.Format(
                                CultureInfo.CurrentCulture,
                                "{0}：依目前速度約 {1:MM/dd HH:mm} 用罄，早於重設時間",
                                key,
                                bucket.ProjectedExhaustAt.Value.ToLocalTime()),
                            ToolTipIcon.Warning);
                    }
                }
                else
                {
                    _exhaustWarned.Remove(key);
                }
            }

            _exhaustWarned.RemoveWhere(
                key => key.StartsWith(prefix, StringComparison.Ordinal) && !activeKeys.Contains(key));
        }

        /// <summary>回傳該供應商最早被預測用罄的額度桶；沒有任何預測時回傳 null。</summary>
        private static UsageBucket EarliestExhaustBucket(ProviderSnapshot provider)
        {
            if (provider == null || provider.Buckets == null)
            {
                return null;
            }
            UsageBucket earliest = null;
            foreach (var candidate in provider.Buckets)
            {
                if (candidate == null || !candidate.ProjectedExhaustAt.HasValue)
                {
                    continue;
                }
                if (earliest == null || candidate.ProjectedExhaustAt.Value < earliest.ProjectedExhaustAt.Value)
                {
                    earliest = candidate;
                }
            }
            return earliest;
        }

        /// <summary>回傳該供應商目前已用比例最高的額度桶（門檻通知以最吃緊的一條為準）。</summary>
        private static UsageBucket HighestUsageBucket(ProviderSnapshot provider)
        {
            if (provider == null || provider.Buckets == null)
            {
                return null;
            }
            UsageBucket highest = null;
            foreach (var candidate in provider.Buckets)
            {
                if (candidate == null)
                {
                    continue;
                }
                if (highest == null || candidate.UsedPercent > highest.UsedPercent)
                {
                    highest = candidate;
                }
            }
            return highest;
        }

        private void CollectNotificationLevels(IDictionary<string, int> target, ProviderSnapshot provider)
        {
            if (!IsFreshForNotifications(provider))
            {
                return;
            }
            var bucket = HighestUsageBucket(provider) ?? UsageSelection.SummaryBucket(provider);
            if (bucket != null)
            {
                var level = bucket.UsedPercent >= _config.CriticalPercent
                    ? 2
                    : bucket.UsedPercent >= _config.WarningPercent ? 1 : 0;
                target[provider.Name + " · " + bucket.Label] = level;
            }
        }

        internal static bool IsFreshForNotifications(ProviderSnapshot provider)
        {
            if (provider == null
                || !provider.IsAvailable
                || provider.UpdatedAt == DateTimeOffset.MinValue)
            {
                return false;
            }
            var age = DateTimeOffset.UtcNow - provider.UpdatedAt.ToUniversalTime();
            return age >= TimeSpan.FromMinutes(-5) && age < TimeSpan.FromMinutes(60);
        }

        private void ApplyPreferences()
        {
            try
            {
                _preferences.Save();
                _cycleHistory.ApplyRetention(_preferences.HistoryRetentionDays);
                LocalDataManager.TrimTimestampedHistory(_preferences.HistoryRetentionDays);

                _syncingPreferences = true;
                _detailItem.Checked = !_preferences.TotalOnly;
                _syncingPreferences = false;
                _center.ReloadPreferences();
                ConfigureGlobalHotkeys(false);

                if (_latestSnapshot != null)
                {
                    _popup.SetSnapshot(UsageSelection.ForDisplay(_latestSnapshot, _preferences.TotalOnly));
                    UpdateUsageCenter(_latestSnapshot);
                }
            }
            catch (Exception ex)
            {
                _syncingPreferences = false;
                Program.LogError(ex);
                _center.SetStatus("無法儲存設定：" + Compact(ex.Message));
                _trayIcon.ShowBalloonTip(4000, "無法儲存設定", Compact(ex.Message), ToolTipIcon.Warning);
            }
        }

        private void ConfigureGlobalHotkeys(bool announceFailure)
        {
            string error;
            if (_hotkeys.Configure(_preferences.GlobalHotkeysEnabled, out error))
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    _center.SetStatus(error);
                    if (announceFailure)
                    {
                        _trayIcon.ShowBalloonTip(5000, "全域快捷鍵已使用備援組合", error, ToolTipIcon.Info);
                    }
                }
                return;
            }

            _preferences.GlobalHotkeysEnabled = false;
            try
            {
                _preferences.Save();
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
            _center.ReloadPreferences();
            _center.SetStatus(error);
            if (announceFailure)
            {
                _trayIcon.ShowBalloonTip(5000, "全域快捷鍵未啟用", error, ToolTipIcon.Warning);
            }
        }

        private void ClearLocalData()
        {
            var answer = MessageBox.Show(
                _center,
                "將清除本程式建立的趨勢、週期、重設與錯誤紀錄。\n設定、Claude Code 設定與官方快取不會被刪除。",
                "清除本機資料",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes)
            {
                return;
            }

            try
            {
                _cycleHistory.Clear();
                var removed = LocalDataManager.ClearOperationalData();
                _resetDetector = new CodexResetDetector();
                _notificationLevels.Clear();
                _exhaustWarned.Clear();
                _hasBaseline = false;
                _resetStatusItem.Text = "Codex 重置 · 等待資料";
                _resetStatusItem.ToolTipText = "尚未偵測到 Codex 額度重置";
                if (_latestSnapshot != null)
                {
                    UpdateUsageCenter(_latestSnapshot);
                }
                _center.SetStatus(string.Format(CultureInfo.CurrentCulture, "已清除 {0} 個本機資料檔", removed));
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                _center.SetStatus("清除失敗：" + Compact(ex.Message));
            }
        }

        private void CopyDiagnostics()
        {
            try
            {
                Clipboard.SetText(UsageDiagnostics.BuildRedactedJson(_latestSnapshot, _preferences, _config));
                _center.SetStatus("已複製去識別診斷報告");
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
                _center.SetStatus("複製失敗：" + Compact(ex.Message));
            }
        }

        private void ExportHistory(bool json)
        {
            using (var dialog = new SaveFileDialog
            {
                AddExtension = true,
                DefaultExt = json ? "json" : "csv",
                Filter = json ? "JSON 檔案 (*.json)|*.json" : "CSV 檔案 (*.csv)|*.csv",
                FileName = "model-usage-history-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)
            })
            {
                if (dialog.ShowDialog(_center) != DialogResult.OK)
                {
                    return;
                }
                try
                {
                    if (json)
                    {
                        _cycleHistory.ExportJson(dialog.FileName);
                    }
                    else
                    {
                        _cycleHistory.ExportCsv(dialog.FileName);
                    }
                    _center.SetStatus("歷史紀錄已匯出");
                }
                catch (Exception ex)
                {
                    Program.LogError(ex);
                    _center.SetStatus("匯出失敗：" + Compact(ex.Message));
                }
            }
        }

        private void RepairClaudeBridge()
        {
            var result = ClaudeBridgeHealth.Repair();
            if (_latestSnapshot != null)
            {
                UpdateUsageCenter(_latestSnapshot);
            }
            switch (result.State)
            {
                case ClaudeBridgeState.Ready:
                    _center.SetStatus("Claude 橋接已就緒");
                    break;
                case ClaudeBridgeState.WaitingForData:
                    _center.SetStatus("Claude 橋接已設定，等待 Claude Code 回報");
                    break;
                case ClaudeBridgeState.CustomStatusLine:
                    _center.SetStatus("已保留既有的 Claude Code 自訂狀態列");
                    break;
                case ClaudeBridgeState.InvalidSettings:
                    _center.SetStatus("Claude Code 設定檔無法讀取");
                    break;
                default:
                    _center.SetStatus("Claude 橋接修復失敗");
                    break;
            }
        }

        private void StartupChanged(object sender, EventArgs e)
        {
            try
            {
                StartupRegistration.SetEnabled(StartupValueName, _startupItem.Checked);
            }
            catch (Exception ex)
            {
                _startupItem.CheckedChanged -= StartupChanged;
                _startupItem.Checked = !_startupItem.Checked;
                _startupItem.CheckedChanged += StartupChanged;
                _trayIcon.ShowBalloonTip(4000, "無法更新啟動設定", Compact(ex.Message), ToolTipIcon.Warning);
            }
        }

        private void ExitApplication()
        {
            _timer.Stop();
            _hotkeys.Disable();
            _trayIcon.Visible = false;
            _widget.Close();
            _popup.Close();
            _center.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                _menuCommandTimer.Dispose();
                if (_showRegistration != null)
                {
                    _showRegistration.Unregister(null);
                }
                _hotkeys.Dispose();
                _center.Dispose();
                _widget.Dispose();
                _popup.Dispose();
                _trayIcon.Dispose();
                _menu.Dispose();
                if (_ownedIcon != null)
                {
                    _ownedIcon.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        private static string Compact(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return "未知錯誤";
            }
            message = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return message.Length > 180 ? message.Substring(0, 180) + "…" : message;
        }
    }

    internal static class StartupRegistration
    {
        private const string RunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

        public static bool IsEnabled(string valueName)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, false))
            {
                return key != null && key.GetValue(valueName) != null;
            }
        }

        public static void SetEnabled(string valueName, bool enabled)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("無法開啟 Windows 啟動登錄機碼");
                }
                if (enabled)
                {
                    key.SetValue(valueName, "\"" + Application.ExecutablePath + "\" --background");
                }
                else
                {
                    key.DeleteValue(valueName, false);
                }
            }
        }
    }

    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer()
            : base(new DarkMenuColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var pen = new Pen(Palette.Border))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            }
        }

        /// <summary>Win11 語彙的懸停高亮：內縮圓角面，取代整行方塊。</summary>
        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
            {
                return;
            }
            var bounds = new Rectangle(3, 1, e.Item.Width - 6, e.Item.Height - 2);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }
            var previousMode = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (var path = NativeVisuals.RoundedRectangle(bounds, 4))
            using (var brush = new SolidBrush(Palette.Track))
            {
                e.Graphics.FillPath(brush, path);
            }
            e.Graphics.SmoothingMode = previousMode;
        }

        /// <summary>勾選改畫乾淨的 Fluent 勾號，不畫系統藍底框。</summary>
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            using (var font = NativeVisuals.CreateGlyphFont(12, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Palette.PrimaryText))
            {
                var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                e.Graphics.DrawString("\uE73E", font, brush, e.ImageRectangle, format);
                format.Dispose();
            }
        }

        /// <summary>停用項（如摘要列）以次要文字色呈現，不走系統灰；用量摘要列分段上品牌色。</summary>
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (string.Equals(e.Item.Tag as string, "usage-summary", StringComparison.Ordinal))
            {
                DrawSummaryText(e);
                return;
            }
            if (!e.Item.Enabled)
            {
                TextRenderer.DrawText(
                    e.Graphics, e.Text, e.TextFont, e.TextRectangle,
                    Palette.SecondaryText, e.TextFormat);
                return;
            }
            base.OnRenderItemText(e);
        }

        /// <summary>摘要列：百分比數字依供應商上品牌色，其餘為次要文字色。</summary>
        private static void DrawSummaryText(ToolStripItemTextRenderEventArgs e)
        {
            var segments = e.Text.Split(new[] { ' ' }, StringSplitOptions.None);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            var x = e.TextRectangle.Left;
            for (var i = 0; i < segments.Length; i++)
            {
                var segment = i == segments.Length - 1 ? segments[i] : segments[i] + " ";
                var color = Palette.SecondaryText;
                if (segment.TrimEnd().EndsWith("%", StringComparison.Ordinal))
                {
                    // 依前一詞判定品牌:前段含 Codex → 綠,含 Claude → 橙。
                    var brand = Palette.SecondaryText;
                    for (var back = i - 1; back >= 0; back--)
                    {
                        if (segments[back].IndexOf("Codex", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            brand = Palette.Codex;
                            break;
                        }
                        if (segments[back].IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            brand = Palette.Claude;
                            break;
                        }
                        if (segments[back].IndexOf("Antigravity", StringComparison.OrdinalIgnoreCase) >= 0 || segments[back].IndexOf("AG", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            brand = Palette.Antigravity;
                            break;
                        }
                    }
                    color = brand;
                }
                var bounds = new Rectangle(x, e.TextRectangle.Top, e.TextRectangle.Right - x, e.TextRectangle.Height);
                TextRenderer.DrawText(e.Graphics, segment, e.TextFont, bounds, color, flags);
                x += TextRenderer.MeasureText(e.Graphics, segment, e.TextFont, bounds.Size, flags).Width;
            }
        }
    }

    /// <summary>Windows 11 下讓 ContextMenuStrip 套用 DWM 原生圓角與邊框色。</summary>
    internal static class MenuChrome
    {
        public static void Attach(ToolStripDropDown menu)
        {
            if (!WindowChrome.SupportsNativeRounding)
            {
                return;
            }
            menu.DropShadowEnabled = false; // 方形 CS_DROPSHADOW 讓位給 DWM 圓角陰影。
            menu.Opening += delegate
            {
                WindowChrome.ApplyRoundedPopupChrome(menu.Handle, Palette.Border);
            };
        }
    }

    internal sealed class DarkMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Palette.Surface; } }
        public override Color ImageMarginGradientBegin { get { return Palette.Surface; } }
        public override Color ImageMarginGradientMiddle { get { return Palette.Surface; } }
        public override Color ImageMarginGradientEnd { get { return Palette.Surface; } }
        public override Color MenuItemSelected { get { return Palette.Track; } }
        public override Color MenuItemBorder { get { return Palette.Border; } }
        public override Color MenuBorder { get { return Palette.Border; } }
        public override Color SeparatorDark { get { return Palette.Border; } }
        public override Color SeparatorLight { get { return Palette.Border; } }
    }

    internal static class TrayIconFactory
    {
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        /// <summary>
        /// 三環用量儀表圖示：外環 Codex、中環 Claude、內環 Antigravity，依第一組限額的已用比例填充，
        /// 不開啟面板也能掃視三邊用量。無資料時顯示灰色軌道；80% / 95% 依主介面規則轉警示色。
        /// </summary>
        public static Icon Create(UsageSnapshot snapshot)
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.Clear(Color.Transparent);

                var codex = snapshot == null ? (double?)null : SummaryUsedPercent(snapshot.Codex);
                var claude = snapshot == null ? (double?)null : SummaryUsedPercent(snapshot.Claude);
                var antigravity = snapshot == null ? (double?)null : SummaryUsedPercent(snapshot.Antigravity);
                DrawGaugeRing(graphics, new RectangleF(1.5f, 1.5f, 29.0f, 29.0f), 3.0f, codex, Palette.Codex);
                DrawGaugeRing(graphics, new RectangleF(6.0f, 6.0f, 20.0f, 20.0f), 3.0f, claude, Palette.Claude);
                DrawGaugeRing(graphics, new RectangleF(10.5f, 10.5f, 11.0f, 11.0f), 3.0f, antigravity, Palette.Antigravity);

                var handle = bitmap.GetHicon();
                try
                {
                    using (var temporary = Icon.FromHandle(handle))
                    {
                        return (Icon)temporary.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }

        internal static double? SummaryUsedPercent(ProviderSnapshot provider)
        {
            var summary = UsageSelection.SummaryBucket(provider);
            if (summary == null)
            {
                return null;
            }
            return Math.Max(0, Math.Min(100, summary.UsedPercent));
        }

        /// <summary>單一用量儀表環；也供桌面小工具的收合圓點重用。</summary>
        public static void DrawGaugeRing(
            Graphics graphics,
            RectangleF bounds,
            float stroke,
            double? usedPercent,
            Color accent)
        {
            var trackColor = usedPercent.HasValue
                ? Color.FromArgb(70, accent)
                : Color.FromArgb(80, 128, 132, 136);
            using (var trackPen = new Pen(trackColor, stroke))
            {
                graphics.DrawEllipse(trackPen, bounds);
            }
            if (!usedPercent.HasValue)
            {
                return;
            }

            var used = usedPercent.Value;
            var fillColor = used >= 95 ? Palette.Critical : used >= 80 ? Palette.Warning : accent;
            var sweep = (float)(used * 3.6);
            if (sweep < 8f)
            {
                sweep = 8f; // 有資料但用量極低時仍保留可見的起始弧。
            }
            using (var fillPen = new Pen(fillColor, stroke))
            {
                fillPen.StartCap = LineCap.Round;
                fillPen.EndCap = LineCap.Round;
                graphics.DrawArc(fillPen, bounds, -90f, sweep);
            }
        }
    }
}
