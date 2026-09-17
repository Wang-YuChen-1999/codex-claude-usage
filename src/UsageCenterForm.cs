using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexClaudeUsage
{
    /// <summary>
    /// The full operational surface for usage, local history, source health and preferences.
    /// The host owns persistence and data collection; this form only presents and edits state.
    /// </summary>
    internal sealed class UsageCenterForm : Form
    {
        private readonly AppConfig _config;
        private readonly UserPreferences _preferences;
        private readonly float _scale;
        private readonly ToolTip _toolTip;
        private readonly CenterNavigation _tabs;
        private readonly Panel _contentHost;
        private readonly Control[] _pages;
        private readonly Label _pageTitle;
        private readonly Label _pageDetail;
        private readonly Panel _overviewPage;
        private readonly Panel _historyPage;
        private readonly Panel _healthPage;
        private readonly Panel _settingsPage;
        private readonly ProviderUsageRow _codexRow;
        private readonly ProviderUsageRow _claudeRow;
        private readonly ProviderUsageRow _antigravityRow;
        private readonly Label _recommendation;
        private readonly ListView _timeline;
        private readonly ListView _history;
        private readonly ListView _health;
        private readonly Label _bridgeStatus;
        private readonly CheckBox _totalOnly;
        private readonly CheckBox _thresholds;
        private readonly CheckBox _exhaustion;
        private readonly CheckBox _resets;
        private readonly CheckBox _codexNotifications;
        private readonly CheckBox _claudeNotifications;
        private readonly CheckBox _antigravityNotifications;
        private readonly CheckBox _quietHours;
        private readonly DarkComboBox _quietStart;
        private readonly DarkComboBox _quietEnd;
        private readonly DarkComboBox _retention;
        private readonly CheckBox _hotkeys;
        private readonly Label _status;

        public UsageCenterForm(AppConfig config, UserPreferences preferences)
        {
            _config = config ?? new AppConfig();
            _preferences = preferences ?? new UserPreferences();
            using (var graphics = CreateGraphics())
            {
                _scale = Math.Max(1f, graphics.DpiX / 96f);
            }
            _toolTip = new ToolTip { AutoPopDelay = 6000, InitialDelay = 350, ReshowDelay = 100 };

            AutoScaleMode = AutoScaleMode.None;
            BackColor = Palette.Background;
            ForeColor = Palette.PrimaryText;
            Font = NativeVisuals.CreateUiFont(9f, FontStyle.Regular);
            Text = "用量中心";
            MinimumSize = new Size(Px(760), Px(540));
            ClientSize = new Size(Px(900), Px(620));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            ShowIcon = false;
            DoubleBuffered = true;

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Palette.Background,
                ColumnCount = 2,
                RowCount = 1,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(154)));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            Controls.Add(root);

            _tabs = new CenterNavigation(_scale,
                new[] { "總覽", "歷史", "健康", "設定" },
                new[] { "\uE80F", "\uE81C", "\uE9D9", "\uE713" })
            {
                Dock = DockStyle.Fill
            };
            _tabs.SelectedIndexChanged += delegate { ShowSelectedPage(); };
            root.Controls.Add(CreateSidebar(_tabs), 0, 0);

            var main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Palette.Background,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(Px(22), Px(14), Px(22), Px(10)),
                Margin = Padding.Empty
            };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(54)));
            main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            main.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(30)));
            root.Controls.Add(main, 1, 0);

            Label pageTitle;
            Label pageDetail;
            main.Controls.Add(CreateHeader(out pageTitle, out pageDetail), 0, 0);
            _pageTitle = pageTitle;
            _pageDetail = pageDetail;
            _contentHost = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Palette.Background,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            main.Controls.Add(_contentHost, 0, 1);

            _overviewPage = CreatePage();
            _historyPage = CreatePage();
            _healthPage = CreatePage();
            _settingsPage = CreatePage();
            _pages = new Control[] { _overviewPage, _historyPage, _healthPage, _settingsPage };
            foreach (var page in _pages)
            {
                page.Visible = false;
                _contentHost.Controls.Add(page);
            }

            _codexRow = new ProviderUsageRow(BrandIconKind.Codex, "Codex", Palette.Codex, _scale) { Dock = DockStyle.Top, Height = Px(82) };
            _claudeRow = new ProviderUsageRow(BrandIconKind.Claude, "Claude Code", Palette.Claude, _scale) { Dock = DockStyle.Top, Height = Px(82) };
            _antigravityRow = new ProviderUsageRow(BrandIconKind.Antigravity, "Antigravity", Palette.Antigravity, _scale) { Dock = DockStyle.Top, Height = Px(82) };
            _recommendation = CreateBandLabel();
            var timeline = CreateListView("模型", "下一次重設", "狀態");
            timeline.BackColor = Palette.Background;
            timeline.EmptyAreaColor = Palette.Background;
            _timeline = timeline;
            BuildOverview();
            _history = CreateListView("模型", "週期", "最高", "較上期", "平均 / 日", "重設");
            BuildHistory();
            _health = CreateListView("來源", "最後更新", "新鮮度", "資料來源", "錯誤");
            _bridgeStatus = CreateBandLabel();
            BuildHealth();
            BuildSettings(out _totalOnly, out _thresholds, out _exhaustion, out _resets,
                out _codexNotifications, out _claudeNotifications, out _antigravityNotifications, out _quietHours,
                out _quietStart, out _quietEnd, out _retention, out _hotkeys);

            _status = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Palette.MutedText,
                Font = NativeVisuals.CreateUiFont(8.5f, FontStyle.Regular),
                Text = "等待用量資料"
            };
            main.Controls.Add(CreateFooter(_status), 0, 2);
            ShowSelectedPage();
            ReloadPreferences();
        }

        public event EventHandler RefreshRequested;
        public event EventHandler PreferencesChanged;
        public event EventHandler ClearDataRequested;
        public event EventHandler ExportDiagnosticsRequested;
        public event EventHandler ExportHistoryCsvRequested;
        public event EventHandler ExportHistoryJsonRequested;
        public event EventHandler RepairBridgeRequested;

        public void ReloadPreferences()
        {
            _totalOnly.Checked = _preferences.TotalOnly;
            _thresholds.Checked = _preferences.NotifyThresholds;
            _exhaustion.Checked = _preferences.NotifyExhaustion;
            _resets.Checked = _preferences.NotifyReset;
            _codexNotifications.Checked = _preferences.NotifyCodex;
            _claudeNotifications.Checked = _preferences.NotifyClaude;
            _antigravityNotifications.Checked = _preferences.NotifyAntigravity;
            _quietHours.Checked = _preferences.QuietHoursEnabled;
            SelectTime(_quietStart, _preferences.QuietStartMinutes);
            SelectTime(_quietEnd, _preferences.QuietEndMinutes);
            _retention.SelectedItem = _preferences.HistoryRetentionDays.ToString(CultureInfo.InvariantCulture);
            if (_retention.SelectedIndex < 0) _retention.SelectedIndex = 3;
            _hotkeys.Checked = _preferences.GlobalHotkeysEnabled;
            UpdateQuietHoursState();
        }

        public void SetSnapshot(
            UsageSnapshot snapshot,
            UsageInsightReport insights,
            IList<CycleHistoryRecord> cycles,
            IList<SourceHealth> health,
            ClaudeBridgeHealthResult bridge)
        {
            if (snapshot != null)
            {
                _codexRow.SetProvider(snapshot.Codex);
                _claudeRow.SetProvider(snapshot.Claude);
                _antigravityRow.SetProvider(snapshot.Antigravity);
                SetStatus("更新於 " + FormatTimestamp(snapshot.UpdatedAt));
            }

            _codexRow.SetInsight(insights == null ? null : insights.Codex);
            _claudeRow.SetInsight(insights == null ? null : insights.Claude);
            _antigravityRow.SetInsight(insights == null ? null : insights.Antigravity);
            var recommendation = FirstText(insights, "Recommendation", "RecommendationText", "Summary", "Message", "Advice", "狀態", "尚無足夠資料判斷使用節奏。");
            _recommendation.Text = recommendation.StartsWith("建議", StringComparison.Ordinal)
                ? recommendation
                : "建議：" + recommendation;
            PopulateTimeline(snapshot, insights);
            PopulateHistory(cycles);
            PopulateHealth(health, bridge);
        }

        public void SetStatus(string status)
        {
            _status.Text = string.IsNullOrWhiteSpace(status) ? "等待用量資料" : status;
        }

        internal void SelectTab(int index)
        {
            _tabs.SelectedIndex = Math.Max(0, Math.Min(_tabs.TabCount - 1, index));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowChrome.ApplyRoundedPopupChrome(Handle, Palette.Border);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Hide();
                return true;
            }
            if (keyData == Keys.F5 || keyData == (Keys.Control | Keys.R))
            {
                Raise(RefreshRequested);
                return true;
            }
            if ((keyData & Keys.Control) == Keys.Control)
            {
                var key = keyData & Keys.KeyCode;
                if (key >= Keys.D1 && key <= Keys.D4)
                {
                    SelectTab((int)key - (int)Keys.D1);
                    return true;
                }
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        private Control CreateSidebar(CenterNavigation navigation)
        {
            var sidebar = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Palette.HeaderSurface,
                ColumnCount = 1,
                RowCount = 3,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
            sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(82)));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            sidebar.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(48)));

            var brand = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Palette.HeaderSurface,
                ColumnCount = 1,
                RowCount = 2,
                Padding = new Padding(Px(16), Px(17), Px(12), Px(12)),
                Margin = Padding.Empty
            };
            brand.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(27)));
            brand.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(20)));
            brand.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "模型用量",
                Font = NativeVisuals.CreateSemiboldFont(12.5f),
                ForeColor = Palette.PrimaryText,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);
            brand.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "CODEX + CLAUDE + AG",
                Font = NativeVisuals.CreateUiFont(7.5f, FontStyle.Regular),
                ForeColor = Palette.MutedText,
                TextAlign = ContentAlignment.TopLeft
            }, 0, 1);
            sidebar.Controls.Add(brand, 0, 0);
            sidebar.Controls.Add(navigation, 0, 1);
            sidebar.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "本機唯讀",
                Font = NativeVisuals.CreateUiFont(8.25f, FontStyle.Regular),
                ForeColor = Palette.MutedText,
                Padding = new Padding(Px(16), 0, 0, 0),
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 2);
            return sidebar;
        }

        private Control CreateHeader(out Label title, out Label detail)
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Palette.Background,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(38)));

            var copy = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Palette.Background,
                ColumnCount = 1,
                RowCount = 2,
                Margin = Padding.Empty
            };
            copy.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(31)));
            copy.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(20)));
            title = new Label
            {
                Dock = DockStyle.Fill,
                Text = "用量總覽",
                Font = NativeVisuals.CreateSemiboldFont(15f),
                ForeColor = Palette.PrimaryText,
                TextAlign = ContentAlignment.BottomLeft
            };
            detail = new Label
            {
                Dock = DockStyle.Fill,
                Text = "配額、消耗節奏與下一次重設",
                Font = NativeVisuals.CreateUiFont(8.25f, FontStyle.Regular),
                ForeColor = Palette.MutedText,
                TextAlign = ContentAlignment.TopLeft
            };
            copy.Controls.Add(title, 0, 0);
            copy.Controls.Add(detail, 0, 1);

            var refresh = CreateIconButton("\uE72C", "立即更新資料");
            refresh.Margin = new Padding(Px(4), Px(8), 0, Px(8));
            refresh.Click += delegate { Raise(RefreshRequested); };
            header.Controls.Add(copy, 0, 0);
            header.Controls.Add(refresh, 1, 0);
            return header;
        }

        private Control CreateFooter(Label status)
        {
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Palette.Background,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = new Padding(0, Px(5), 0, 0)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.Controls.Add(status, 0, 0);
            footer.Controls.Add(new Label
            {
                AutoSize = true,
                Text = "資料留存在這台電腦",
                Font = NativeVisuals.CreateUiFont(8.25f, FontStyle.Regular),
                ForeColor = Palette.MutedText,
                TextAlign = ContentAlignment.MiddleRight,
                Margin = new Padding(Px(12), Px(2), 0, 0)
            }, 1, 0);
            return footer;
        }

        private void ShowSelectedPage()
        {
            if (_pages == null || _pages.Length == 0) return;
            var index = Math.Max(0, Math.Min(_pages.Length - 1, _tabs.SelectedIndex));
            for (var pageIndex = 0; pageIndex < _pages.Length; pageIndex++)
            {
                _pages[pageIndex].Visible = pageIndex == index;
            }
            _pages[index].BringToFront();

            var titles = new[] { "用量總覽", "週期歷史", "資料健康", "偏好設定" };
            var details = new[]
            {
                "配額、消耗節奏與下一次重設",
                "比較近期週期與每日平均用量",
                "確認資料來源、更新時間與橋接狀態",
                "控制顯示方式、通知與本機紀錄"
            };
            if (_pageTitle != null) _pageTitle.Text = titles[index];
            if (_pageDetail != null) _pageDetail.Text = details[index];
        }

        private void BuildOverview()
        {
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 7,
                BackColor = Palette.Background,
                Margin = Padding.Empty,
                Padding = new Padding(0, Px(8), 0, 0)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(38)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(42)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(84)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(84)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(84)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(48)));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _recommendation.Dock = DockStyle.Fill;
            _recommendation.Margin = new Padding(0, 0, 0, Px(6));
            layout.Controls.Add(_recommendation, 0, 0);
            layout.Controls.Add(CreateSectionHeading("目前用量", "每週模型總量與使用節奏"), 0, 1);
            _codexRow.Dock = DockStyle.Fill;
            _codexRow.Margin = new Padding(0, 0, 0, Px(6));
            layout.Controls.Add(_codexRow, 0, 2);
            _claudeRow.Dock = DockStyle.Fill;
            _claudeRow.Margin = new Padding(0, 0, 0, Px(6));
            layout.Controls.Add(_claudeRow, 0, 3);
            _antigravityRow.Dock = DockStyle.Fill;
            _antigravityRow.Margin = new Padding(0, 0, 0, Px(6));
            layout.Controls.Add(_antigravityRow, 0, 4);
            var heading = CreateSectionHeading("重設時間軸", "各模型下一次重設時間");
            heading.Margin = new Padding(0, Px(4), 0, 0);
            layout.Controls.Add(heading, 0, 5);
            layout.Controls.Add(_timeline, 0, 6);
            _overviewPage.Controls.Add(layout);
        }

        private void BuildHistory()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(0, 0, 0, Px(2)) };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(42)));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(40)));
            layout.Controls.Add(CreateSectionHeading("週期紀錄", "最近週期的最高用量與平均使用速度"), 0, 0);
            layout.Controls.Add(_history, 0, 1);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, Px(5), 0, 0) };
            var json = CreateActionButton("\uE8A5", "匯出 JSON", Palette.SurfaceRaised);
            json.Click += delegate { Raise(ExportHistoryJsonRequested); };
            var csv = CreateActionButton("\uE8A5", "匯出 CSV", Palette.SurfaceRaised);
            csv.Click += delegate { Raise(ExportHistoryCsvRequested); };
            _toolTip.SetToolTip(json, "匯出本機週期紀錄為 JSON");
            _toolTip.SetToolTip(csv, "匯出本機週期紀錄為 CSV");
            actions.Controls.Add(json);
            actions.Controls.Add(csv);
            layout.Controls.Add(actions, 0, 2);
            _historyPage.Controls.Add(layout);
        }

        private void BuildHealth()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(42)));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(36)));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(40)));
            layout.Controls.Add(CreateSectionHeading("資料健康", "資料來源、更新時間與橋接狀態"), 0, 0);
            layout.Controls.Add(_health, 0, 1);
            layout.Controls.Add(_bridgeStatus, 0, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, Px(5), 0, 0) };
            var repair = CreateActionButton("\uE777", "修復橋接", Palette.SurfaceRaised);
            var copy = CreateActionButton("\uE8C8", "複製診斷", Palette.SurfaceRaised);
            var refresh = CreateActionButton("\uE72C", "重新檢查", Palette.SurfaceRaised);
            repair.Click += delegate { Raise(RepairBridgeRequested); };
            copy.Click += delegate { Raise(ExportDiagnosticsRequested); };
            refresh.Click += delegate { Raise(RefreshRequested); };
            _toolTip.SetToolTip(repair, "重新建立 Claude Code 狀態橋接");
            _toolTip.SetToolTip(copy, "複製不含憑證與本機路徑的診斷報告");
            actions.Controls.Add(repair);
            actions.Controls.Add(copy);
            actions.Controls.Add(refresh);
            layout.Controls.Add(actions, 0, 3);
            _healthPage.Controls.Add(layout);
        }

        private void BuildSettings(
            out CheckBox totalOnly, out CheckBox thresholds, out CheckBox exhaustion, out CheckBox resets,
            out CheckBox codex, out CheckBox claude, out CheckBox antigravity, out CheckBox quietHours,
            out DarkComboBox quietStart, out DarkComboBox quietEnd, out DarkComboBox retention, out CheckBox hotkeys)
        {
            var outer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                BackColor = Palette.Background,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(0, Px(8), 0, Px(4)),
                Margin = Padding.Empty
            };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var left = CreateVerticalLayout();
            var right = CreateVerticalLayout();
            left.Margin = new Padding(0, 0, Px(12), 0);
            right.Margin = new Padding(Px(12), 0, 0, 0);
            outer.Controls.Add(left, 0, 0);
            outer.Controls.Add(right, 1, 0);

            totalOnly = CreateCheckBox("只顯示模型總用量", "收起短期視窗，保留每週模型總量。");
            left.Controls.Add(CreateSectionHeading("顯示與通知", "通知會依照全域通知設定送出"));
            left.Controls.Add(totalOnly);
            thresholds = CreateCheckBox("高用量提醒", "達到設定的警示用量時提醒。");
            exhaustion = CreateCheckBox("即將耗盡提醒", "預計在重設前耗盡時提醒。");
            resets = CreateCheckBox("額度重設提醒", "模型額度重設後提醒。");
            left.Controls.Add(thresholds);
            left.Controls.Add(exhaustion);
            left.Controls.Add(resets);
            var providerLabel = CreateSubLabel("通知模型");
            providerLabel.Margin = new Padding(0, Px(10), 0, Px(2));
            left.Controls.Add(providerLabel);
            codex = CreateCheckBox("Codex", "允許 Codex 通知。");
            claude = CreateCheckBox("Claude Code", "允許 Claude Code 通知。");
            antigravity = CreateCheckBox("Antigravity", "允許 Antigravity 通知。");
            left.Controls.Add(codex);
            left.Controls.Add(claude);
            left.Controls.Add(antigravity);

            right.Controls.Add(CreateSectionHeading("本機與快捷鍵", "所有設定與紀錄僅保存於本機"));
            quietHours = CreateCheckBox("免打擾時段", "暫停通知，不影響背景更新。");
            right.Controls.Add(quietHours);
            var quietRow = new FlowLayoutPanel
            {
                AutoSize = false,
                Height = Px(36),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Palette.Background,
                Margin = new Padding(Px(12), 0, 0, Px(7))
            };
            quietRow.Controls.Add(CreateSubLabel("從"));
            quietStart = CreateTimePicker();
            quietRow.Controls.Add(quietStart);
            quietRow.Controls.Add(CreateSubLabel("到"));
            quietEnd = CreateTimePicker();
            quietRow.Controls.Add(quietEnd);
            right.Controls.Add(quietRow);
            var retentionRow = new FlowLayoutPanel
            {
                AutoSize = false,
                Height = Px(36),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                BackColor = Palette.Background,
                Margin = new Padding(0, Px(7), 0, Px(7))
            };
            retentionRow.Controls.Add(CreateSubLabel("保留歷史"));
            retention = new DarkComboBox(_scale) { Width = Px(82) };
            retention.Items.AddRange(new object[] { "1", "7", "14", "30" });
            retentionRow.Controls.Add(retention);
            retentionRow.Controls.Add(CreateSubLabel("天"));
            right.Controls.Add(retentionRow);
            hotkeys = CreateCheckBox("啟用全域快捷鍵", "Win+Alt+U 開關面板，Win+Alt+R 更新資料；衝突時自動加用 Shift。");
            right.Controls.Add(hotkeys);
            var clear = CreateActionButton("\uE74D", "清除本機資料", Color.FromArgb(66, 40, 39));
            clear.Margin = new Padding(0, Px(12), 0, 0);
            clear.Click += delegate { Raise(ClearDataRequested); };
            _toolTip.SetToolTip(clear, "清除本機儲存的趨勢、週期紀錄與重置紀錄");
            right.Controls.Add(clear);
            var apply = CreateActionButton("\uE73E", "套用設定", Palette.SurfaceRaised);
            apply.Margin = new Padding(0, Px(8), 0, 0);
            apply.Click += ApplyPreferences;
            right.Controls.Add(apply);
            _settingsPage.Controls.Add(outer);
        }

        private void ApplyPreferences(object sender, EventArgs e)
        {
            _preferences.TotalOnly = _totalOnly.Checked;
            _preferences.NotifyThresholds = _thresholds.Checked;
            _preferences.NotifyExhaustion = _exhaustion.Checked;
            _preferences.NotifyReset = _resets.Checked;
            _preferences.NotifyCodex = _codexNotifications.Checked;
            _preferences.NotifyClaude = _claudeNotifications.Checked;
            _preferences.NotifyAntigravity = _antigravityNotifications.Checked;
            _preferences.QuietHoursEnabled = _quietHours.Checked;
            _preferences.QuietStartMinutes = SelectedTimeMinutes(_quietStart);
            _preferences.QuietEndMinutes = SelectedTimeMinutes(_quietEnd);
            int days;
            _preferences.HistoryRetentionDays = int.TryParse(Convert.ToString(_retention.SelectedItem), out days) ? days : 30;
            _preferences.GlobalHotkeysEnabled = _hotkeys.Checked;
            SetStatus("設定已套用");
            Raise(PreferencesChanged);
        }

        private void UpdateQuietHoursState()
        {
            _quietStart.Enabled = _quietHours.Checked;
            _quietEnd.Enabled = _quietHours.Checked;
        }

        private void PopulateTimeline(UsageSnapshot snapshot, UsageInsightReport insights)
        {
            _timeline.BeginUpdate();
            _timeline.Items.Clear();
            if (insights != null && insights.Timeline != null && insights.Timeline.Count > 0)
            {
                foreach (var timelineItem in insights.Timeline)
                {
                    var item = new ListViewItem(timelineItem.Provider);
                    item.SubItems.Add(TimeUtil.ResetLabel(timelineItem.ResetsAt));
                    item.SubItems.Add(string.Format(
                        CultureInfo.CurrentCulture,
                        "剩餘 {0:0}% · {1}",
                        timelineItem.RemainingPercent,
                        timelineItem.Status));
                    _timeline.Items.Add(item);
                }
            }
            else if (snapshot != null)
            {
                AddTimeline(snapshot.Codex, "Codex");
                AddTimeline(snapshot.Claude, "Claude Code");
                AddTimeline(snapshot.Antigravity, "Antigravity");
            }
            _timeline.EndUpdate();
        }

        private void AddTimeline(ProviderSnapshot provider, string name)
        {
            if (provider == null || !provider.IsAvailable || provider.Buckets == null || provider.Buckets.Count == 0)
            {
                var placeholder = new ListViewItem(name);
                placeholder.SubItems.Add("重設時間未知");
                placeholder.SubItems.Add("等待資料");
                _timeline.Items.Add(placeholder);
                return;
            }

            var multiple = provider.Buckets.Count > 1;
            foreach (var bucket in provider.Buckets)
            {
                if (bucket == null)
                {
                    continue;
                }
                var label = multiple && !string.IsNullOrWhiteSpace(bucket.Label)
                    ? name + " · " + bucket.Label
                    : name;
                var item = new ListViewItem(label);
                item.SubItems.Add(TimeUtil.ResetLabel(bucket));
                item.SubItems.Add(UsageText(bucket.UsedPercent));
                _timeline.Items.Add(item);
            }
        }

        private void PopulateHistory(IList<CycleHistoryRecord> cycles)
        {
            _history.BeginUpdate();
            _history.Items.Clear();
            if (cycles != null)
            {
                foreach (var cycle in cycles)
                {
                    var item = new ListViewItem(FirstText(cycle, "Provider", "ProviderName", "Model", "模型", "未知"));
                    item.SubItems.Add(FormatCyclePeriod(cycle));
                    item.SubItems.Add(FirstPercent(cycle, "PeakUsedPercent", "PeakPercent", "Peak", "MaxUsed", "-"));
                    item.SubItems.Add(FormatComparison(cycle, cycles));
                    item.SubItems.Add(FormatDailyAverage(cycle));
                    item.SubItems.Add(cycle.ResetAt.HasValue ? TimeUtil.ResetLabel(cycle.ResetAt) : "-" );
                    _history.Items.Add(item);
                }
            }
            _history.EndUpdate();
        }

        private void PopulateHealth(IList<SourceHealth> health, ClaudeBridgeHealthResult bridge)
        {
            _health.BeginUpdate();
            _health.Items.Clear();
            if (health != null)
            {
                foreach (var source in health)
                {
                    var item = new ListViewItem(FirstText(source, "Name", "Provider", "SourceName", "來源", "資料來源"));
                    item.SubItems.Add(AgeLabel(source.AgeSeconds));
                    item.SubItems.Add(HealthStateLabel(source.State));
                    item.SubItems.Add(SourceLabel(source.Source));
                    item.SubItems.Add(string.IsNullOrWhiteSpace(source.Error) ? source.Status : source.Error);
                    _health.Items.Add(item);
                }
            }
            _health.EndUpdate();
            _bridgeStatus.Text = "Claude 橋接：" + BridgeLabel(bridge);
        }

        private static string HealthStateLabel(string state)
        {
            switch ((state ?? string.Empty).ToLowerInvariant())
            {
                case "healthy": return "正常";
                case "stale": return "已過期";
                case "waiting-sync": return "待同步";
                case "unavailable": return "無法使用";
                case "missing": return "缺少資料";
                case "unknown-age": return "時間未知";
                default: return string.IsNullOrWhiteSpace(state) ? "未知" : state;
            }
        }

        private static string SourceLabel(string source)
        {
            switch (source ?? string.Empty)
            {
                case "CodexAppServer": return "Codex 官方";
                case "CodexSessionLog": return "Codex 本機紀錄";
                case "ClaudeDesktop": return "Claude Desktop";
                case "AntigravityServer": return "Antigravity 伺服器";
                case "Antigravity": return "Antigravity";
                default: return string.IsNullOrWhiteSpace(source) ? "未知" : source;
            }
        }

        private static string BridgeLabel(ClaudeBridgeHealthResult bridge)
        {
            if (bridge == null) return "尚未檢查";
            switch (bridge.State)
            {
                case ClaudeBridgeState.Ready: return "已就緒";
                case ClaudeBridgeState.WaitingForData: return "已設定，等待 Claude Code 回報";
                case ClaudeBridgeState.MissingSettings: return "找不到 Claude Code 設定";
                case ClaudeBridgeState.MissingStatusLine: return "尚未設定狀態列";
                case ClaudeBridgeState.MissingBridge: return "橋接檔案遺失";
                case ClaudeBridgeState.CustomStatusLine: return "已保留自訂狀態列";
                case ClaudeBridgeState.InvalidSettings: return "設定檔無法讀取";
                default: return "檢查失敗";
            }
        }

        private static UsageBucket PrimaryBucket(ProviderSnapshot provider)
        {
            return UsageSelection.SummaryBucket(provider);
        }

        private static string UsageText(double used)
        {
            return string.Format(CultureInfo.CurrentCulture, "已用 {0:0}% · 剩餘 {1:0}%", used, Math.Max(0, 100 - used));
        }

        private static string FormatCyclePeriod(CycleHistoryRecord cycle)
        {
            if (cycle == null || cycle.CycleStartAt == DateTimeOffset.MinValue) return "-";
            var start = cycle.CycleStartAt.ToLocalTime();
            if (!cycle.CycleEndAt.HasValue) return start.ToString("MM/dd HH:mm", CultureInfo.CurrentCulture) + " 起";
            return string.Format(CultureInfo.CurrentCulture, "{0:MM/dd} - {1:MM/dd}", start, cycle.CycleEndAt.Value.ToLocalTime());
        }

        private static string FormatDailyAverage(CycleHistoryRecord cycle)
        {
            if (cycle == null || cycle.CycleStartAt == DateTimeOffset.MinValue) return "-";
            var end = cycle.CycleEndAt ?? cycle.ObservedAt;
            var days = Math.Max(1d, (end - cycle.CycleStartAt).TotalDays);
            return string.Format(CultureInfo.CurrentCulture, "{0:0.0}%", cycle.LastUsedPercent / days);
        }

        private static string FormatComparison(CycleHistoryRecord cycle, IEnumerable<CycleHistoryRecord> cycles)
        {
            if (cycle == null || cycles == null) return "-";
            var previous = cycles
                .Where(item => item != null
                    && string.Equals(item.Provider, cycle.Provider, StringComparison.OrdinalIgnoreCase)
                    && item.CycleStartAt < cycle.CycleStartAt)
                .OrderByDescending(item => item.CycleStartAt)
                .FirstOrDefault();
            if (previous == null) return "-";
            var delta = cycle.PeakUsedPercent - previous.PeakUsedPercent;
            return string.Format(CultureInfo.CurrentCulture, "{0}{1:0}%", delta > 0 ? "+" : string.Empty, delta);
        }

        private static string AgeLabel(long seconds)
        {
            if (seconds < 0) return "時間未知";
            if (seconds < 60) return "剛剛";
            if (seconds < 3600) return string.Format(CultureInfo.CurrentCulture, "{0} 分前", seconds / 60);
            if (seconds < 86400) return string.Format(CultureInfo.CurrentCulture, "{0} 小時前", seconds / 3600);
            return string.Format(CultureInfo.CurrentCulture, "{0} 天前", seconds / 86400);
        }

        private Panel CreatePage()
        {
            return new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = Palette.Background,
                Padding = Padding.Empty,
                Margin = Padding.Empty
            };
        }

        private FlowLayoutPanel CreateVerticalLayout()
        {
            var layout = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Palette.Background,
                Padding = Padding.Empty
            };
            EventHandler resizeChildren = delegate
            {
                var width = Math.Max(Px(120), layout.ClientSize.Width - layout.Padding.Horizontal - Px(3));
                foreach (Control child in layout.Controls)
                {
                    child.Width = Math.Max(Px(80), width - child.Margin.Horizontal);
                }
            };
            layout.ControlAdded += delegate { resizeChildren(layout, EventArgs.Empty); };
            layout.SizeChanged += resizeChildren;
            return layout;
        }

        private Label CreateBandLabel()
        {
            return new Label
            {
                AutoSize = false,
                Height = Px(32),
                Dock = DockStyle.Top,
                BackColor = Palette.BandHighlight,
                ForeColor = Palette.SecondaryText,
                Font = NativeVisuals.CreateSemiboldFont(8.75f),
                Padding = new Padding(Px(12), 0, Px(12), 0),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = "建議：等待資料"
            };
        }

        private Control CreateSectionHeading(string title, string detail)
        {
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Palette.Background, ColumnCount = 1, RowCount = 2, Height = Px(42), Margin = Padding.Empty };
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(24)));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(18)));
            panel.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, ForeColor = Palette.PrimaryText, Font = NativeVisuals.CreateSemiboldFont(10f), TextAlign = ContentAlignment.BottomLeft }, 0, 0);
            panel.Controls.Add(new Label { Text = detail, Dock = DockStyle.Fill, ForeColor = Palette.MutedText, Font = NativeVisuals.CreateUiFont(8.25f, FontStyle.Regular), TextAlign = ContentAlignment.TopLeft }, 0, 1);
            return panel;
        }

        private ProfessionalListView CreateListView(params string[] columns)
        {
            var weights = columns.Length == 3
                ? new[] { 38, 30, 32 } // 時間軸：模型欄需容納「供應商 · 額度名稱」。
                : columns.Length == 6
                    ? new[] { 18, 24, 10, 12, 14, 22 }
                    : new[] { 15, 14, 13, 18, 40 };
            var list = new ProfessionalListView(_scale, weights) { Dock = DockStyle.Fill };
            foreach (var column in columns) list.Columns.Add(column, Px(100), HorizontalAlignment.Left);
            list.ReflowColumns();
            return list;
        }

        private Button CreateActionButton(string glyph, string text, Color background)
        {
            var button = new Button
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Height = Px(30),
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 1, BorderColor = Palette.BorderStrong, MouseOverBackColor = Palette.Track, MouseDownBackColor = Palette.BandHighlight },
                BackColor = background,
                ForeColor = Palette.PrimaryText,
                Font = NativeVisuals.CreateUiFont(8.75f, FontStyle.Regular),
                Text = text,
                Image = CreateGlyphBitmap(glyph, 17),
                ImageAlign = ContentAlignment.MiddleLeft,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                Padding = new Padding(Px(8), Px(3), Px(9), Px(3)),
                UseVisualStyleBackColor = false,
                AccessibleName = text
            };
            return button;
        }

        private Button CreateIconButton(string glyph, string toolTip)
        {
            var button = new Button
            {
                Dock = DockStyle.Fill,
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0, MouseOverBackColor = Palette.SurfaceRaised, MouseDownBackColor = Palette.Track },
                BackColor = Palette.Background,
                ForeColor = Palette.PrimaryText,
                Image = CreateGlyphBitmap(glyph, 18),
                ImageAlign = ContentAlignment.MiddleCenter,
                UseVisualStyleBackColor = false,
                AccessibleName = toolTip,
                TabStop = true
            };
            _toolTip.SetToolTip(button, toolTip);
            return button;
        }

        private Bitmap CreateGlyphBitmap(string glyph, int logicalSize)
        {
            var size = Px(logicalSize);
            var bitmap = new Bitmap(size, size);
            bitmap.SetResolution(96f * _scale, 96f * _scale);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var font = NativeVisuals.CreateGlyphFont(Px(logicalSize - 3), GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(Palette.SecondaryText))
            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                graphics.Clear(Color.Transparent);
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                graphics.DrawString(glyph, font, brush, new RectangleF(0, 0, size, size), format);
            }
            return bitmap;
        }

        private CheckBox CreateCheckBox(string text, string toolTip)
        {
            var box = new SettingToggle(text, _scale)
            {
                Height = Px(40),
                Margin = new Padding(0, Px(2), 0, Px(2))
            };
            box.AccessibleDescription = toolTip;
            _toolTip.SetToolTip(box, toolTip);
            if (text == "免打擾時段") box.CheckedChanged += delegate { UpdateQuietHoursState(); };
            return box;
        }

        private Label CreateSubLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                ForeColor = Palette.SecondaryText,
                Font = NativeVisuals.CreateUiFont(8.25f, FontStyle.Regular),
                Margin = new Padding(0, Px(8), Px(6), 0)
            };
        }

        private DarkComboBox CreateTimePicker()
        {
            var picker = new DarkComboBox(_scale) { Width = Px(72) };
            for (var minutes = 0; minutes < 1440; minutes += 30)
            {
                picker.Items.Add(string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", minutes / 60, minutes % 60));
            }
            return picker;
        }

        private static void SelectTime(ComboBox comboBox, int minutes)
        {
            var clamped = Math.Max(0, Math.Min(1439, minutes));
            var text = string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", clamped / 60, clamped % 60);
            var index = comboBox.Items.IndexOf(text);
            if (index < 0)
            {
                comboBox.Items.Add(text);
                index = comboBox.Items.IndexOf(text);
            }
            comboBox.SelectedIndex = index;
        }

        private static int SelectedTimeMinutes(ComboBox comboBox)
        {
            var text = Convert.ToString(comboBox.SelectedItem, CultureInfo.InvariantCulture) ?? "00:00";
            var parts = text.Split(':');
            int hour;
            int minute;
            return parts.Length == 2
                && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)
                && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minute)
                ? Math.Max(0, Math.Min(1439, (hour * 60) + minute))
                : 0;
        }

        private int Px(int logicalPixels)
        {
            return Math.Max(1, (int)Math.Round(logicalPixels * _scale));
        }

        private static string FormatTimestamp(DateTimeOffset value)
        {
            return value == DateTimeOffset.MinValue ? "時間未知" : value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
        }

        private void Raise(EventHandler handler)
        {
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private static string FirstPercent(object source, params string[] names)
        {
            object value = FindProperty(source, names);
            if (value == null) return names[names.Length - 1] == "-" ? "-" : "-";
            double percent;
            if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out percent))
            {
                return string.Format(CultureInfo.CurrentCulture, "{0:0}%", percent);
            }
            return Convert.ToString(value, CultureInfo.CurrentCulture);
        }

        private static string FirstText(object source, params string[] names)
        {
            object value = FindProperty(source, names);
            if (value == null) return names.Length == 0 ? string.Empty : names[names.Length - 1];
            var date = value as DateTimeOffset?;
            if (date.HasValue) return FormatTimestamp(date.Value);
            if (value is DateTimeOffset) return FormatTimestamp((DateTimeOffset)value);
            if (value is DateTime) return ((DateTime)value).ToString("g", CultureInfo.CurrentCulture);
            return Convert.ToString(value, CultureInfo.CurrentCulture);
        }

        private static object FindProperty(object source, string[] names)
        {
            if (source == null) return null;
            var type = source.GetType();
            foreach (var name in names)
            {
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (property != null)
                {
                    try { return property.GetValue(source, null); } catch { return null; }
                }
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (field != null)
                {
                    try { return field.GetValue(source); } catch { return null; }
                }
            }
            return null;
        }

        private sealed class CenterNavigation : Panel
        {
            private readonly float _scale;
            private readonly List<NavigationButton> _buttons = new List<NavigationButton>();
            private int _selectedIndex;

            public CenterNavigation(float scale, string[] labels, string[] glyphs)
            {
                _scale = Math.Max(1f, scale);
                BackColor = Palette.HeaderSurface;
                AccessibleName = "用量中心分頁";
                AccessibleRole = AccessibleRole.PageTabList;
                for (var index = 0; index < labels.Length; index++)
                {
                    var captured = index;
                    var button = new NavigationButton(labels[index], glyphs[index], _scale)
                    {
                        TabIndex = index,
                        Selected = index == 0
                    };
                    button.Click += delegate { SelectedIndex = captured; };
                    button.KeyDown += delegate(object sender, KeyEventArgs args)
                    {
                        if (args.KeyCode != Keys.Up && args.KeyCode != Keys.Down) return;
                        var delta = args.KeyCode == Keys.Up ? -1 : 1;
                        SelectedIndex = (_selectedIndex + delta + _buttons.Count) % _buttons.Count;
                        _buttons[_selectedIndex].Focus();
                        args.Handled = true;
                    };
                    _buttons.Add(button);
                    Controls.Add(button);
                }
                LayoutButtons();
            }

            public event EventHandler SelectedIndexChanged;

            public int TabCount
            {
                get { return _buttons.Count; }
            }

            public int SelectedIndex
            {
                get { return _selectedIndex; }
                set
                {
                    if (_buttons.Count == 0) return;
                    var next = Math.Max(0, Math.Min(_buttons.Count - 1, value));
                    if (_selectedIndex == next && _buttons[next].Selected) return;
                    _selectedIndex = next;
                    for (var index = 0; index < _buttons.Count; index++)
                    {
                        _buttons[index].Selected = index == next;
                    }
                    var handler = SelectedIndexChanged;
                    if (handler != null) handler(this, EventArgs.Empty);
                }
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                LayoutButtons();
            }

            private void LayoutButtons()
            {
                for (var index = 0; index < _buttons.Count; index++)
                {
                    _buttons[index].SetBounds(Px(8), Px(8 + (index * 44)), Math.Max(Px(80), Width - Px(16)), Px(40));
                }
            }

            private int Px(int value)
            {
                return Math.Max(1, (int)Math.Round(value * _scale));
            }
        }

        private sealed class NavigationButton : Control
        {
            private readonly string _glyph;
            private readonly float _scale;
            private readonly Font _glyphFont;
            private readonly Font _labelFont;
            private bool _hovered;
            private bool _selected;

            public NavigationButton(string label, string glyph, float scale)
            {
                _glyph = glyph;
                _scale = Math.Max(1f, scale);
                _glyphFont = NativeVisuals.CreateGlyphFont(Px(14), GraphicsUnit.Pixel);
                _labelFont = NativeVisuals.CreateSemiboldFont(9f);
                Text = label;
                TabStop = true;
                BackColor = Palette.HeaderSurface;
                ForeColor = Palette.SecondaryText;
                AccessibleName = label;
                AccessibleRole = AccessibleRole.PageTab;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            }

            public bool Selected
            {
                get { return _selected; }
                set
                {
                    if (_selected == value) return;
                    _selected = value;
                    Invalidate();
                }
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                _hovered = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hovered = false;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                Focus();
                base.OnMouseDown(e);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    OnClick(EventArgs.Empty);
                    e.Handled = true;
                }
                base.OnKeyDown(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var background = _selected ? Palette.Surface : _hovered ? Palette.SurfaceRaised : Palette.HeaderSurface;
                using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, ClientRectangle);

                var foreground = Enabled
                    ? (_selected ? Palette.PrimaryText : Palette.SecondaryText)
                    : Palette.MutedText;
                var glyphColor = Enabled && _selected ? Palette.Focus : foreground;
                TextRenderer.DrawText(e.Graphics, _glyph, _glyphFont,
                    new Rectangle(Px(13), 0, Px(22), Height), glyphColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(e.Graphics, Text, _labelFont,
                    new Rectangle(Px(44), 0, Math.Max(Px(20), Width - Px(54)), Height), foreground,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (Focused && ShowFocusCues)
                {
                    var focus = new Rectangle(Px(6), Px(4), Math.Max(1, Width - Px(12)), Math.Max(1, Height - Px(8)));
                    ControlPaint.DrawFocusRectangle(e.Graphics, focus, foreground, background);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _glyphFont.Dispose();
                    _labelFont.Dispose();
                }
                base.Dispose(disposing);
            }

            private int Px(int value)
            {
                return Math.Max(1, (int)Math.Round(value * _scale));
            }
        }

        private sealed class ProfessionalListView : ListView
        {
            private const int WmPaint = 0x000F;
            private const int WmPrint = 0x0317;
            private const int WmPrintClient = 0x0318;
            private readonly float _scale;
            private readonly int[] _weights;
            private readonly Font _headerFont;
            private readonly Font _bodyFont;
            private readonly Font _strongFont;
            private readonly ImageList _rowHeight;

            public ProfessionalListView(float scale, int[] weights)
            {
                _scale = Math.Max(1f, scale);
                _weights = weights ?? new int[0];
                _headerFont = NativeVisuals.CreateSemiboldFont(8.25f);
                _bodyFont = NativeVisuals.CreateUiFont(8.75f, FontStyle.Regular);
                _strongFont = NativeVisuals.CreateSemiboldFont(8.75f);
                _rowHeight = new ImageList { ImageSize = new Size(1, Px(29)), ColorDepth = ColorDepth.Depth32Bit };
                SmallImageList = _rowHeight;
                Dock = DockStyle.Fill;
                View = View.Details;
                FullRowSelect = true;
                GridLines = false;
                HeaderStyle = ColumnHeaderStyle.Nonclickable;
                MultiSelect = false;
                HideSelection = false;
                OwnerDraw = true;
                BackColor = Palette.ProviderSurface;
                EmptyAreaColor = Palette.ProviderSurface;
                ForeColor = Palette.PrimaryText;
                BorderStyle = BorderStyle.None;
                Font = _bodyFont;
                SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
            }

            public Color EmptyAreaColor { get; set; }

            public void ReflowColumns()
            {
                if (Columns.Count == 0 || ClientSize.Width <= 0) return;
                var dataColumnCount = Math.Min(_weights.Length, Columns.Count);
                var available = Math.Max(Px(120), ClientSize.Width);
                var totalWeight = Math.Max(1, _weights.Sum());
                var assigned = 0;
                for (var index = 0; index < dataColumnCount; index++)
                {
                    var width = index == dataColumnCount - 1
                        ? Math.Max(Px(54), available - assigned)
                        : Math.Max(Px(54), (int)Math.Floor(available * (_weights[index] / (double)totalWeight)));
                    Columns[index].Width = width;
                    assigned += width;
                }
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                NativeControlTheme.ApplyDark(Handle);
                NativeControlTheme.ApplyDark(NativeControlTheme.GetListHeader(Handle));
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                ReflowColumns();
            }

            protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
            {
                if (SystemInformation.HighContrast)
                {
                    e.DrawDefault = true;
                    return;
                }
                using (var background = new SolidBrush(Palette.SurfaceRaised)) e.Graphics.FillRectangle(background, e.Bounds);
                using (var divider = new Pen(Palette.Border)) e.Graphics.DrawLine(divider, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                var textBounds = Rectangle.Inflate(e.Bounds, -Px(10), 0);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, _headerFont, textBounds, Palette.SecondaryText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if (e.ColumnIndex == Columns.Count - 1)
                {
                    var state = e.Graphics.Save();
                    e.Graphics.SetClip(new Rectangle(e.Bounds.Right - 1, e.Bounds.Top, Px(6), e.Bounds.Height), CombineMode.Replace);
                    using (var remainder = new SolidBrush(Palette.SurfaceRaised))
                    {
                        e.Graphics.FillRectangle(remainder, e.Bounds.Right - 1, e.Bounds.Top, Px(6), e.Bounds.Height);
                    }
                    e.Graphics.Restore(state);
                }
            }

            protected override void OnDrawItem(DrawListViewItemEventArgs e)
            {
                if (View != View.Details || SystemInformation.HighContrast) e.DrawDefault = true;
            }

            protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
            {
                if (SystemInformation.HighContrast)
                {
                    e.DrawDefault = true;
                    return;
                }
                var selected = e.Item.Selected;
                using (var background = new SolidBrush(selected ? Palette.BandHighlight : Palette.ProviderSurface))
                {
                    e.Graphics.FillRectangle(background, e.Bounds);
                }
                var color = SemanticTextColor(e.SubItem.Text, selected);
                var font = e.ColumnIndex == 0 ? _strongFont : _bodyFont;
                var textBounds = Rectangle.Inflate(e.Bounds, -Px(10), 0);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, font, textBounds, color,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                using (var divider = new Pen(Palette.Border))
                {
                    e.Graphics.DrawLine(divider, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                }
            }

            protected override void WndProc(ref Message message)
            {
                base.WndProc(ref message);
                if (!IsHandleCreated || SystemInformation.HighContrast) return;
                if (message.Msg == WmPaint)
                {
                    using (var graphics = Graphics.FromHwnd(Handle)) PaintUnusedArea(graphics);
                }
                else if ((message.Msg == WmPrint || message.Msg == WmPrintClient) && message.WParam != IntPtr.Zero)
                {
                    using (var graphics = Graphics.FromHdc(message.WParam)) PaintUnusedArea(graphics);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _headerFont.Dispose();
                    _bodyFont.Dispose();
                    _strongFont.Dispose();
                    _rowHeight.Dispose();
                }
                base.Dispose(disposing);
            }

            private static Color SemanticTextColor(string text, bool selected)
            {
                if (selected) return Palette.PrimaryText;
                var value = text ?? string.Empty;
                if (value.Contains("正常") || value.Contains("就緒")) return Palette.Success;
                if (value.Contains("過期") || value.Contains("待同步") || value.Contains("失敗") || value.Contains("無法") || value.Contains("缺少")) return Palette.Warning;
                return Palette.PrimaryText;
            }

            private void PaintUnusedArea(Graphics graphics)
            {
                var top = Px(27);
                if (Items.Count > 0)
                {
                    try { top = GetItemRect(Items.Count - 1).Bottom; } catch { }
                }
                if (top >= ClientSize.Height) return;
                using (var background = new SolidBrush(EmptyAreaColor))
                {
                    graphics.FillRectangle(background, 0, top, ClientSize.Width, ClientSize.Height - top);
                }
            }

            private int Px(int value)
            {
                return Math.Max(1, (int)Math.Round(value * _scale));
            }
        }

        private sealed class SettingToggle : CheckBox
        {
            private readonly float _scale;
            private readonly Font _labelFont;
            private bool _hovered;

            public SettingToggle(string text, float scale)
            {
                _scale = Math.Max(1f, scale);
                _labelFont = NativeVisuals.CreateUiFont(8.75f, FontStyle.Regular);
                Text = text;
                Appearance = Appearance.Button;
                AutoSize = false;
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                FlatAppearance.CheckedBackColor = Palette.Background;
                BackColor = Palette.Background;
                ForeColor = Palette.PrimaryText;
                UseVisualStyleBackColor = false;
                AccessibleName = text;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                _hovered = true;
                Invalidate();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hovered = false;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var background = _hovered ? Palette.Surface : Palette.Background;
                using (var fill = new SolidBrush(background)) e.Graphics.FillRectangle(fill, ClientRectangle);
                using (var divider = new Pen(Palette.Border)) e.Graphics.DrawLine(divider, 0, Height - 1, Width, Height - 1);

                var foreground = Enabled ? Palette.PrimaryText : Palette.MutedText;
                TextRenderer.DrawText(e.Graphics, Text, _labelFont,
                    new Rectangle(Px(4), 0, Math.Max(Px(40), Width - Px(54)), Height), foreground,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                var track = new Rectangle(Math.Max(Px(8), Width - Px(42)), (Height - Px(20)) / 2, Px(36), Px(20));
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var path = NativeVisuals.RoundedRectangle(track, track.Height / 2))
                using (var trackBrush = new SolidBrush(Checked && Enabled ? Palette.Focus : Palette.TrackBorder))
                {
                    e.Graphics.FillPath(trackBrush, path);
                }
                var knobSize = Px(14);
                var knobX = Checked ? track.Right - knobSize - Px(3) : track.Left + Px(3);
                using (var knob = new SolidBrush(Enabled ? Palette.PrimaryText : Palette.MutedText))
                {
                    e.Graphics.FillEllipse(knob, knobX, track.Top + Px(3), knobSize, knobSize);
                }
                e.Graphics.SmoothingMode = SmoothingMode.None;
                if (Focused && ShowFocusCues)
                {
                    ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -Px(2), -Px(2)), foreground, background);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _labelFont.Dispose();
                base.Dispose(disposing);
            }

            private int Px(int value)
            {
                return Math.Max(1, (int)Math.Round(value * _scale));
            }
        }

        private sealed class DarkComboBox : ComboBox
        {
            private const int WmPaint = 0x000F;
            private const int WmPrint = 0x0317;
            private const int WmPrintClient = 0x0318;
            private readonly float _scale;
            private readonly Font _itemFont;
            private readonly Font _glyphFont;

            public DarkComboBox(float scale)
            {
                _scale = Math.Max(1f, scale);
                _itemFont = NativeVisuals.CreateUiFont(8.75f, FontStyle.Regular);
                _glyphFont = NativeVisuals.CreateGlyphFont(Px(10), GraphicsUnit.Pixel);
                DropDownStyle = ComboBoxStyle.DropDownList;
                DrawMode = DrawMode.OwnerDrawFixed;
                FlatStyle = FlatStyle.Flat;
                BackColor = Palette.SurfaceRaised;
                ForeColor = Palette.PrimaryText;
                Font = _itemFont;
                ItemHeight = Px(22);
                IntegralHeight = false;
                DropDownHeight = Px(220);
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                NativeControlTheme.ApplyDark(Handle);
            }

            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                if (e.Index < 0) return;
                var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
                using (var background = new SolidBrush(selected ? Palette.BandHighlight : Palette.SurfaceRaised))
                {
                    e.Graphics.FillRectangle(background, e.Bounds);
                }
                var bounds = Rectangle.Inflate(e.Bounds, -Px(7), 0);
                TextRenderer.DrawText(e.Graphics, Convert.ToString(Items[e.Index], CultureInfo.CurrentCulture), _itemFont,
                    bounds, Enabled ? Palette.PrimaryText : Palette.MutedText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                if ((e.State & DrawItemState.Focus) == DrawItemState.Focus) e.DrawFocusRectangle();
            }

            protected override void WndProc(ref Message message)
            {
                base.WndProc(ref message);
                if (!IsHandleCreated) return;
                if (message.Msg == WmPaint)
                {
                    using (var graphics = Graphics.FromHwnd(Handle)) PaintDropButton(graphics);
                }
                else if ((message.Msg == WmPrint || message.Msg == WmPrintClient) && message.WParam != IntPtr.Zero)
                {
                    using (var graphics = Graphics.FromHdc(message.WParam)) PaintDropButton(graphics);
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) _itemFont.Dispose();
                if (disposing) _glyphFont.Dispose();
                base.Dispose(disposing);
            }

            private void PaintDropButton(Graphics graphics)
            {
                var width = Px(21);
                var bounds = new Rectangle(Math.Max(0, ClientSize.Width - width), 1, width - 1, Math.Max(1, ClientSize.Height - 2));
                using (var background = new SolidBrush(Enabled ? Palette.SurfaceRaised : Palette.Surface))
                using (var divider = new Pen(Palette.BorderStrong))
                {
                    graphics.FillRectangle(background, bounds);
                    graphics.DrawLine(divider, bounds.Left, bounds.Top + Px(4), bounds.Left, bounds.Bottom - Px(4));
                }
                TextRenderer.DrawText(graphics, "\uE70D", _glyphFont, bounds,
                    Enabled ? Palette.SecondaryText : Palette.MutedText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }

            private int Px(int value)
            {
                return Math.Max(1, (int)Math.Round(value * _scale));
            }
        }

        private static class NativeControlTheme
        {
            private const int LvmGetHeader = 0x101F;

            public static void ApplyDark(IntPtr handle)
            {
                if (handle == IntPtr.Zero) return;
                try { SetWindowTheme(handle, "DarkMode_Explorer", null); } catch { }
            }

            public static IntPtr GetListHeader(IntPtr listHandle)
            {
                if (listHandle == IntPtr.Zero) return IntPtr.Zero;
                try { return SendMessage(listHandle, LvmGetHeader, IntPtr.Zero, IntPtr.Zero); } catch { return IntPtr.Zero; }
            }

            [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
            private static extern int SetWindowTheme(IntPtr handle, string subAppName, string subIdList);

            [DllImport("user32.dll")]
            private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
        }

        private sealed class ProviderUsageRow : Control
        {
            private readonly BrandIconKind _icon;
            private readonly string _name;
            private readonly Color _accent;
            private readonly float _scale;
            private readonly Font _nameFont;
            private readonly Font _valueFont;
            private readonly Font _labelFont;
            private readonly Font _detailFont;
            private ProviderSnapshot _provider;
            private ProviderInsight _insight;

            public ProviderUsageRow(BrandIconKind icon, string name, Color accent, float scale)
            {
                _icon = icon;
                _name = name;
                _accent = accent;
                _scale = Math.Max(1f, scale);
                _nameFont = NativeVisuals.CreateSemiboldFont(10.5f);
                _valueFont = NativeVisuals.CreateValueFont(11f, FontStyle.Bold);
                _labelFont = NativeVisuals.CreateUiFont(8.75f, FontStyle.Regular);
                _detailFont = NativeVisuals.CreateUiFont(8.25f, FontStyle.Regular);
                BackColor = Palette.Background;
                ForeColor = Palette.PrimaryText;
                DoubleBuffered = true;
                Margin = new Padding(0, 0, 0, Px(8));
                AccessibleRole = AccessibleRole.StaticText;
                AccessibleName = name + " 用量";
            }

            public void SetProvider(ProviderSnapshot provider)
            {
                _provider = provider;
                UpdateAccessibility();
                Invalidate();
            }

            public void SetInsight(ProviderInsight insight)
            {
                _insight = insight;
                UpdateAccessibility();
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var bucket = PrimaryBucket(_provider);
                var used = _insight == null
                    ? (bucket == null ? 0 : Math.Max(0, Math.Min(100, bucket.UsedPercent)))
                    : Math.Max(0, Math.Min(100, _insight.UsedPercent));
                var remaining = _insight == null ? Math.Max(0, 100 - used) : _insight.RemainingPercent;
                var contentLeft = Px(56);
                var contentRight = Width - Px(14);
                var bar = new Rectangle(contentLeft, Px(47), Math.Max(Px(20), contentRight - contentLeft), Px(5));
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                NativeVisuals.DrawBrandIcon(e.Graphics, _icon, new Rectangle(Px(14), Px(17), Px(28), Px(28)));
                using (var track = new SolidBrush(Palette.Track))
                using (var fill = new SolidBrush(used >= 95 ? Palette.Critical : used >= 80 ? Palette.Warning : _accent))
                {
                    TextRenderer.DrawText(e.Graphics, _name, _nameFont,
                        new Rectangle(contentLeft, Px(9), Math.Max(Px(80), Width / 2 - contentLeft), Px(24)),
                        Palette.PrimaryText,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    var usedText = bucket == null && _insight == null
                        ? "等待資料"
                        : string.Format(CultureInfo.CurrentCulture, "已用 {0:0}%", used);
                    TextRenderer.DrawText(e.Graphics, usedText, _labelFont,
                        new Rectangle(contentLeft, Px(29), Math.Max(Px(80), Width / 2 - contentLeft), Px(17)),
                        Palette.SecondaryText,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

                    var percentText = string.Format(CultureInfo.CurrentCulture, "{0:0}%", remaining);
                    var percentSize = TextRenderer.MeasureText(percentText, _valueFont, Size.Empty, TextFormatFlags.NoPadding);
                    var labelSize = TextRenderer.MeasureText("剩餘", _labelFont, Size.Empty, TextFormatFlags.NoPadding);
                    var percentLeft = Math.Max(contentLeft + Px(120), contentRight - percentSize.Width);
                    var amountColor = used >= 95 ? Palette.Critical : used >= 80 ? Palette.Warning : Palette.PrimaryText;
                    TextRenderer.DrawText(e.Graphics, "剩餘", _labelFont,
                        new Rectangle(Math.Max(contentLeft, percentLeft - labelSize.Width - Px(6)), Px(9), labelSize.Width, Px(27)),
                        Palette.SecondaryText,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(e.Graphics, percentText, _valueFont,
                        new Rectangle(percentLeft, Px(9), percentSize.Width, Px(27)), amountColor,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

                    using (var trackPath = NativeVisuals.RoundedRectangle(bar, Math.Max(1, bar.Height / 2)))
                    {
                        e.Graphics.FillPath(track, trackPath);
                    }
                    var fillWidth = (int)Math.Round(bar.Width * used / 100d);
                    if (fillWidth > 0)
                    {
                        var fillBounds = new Rectangle(bar.Left, bar.Top, Math.Max(1, fillWidth), bar.Height);
                        using (var fillPath = NativeVisuals.RoundedRectangle(fillBounds, Math.Max(1, Math.Min(fillBounds.Width, fillBounds.Height) / 2)))
                        {
                            e.Graphics.FillPath(fill, fillPath);
                        }
                    }
                    var reset = _insight != null
                        ? TimeUtil.ResetLabel(_insight.ResetsAt)
                        : (bucket == null ? "重設時間未知" : TimeUtil.ResetLabel(bucket));
                    var pace = _insight != null
                        ? (_insight.IsFresh
                            ? string.Format(CultureInfo.CurrentCulture, "{0} · 每日 {1:0.0}%", _insight.Status, _insight.DailyAllowancePercent)
                            : _insight.Status)
                        : (bucket == null ? "節奏未知" : PaceText(bucket));
                    var resetWidth = Math.Max(Px(90), (contentRight - contentLeft) * 3 / 5);
                    TextRenderer.DrawText(e.Graphics, reset, _detailFont,
                        new Rectangle(contentLeft, Px(57), resetWidth, Px(19)), Palette.MutedText,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(e.Graphics, pace, _detailFont,
                        new Rectangle(contentLeft + resetWidth, Px(57), Math.Max(Px(30), contentRight - contentLeft - resetWidth), Px(19)), Palette.MutedText,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
                }
                using (var divider = new Pen(Palette.Border))
                {
                    e.Graphics.DrawLine(divider, contentLeft, Height - 1, contentRight, Height - 1);
                }
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _nameFont.Dispose();
                    _valueFont.Dispose();
                    _labelFont.Dispose();
                    _detailFont.Dispose();
                }
                base.Dispose(disposing);
            }

            private void UpdateAccessibility()
            {
                var bucket = PrimaryBucket(_provider);
                var used = _insight == null
                    ? (bucket == null ? 0 : bucket.UsedPercent)
                    : _insight.UsedPercent;
                AccessibleDescription = bucket == null && _insight == null
                    ? _name + " 等待資料"
                    : string.Format(CultureInfo.CurrentCulture, "{0} 已用 {1:0}%，剩餘 {2:0}%", _name, used, Math.Max(0, 100 - used));
            }

            private int Px(int value)
            {
                return Math.Max(1, (int)Math.Round(value * _scale));
            }

            private static string PaceText(UsageBucket bucket)
            {
                if (bucket.ProjectedExhaustAt.HasValue) return "消耗偏快";
                if (bucket.WindowDurationMinutes >= 1440)
                {
                    var days = Math.Max(1d, bucket.WindowDurationMinutes / 1440d);
                    return string.Format(CultureInfo.CurrentCulture, "約 {0:0.0}% / 日", bucket.UsedPercent / days);
                }
                return "節奏正常";
            }
        }
    }
}
