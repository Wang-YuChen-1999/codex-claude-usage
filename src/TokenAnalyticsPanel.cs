using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexClaudeUsage
{
    internal sealed class TokenAnalyticsPanel : Panel
    {
        private readonly float _scale;
        private readonly UserPreferences _preferences;
        private readonly ToolTip _tips;
        private readonly TableLayoutPanel _layout;
        private readonly UsageCenterForm.DarkComboBox _period;
        private readonly DateTimePicker _start;
        private readonly DateTimePicker _end;
        private readonly UsageCenterForm.DarkComboBox _provider;
        private readonly UsageCenterForm.DarkComboBox _model;
        private readonly UsageCenterForm.DarkComboBox _project;
        private readonly Label _coverage;
        private readonly Label _total;
        private readonly Label _comparison;
        private readonly TokenTrendControl _trend;
        private readonly TokenBreakdownControl _breakdown;
        private readonly UsageCenterForm.ProfessionalListView _models;
        private readonly UsageCenterForm.ProfessionalListView _projects;
        private readonly NumericUpDown _budget;
        private readonly CheckBox _notifyBudget;
        private readonly Label _budgetStatus;
        private readonly TokenBudgetBar _budgetBar;
        private readonly Button _export;
        private TokenAnalyticsSnapshot _snapshot = new TokenAnalyticsSnapshot();
        private List<TokenUsageEntry> _filteredRows = new List<TokenUsageEntry>();
        private bool _loading;

        public TokenAnalyticsPanel(float scale, UserPreferences preferences)
        {
            _scale = Math.Max(1f, scale);
            _preferences = preferences ?? new UserPreferences();
            _tips = new ToolTip { InitialDelay = 350, AutoPopDelay = 10000, ReshowDelay = 100 };
            AutoScroll = true;
            BackColor = Palette.Background;
            ForeColor = Palette.PrimaryText;
            Margin = Padding.Empty;
            Padding = Padding.Empty;
            Font = NativeVisuals.CreateUiFont(9f, FontStyle.Regular);
            _layout = new TableLayoutPanel
            {
                Dock = DockStyle.Top, ColumnCount = 1, RowCount = 0,
                BackColor = Palette.Background, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = Padding.Empty, Padding = new Padding(0, 0, 0, Px(8))
            };
            _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(_layout);

            _period = Combo("統計期間");
            _period.Items.AddRange(new object[] { "今日", "本週", "本月", "近 30 天", "自訂期間" });
            _period.SelectedIndex = 1;
            _start = DatePicker("開始日期");
            _end = DatePicker("結束日期");
            AddRow(Fields(new[] { "期間", "開始", "結束" }, new Control[] { _period, _start, _end }, new[] { 28f, 36f, 36f }), 61);
            _provider = Combo("來源篩選");
            _model = Combo("模型篩選");
            _project = Combo("專案篩選");
            AddRow(Fields(new[] { "來源", "模型", "專案" }, new Control[] { _provider, _model, _project }, new[] { 28f, 36f, 36f }), 61);

            _coverage = TextLabel(string.Empty, 8.25f, Palette.MutedText);
            _coverage.Padding = new Padding(0, Px(3), 0, 0);
            AddRow(_coverage, 61);
            _export = CreateExportButton();
            var summaryHeader = new TableLayoutPanel
            {
                ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill,
                Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Palette.Background
            };
            summaryHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            summaryHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, Px(118)));
            summaryHeader.Controls.Add(Heading("本期消耗"), 0, 0);
            summaryHeader.Controls.Add(_export, 1, 0);
            AddRow(summaryHeader, 36);
            _total = TextLabel("等待本機紀錄", 19f, Palette.PrimaryText);
            _total.Font = NativeVisuals.CreateValueFont(19f, FontStyle.Bold);
            _total.AccessibleName = "本期 Token 累計";
            AddRow(_total, 39);
            _comparison = TextLabel(string.Empty, 8.5f, Palette.SecondaryText);
            AddRow(_comparison, 28);

            AddRow(Heading("每日消耗"), 29);
            _trend = new TokenTrendControl(_scale) { Dock = DockStyle.Fill, Margin = Padding.Empty };
            AddRow(_trend, 165);
            AddRow(Heading("消耗組成"), 31);
            _breakdown = new TokenBreakdownControl(_scale) { Dock = DockStyle.Fill, Margin = Padding.Empty };
            AddRow(_breakdown, 142);

            AddRow(Heading("按模型"), 31);
            _models = UsageList("模型");
            AddRow(_models, 145);
            AddRow(Heading("按專案"), 34);
            _projects = UsageList("專案");
            AddRow(_projects, 145);

            AddRow(Heading("每日 Token 預算"), 36);
            _budget = new NumericUpDown
            {
                Minimum = 0, Maximum = 9000000000000000m,
                DecimalPlaces = 0, Increment = 10000, ThousandsSeparator = true,
                BackColor = Palette.SurfaceRaised, ForeColor = Palette.PrimaryText,
                BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill,
                Font = NativeVisuals.CreateValueFont(9f, FontStyle.Regular),
                AccessibleName = "每日 Token 預算，0 表示停用", Name = "TokenDailyBudget"
            };
            _notifyBudget = new CheckBox
            {
                Text = "接近或達到預算時通知", AutoSize = false,
                Dock = DockStyle.Fill, CheckAlign = ContentAlignment.MiddleLeft,
                TextAlign = ContentAlignment.MiddleLeft, ForeColor = Palette.PrimaryText,
                BackColor = Palette.Background, AccessibleName = "每日 Token 預算通知",
                Margin = new Padding(Px(10), 0, 0, 0)
            };
            AddRow(Fields(new[] { "每日上限（0 為停用）", "通知" }, new Control[] { _budget, _notifyBudget }, new[] { 46f, 54f }), 62);
            _budgetStatus = TextLabel(string.Empty, 8.5f, Palette.SecondaryText);
            AddRow(_budgetStatus, 27);
            _budgetBar = new TokenBudgetBar { Dock = DockStyle.Fill, Margin = new Padding(0, Px(3), 0, Px(8)) };
            AddRow(_budgetBar, 17);

            _tips.SetToolTip(_budget, "以這台電腦今日所有來源的 Token 合計計算，不受上方篩選影響。預算不是供應商方案額度。");

            _period.SelectedIndexChanged += delegate { ApplyPeriod(); UpdateReport(); };
            _start.ValueChanged += delegate { DateChanged(true); };
            _end.ValueChanged += delegate { DateChanged(false); };
            _provider.SelectedIndexChanged += delegate { FiltersChanged(true); };
            _model.SelectedIndexChanged += delegate { FiltersChanged(false); };
            _project.SelectedIndexChanged += delegate { if (!_loading) UpdateReport(); };
            _budget.ValueChanged += delegate { SaveBudget(); };
            _notifyBudget.CheckedChanged += delegate { SaveBudget(); };
            _export.Click += delegate { ExportCurrentRows(); };
            ApplyPeriod();
            ReloadPreferences();
            SetSnapshot(null);
        }

        public event EventHandler PreferencesChanged;
        public event Action<List<TokenUsageEntry>> ExportCsvRequested;

        public void SetSnapshot(TokenAnalyticsSnapshot snapshot)
        {
            _snapshot = snapshot ?? new TokenAnalyticsSnapshot();
            _loading = true;
            SetChoices(_provider, "全部來源", Rows.Select(r => r.Provider));
            RebuildDependentFilters(true);
            SetDateLimits(_period.SelectedIndex == 4);
            _loading = false;
            if (_period.SelectedIndex != 4) ApplyPeriod();
            UpdateReport();
        }

        public void ReloadPreferences()
        {
            _loading = true;
            _budget.Value = Math.Min(_budget.Maximum, Math.Max(0m, _preferences.TokenDailyBudget));
            _notifyBudget.Checked = _preferences.NotifyTokenBudget;
            _loading = false;
            UpdateBudget();
        }

        internal List<TokenUsageEntry> GetFilteredRows()
        {
            return new List<TokenUsageEntry>(_filteredRows);
        }

        private IEnumerable<TokenUsageEntry> Rows
        {
            get { return _snapshot.Rows == null ? Enumerable.Empty<TokenUsageEntry>() : _snapshot.Rows.Where(r => r != null); }
        }

        private void ApplyPeriod()
        {
            if (_loading) return;
            _loading = true;
            var today = DateTime.Today;
            var start = today;
            if (_period.SelectedIndex == 1) start = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
            if (_period.SelectedIndex == 2) start = new DateTime(today.Year, today.Month, 1);
            if (_period.SelectedIndex == 3) start = today.AddDays(-29);
            var custom = _period.SelectedIndex == 4;
            SetDateLimits(custom);
            if (!custom)
            {
                _start.Value = start;
                _end.Value = today;
            }
            _start.Enabled = custom;
            _end.Enabled = custom;
            _loading = false;
        }

        private void DateChanged(bool startChanged)
        {
            if (_loading) return;
            _loading = true;
            if (_start.Value.Date > _end.Value.Date)
            {
                if (startChanged) _end.Value = _start.Value.Date;
                else _start.Value = _end.Value.Date;
            }
            _loading = false;
            UpdateReport();
        }

        private void SetDateLimits(bool custom)
        {
            var earliest = custom && _snapshot.CoverageStart != default(DateTime)
                ? _snapshot.CoverageStart.Date : new DateTime(2000, 1, 1);
            if (earliest > DateTime.Today) earliest = DateTime.Today;
            _start.MaxDate = DateTime.Today;
            _end.MaxDate = DateTime.Today;
            _start.MinDate = earliest;
            _end.MinDate = earliest;
        }

        private void FiltersChanged(bool providerChanged)
        {
            if (_loading) return;
            _loading = true;
            RebuildDependentFilters(providerChanged);
            _loading = false;
            UpdateReport();
        }

        private void RebuildDependentFilters(bool refreshModels)
        {
            var provider = SelectedValue(_provider);
            var rows = Rows.Where(r => provider == null || string.Equals(r.Provider ?? string.Empty, provider, StringComparison.Ordinal));
            if (refreshModels) SetChoices(_model, "全部模型", rows.Select(r => r.Model));
            var model = SelectedValue(_model);
            SetChoices(_project, "全部專案", rows.Where(r => model == null || string.Equals(r.Model ?? string.Empty, model, StringComparison.Ordinal)).Select(r => r.Project));
            _tips.SetToolTip(_project, SelectedValue(_project) ?? "全部專案");
            _tips.SetToolTip(_model, SelectedValue(_model) ?? "全部模型");
        }

        private static string SelectedValue(ComboBox combo)
        {
            var item = combo.SelectedItem as FilterChoice;
            return item == null ? null : item.Value;
        }

        private static void SetChoices(ComboBox combo, string all, IEnumerable<string> values)
        {
            var choices = values.Select(v => v ?? string.Empty).Distinct(StringComparer.Ordinal)
                .OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (combo.Items.Count == choices.Count + 1 &&
                combo.Items.Cast<FilterChoice>().Skip(1).Select(v => v.Value).SequenceEqual(choices, StringComparer.Ordinal)) return;
            var selected = SelectedValue(combo);
            combo.BeginUpdate();
            combo.Items.Clear();
            combo.Items.Add(new FilterChoice(null, all));
            foreach (var value in choices)
            {
                combo.Items.Add(new FilterChoice(value, string.IsNullOrWhiteSpace(value) ? "未記錄" : value));
            }
            combo.SelectedIndex = 0;
            for (var i = 1; i < combo.Items.Count; i++)
            {
                if (string.Equals(((FilterChoice)combo.Items[i]).Value, selected, StringComparison.Ordinal)) combo.SelectedIndex = i;
            }
            combo.EndUpdate();
        }

        private void UpdateReport()
        {
            if (_loading) return;
            var from = _start.Value.Date;
            var through = _end.Value.Date;
            var provider = SelectedValue(_provider);
            var model = SelectedValue(_model);
            var project = SelectedValue(_project);
            var selected = Rows.Where(r => (provider == null || string.Equals(r.Provider ?? string.Empty, provider, StringComparison.Ordinal)) &&
                (model == null || string.Equals(r.Model ?? string.Empty, model, StringComparison.Ordinal)) &&
                (project == null || string.Equals(r.Project ?? string.Empty, project, StringComparison.Ordinal))).ToList();
            _filteredRows = selected.Where(r => r.Date.Date >= from && r.Date.Date <= through).ToList();
            var totals = new TokenTotals(_filteredRows);
            var previousEnd = from.AddDays(-1);
            var previousStart = previousEnd.AddDays(-(through - from).Days);
            if (_period.SelectedIndex == 1)
            {
                previousStart = from.AddDays(-7);
                previousEnd = through.AddDays(-7);
            }
            else if (_period.SelectedIndex == 2)
            {
                previousStart = from.AddMonths(-1);
                previousEnd = through.AddMonths(-1);
            }
            var previous = new TokenTotals(selected.Where(r => r.Date.Date >= previousStart && r.Date.Date <= previousEnd));
            var ready = _snapshot.CapturedAt != default(DateTimeOffset);
            var rangeComplete = HasCompleteCoverage(from);
            _total.Text = !ready ? "等待本機紀錄" : !rangeComplete && _filteredRows.Count == 0 ?
                (_snapshot.IsIndexing ? "正在讀取本機紀錄" : "尚無可用紀錄") : Number(totals.Total) + " Token";
            var change = previous.Total == 0
                ? (totals.Total == 0 ? "尚無消耗紀錄" : "前期沒有可比較紀錄")
                : "較前期 " + ((totals.Total - previous.Total) / previous.Total).ToString("+0.0%;-0.0%;0.0%", CultureInfo.CurrentCulture);
            var hasPreviousCoverage = HasCompleteCoverage(previousStart);
            _comparison.Text = !ready ? "讀取完成後即可比較" : !hasPreviousCoverage ?
                (_snapshot.IsIndexing ? "讀取中 · 完成後提供前期比較" : "尚無完整前期資料") :
                change + "   ·   " + previousStart.ToString("MM/dd") + "–" + previousEnd.ToString("MM/dd") + "  " + Number(previous.Total) + " Token";
            _tips.SetToolTip(_comparison, "本期 " + from.ToString("yyyy/MM/dd") + "–" + through.ToString("yyyy/MM/dd") +
                "\n前期 " + previousStart.ToString("yyyy/MM/dd") + "–" + previousEnd.ToString("yyyy/MM/dd") + "\n本週與本月比較相同已經過的日期範圍。");
            var last = _snapshot.LastRecordAt.HasValue ? _snapshot.LastRecordAt.Value.ToLocalTime().ToString("MM/dd HH:mm") : "尚無紀錄";
            _coverage.Text = "本機紀錄 · " + (_snapshot.IsComplete ? "已讀取可用紀錄" : _snapshot.IsIndexing ? "讀取中 · 部分紀錄" : "部分紀錄") + " · 最近紀錄 " + last +
                "\n" + (_snapshot.Status ?? "等待本機 Token 紀錄") + "\nAntigravity 未提供 Token 計數";
            _tips.SetToolTip(_coverage, (_snapshot.Status ?? string.Empty) + "\n掃描 " + _snapshot.FilesScanned + " 個檔案 · 略過 " + _snapshot.FilesSkipped + " 個檔案\n不包含其他裝置或未保留的紀錄。" +
                (_snapshot.CompleteFrom.HasValue && _snapshot.CompleteFrom.Value.Date <= DateTime.Today && !_snapshot.IsIndexing ?
                    "\n已確認完整範圍：" + _snapshot.CompleteFrom.Value.ToString("yyyy/MM/dd") + " 至今日" : string.Empty));
            _trend.SetData(_filteredRows, from, through, !rangeComplete);
            _breakdown.SetTotals(totals, !rangeComplete && _filteredRows.Count == 0);
            var empty = rangeComplete ? "此範圍沒有本機紀錄" : _snapshot.IsIndexing ? "本機紀錄讀取中" : "尚無可用紀錄";
            PopulateList(_models, _filteredRows.GroupBy(r => (r.Provider ?? "未記錄") + " · " + (r.Model ?? "未記錄")), totals.Total, empty);
            PopulateList(_projects, _filteredRows.GroupBy(r => string.IsNullOrWhiteSpace(r.Project) ? "未記錄專案" : r.Project), totals.Total, empty);
            _export.Enabled = _filteredRows.Count > 0;
            UpdateBudget();
        }

        private static void PopulateList(UsageCenterForm.ProfessionalListView list, IEnumerable<IGrouping<string, TokenUsageEntry>> groups, decimal total, string empty)
        {
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var group in groups)
            {
                var amount = new TokenTotals(group).Total;
                var fraction = total == 0 ? 0 : amount / total;
                var item = new ListViewItem(group.Key) { Tag = new TokenGroup { Name = group.Key, Total = amount, Share = fraction }, ToolTipText = group.Key + "\n" + Number(amount) + " Token" };
                item.SubItems.Add(Number(amount));
                item.SubItems.Add(fraction.ToString("0.0%", CultureInfo.CurrentCulture));
                list.Items.Add(item);
            }
            if (list.Items.Count == 0) list.Items.Add(new ListViewItem(new[] { empty, string.Empty, string.Empty }));
            list.Sort();
            list.EndUpdate();
        }

        private void UpdateBudget()
        {
            var today = new TokenTotals(Rows.Where(r => r.Date.Date == DateTime.Today)).Total;
            var budget = Math.Max(0m, _preferences.TokenDailyBudget);
            var todayComplete = HasCompleteCoverage(DateTime.Today);
            _notifyBudget.Enabled = budget > 0;
            _budgetStatus.Text = "今日" + (todayComplete ? "全部來源 " : "已讀取 ") + Number(today) + " Token · " + (budget <= 0 ? "預算未啟用" :
                "預算 " + Number(budget) + " · " + (today / budget).ToString("0.0%", CultureInfo.CurrentCulture)) +
                (budget > 0 && !todayComplete ? " · 待確認" : string.Empty);
            _budgetStatus.ForeColor = budget > 0 && today >= budget ? Palette.Warning : Palette.SecondaryText;
            _budgetBar.Fraction = budget <= 0 ? 0 : (double)Math.Min(1m, today / budget);
            _budgetBar.EnabledBudget = budget > 0;
            _budgetBar.Invalidate();
            _tips.SetToolTip(_budgetStatus, "本機今日 " + DateTime.Today.ToString("yyyy/MM/dd") + " 全部紀錄，與上方篩選分開計算。" +
                (todayComplete ? string.Empty : "\n目前僅統計已讀取的部分紀錄。\n紀錄未確認完整前暫停預算通知。"));
        }

        private bool HasCompleteCoverage(DateTime from)
        {
            return !_snapshot.IsIndexing && (_snapshot.IsComplete || (_snapshot.CompleteFrom.HasValue && _snapshot.CompleteFrom.Value.Date <= from.Date)) &&
                (_snapshot.CoverageStart == default(DateTime) || _snapshot.CoverageStart.Date <= from.Date);
        }

        private void SaveBudget()
        {
            if (_loading) return;
            _preferences.TokenDailyBudget = decimal.ToInt64(_budget.Value);
            _preferences.NotifyTokenBudget = _notifyBudget.Checked;
            UpdateBudget();
            var handler = PreferencesChanged;
            if (handler != null) handler(this, EventArgs.Empty);
        }

        private void ExportCurrentRows()
        {
            var handler = ExportCsvRequested;
            if (handler != null && _filteredRows.Count > 0) handler(GetFilteredRows());
        }

        private UsageCenterForm.ProfessionalListView UsageList(string firstColumn)
        {
            var list = new UsageCenterForm.ProfessionalListView(_scale, new[] { 51, 34, 15 })
            {
                Dock = DockStyle.Fill, Margin = Padding.Empty, HeaderStyle = ColumnHeaderStyle.Clickable,
                ShowItemToolTips = true, AccessibleName = firstColumn + " Token 消耗，欄位可排序",
                RightColumnInset = Px(4)
            };
            list.Columns.Add(firstColumn, Px(260));
            list.Columns.Add("Token ↓", Px(170));
            list.Columns.Add("占比", Px(80));
            var sort = new TokenGroupComparer { Column = 1, Descending = true };
            list.ListViewItemSorter = sort;
            list.ColumnClick += delegate(object sender, ColumnClickEventArgs e)
            {
                sort.Descending = sort.Column == e.Column ? !sort.Descending : e.Column != 0;
                sort.Column = e.Column;
                list.Columns[0].Text = firstColumn;
                list.Columns[1].Text = "Token";
                list.Columns[2].Text = "占比";
                list.Columns[e.Column].Text += sort.Descending ? " ↓" : " ↑";
                list.Sort();
                list.Invalidate();
            };
            return list;
        }

        private Button CreateExportButton()
        {
            var button = new Button
            {
                Text = "匯出 CSV", AutoSize = false, Width = Px(118), Height = Px(30),
                BackColor = Palette.SurfaceRaised, ForeColor = Palette.PrimaryText,
                FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false,
                TextImageRelation = TextImageRelation.ImageBeforeText,
                ImageAlign = ContentAlignment.MiddleLeft, TextAlign = ContentAlignment.MiddleCenter,
                Padding = new Padding(Px(8), 0, Px(6), 0), AccessibleName = "匯出目前篩選結果為 CSV",
                Margin = new Padding(0, Px(2), 0, Px(2)), Image = Glyph("\uE8A5", 17)
            };
            button.FlatAppearance.BorderColor = Palette.BorderStrong;
            button.FlatAppearance.MouseOverBackColor = Palette.Track;
            _tips.SetToolTip(button, "匯出目前期間與來源、模型、專案篩選後的本機紀錄");
            return button;
        }

        private void AddRow(Control control, int height)
        {
            control.Dock = DockStyle.Fill;
            var index = _layout.RowCount++;
            _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(height)));
            _layout.Controls.Add(control, 0, index);
        }

        private TableLayoutPanel Fields(string[] titles, Control[] controls, float[] weights)
        {
            var table = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = controls.Length, RowCount = 2,
                Margin = Padding.Empty, Padding = Padding.Empty, BackColor = Palette.Background
            };
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(22)));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, Px(31)));
            for (var i = 0; i < controls.Length; i++)
            {
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, weights[i]));
                var label = TextLabel(titles[i], 8.25f, Palette.SecondaryText);
                label.TextAlign = ContentAlignment.BottomLeft;
                label.Margin = new Padding(0, 0, Px(8), Px(4));
                table.Controls.Add(label, i, 0);
                controls[i].Dock = DockStyle.Top;
                controls[i].Margin = new Padding(0, 0, i == controls.Length - 1 ? 0 : Px(10), 0);
                controls[i].Height = Px(28);
                table.Controls.Add(controls[i], i, 1);
            }
            return table;
        }

        private Label Heading(string text)
        {
            var label = TextLabel(text, 9.5f, Palette.PrimaryText);
            label.Font = NativeVisuals.CreateSemiboldFont(9.5f);
            label.TextAlign = ContentAlignment.BottomLeft;
            label.Padding = new Padding(0, 0, 0, Px(5));
            return label;
        }

        private Label TextLabel(string text, float size, Color color)
        {
            return new Label
            {
                Text = text, AutoSize = false, Dock = DockStyle.Fill, Margin = Padding.Empty,
                Font = NativeVisuals.CreateUiFont(size, FontStyle.Regular), ForeColor = color,
                BackColor = Palette.Background, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true
            };
        }

        private UsageCenterForm.DarkComboBox Combo(string label)
        {
            return new UsageCenterForm.DarkComboBox(_scale) { AccessibleName = label, DropDownWidth = Px(360), PaintSelectionSurface = true };
        }

        private DateTimePicker DatePicker(string label)
        {
            return new AnalyticsDatePicker(_scale)
            {
                Format = DateTimePickerFormat.Custom, CustomFormat = "yyyy/MM/dd", Value = DateTime.Today,
                MinDate = new DateTime(2000, 1, 1), MaxDate = DateTime.Today,
                AccessibleName = label, CalendarMonthBackground = Palette.SurfaceRaised,
                CalendarForeColor = Palette.PrimaryText, CalendarTitleBackColor = Palette.Surface,
                CalendarTitleForeColor = Palette.PrimaryText, Font = NativeVisuals.CreateUiFont(9f, FontStyle.Regular)
            };
        }

        private Bitmap Glyph(string text, int size)
        {
            var bitmap = new Bitmap(Px(size), Px(size));
            using (var graphics = Graphics.FromImage(bitmap))
            using (var font = NativeVisuals.CreateGlyphFont(Px(size - 3), GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.Transparent);
                TextRenderer.DrawText(graphics, text, font, new Rectangle(0, 0, bitmap.Width, bitmap.Height), Palette.PrimaryText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            return bitmap;
        }

        private int Px(int value) { return Math.Max(1, (int)Math.Round(value * _scale)); }
        private static string Number(decimal value) { return value.ToString("N0", CultureInfo.CurrentCulture); }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (!IsHandleCreated || SystemInformation.HighContrast || !VerticalScroll.Visible) return;
            if ((message.Msg == 0x0317 || message.Msg == 0x0318) && message.WParam != IntPtr.Zero)
            {
                using (var graphics = Graphics.FromHdc(message.WParam)) PaintScrollBar(graphics);
            }
            else if (message.Msg == 0x000F || message.Msg == 0x0085 || message.Msg == 0x0115 ||
                message.Msg == 0x00A0 || message.Msg == 0x00A1 || message.Msg == 0x00A2 || message.Msg == 0x020A)
            {
                var dc = GetWindowDC(Handle);
                if (dc == IntPtr.Zero) return;
                try { using (var graphics = Graphics.FromHdc(dc)) PaintScrollBar(graphics); }
                finally { ReleaseDC(Handle, dc); }
            }
        }

        private void PaintScrollBar(Graphics graphics)
        {
            var width = Width - ClientSize.Width;
            if (width < 1 || Height < 1) return;
            var bounds = new Rectangle(ClientSize.Width, 0, width, Height);
            using (var background = new SolidBrush(Palette.Background)) graphics.FillRectangle(background, bounds);
            var button = Math.Min(SystemInformation.VerticalScrollBarArrowHeight, Height / 2);
            var trackHeight = Math.Max(1, Height - button * 2);
            var range = Math.Max(1, VerticalScroll.Maximum - VerticalScroll.Minimum + 1);
            var thumbHeight = Math.Min(trackHeight, Math.Max(Px(24), (int)(trackHeight * Math.Min(1d, VerticalScroll.LargeChange / (double)range))));
            var travel = trackHeight - thumbHeight;
            var maximum = Math.Max(1, range - VerticalScroll.LargeChange);
            var thumbY = button + (int)Math.Round(travel * Math.Min(1d, Math.Max(0d, VerticalScroll.Value / (double)maximum)));
            var thumbWidth = Math.Max(3, width / 3);
            using (var brush = new SolidBrush(Palette.TrackBorder))
                graphics.FillRectangle(brush, bounds.Left + (width - thumbWidth) / 2, thumbY, thumbWidth, thumbHeight);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowDC(IntPtr handle);
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr handle, IntPtr dc);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tips.Dispose();
                if (_export.Image != null) _export.Image.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class FilterChoice
        {
            public readonly string Value;
            private readonly string _label;
            public FilterChoice(string value, string label) { Value = value; _label = label; }
            public override string ToString() { return _label; }
        }

        private sealed class AnalyticsDatePicker : DateTimePicker
        {
            private readonly float _scale;
            private readonly Font _glyphFont;
            public AnalyticsDatePicker(float scale)
            {
                _scale = scale;
                _glyphFont = NativeVisuals.CreateGlyphFont(Math.Max(10, 11 * scale), GraphicsUnit.Pixel);
            }
            protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
            protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
            protected override void WndProc(ref Message message)
            {
                base.WndProc(ref message);
                // 編輯時保留系統日期欄位與鍵盤選取，閒置時使用原生深色表面。
                if (!IsHandleCreated || (Focused && Enabled) || SystemInformation.HighContrast) return;
                if (message.Msg == 0x000F)
                {
                    using (var graphics = Graphics.FromHwnd(Handle)) PaintIdle(graphics);
                }
                else if ((message.Msg == 0x0317 || message.Msg == 0x0318) && message.WParam != IntPtr.Zero)
                {
                    using (var graphics = Graphics.FromHdc(message.WParam)) PaintIdle(graphics);
                }
            }
            private void PaintIdle(Graphics graphics)
            {
                var bounds = ClientRectangle;
                using (var brush = new SolidBrush(Enabled ? Palette.SurfaceRaised : Palette.Surface)) graphics.FillRectangle(brush, bounds);
                using (var border = new Pen(Palette.BorderStrong)) graphics.DrawRectangle(border, 0, 0, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1));
                var glyphWidth = Math.Max(18, (int)Math.Round(24 * _scale));
                var inset = Math.Max(4, (int)Math.Round(7 * _scale));
                TextRenderer.DrawText(graphics, Value.ToString("yyyy/MM/dd"), Font,
                    new Rectangle(inset, 0, Math.Max(1, bounds.Width - glyphWidth - inset), bounds.Height),
                    Enabled ? Palette.PrimaryText : Palette.MutedText,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(graphics, "\uE787", _glyphFont,
                    new Rectangle(bounds.Width - glyphWidth, 0, glyphWidth, bounds.Height),
                    Enabled ? Palette.SecondaryText : Palette.MutedText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            protected override void Dispose(bool disposing)
            {
                if (disposing) _glyphFont.Dispose();
                base.Dispose(disposing);
            }
        }

        private sealed class TokenGroup
        {
            public string Name;
            public decimal Total;
            public decimal Share;
        }

        private sealed class TokenGroupComparer : IComparer
        {
            public int Column;
            public bool Descending;
            public int Compare(object x, object y)
            {
                var a = ((ListViewItem)x).Tag as TokenGroup;
                var b = ((ListViewItem)y).Tag as TokenGroup;
                if (a == null || b == null) return a == null ? (b == null ? 0 : 1) : -1;
                var result = Column == 0 ? string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase) :
                    Column == 1 ? a.Total.CompareTo(b.Total) : a.Share.CompareTo(b.Share);
                if (result == 0) result = string.Compare(a.Name, b.Name, StringComparison.Ordinal);
                return Descending ? -result : result;
            }
        }

        private sealed class TokenTotals
        {
            public decimal Input;
            public decimal CacheRead;
            public decimal CacheWrite;
            public decimal Output;
            public decimal Total { get { return Input + CacheRead + CacheWrite + Output; } }
            public TokenTotals(IEnumerable<TokenUsageEntry> rows)
            {
                foreach (var row in rows)
                {
                    Input += Math.Max(0, row.InputTokens);
                    CacheRead += Math.Max(0, row.CacheReadTokens);
                    CacheWrite += Math.Max(0, row.CacheWriteTokens);
                    Output += Math.Max(0, row.OutputTokens);
                }
            }
        }

        private sealed class TokenBreakdownControl : Control
        {
            private readonly float _scale;
            private readonly Font _labelFont;
            private readonly Font _valueFont;
            private TokenTotals _totals = new TokenTotals(Enumerable.Empty<TokenUsageEntry>());
            private bool _waiting;
            private readonly Color[] _colors = { Palette.Focus, Palette.Codex, Palette.Claude, Palette.Warning };
            public TokenBreakdownControl(float scale)
            {
                _scale = scale;
                _labelFont = NativeVisuals.CreateUiFont(8.25f, FontStyle.Regular);
                _valueFont = NativeVisuals.CreateValueFont(10f, FontStyle.Bold);
                DoubleBuffered = true;
                BackColor = Palette.Background;
                AccessibleRole = AccessibleRole.StaticText;
            }
            public void SetTotals(TokenTotals totals, bool waiting)
            {
                _totals = totals;
                _waiting = waiting;
                AccessibleName = "未快取輸入 " + Number(totals.Input) + "，快取讀取 " + Number(totals.CacheRead) +
                    "，快取寫入 " + Number(totals.CacheWrite) + "，輸出 " + Number(totals.Output);
                Invalidate();
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                if (_waiting)
                {
                    TextRenderer.DrawText(e.Graphics, "尚無足夠紀錄分析消耗組成", _labelFont, ClientRectangle, Palette.MutedText,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    return;
                }
                var values = new[] { _totals.Input, _totals.CacheRead, _totals.CacheWrite, _totals.Output };
                var labels = new[] { "輸入（未快取）", "快取讀取", "快取寫入", "輸出" };
                var total = _totals.Total;
                using (var track = new SolidBrush(Palette.Track)) e.Graphics.FillRectangle(track, 0, 0, Width, Px(5));
                float left = 0;
                for (var i = 0; i < values.Length; i++)
                {
                    var fraction = total == 0 ? 0 : values[i] / total;
                    var width = Width * (float)fraction;
                    using (var brush = new SolidBrush(_colors[i])) e.Graphics.FillRectangle(brush, left, 0, width, Px(5));
                    left += width;
                    var x = (i % 2) * Width / 2;
                    var y = Px(13 + (i / 2) * 45);
                    using (var brush = new SolidBrush(_colors[i])) e.Graphics.FillRectangle(brush, x, y + Px(5), Px(5), Px(5));
                    Draw(e.Graphics, labels[i] + " · " + fraction.ToString("0.0%"), _labelFont,
                        new Rectangle(x + Px(12), y, Math.Max(1, Width / 2 - Px(20)), Px(18)), Palette.SecondaryText);
                    Draw(e.Graphics, Number(values[i]), _valueFont,
                        new Rectangle(x + Px(12), y + Px(17), Math.Max(1, Width / 2 - Px(20)), Px(23)), Palette.PrimaryText);
                }
                var inputTotal = _totals.Input + _totals.CacheRead + _totals.CacheWrite;
                var rate = inputTotal == 0 ? "—" : (_totals.CacheRead / inputTotal).ToString("0.0%");
                Draw(e.Graphics, "輸入快取命中率 " + rate + " · 快取讀取 / 全部輸入", _labelFont,
                    new Rectangle(0, Px(108), Width, Px(24)), Palette.MutedText);
            }
            private static void Draw(Graphics graphics, string text, Font font, Rectangle bounds, Color color)
            {
                TextRenderer.DrawText(graphics, text, font, bounds, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            private int Px(int value) { return Math.Max(1, (int)Math.Round(value * _scale)); }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { _labelFont.Dispose(); _valueFont.Dispose(); }
                base.Dispose(disposing);
            }
        }

        private sealed class TokenBudgetBar : Control
        {
            public double Fraction;
            public bool EnabledBudget;
            public TokenBudgetBar() { DoubleBuffered = true; BackColor = Palette.Background; }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                using (var brush = new SolidBrush(Palette.Track)) e.Graphics.FillRectangle(brush, ClientRectangle);
                if (EnabledBudget && Fraction > 0)
                {
                    using (var brush = new SolidBrush(Fraction >= 1 ? Palette.Warning : Palette.Focus))
                        e.Graphics.FillRectangle(brush, 0, 0, (float)(Width * Math.Max(0, Math.Min(1, Fraction))), Height);
                }
            }
        }

        private sealed class TokenTrendControl : Control
        {
            private readonly float _scale;
            private readonly Font _font;
            private readonly ToolTip _tips;
            private List<TrendPoint> _points = new List<TrendPoint>();
            private DateTime _from;
            private DateTime _through;
            private bool _hasRows;
            private bool _partial;
            private int _daysPerPoint = 1;
            private int _hover = -1;
            public TokenTrendControl(float scale)
            {
                _scale = scale;
                _font = NativeVisuals.CreateUiFont(8.25f, FontStyle.Regular);
                _tips = new ToolTip { InitialDelay = 100, AutoPopDelay = 7000, ReshowDelay = 50 };
                BackColor = Palette.Background;
                DoubleBuffered = true;
                AccessibleName = "每日 Token 消耗趨勢";
                AccessibleRole = AccessibleRole.Chart;
            }
            public void SetData(List<TokenUsageEntry> rows, DateTime from, DateTime through, bool partial)
            {
                _from = from; _through = through; _hasRows = rows.Count > 0; _hover = -1;
                _partial = partial;
                _tips.SetToolTip(this, string.Empty);
                var days = (through - from).Days + 1;
                var groupSize = Math.Max(1, (int)Math.Ceiling(days / 90d));
                _daysPerPoint = groupSize;
                var totals = rows.GroupBy(r => (r.Date.Date - from).Days / groupSize)
                    .ToDictionary(g => g.Key, g => new TokenTotals(g).Total);
                _points = new List<TrendPoint>();
                for (var day = 0; day < days; day += groupSize)
                {
                    decimal value;
                    totals.TryGetValue(day / groupSize, out value);
                    _points.Add(new TrendPoint { From = from.AddDays(day), Through = from.AddDays(Math.Min(days - 1, day + groupSize - 1)), Value = value });
                }
                var sum = _points.Sum(p => p.Value);
                var peak = _points.OrderByDescending(p => p.Value).FirstOrDefault();
                AccessibleDescription = !_hasRows ? "此範圍沒有本機紀錄" :
                    from.ToString("yyyy/MM/dd") + " 至 " + through.ToString("yyyy/MM/dd") + "，共 " + Number(sum) +
                    " Token，最高 " + (peak == null ? string.Empty : peak.From.ToString("MM/dd") + " " + Number(peak.Value));
                Invalidate();
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                var plot = PlotBounds;
                if (plot.Width < 1 || plot.Height < 1) return;
                var max = _points.Count == 0 ? 0 : _points.Max(p => p.Value);
                if (!_hasRows || max <= 0)
                {
                    TextRenderer.DrawText(e.Graphics, _partial ? "尚未讀到此範圍的紀錄" : "此範圍沒有 Token 消耗紀錄", _font, ClientRectangle, Palette.MutedText,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    return;
                }
                using (var line = new Pen(Palette.Border))
                {
                    for (var step = 0; step <= 2; step++)
                    {
                        var y = plot.Top + plot.Height * step / 2;
                        e.Graphics.DrawLine(line, plot.Left, y, plot.Right, y);
                    }
                }
                var cell = plot.Width / (float)_points.Count;
                for (var i = 0; i < _points.Count; i++)
                {
                    var height = plot.Height * (float)(_points[i].Value / max);
                    var width = Math.Min(Px(44), Math.Max(1f, cell - Px(3)));
                    var x = plot.Left + cell * i + (cell - width) / 2;
                    using (var brush = new SolidBrush(i == _hover ? Palette.Codex : Palette.Focus))
                        e.Graphics.FillRectangle(brush, x, plot.Bottom - height, width, height);
                }
                TextRenderer.DrawText(e.Graphics, Number(max) + " Token" + (_daysPerPoint > 1 ? " · 每 " + _daysPerPoint + " 日彙整" : string.Empty) +
                    (_partial ? " · 部分紀錄" : string.Empty), _font, new Rectangle(0, 0, Width, Px(19)), Palette.SecondaryText, TextFormatFlags.Left | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(e.Graphics, _from.ToString("MM/dd"), _font, new Rectangle(plot.Left, plot.Bottom + Px(5), Width / 2, Px(18)), Palette.MutedText, TextFormatFlags.Left | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(e.Graphics, _through.ToString("MM/dd"), _font, new Rectangle(Width / 2, plot.Bottom + Px(5), Width / 2, Px(18)), Palette.MutedText, TextFormatFlags.Right | TextFormatFlags.NoPadding);
            }
            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                var plot = PlotBounds;
                var index = plot.Contains(e.Location) && _points.Count > 0 ? Math.Min(_points.Count - 1, (int)((e.X - plot.Left) / (float)plot.Width * _points.Count)) : -1;
                if (_hover == index) return;
                _hover = index;
                if (index < 0) _tips.SetToolTip(this, string.Empty);
                else
                {
                    var point = _points[index];
                    _tips.SetToolTip(this, point.From.ToString("yyyy/MM/dd") +
                        (point.Through != point.From ? "–" + point.Through.ToString("MM/dd") : string.Empty) + "\n" +
                        (_partial && point.Value == 0 ? "尚未讀到紀錄" : Number(point.Value) + " Token") + (_partial ? "\n部分紀錄" : string.Empty));
                }
                Invalidate();
            }
            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e); _hover = -1; _tips.SetToolTip(this, string.Empty); Invalidate();
            }
            private Rectangle PlotBounds { get { return new Rectangle(0, Px(26), Math.Max(1, Width - 1), Math.Max(1, Height - Px(52))); } }
            private int Px(int value) { return Math.Max(1, (int)Math.Round(value * _scale)); }
            protected override void Dispose(bool disposing)
            {
                if (disposing) { _font.Dispose(); _tips.Dispose(); }
                base.Dispose(disposing);
            }
            private sealed class TrendPoint { public DateTime From; public DateTime Through; public decimal Value; }
        }
    }
}
