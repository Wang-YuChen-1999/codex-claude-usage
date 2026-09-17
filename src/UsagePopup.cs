using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CodexClaudeUsage
{
    internal static class Palette
    {
        public static readonly Color Background = Color.FromArgb(22, 24, 26);
        public static readonly Color HeaderSurface = Color.FromArgb(28, 31, 35);
        public static readonly Color ProviderSurface = Color.FromArgb(26, 29, 32);
        public static readonly Color FooterSurface = Color.FromArgb(25, 28, 31);
        public static readonly Color Surface = Color.FromArgb(31, 35, 38);
        public static readonly Color SurfaceRaised = Color.FromArgb(36, 40, 43);
        public static readonly Color Border = Color.FromArgb(51, 56, 61);
        public static readonly Color BorderStrong = Color.FromArgb(61, 67, 72);
        public static readonly Color Track = Color.FromArgb(46, 51, 56);
        public static readonly Color TrackBorder = Color.FromArgb(66, 72, 78);
        public static readonly Color BandHighlight = Color.FromArgb(38, 42, 46);
        public static readonly Color BandShadow = Color.FromArgb(17, 19, 21);
        public static readonly Color PrimaryText = Color.FromArgb(239, 241, 242);
        public static readonly Color SecondaryText = Color.FromArgb(184, 188, 192);
        public static readonly Color MutedText = Color.FromArgb(132, 138, 143);
        public static readonly Color Success = Color.FromArgb(72, 199, 159);
        public static readonly Color Codex = Color.FromArgb(74, 205, 166);
        public static readonly Color Claude = Color.FromArgb(220, 151, 98);
        public static readonly Color Antigravity = Color.FromArgb(66, 133, 244);
        public static readonly Color Warning = Color.FromArgb(229, 176, 72);
        public static readonly Color Critical = Color.FromArgb(235, 93, 102);
        public static readonly Color Focus = Color.FromArgb(114, 157, 231);
    }

    internal sealed class UsagePopup : Form
    {
        private const int PopupWidth = 516;
        private const int HeaderHeight = 70;
        private const int FooterHeight = 46;
        private const int ProviderBandHeight = 48;
        private const int CardMargin = 12;   // 卡片左右外距。
        private const int CardGap = 10;      // 標頭與卡片、卡片與卡片、卡片與頁腳的間距。
        private const int CardRadius = 8;
        private const int CardPadBottom = 8; // 卡片底部內距。
        private const int ProviderHeaderHeight = 58; // 色帶 48 + 帶下 10 呼吸間距，避免首行標籤貼齊色塊
        private const int BucketHeight = 60;
        private const int MaxBucketsPerCard = 4; // Antigravity 有四個額度桶，卡片最多完整呈現四列。

        // 緊湊密度：三家供應商合計最多九列額度，標準間距在多數螢幕上會超出工作區。
        // 空間不足時整體改用較緊的垂直節奏（字級不變，只壓縮間距），先求「全部看得到」。
        private const int CompactBandHeight = 40;
        private const int CompactHeaderHeight = 48;
        private const int CompactBucketHeight = 50;
        private const int CompactCardGap = 8;
        private const int CompactCardPadBottom = 6;
        private const int CsDropShadow = 0x00020000;
        private const int EnterDuration = 160;
        private const int ExitDuration = 120;
        private const int UsageDuration = 360;
        internal const int RefreshRotationDuration = 720;
        private const int FeedbackDuration = 400;

        private readonly IconButton _refreshButton;
        private readonly IconButton _closeButton;
        private readonly IconButton _pinButton;
        private bool _pinnedOpen; // 釘選：失焦不自動關閉。
        private readonly Dictionary<int, KeyValuePair<Rectangle, string>> _hoverHints =
            new Dictionary<int, KeyValuePair<Rectangle, string>>(); // 重設時間 hover 提示（keyed by bucket top）。
        private string _activeHint = "";
        private int _hoverBucketTop = -1;      // 懸停的 bucket 行（微亮高亮）。
        private readonly HashSet<int> _bucketTops = new HashSet<int>(); // 目前版面的 bucket 行位置。
        private long _sparkRevealStarted = -1; // sparkline 描線進場（面板開啟時播放一次）。
        private bool _placedAtLanding; // 目前顯示位置來自液滴落點：資料更新時保持原位，不拉回托盤位。
        private const int SparkRevealDuration = 600;
        private readonly ToolTip _toolTip;
        private readonly Timer _motionTimer;
        private readonly Stopwatch _motionClock;
        private readonly bool _animationsEnabled;
        private readonly Dictionary<string, AnimatedNumber> _usageAnimations;
        private readonly Font _titleFont;
        private readonly Font _subtitleFont;
        private readonly Font _providerFont;
        private readonly Font _statusFont;
        private readonly Font _bucketLabelFont;
        private readonly Font _valueFont;
        private readonly Font _valueStrongFont;
        private readonly Font _detailFont;
        private readonly Font _emptyFont;
        private readonly Font _footerFont;
        private readonly Font _smallGlyphFont;

        private float _scale;
        // 每張卡片實際顯示的額度列數（依螢幕工作區高度自動伸縮，至少各保留一列）。
        private readonly int[] _cardBuckets = new int[] { 1, 1, 1 };
        private bool _compact; // 目前是否採用緊湊垂直節奏。
        private bool _nativeChrome;
        private UsageSnapshot _snapshot;
        private bool _loading;
        private long _loadingStarted;
        private long _feedbackStarted = -1;
        private WindowMotion _windowMotion;
        private long _windowMotionStarted;
        private double _windowStartOpacity;
        private int _windowStartOffset;
        private Point _restLocation;
        private long _revealStarted = -1;   // 液滴落點水波揭示進場。
        private Point _revealCenterClient;
        private const int RevealDuration = 260;

        public UsagePopup()
        {
            AutoScaleMode = AutoScaleMode.None;
            using (var graphics = CreateGraphics())
            {
                _scale = graphics.DpiX / 96f;
            }

            _animationsEnabled = Motion.IsEnabled;
            _usageAnimations = new Dictionary<string, AnimatedNumber>(StringComparer.Ordinal);
            _motionClock = Stopwatch.StartNew();
            _motionTimer = new Timer { Interval = 16 };
            _motionTimer.Tick += MotionTick;

            _titleFont = NativeVisuals.CreateSemiboldFont(14f);
            _subtitleFont = NativeVisuals.CreateUiFont(8.5f, FontStyle.Regular);
            _providerFont = NativeVisuals.CreateDisplayFont(11.75f, FontStyle.Bold);
            _statusFont = NativeVisuals.CreateUiFont(8.5f, FontStyle.Regular);
            _bucketLabelFont = NativeVisuals.CreateSemiboldFont(9.25f);
            _valueFont = NativeVisuals.CreateValueFont(9.5f, FontStyle.Regular);
            _valueStrongFont = NativeVisuals.CreateValueFont(9.5f, FontStyle.Bold);
            _detailFont = NativeVisuals.CreateUiFont(8.5f, FontStyle.Regular);
            _emptyFont = NativeVisuals.CreateUiFont(9f, FontStyle.Regular);
            _footerFont = NativeVisuals.CreateUiFont(8.5f, FontStyle.Regular);
            _smallGlyphFont = NativeVisuals.CreateGlyphFont(7.75f, GraphicsUnit.Point);

            BackColor = Palette.Background;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Codex + Claude + Antigravity 用量";
            TopMost = true;
            DoubleBuffered = true;
            KeyPreview = true;
            AccessibleName = "Codex、Claude Code 與 Antigravity 模型用量";
            AccessibleDescription = "尚未更新用量資料";
            AccessibleRole = AccessibleRole.Window;

            _toolTip = new ToolTip
            {
                AutoPopDelay = 5000,
                InitialDelay = 350,
                ReshowDelay = 100
            };

            _refreshButton = CreateIconButton(
                "\uE72C",
                "立即更新",
                Palette.SurfaceRaised,
                Palette.Track,
                Palette.BorderStrong,
                Palette.PrimaryText,
                0);
            _toolTip.SetToolTip(_refreshButton, "立即更新 (Ctrl+R)");
            _refreshButton.Click += delegate { RequestRefresh(); };

            _closeButton = CreateIconButton(
                "\uE8BB",
                "關閉",
                Color.FromArgb(60, 39, 35),
                Color.FromArgb(79, 44, 41),
                Color.FromArgb(124, 67, 62),
                Color.FromArgb(244, 205, 201),
                1);
            _toolTip.SetToolTip(_closeButton, "關閉 (Esc)");
            _closeButton.Click += delegate { Dismiss(); };

            _pinButton = CreateIconButton(
                "\uE718",
                "釘選面板",
                Palette.SurfaceRaised,
                Palette.Track,
                Palette.BorderStrong,
                Palette.PrimaryText,
                2);
            _toolTip.SetToolTip(_pinButton, "釘選：保持開啟，點外部不關閉");
            _pinButton.Click += delegate { TogglePinnedOpen(); };

            Controls.Add(_refreshButton);
            Controls.Add(_closeButton);
            Controls.Add(_pinButton);

            if (!string.Equals(
                Environment.GetEnvironmentVariable("CODEX_CLAUDE_USAGE_KEEP_OPEN"),
                "1",
                StringComparison.Ordinal))
            {
                Deactivate += delegate
                {
                    if (!_pinnedOpen)
                    {
                        HideImmediately();
                    }
                };
            }

            ApplyScaledLayout();
        }

        public event EventHandler RefreshRequested;

        protected override bool ShowWithoutActivation
        {
            get { return false; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                if (!WindowChrome.SupportsNativeRounding)
                {
                    // Windows 10 後備：無 DWM 圓角時改用傳統視窗陰影。
                    parameters.ClassStyle |= CsDropShadow;
                }
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            _nativeChrome = WindowChrome.ApplyRoundedPopupChrome(Handle, Palette.Border);
            UpdateRoundedRegion();
        }

        public void SetLoading(bool value)
        {
            var completed = _loading && !value;
            var refreshHadFocus = _refreshButton.Focused;
            _loading = value;
            _refreshButton.Enabled = !value;
            _refreshButton.IsSpinning = value;
            if (value && refreshHadFocus)
            {
                ActiveControl = null;
            }

            if (value)
            {
                _loadingStarted = _motionClock.ElapsedMilliseconds;
                if (Visible && _animationsEnabled)
                {
                    EnsureMotionTimer();
                }
            }
            else
            {
                _refreshButton.Rotation = 0;
                if (completed && Visible && _animationsEnabled)
                {
                    _feedbackStarted = _motionClock.ElapsedMilliseconds;
                    EnsureMotionTimer();
                }
            }

            _refreshButton.Invalidate();
            Invalidate(new Rectangle(0, 0, ClientSize.Width, Px(HeaderHeight)));
            Invalidate(new Rectangle(0, ClientSize.Height - Px(FooterHeight), ClientSize.Width, Px(FooterHeight)));
        }

        public void SetSnapshot(UsageSnapshot snapshot)
        {
            ScheduleUsageAnimations(snapshot);
            _snapshot = snapshot;
            var heightBefore = Height;
            ApplyScaledLayout();
            UpdateAccessibleSummary(snapshot);

            if (Visible)
            {
                if (_placedAtLanding)
                {
                    // 落點開啟的面板：更新時留在原地（頂錨），僅夾回工作區。
                    ClampToWorkingArea();
                }
                else if (Height != heightBefore)
                {
                    // 托盤位（底錨）：只有高度變化時才需要重新對位。
                    PositionNearTray();
                }
            }
            Invalidate();
        }

        /// <summary>高度變化後確保面板完整落在工作區內（位置盡量不動）。</summary>
        private void ClampToWorkingArea()
        {
            var workingArea = Screen.FromPoint(Location).WorkingArea;
            var x = Math.Min(Math.Max(Location.X, workingArea.Left + 8), Math.Max(workingArea.Left + 8, workingArea.Right - Width - 12));
            var y = Math.Min(Math.Max(Location.Y, workingArea.Top + 8), Math.Max(workingArea.Top + 8, workingArea.Bottom - Height - 12));
            var target = new Point(x, y);
            if (Location != target)
            {
                Location = target;
            }
            _restLocation = Location;
        }

        public void ShowNearTray()
        {
            if (_revealStarted >= 0)
            {
                FinishReveal();
            }
            var wasVisible = Visible;
            _placedAtLanding = false;
            PositionNearTray();

            if (!wasVisible)
            {
                if (_animationsEnabled)
                {
                    _windowMotion = WindowMotion.Entering;
                    _windowMotionStarted = _motionClock.ElapsedMilliseconds;
                    _windowStartOpacity = 0;
                    _windowStartOffset = Px(6);
                    Opacity = 0;
                    Location = new Point(_restLocation.X, _restLocation.Y + _windowStartOffset);
                }
                else
                {
                    Opacity = 1;
                    Location = _restLocation;
                }
                Show();
            }
            else if (_animationsEnabled && _windowMotion == WindowMotion.Exiting)
            {
                _windowMotion = WindowMotion.Entering;
                _windowMotionStarted = _motionClock.ElapsedMilliseconds;
                _windowStartOpacity = Opacity;
                _windowStartOffset = Location.Y - _restLocation.Y;
            }

            if (!wasVisible && _animationsEnabled)
            {
                _sparkRevealStarted = _motionClock.ElapsedMilliseconds; // 趨勢線描線進場。
            }

            // 已經上屏，這時才知道面板真正落在哪一面螢幕：重新確認版面放得下。
            EnsureFitsCurrentScreen();

            Activate();
            BringToFront();
            ActiveControl = null;

            if (_animationsEnabled && (_windowMotion != WindowMotion.None || _loading || _sparkRevealStarted >= 0))
            {
                EnsureMotionTimer();
            }
        }

        /// <summary>
        /// 液態揭示進場：面板定位於液滴落點，以落點為圓心的水波
        /// （Region 圓形擴散）揭示開啟；完成後恢復原生圓角 chrome。
        /// </summary>
        public void ShowLiquidReveal(Point origin)
        {
            if (_revealStarted >= 0)
            {
                FinishReveal();
            }
            var wasVisible = Visible;
            _placedAtLanding = true;
            _windowMotion = WindowMotion.None;
            Opacity = 1;
            PositionForReveal(origin);

            if (_animationsEnabled)
            {
                WindowChrome.SetCornerRounding(Handle, false);
                _revealStarted = _motionClock.ElapsedMilliseconds;
                _sparkRevealStarted = _revealStarted; // 趨勢線隨面板一同描線進場。
                ApplyRevealRegion(0);
                if (!wasVisible)
                {
                    Show();
                    // 首次顯示可能觸發 DPI 遷移（尺寸/位置重算）：
                    // 以遷移後的實際尺寸重新對準落點並重算水波圓心。
                    PositionForReveal(origin);
                    ApplyRevealRegion(0);
                }
                EnsureMotionTimer();
            }
            else if (!wasVisible)
            {
                Show();
                PositionForReveal(origin);
            }

            Activate();
            BringToFront();
            ActiveControl = null;
        }

        /// <summary>面板定位於液滴落點（頂邊中心對齊、夾工作區），並記下水波圓心。</summary>
        private void PositionForReveal(Point origin)
        {
            var workingArea = Screen.FromPoint(origin).WorkingArea;
            _restLocation = new Point(
                Math.Min(Math.Max(origin.X - Width / 2, workingArea.Left + 8), Math.Max(workingArea.Left + 8, workingArea.Right - Width - 12)),
                Math.Min(Math.Max(origin.Y - Px(6), workingArea.Top + 8), Math.Max(workingArea.Top + 8, workingArea.Bottom - Height - 12)));
            Location = _restLocation;
            _revealCenterClient = new Point(
                Math.Min(Math.Max(origin.X - _restLocation.X, 0), Width),
                Math.Min(Math.Max(origin.Y - _restLocation.Y, 0), Height));
        }

        /// <summary>以落點為圓心的圓形 Region，半徑依進度擴散至覆蓋全窗。</summary>
        private void ApplyRevealRegion(double progress)
        {
            var maxRadius = Math.Sqrt((double)Width * Width + Height * (double)Height);
            var radius = Math.Max(Px(8), (int)Math.Round(maxRadius * progress));
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(
                    _revealCenterClient.X - radius,
                    _revealCenterClient.Y - radius,
                    radius * 2,
                    radius * 2);
                var stale = Region;
                Region = new Region(path);
                if (stale != null)
                {
                    stale.Dispose();
                }
            }
        }

        private void FinishReveal()
        {
            _revealStarted = -1;
            UpdateRoundedRegion(); // Win11：清 Region；Win10：恢復圓角 Region。
            if (_nativeChrome)
            {
                WindowChrome.SetCornerRounding(Handle, true);
            }
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var hint = "";
            var hoverTop = -1;
            foreach (var entry in _bucketTops)
            {
                if (e.Y >= entry - Px(6) && e.Y < entry - Px(6) + Px(RowH))
                {
                    hoverTop = entry;
                    break;
                }
            }
            foreach (var entry in _hoverHints.Values)
            {
                if (entry.Key.Contains(e.Location))
                {
                    hint = entry.Value;
                    break;
                }
            }
            if (hoverTop != _hoverBucketTop)
            {
                var previous = _hoverBucketTop;
                _hoverBucketTop = hoverTop;
                if (previous >= 0)
                {
                    Invalidate(new Rectangle(0, previous - Px(6), ClientSize.Width, Px(RowH)));
                }
                if (hoverTop >= 0)
                {
                    Invalidate(new Rectangle(0, hoverTop - Px(6), ClientSize.Width, Px(RowH)));
                }
            }
            if (!string.Equals(hint, _activeHint, StringComparison.Ordinal))
            {
                _activeHint = hint;
                _toolTip.SetToolTip(this, hint.Length == 0 ? null : hint);
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoverBucketTop >= 0)
            {
                var previous = _hoverBucketTop;
                _hoverBucketTop = -1;
                Invalidate(new Rectangle(0, previous - Px(6), ClientSize.Width, Px(RowH)));
            }
            base.OnMouseLeave(e);
        }

        /// <summary>切換釘選：釘住時失焦不關閉，圖示轉為實心並以品牌色提示。</summary>
        private void TogglePinnedOpen()
        {
            _pinnedOpen = !_pinnedOpen;
            _pinButton.Glyph = _pinnedOpen ? "\uE840" : "\uE718";
            _pinButton.ForeColor = _pinnedOpen ? Palette.Codex : Palette.SecondaryText;
            _pinButton.Invalidate();
        }

        public void Dismiss()
        {
            if (!Visible)
            {
                return;
            }
            if (_revealStarted >= 0)
            {
                FinishReveal();
            }
            if (!_animationsEnabled)
            {
                HideImmediately();
                return;
            }
            if (_windowMotion == WindowMotion.Exiting)
            {
                return;
            }

            _windowMotion = WindowMotion.Exiting;
            _windowMotionStarted = _motionClock.ElapsedMilliseconds;
            _windowStartOpacity = Opacity;
            _windowStartOffset = Location.Y - _restLocation.Y;
            EnsureMotionTimer();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            _scale = e.DeviceDpiNew / 96f;
            base.OnDpiChanged(e);
            ApplyScaledLayout();
            if (Visible && _revealStarted < 0)
            {
                PositionNearTray(); // 水波揭示進場時位置由落點決定，不跳回托盤位。
            }
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Dismiss();
                return true;
            }
            if (keyData == (Keys.Control | Keys.R))
            {
                RequestRefresh();
                return true;
            }
            if (keyData == (Keys.Control | Keys.C))
            {
                CopyUsageSummary();
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            if (!_nativeChrome && ClipTouchesBorder(e.ClipRectangle))
            {
                using (var borderPen = new Pen(Palette.Border))
                using (var borderPath = NativeVisuals.RoundedRectangle(
                    new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), Px(10)))
                {
                    graphics.DrawPath(borderPen, borderPath);
                }
            }

            var headerBounds = new Rectangle(0, 0, ClientSize.Width, Px(HeaderHeight));
            if (e.ClipRectangle.IntersectsWith(headerBounds))
            {
                DrawHeader(graphics);
            }

            var codex = _snapshot == null ? null : _snapshot.Codex;
            var claude = _snapshot == null ? null : _snapshot.Claude;
            var antigravity = _snapshot == null ? null : _snapshot.Antigravity;
            // 卡片式版面：每個供應商一張圓角卡片，窗底與卡面分出層次。
            var codexTop = Px(HeaderHeight) + Px(GapH);
            var codexHeight = Px(CardHeadH + DisplayCount(codex) * RowH + PadBottomH);
            var codexBounds = new Rectangle(Px(CardMargin), codexTop, ClientSize.Width - Px(CardMargin) * 2, codexHeight);
            if (e.ClipRectangle.IntersectsWith(codexBounds))
            {
                DrawProvider(graphics, codex, codexBounds, Palette.Codex, BrandIconKind.Codex);
            }

            var claudeTop = codexTop + codexHeight + Px(GapH);
            var claudeHeight = Px(CardHeadH + DisplayCount(claude) * RowH + PadBottomH);
            var claudeBounds = new Rectangle(Px(CardMargin), claudeTop, ClientSize.Width - Px(CardMargin) * 2, claudeHeight);
            if (e.ClipRectangle.IntersectsWith(claudeBounds))
            {
                DrawProvider(graphics, claude, claudeBounds, Palette.Claude, BrandIconKind.Claude);
            }

            var antigravityTop = claudeTop + claudeHeight + Px(GapH);
            var antigravityHeight = Px(CardHeadH + DisplayCount(antigravity) * RowH + PadBottomH);
            var antigravityBounds = new Rectangle(Px(CardMargin), antigravityTop, ClientSize.Width - Px(CardMargin) * 2, antigravityHeight);
            if (e.ClipRectangle.IntersectsWith(antigravityBounds))
            {
                DrawProvider(graphics, antigravity, antigravityBounds, Palette.Antigravity, BrandIconKind.Antigravity);
            }

            var footerBounds = new Rectangle(0, ClientSize.Height - Px(FooterHeight), ClientSize.Width, Px(FooterHeight));
            if (e.ClipRectangle.IntersectsWith(footerBounds))
            {
                DrawFooter(graphics);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _motionTimer.Dispose();
                _motionClock.Stop();
                _toolTip.Dispose();
                _titleFont.Dispose();
                _subtitleFont.Dispose();
                _providerFont.Dispose();
                _statusFont.Dispose();
                _bucketLabelFont.Dispose();
                _valueFont.Dispose();
                _valueStrongFont.Dispose();
                _detailFont.Dispose();
                _emptyFont.Dispose();
                _footerFont.Dispose();
                _smallGlyphFont.Dispose();
                _refreshButton.Font.Dispose();
                _closeButton.Font.Dispose();
                _pinButton.Font.Dispose();
            }
            base.Dispose(disposing);
        }

        /// <summary>Ctrl+C：複製三模型剩餘摘要到剪貼簿，並以浮動提示回饋。</summary>
        private void CopyUsageSummary()
        {
            var codexUsed = FirstBucketUsed(_snapshot == null ? null : _snapshot.Codex);
            var claudeUsed = FirstBucketUsed(_snapshot == null ? null : _snapshot.Claude);
            var antigravityUsed = FirstBucketUsed(_snapshot == null ? null : _snapshot.Antigravity);
            var summary = string.Format(
                CultureInfo.CurrentCulture,
                "Codex 剩 {0} · Claude 剩 {1} · Antigravity 剩 {2}",
                codexUsed.HasValue ? Math.Round(100 - codexUsed.Value) + "%" : "--",
                claudeUsed.HasValue ? Math.Round(100 - claudeUsed.Value) + "%" : "--",
                antigravityUsed.HasValue ? Math.Round(100 - antigravityUsed.Value) + "%" : "--");
            try
            {
                Clipboard.SetText(summary);
                _toolTip.Show("已複製：" + summary, this, Px(20), ClientSize.Height - Px(38), 1400);
            }
            catch
            {
            }
        }

        private static double? FirstBucketUsed(ProviderSnapshot provider)
        {
            return provider != null && provider.IsAvailable && provider.Buckets.Count > 0
                ? Math.Max(0, Math.Min(100, provider.Buckets[0].UsedPercent))
                : (double?)null;
        }

        private int EdgeInset()
        {
            // 自繪 1px 外框（Windows 10 後備）時，表面填充需避開邊框；原生 chrome 不需要。
            return _nativeChrome ? 0 : Px(1);
        }

        private void DrawHeader(Graphics graphics)
        {
            var inset = EdgeInset();
            using (var headerBrush = new SolidBrush(Palette.HeaderSurface))
            using (var titleBrush = new SolidBrush(Palette.PrimaryText))
            using (var subtitleBrush = new SolidBrush(Palette.SecondaryText))
            {
                graphics.FillRectangle(headerBrush, inset, inset, ClientSize.Width - inset * 2, Px(HeaderHeight) - inset);
                graphics.DrawString("模型用量", _titleFont, titleBrush, Px(20), Px(10));
                graphics.DrawString("Codex、Claude 與 Antigravity", _subtitleFont, subtitleBrush, Px(21), Px(39));

                // 標頭迷你三環儀表：與托盤、小工具同語彙的即時總覽。
                if (_snapshot != null)
                {
                    var gaugeSize = Px(26);
                    var gaugeLeft = ClientSize.Width - Px(122) - gaugeSize - Px(14);
                    var gaugeTop = (Px(HeaderHeight) - gaugeSize) / 2;
                    var codexUsed = FirstBucketUsed(_snapshot.Codex);
                    var claudeUsed = FirstBucketUsed(_snapshot.Claude);
                    var antigravityUsed = FirstBucketUsed(_snapshot.Antigravity);
                    var previousSmoothing = graphics.SmoothingMode;
                    graphics.SmoothingMode = SmoothingMode.AntiAlias;
                    var stroke = Math.Max(1.6f, gaugeSize * 0.09f);
                    TrayIconFactory.DrawGaugeRing(
                        graphics,
                        new RectangleF(gaugeLeft + gaugeSize * 0.05f, gaugeTop + gaugeSize * 0.05f, gaugeSize * 0.90f, gaugeSize * 0.90f),
                        stroke,
                        codexUsed,
                        Palette.Codex);
                    TrayIconFactory.DrawGaugeRing(
                        graphics,
                        new RectangleF(gaugeLeft + gaugeSize * 0.20f, gaugeTop + gaugeSize * 0.20f, gaugeSize * 0.60f, gaugeSize * 0.60f),
                        stroke,
                        claudeUsed,
                        Palette.Claude);
                    TrayIconFactory.DrawGaugeRing(
                        graphics,
                        new RectangleF(gaugeLeft + gaugeSize * 0.35f, gaugeTop + gaugeSize * 0.35f, gaugeSize * 0.30f, gaugeSize * 0.30f),
                        stroke,
                        antigravityUsed,
                        Palette.Antigravity);
                    graphics.SmoothingMode = previousSmoothing;
                }
                if (_loading)
                {
                    var loadingFormat = new StringFormat
                    {
                        Alignment = StringAlignment.Far,
                        LineAlignment = StringAlignment.Center
                    };
                    graphics.DrawString("更新中…", _subtitleFont, subtitleBrush, RectF(206, 37, 132, 18), loadingFormat);
                    loadingFormat.Dispose();
                }
            }
            // 標頭與 Codex 色帶之間不再另畫分隔線；色帶頂緣的全寬亮邊線即為分界，避免雙線重疊。
        }

        private void DrawProvider(
            Graphics graphics,
            ProviderSnapshot provider,
            Rectangle cardBounds,
            Color accent,
            BrandIconKind iconKind)
        {
            var top = cardBounds.Top;
            // 卡片：圓角面（頂部微亮漸層）＋ 1px 邊框，取代原本的全寬色帶與 hairline。
            using (var cardBrush = new LinearGradientBrush(
                new Rectangle(cardBounds.X, cardBounds.Y - 1, cardBounds.Width, cardBounds.Height + 2),
                NativeVisuals.Lighten(Palette.ProviderSurface, 0.03),
                Palette.ProviderSurface,
                LinearGradientMode.Vertical))
            using (var cardPath = NativeVisuals.RoundedRectangle(cardBounds, Px(CardRadius)))
            using (var cardPen = new Pen(Palette.Border))
            using (var cardBorderPath = NativeVisuals.RoundedRectangle(
                new Rectangle(cardBounds.X, cardBounds.Y, cardBounds.Width - 1, cardBounds.Height - 1), Px(CardRadius)))
            {
                graphics.FillPath(cardBrush, cardPath);
                graphics.DrawPath(cardPen, cardBorderPath);
            }

            var cardLeft = cardBounds.Left;
            var cardRight = cardBounds.Right;
            var headerBounds = new Rectangle(cardLeft, top, cardBounds.Width, Px(BandH));
            if (graphics.IsVisible(headerBounds))
            {
                var headerY = top + Px(CardHeadPadY);
                var iconSize = Px(CardIconSize);
                var hairline = Math.Max(1, Px(1));
                using (var shadowBrush = new SolidBrush(Palette.BandShadow))
                {
                    // 卡片頭與內容的細分隔線。
                    graphics.FillRectangle(shadowBrush, cardLeft + Px(10), headerBounds.Bottom - hairline, cardBounds.Width - Px(20), hairline);
                }
                NativeVisuals.DrawBrandIcon(
                    graphics,
                    iconKind,
                    new Rectangle(cardLeft + Px(_compact ? 14 : 16), headerY, iconSize, iconSize));

                using (var dotBrush = new SolidBrush(ProviderDotColor(provider)))
                using (var dotBorder = new Pen(Palette.ProviderSurface, Px(2)))
                using (var nameBrush = new SolidBrush(Palette.PrimaryText))
                using (var statusBrush = new SolidBrush(Palette.SecondaryText))
                {
                    {
                        var dotSize = Px(_compact ? 6 : 7);
                        var dot = new Rectangle(
                            cardLeft + Px(_compact ? 32 : 38),
                            headerY + Px(_compact ? 17 : 21),
                            dotSize,
                            dotSize);
                        graphics.FillEllipse(dotBrush, dot);
                        graphics.DrawEllipse(dotBorder, dot);
                    }

                    var name = provider == null
                        ? (iconKind == BrandIconKind.Codex ? "Codex" : (iconKind == BrandIconKind.Claude ? "Claude Code" : "Antigravity"))
                        : provider.Name;
                    graphics.DrawString(name, _providerFont, nameBrush, cardLeft + Px(_compact ? 46 : 54), headerY + Px(_compact ? 0 : 2));

                    var status = FriendlyStatus(provider, iconKind);
                    var measured = graphics.MeasureString(status, _statusFont);
                    var statusWidth = Math.Min(Px(222), (int)Math.Ceiling(measured.Width));
                    var statusRight = cardRight - Px(16);
                    var statusLeft = statusRight - statusWidth;
                    var statusFormat = new StringFormat
                    {
                        Alignment = StringAlignment.Far,
                        LineAlignment = StringAlignment.Center,
                        Trimming = StringTrimming.EllipsisCharacter,
                        FormatFlags = StringFormatFlags.NoWrap
                    };
                    graphics.DrawString(
                        status,
                        _statusFont,
                        statusBrush,
                        new RectangleF(statusLeft, headerY + Px(_compact ? 2 : 4), statusWidth, Px(18)),
                        statusFormat);
                    statusFormat.Dispose();
                }
            }

            var y = top + Px(CardHeadH);
            if (provider == null)
            {
                if (graphics.IsVisible(new Rectangle(cardLeft, y, cardBounds.Width, Px(RowH))))
                {
                    DrawEmpty(graphics, y, "正在等候第一次更新");
                }
                return;
            }
            if (!provider.IsAvailable || provider.Buckets.Count == 0)
            {
                if (graphics.IsVisible(new Rectangle(cardLeft, y, cardBounds.Width, Px(RowH))))
                {
                    DrawEmpty(graphics, y, string.IsNullOrWhiteSpace(provider.Error) ? "目前沒有可顯示的用量" : provider.Error);
                }
                return;
            }

            var visibleBuckets = DisplayCount(provider);
            for (var i = 0; i < Math.Min(visibleBuckets, provider.Buckets.Count); i++)
            {
                var bucket = provider.Buckets[i];
                if (graphics.IsVisible(new Rectangle(cardLeft, y, cardBounds.Width, Px(RowH))))
                {
                    DrawBucket(graphics, bucket, y, accent, GetAnimatedUsage(iconKind, i, bucket));
                }
                y += Px(RowH);
            }
        }

        // 以下幾何隨密度切換；標準值即原本的常數，緊湊值只壓縮間距不縮字級。
        private int BandH { get { return _compact ? CompactBandHeight : ProviderBandHeight; } }
        private int CardHeadH { get { return _compact ? CompactHeaderHeight : ProviderHeaderHeight; } }
        private int RowH { get { return _compact ? CompactBucketHeight : BucketHeight; } }
        private int GapH { get { return _compact ? CompactCardGap : CardGap; } }
        private int PadBottomH { get { return _compact ? CompactCardPadBottom : CardPadBottom; } }
        private int RowProgressY { get { return _compact ? 20 : 24; } }
        private int RowDetailY { get { return _compact ? 29 : 36; } }
        private int RowGlyphY { get { return _compact ? 30 : 37; } }
        private int RowSparkY { get { return _compact ? 31 : 38; } }
        private int CardHeadPadY { get { return _compact ? 8 : 10; } }
        private int CardIconSize { get { return _compact ? 24 : 28; } }

        private int CardLeft()
        {
            return Px(CardMargin);
        }

        private int CardRight()
        {
            return ClientSize.Width - Px(CardMargin);
        }

        private void DrawBucket(Graphics graphics, UsageBucket bucket, int top, Color accent, double animatedUsed)
        {
            _bucketTops.Add(top);
            // 懸停微亮：卡片內圓角高亮，回饋游標所在。
            if (top == _hoverBucketTop)
            {
                using (var hoverBrush = new SolidBrush(Color.FromArgb(90, Palette.BandHighlight)))
                using (var hoverPath = NativeVisuals.RoundedRectangle(
                    new Rectangle(CardLeft() + Px(5), top - Px(_compact ? 4 : 5), CardRight() - CardLeft() - Px(10), Px(RowH) - Px(2)), Px(5)))
                {
                    graphics.FillPath(hoverBrush, hoverPath);
                }
            }
            var left = CardLeft() + Px(16);
            var right = CardRight() - Px(16);
            var used = Math.Max(0, Math.Min(100, animatedUsed));
            var remaining = Math.Max(0, 100 - used);

            var strongColor = used >= 95 ? Palette.Critical : used >= 80 ? Palette.Warning : Palette.PrimaryText;
            using (var labelBrush = new SolidBrush(Palette.PrimaryText))
            using (var valueStrongBrush = new SolidBrush(strongColor))
            using (var valueBrush = new SolidBrush(Palette.SecondaryText))
            using (var detailBrush = new SolidBrush(Palette.MutedText))
            {
                DrawBucketLabel(graphics, bucket.Label, left, top, labelBrush, valueBrush);
                DrawUsageValue(graphics, right, top, used, remaining, valueStrongBrush, valueBrush);
                DrawProgress(graphics, new Rectangle(left, top + Px(RowProgressY), right - left, Px(6)), used, accent);
                DrawGlyph(
                    graphics,
                    "\uE823",
                    Palette.MutedText,
                    new RectangleF(left, top + Px(RowGlyphY), Px(14), Px(16)));
                graphics.DrawString(TimeUtil.ResetLabel(bucket), _detailFont, detailBrush, left + Px(19), top + Px(RowDetailY));

                // 重設時間 hover 提示：顯示絕對時刻（推估值標註）。
                if (bucket.ResetsAt.HasValue)
                {
                    var hint = string.Format(
                        CultureInfo.CurrentCulture,
                        "重設於 {0:yyyy/MM/dd HH:mm}",
                        bucket.ResetsAt.Value.ToLocalTime());
                    if (bucket.ResetEstimate != ResetEstimateKind.None)
                    {
                        hint += "（推估）";
                    }
                    if (!string.IsNullOrWhiteSpace(bucket.Detail))
                    {
                        // 額度涵蓋的模型（例如 Gemini Flash, Gemini Pro）只在懸停時揭露，保持版面安靜。
                        hint += " · " + bucket.Detail;
                    }
                    _hoverHints[top] = new KeyValuePair<Rectangle, string>(
                        new Rectangle(left, top + Px(RowDetailY - 2), Px(250), Px(22)), hint);
                }
                else
                {
                    _hoverHints.Remove(top);
                }

                // \u6642\u9418\u884C\u53F3\u5074\uFF1A7 \u5929\u8DA8\u52E2\u8FF7\u4F60\u5716\u8207\u8017\u76E1\u9810\u8B66\uFF08\u50C5\u6BCF\u9031 bucket \u6703\u6709\u8CC7\u6599\uFF09\u3002
                var infoRight = (float)right;
                if (bucket.TrendPoints != null)
                {
                    var sparkBounds = new Rectangle(right - Px(88), top + Px(RowSparkY), Px(88), Px(13));
                    DrawSparkline(graphics, sparkBounds, bucket.TrendPoints, accent);
                    infoRight = sparkBounds.Left - Px(12);
                }
                if (bucket.ProjectedExhaustAt.HasValue)
                {
                    var exhaustText = string.Format(
                        CultureInfo.CurrentCulture,
                        "\u7D04 {0:ddd HH:mm} \u7528\u7F44",
                        bucket.ProjectedExhaustAt.Value.ToLocalTime());
                    using (var warnBrush = new SolidBrush(Palette.Warning))
                    {
                        var textWidth = MeasureSegment(graphics, exhaustText, _detailFont);
                        DrawSegment(graphics, exhaustText, _detailFont, warnBrush, infoRight - textWidth, top + Px(RowDetailY));
                    }
                }
            }
        }

        /// <summary>\u5B89\u975C\u7684 7 \u5929\u8DA8\u52E2\u6298\u7DDA\uFF1A\u534A\u900F\u660E\u54C1\u724C\u8272\u7D30\u7DDA\u52A0\u672B\u7AEF\u5BE6\u5FC3\u9EDE\uFF0C\u7121\u8EF8\u7DDA\u8207\u586B\u5145\u3002</summary>
        private void DrawSparkline(Graphics graphics, Rectangle bounds, double[] points, Color accent)
        {
            // 描線進場：面板開啟後 600ms 內由左至右揭示（EaseOutCubic）。
            var revealProgress = 1.0;
            if (_sparkRevealStarted >= 0)
            {
                revealProgress = Motion.EaseOutCubic(
                    (_motionClock.ElapsedMilliseconds - _sparkRevealStarted) / (double)SparkRevealDuration);
            }
            var firstValid = -1;
            for (var i = 0; i < points.Length; i++)
            {
                if (!double.IsNaN(points[i]))
                {
                    firstValid = i;
                    break;
                }
            }
            if (firstValid < 0 || firstValid >= points.Length - 1)
            {
                return;
            }

            var vertices = new List<PointF>(points.Length - firstValid);
            for (var i = firstValid; i < points.Length; i++)
            {
                var value = double.IsNaN(points[i]) ? 0 : points[i];
                vertices.Add(new PointF(
                    bounds.X + bounds.Width * (i / (float)(points.Length - 1)),
                    bounds.Bottom - (float)(value / 100.0) * bounds.Height));
            }

            var previousClip = graphics.Clip;
            if (revealProgress < 1)
            {
                graphics.SetClip(new RectangleF(
                    bounds.X, bounds.Y - Px(4),
                    (float)(bounds.Width * revealProgress), bounds.Height + Px(8)));
            }
            using (var linePen = new Pen(Color.FromArgb(150, accent), Math.Max(1f, Px(1) * 1.25f)))
            {
                linePen.StartCap = LineCap.Round;
                linePen.EndCap = LineCap.Round;
                linePen.LineJoin = LineJoin.Round;
                graphics.DrawLines(linePen, vertices.ToArray());
            }

            if (revealProgress >= 1)
            {
                var lastVertex = vertices[vertices.Count - 1];
                using (var dotBrush = new SolidBrush(accent))
                {
                    var radius = Math.Max(1.5f, Px(2) * 0.8f);
                    graphics.FillEllipse(dotBrush, lastVertex.X - radius, lastVertex.Y - radius, radius * 2, radius * 2);
                }
            }
            graphics.Clip = previousClip;
        }

        private void DrawBucketLabel(
            Graphics graphics,
            string label,
            int left,
            int top,
            Brush primaryBrush,
            Brush secondaryBrush)
        {
            const string separator = " · ";
            var separatorIndex = label.IndexOf(separator, StringComparison.Ordinal);
            if (separatorIndex > 0)
            {
                var primary = label.Substring(0, separatorIndex);
                var secondary = label.Substring(separatorIndex);
                var primaryWidth = MeasureSegment(graphics, primary, _bucketLabelFont);
                var secondaryWidth = MeasureSegment(graphics, secondary, _valueFont);
                if (primaryWidth + secondaryWidth <= Px(252))
                {
                    DrawSegment(graphics, primary, _bucketLabelFont, primaryBrush, left, top);
                    DrawSegment(graphics, secondary, _valueFont, secondaryBrush, left + primaryWidth, top + Px(1));
                    return;
                }
            }

            using (var format = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                graphics.DrawString(
                    label,
                    _bucketLabelFont,
                    primaryBrush,
                    new RectangleF(left, top, Px(252), Px(20)),
                    format);
            }
        }

        private void DrawUsageValue(
            Graphics graphics,
            int right,
            int top,
            double used,
            double remaining,
            Brush strongBrush,
            Brush regularBrush)
        {
            if (used <= 0.05)
            {
                var amount = "100%";
                var suffix = "可用";
                var amountWidth = MeasureSegment(graphics, amount, _valueStrongFont);
                var suffixWidth = MeasureSegment(graphics, suffix, _valueFont);
                var gap = Px(5);
                var left = right - amountWidth - gap - suffixWidth;
                DrawSegment(graphics, amount, _valueStrongFont, strongBrush, left, top);
                DrawSegment(graphics, suffix, _valueFont, regularBrush, left + amountWidth + gap, top);
                return;
            }

            var remainingPrefix = "剩餘";
            var remainingValue = string.Format(CultureInfo.CurrentCulture, "{0:0}%", remaining);
            var separator = "·";
            var prefix = "已用";
            var usedValue = string.Format(CultureInfo.CurrentCulture, "{0:0}%", used);
            var separatorWidth = MeasureSegment(graphics, separator, _valueFont);
            var remainingPrefixWidth = MeasureSegment(graphics, remainingPrefix, _valueFont);
            var remainingWidth = MeasureSegment(graphics, remainingValue, _valueStrongFont);
            var prefixWidth = MeasureSegment(graphics, prefix, _valueFont);
            var usedWidth = MeasureSegment(graphics, usedValue, _valueFont);
            var smallGap = Px(4);
            var groupGap = Px(7);
            var totalWidth = remainingPrefixWidth + smallGap + remainingWidth
                + groupGap + separatorWidth + groupGap
                + prefixWidth + smallGap + usedWidth;
            var x = right - totalWidth;

            DrawSegment(graphics, remainingPrefix, _valueFont, regularBrush, x, top);
            x += remainingPrefixWidth + smallGap;
            DrawSegment(graphics, remainingValue, _valueStrongFont, strongBrush, x, top);
            x += remainingWidth + groupGap;
            DrawSegment(graphics, separator, _valueFont, regularBrush, x, top);
            x += separatorWidth + groupGap;
            DrawSegment(graphics, prefix, _valueFont, regularBrush, x, top);
            x += prefixWidth + smallGap;
            DrawSegment(graphics, usedValue, _valueFont, regularBrush, x, top);
        }

        private static float MeasureSegment(Graphics graphics, string text, Font font)
        {
            return graphics.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width;
        }

        private static void DrawSegment(Graphics graphics, string text, Font font, Brush brush, float x, float y)
        {
            graphics.DrawString(text, font, brush, new PointF(x, y), StringFormat.GenericTypographic);
        }

        private void DrawProgress(Graphics graphics, Rectangle bounds, double used, Color accent)
        {
            var radius = Math.Max(1, bounds.Height / 2);
            using (var trackPath = NativeVisuals.RoundedRectangle(bounds, radius))
            using (var trackBrush = new SolidBrush(Palette.Track))
            {
                graphics.FillPath(trackBrush, trackPath);
            }

            DrawThresholdTick(graphics, bounds, 80);
            DrawThresholdTick(graphics, bounds, 95);

            var width = (int)Math.Round(bounds.Width * used / 100.0);
            if (width <= 0)
            {
                return;
            }
            var fillColor = used >= 95 ? Palette.Critical : used >= 80 ? Palette.Warning : accent;
            var fillBounds = new Rectangle(bounds.X, bounds.Y, Math.Max(bounds.Height, Math.Min(bounds.Width, width)), bounds.Height);
            // 漸層範圍略大於填充路徑，避免 LinearGradientBrush 邊緣繞回產生接縫。
            var gradientBounds = new Rectangle(
                fillBounds.X, fillBounds.Y - 1, fillBounds.Width, fillBounds.Height + 2);
            using (var fillPath = NativeVisuals.RoundedRectangle(fillBounds, radius))
            using (var fillBrush = new LinearGradientBrush(
                gradientBounds,
                NativeVisuals.Lighten(fillColor, 0.14),
                fillColor,
                LinearGradientMode.Vertical))
            {
                graphics.FillPath(fillBrush, fillPath);
            }

            // 液面光點：填充前緣一枚柔和亮點，強化「液位」語彙。
            if (fillBounds.Width > bounds.Height * 2 && fillBounds.Right < bounds.Right - 2)
            {
                var glowRadius = bounds.Height * 0.62f;
                var glowCenterX = fillBounds.Right - glowRadius;
                var glowCenterY = bounds.Y + bounds.Height / 2f;
                using (var glowBrush = new SolidBrush(Color.FromArgb(140, NativeVisuals.Lighten(fillColor, 0.42))))
                {
                    graphics.FillEllipse(
                        glowBrush,
                        glowCenterX - glowRadius / 2f, glowCenterY - glowRadius / 2f,
                        glowRadius, glowRadius);
                }
            }
        }

        private void DrawThresholdTick(Graphics graphics, Rectangle bounds, double percent)
        {
            // 80% / 95% 警戒刻度：畫在軌道上，讓距離警戒線的餘裕可一眼判讀；
            // 用量達標後由填充自然覆蓋。
            var tickWidth = Math.Max(1, Px(1));
            var x = bounds.X + (int)Math.Round(bounds.Width * percent / 100.0) - tickWidth / 2;
            if (x <= bounds.X || x + tickWidth >= bounds.Right)
            {
                return;
            }

            var previousMode = graphics.SmoothingMode;
            graphics.SmoothingMode = SmoothingMode.None;
            using (var tickBrush = new SolidBrush(Palette.TrackBorder))
            {
                graphics.FillRectangle(tickBrush, x, bounds.Y, tickWidth, bounds.Height);
            }
            graphics.SmoothingMode = previousMode;
        }

        private void DrawEmpty(Graphics graphics, int top, string message)
        {
            using (var brush = new SolidBrush(Palette.SecondaryText))
            {
                var format = new StringFormat
                {
                    Trimming = StringTrimming.EllipsisCharacter,
                    FormatFlags = StringFormatFlags.NoWrap
                };
                graphics.DrawString(
                    message, _emptyFont, brush,
                    new RectangleF(CardLeft() + Px(16), top + Px(16), CardRight() - CardLeft() - Px(32), Px(24)), format);
                format.Dispose();
            }
        }

        private void DrawFooter(Graphics graphics)
        {
            var y = ClientSize.Height - Px(FooterHeight);
            var textColor = Palette.SecondaryText;
            var glyph = "\uE895";
            var updated = _snapshot == null
                ? "尚未更新"
                : string.Format(CultureInfo.CurrentCulture, "更新於 {0:HH:mm}", _snapshot.UpdatedAt.ToLocalTime());

            if (_feedbackStarted >= 0)
            {
                var progress = (_motionClock.ElapsedMilliseconds - _feedbackStarted) / (double)FeedbackDuration;
                textColor = Motion.Lerp(Palette.Success, Palette.SecondaryText, Motion.EaseOutQuart(progress));
                glyph = "\uE73E";
                if (_snapshot != null)
                {
                    updated = string.Format(CultureInfo.CurrentCulture, "已更新 · {0:HH:mm}", _snapshot.UpdatedAt.ToLocalTime());
                }
            }

            var inset = EdgeInset();
            using (var background = new SolidBrush(Palette.FooterSurface))
            using (var divider = new Pen(Palette.Border))
            using (var brush = new SolidBrush(textColor))
            using (var secondaryBrush = new SolidBrush(Palette.SecondaryText))
            {
                graphics.FillRectangle(background, inset, y, ClientSize.Width - inset * 2, Px(FooterHeight) - inset);
                graphics.DrawLine(divider, Px(20), y, ClientSize.Width - Px(20), y);
                DrawGlyph(graphics, glyph, textColor, new RectangleF(Px(20), y + Px(13), Px(15), Px(18)));
                graphics.DrawString(updated, _footerFont, brush, Px(41), y + Px(13));

                const string localText = "本機唯讀";
                var textWidth = (int)Math.Ceiling(graphics.MeasureString(localText, _footerFont).Width);
                var textLeft = ClientSize.Width - Px(20) - textWidth;
                DrawGlyph(graphics, "\uE72E", Palette.SecondaryText, new RectangleF(textLeft - Px(23), y + Px(13), Px(15), Px(18)));
                graphics.DrawString(localText, _footerFont, secondaryBrush, textLeft, y + Px(13));
            }
        }

        private void DrawGlyph(Graphics graphics, string glyph, Color color, RectangleF bounds)
        {
            using (var brush = new SolidBrush(color))
            {
                var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                graphics.DrawString(glyph, _smallGlyphFont, brush, bounds, format);
                format.Dispose();
            }
        }

        private IconButton CreateIconButton(
            string glyph,
            string accessibleName,
            Color hoverColor,
            Color pressedColor,
            Color borderColor,
            Color hoverGlyphColor,
            int tabIndex)
        {
            var button = new IconButton
            {
                Glyph = glyph,
                BackColor = Palette.HeaderSurface,
                ForeColor = Palette.SecondaryText,
                FlatStyle = FlatStyle.Flat,
                Font = NativeVisuals.CreateGlyphFont(10.5f, GraphicsUnit.Point),
                TabStop = true,
                TabIndex = tabIndex,
                Cursor = Cursors.Default,
                AccessibleName = accessibleName,
                AccessibleDescription = accessibleName,
                AccessibleRole = AccessibleRole.PushButton,
                UseVisualStyleBackColor = false,
                HoverBorderColor = borderColor,
                HoverGlyphColor = hoverGlyphColor,
                FocusBorderColor = Palette.Focus
            };
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = hoverColor;
            button.FlatAppearance.MouseDownBackColor = pressedColor;
            return button;
        }

        private void ApplyScaledLayout()
        {
            ResolveCardBuckets();
            var desiredSize = new Size(Px(PopupWidth), Px(ResolveLogicalHeight(_snapshot)));
            var sizeChanged = ClientSize != desiredSize;
            var desiredPadding = new Padding(Px(1));
            if (Padding != desiredPadding)
            {
                Padding = desiredPadding;
            }
            if (sizeChanged)
            {
                ClientSize = desiredSize;
            }
            var buttonTop = Px((HeaderHeight - 32) / 2);
            _pinButton.Location = new Point(ClientSize.Width - Px(122), buttonTop);
            _pinButton.Size = new Size(Px(32), Px(32));
            _refreshButton.Location = new Point(ClientSize.Width - Px(84), buttonTop);
            _refreshButton.Size = new Size(Px(32), Px(32));
            _closeButton.Location = new Point(ClientSize.Width - Px(46), buttonTop);
            _closeButton.Size = new Size(Px(32), Px(32));
            _hoverHints.Clear();
            _bucketTops.Clear();
            if (sizeChanged || Region == null)
            {
                UpdateRoundedRegion();
            }
        }

        private void PositionNearTray()
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            var screen = taskbar == IntPtr.Zero ? Screen.PrimaryScreen : Screen.FromHandle(taskbar);
            var workingArea = screen.WorkingArea;
            _restLocation = new Point(
                Math.Max(workingArea.Left + 8, workingArea.Right - Width - 12),
                Math.Max(workingArea.Top + 8, workingArea.Bottom - Height - 12));
            if (_windowMotion == WindowMotion.None)
            {
                Location = _restLocation;
            }
        }

        private void HideImmediately()
        {
            if (_revealStarted >= 0)
            {
                FinishReveal();
            }
            _sparkRevealStarted = -1;
            _windowMotion = WindowMotion.None;
            Opacity = 1;
            Hide();
            if (!_loading && !HasActiveUsageAnimation())
            {
                _motionTimer.Stop();
            }
        }

        private void RequestRefresh()
        {
            if (_loading)
            {
                return;
            }
            var handler = RefreshRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void ScheduleUsageAnimations(UsageSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            var now = _motionClock.ElapsedMilliseconds;
            var activeKeys = new HashSet<string>(StringComparer.Ordinal);
            ScheduleProvider(snapshot.Codex, BrandIconKind.Codex, activeKeys, now);
            ScheduleProvider(snapshot.Claude, BrandIconKind.Claude, activeKeys, now);
            ScheduleProvider(snapshot.Antigravity, BrandIconKind.Antigravity, activeKeys, now);

            var staleKeys = new List<string>();
            foreach (var key in _usageAnimations.Keys)
            {
                if (!activeKeys.Contains(key))
                {
                    staleKeys.Add(key);
                }
            }
            foreach (var key in staleKeys)
            {
                _usageAnimations.Remove(key);
            }
        }

        private void ScheduleProvider(
            ProviderSnapshot provider,
            BrandIconKind kind,
            ISet<string> activeKeys,
            long now)
        {
            if (provider == null || !provider.IsAvailable)
            {
                return;
            }

            for (var i = 0; i < Math.Min(MaxBucketsPerCard, provider.Buckets.Count); i++)
            {
                var bucket = provider.Buckets[i];
                var target = Math.Max(0, Math.Min(100, bucket.UsedPercent));
                var key = BucketKey(kind, i, bucket.Label);
                activeKeys.Add(key);

                AnimatedNumber animation;
                if (!_usageAnimations.TryGetValue(key, out animation))
                {
                    var start = _animationsEnabled && Visible ? 0 : target;
                    animation = new AnimatedNumber(start);
                    _usageAnimations[key] = animation;
                }

                if (_animationsEnabled && Visible && Math.Abs(animation.Current - target) > 0.01)
                {
                    animation.AnimateTo(target, now, UsageDuration);
                    EnsureMotionTimer();
                }
                else
                {
                    animation.Jump(target);
                }
            }
        }

        private double GetAnimatedUsage(BrandIconKind kind, int index, UsageBucket bucket)
        {
            AnimatedNumber animation;
            return _usageAnimations.TryGetValue(BucketKey(kind, index, bucket.Label), out animation)
                ? animation.Current
                : bucket.UsedPercent;
        }

        private static string BucketKey(BrandIconKind kind, int index, string label)
        {
            return kind + "|" + index.ToString(CultureInfo.InvariantCulture) + "|" + label;
        }

        private void MotionTick(object sender, EventArgs eventArgs)
        {
            var now = _motionClock.ElapsedMilliseconds;
            var keepRunning = UpdateWindowMotion(now);

            if (_revealStarted >= 0)
            {
                var revealTime = (now - _revealStarted) / (double)RevealDuration;
                if (revealTime >= 1)
                {
                    FinishReveal();
                }
                else
                {
                    ApplyRevealRegion(Motion.EaseOutCubic(revealTime));
                    keepRunning = true;
                    Invalidate();
                    Update(); // 揭示與內容同幀呈現，水波邊緣更順。
                }
            }

            if (_sparkRevealStarted >= 0)
            {
                var sparkTime = (now - _sparkRevealStarted) / (double)SparkRevealDuration;
                if (sparkTime >= 1)
                {
                    _sparkRevealStarted = -1;
                    Invalidate();
                }
                else
                {
                    keepRunning = true;
                    Invalidate(); // 描線進行中逐幀重繪趨勢線。
                }
            }
            var usageChanged = false;

            foreach (var animation in _usageAnimations.Values)
            {
                var previous = animation.Current;
                var active = animation.Update(now);
                keepRunning = keepRunning || active;
                usageChanged = usageChanged || Math.Abs(previous - animation.Current) > 0.001;
            }

            if (usageChanged && Visible)
            {
                InvalidateAnimatedUsage();
            }

            if (_loading && Visible && _animationsEnabled)
            {
                var elapsed = Math.Max(0, now - _loadingStarted);
                _refreshButton.Rotation = (float)((elapsed % RefreshRotationDuration) * 360.0 / RefreshRotationDuration);
                _refreshButton.Invalidate();
                keepRunning = true;
            }

            if (_feedbackStarted >= 0)
            {
                var feedbackProgress = (now - _feedbackStarted) / (double)FeedbackDuration;
                if (feedbackProgress >= 1 || !Visible)
                {
                    _feedbackStarted = -1;
                }
                else
                {
                    keepRunning = true;
                }
                Invalidate(new Rectangle(0, ClientSize.Height - Px(FooterHeight), ClientSize.Width, Px(FooterHeight)));
            }

            if (!keepRunning)
            {
                _motionTimer.Stop();
            }
        }

        private bool UpdateWindowMotion(long now)
        {
            if (_windowMotion == WindowMotion.None)
            {
                return false;
            }

            if (_windowMotion == WindowMotion.Entering)
            {
                var progress = (now - _windowMotionStarted) / (double)EnterDuration;
                if (progress >= 1)
                {
                    Opacity = 1;
                    Location = _restLocation;
                    _windowMotion = WindowMotion.None;
                    return false;
                }

                var eased = Motion.EaseOutQuart(progress);
                Opacity = Motion.Lerp(_windowStartOpacity, 1, eased);
                Location = new Point(
                    _restLocation.X,
                    _restLocation.Y + (int)Math.Round(_windowStartOffset * (1 - eased)));
                return true;
            }

            var exitProgress = (now - _windowMotionStarted) / (double)ExitDuration;
            if (exitProgress >= 1)
            {
                HideImmediately();
                return false;
            }

            var exitEased = Motion.EaseInQuart(exitProgress);
            Opacity = Motion.Lerp(_windowStartOpacity, 0, exitEased);
            var targetOffset = Px(4);
            var offset = Motion.Lerp(_windowStartOffset, targetOffset, exitEased);
            Location = new Point(_restLocation.X, _restLocation.Y + (int)Math.Round(offset));
            return true;
        }

        private void EnsureMotionTimer()
        {
            if (!_motionTimer.Enabled)
            {
                _motionTimer.Start();
            }
        }

        private void InvalidateAnimatedUsage()
        {
            var codex = _snapshot == null ? null : _snapshot.Codex;
            var claude = _snapshot == null ? null : _snapshot.Claude;
            var antigravity = _snapshot == null ? null : _snapshot.Antigravity;
            var codexTop = Px(HeaderHeight) + Px(GapH);
            InvalidateProviderUsage(codex, codexTop);
            var claudeTop = codexTop
                + Px(CardHeadH + DisplayCount(codex) * RowH + PadBottomH) + Px(GapH);
            InvalidateProviderUsage(claude, claudeTop);
            var antigravityTop = claudeTop
                + Px(CardHeadH + DisplayCount(claude) * RowH + PadBottomH) + Px(GapH);
            InvalidateProviderUsage(antigravity, antigravityTop);
        }

        private void InvalidateProviderUsage(ProviderSnapshot provider, int top)
        {
            if (provider == null || !provider.IsAvailable || provider.Buckets.Count == 0)
            {
                return;
            }

            var bucketTop = top + Px(CardHeadH);
            for (var i = 0; i < DisplayCount(provider); i++)
            {
                Invalidate(new Rectangle(
                    Px(18),
                    bucketTop - Px(1),
                    ClientSize.Width - Px(36),
                    Px(36)));
                bucketTop += Px(RowH);
            }
        }

        private bool ClipTouchesBorder(Rectangle clip)
        {
            var edge = Math.Max(2, Px(2));
            return clip.Left < edge
                || clip.Top < edge
                || clip.Right > ClientSize.Width - edge
                || clip.Bottom > ClientSize.Height - edge;
        }

        private bool HasActiveUsageAnimation()
        {
            foreach (var animation in _usageAnimations.Values)
            {
                if (Math.Abs(animation.Current - animation.Target) > 0.001)
                {
                    return true;
                }
            }
            return false;
        }

        private void UpdateAccessibleSummary(UsageSnapshot snapshot)
        {
            if (snapshot == null)
            {
                AccessibleDescription = "尚未更新用量資料";
                return;
            }

            var summary = new StringBuilder();
            AppendProviderSummary(summary, snapshot.Codex);
            AppendProviderSummary(summary, snapshot.Claude);
            AppendProviderSummary(summary, snapshot.Antigravity);
            AccessibleDescription = summary.ToString().Trim();
            AccessibilityNotifyClients(AccessibleEvents.DescriptionChange, -1);
        }

        private static void AppendProviderSummary(StringBuilder summary, ProviderSnapshot provider)
        {
            if (provider == null)
            {
                return;
            }
            summary.Append(provider.Name).Append("。");
            if (!provider.IsAvailable)
            {
                summary.Append(string.IsNullOrWhiteSpace(provider.Error) ? "用量不可用。" : provider.Error + "。");
                return;
            }
            foreach (var bucket in provider.Buckets)
            {
                summary.Append(bucket.Label)
                    .Append("已用 ")
                    .Append(bucket.UsedPercent.ToString("0", CultureInfo.CurrentCulture))
                    .Append("%，")
                    .Append(TimeUtil.ResetLabel(bucket))
                    .Append("。");
            }
        }

        /// <summary>
        /// 供應商狀態點顏色：資料新鮮（60 分鐘內）為綠、偏舊或時間未知為橘、不可用為灰。
        /// </summary>
        private static Color ProviderDotColor(ProviderSnapshot provider)
        {
            if (provider == null || !provider.IsAvailable)
            {
                return Palette.MutedText;
            }
            if (provider.UpdatedAt == DateTimeOffset.MinValue)
            {
                return Palette.Warning;
            }
            var age = DateTimeOffset.Now - provider.UpdatedAt.ToLocalTime();
            return age.TotalMinutes >= 60 ? Palette.Warning : Palette.Success;
        }

        private static string FriendlyStatus(ProviderSnapshot provider, BrandIconKind kind)
        {
            if (provider == null)
            {
                return "等待更新";
            }
            var status = string.IsNullOrWhiteSpace(provider.Status) ? "資料不可用" : provider.Status;
            if (kind == BrandIconKind.Codex && status.Contains("官方 app-server"))
            {
                var plan = status.Replace(" · 官方 app-server", string.Empty)
                    .Replace("官方 app-server", string.Empty)
                    .Trim();
                return string.IsNullOrEmpty(plan) ? "官方即時資料" : "官方即時資料 · " + plan;
            }
            if (kind == BrandIconKind.Claude)
            {
                return status
                    .Replace("Claude Desktop 快取", "快取資料")
                    .Replace("Claude Code 狀態列", "狀態列資料")
                    .Replace(" 分前", " 分鐘前");
            }
            if (kind == BrandIconKind.Antigravity)
            {
                return status
                    .Replace("Language Server 即時連線", "即時連線")
                    .Replace("本機快取資料", "快取資料");
            }
            return status;
        }

        private void UpdateRoundedRegion()
        {
            if (_nativeChrome)
            {
                // Windows 11 由 DWM 裁切圓角與繪製陰影，不需要 Region 剪裁。
                var stale = Region;
                if (stale != null)
                {
                    Region = null;
                    stale.Dispose();
                }
                return;
            }

            if (_scale <= 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                return;
            }
            using (var path = NativeVisuals.RoundedRectangle(
                new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
                Px(8)))
            {
                var previous = Region;
                Region = new Region(path);
                if (previous != null)
                {
                    previous.Dispose();
                }
            }
        }

        private int Px(int logicalPixels)
        {
            return (int)Math.Round(logicalPixels * _scale);
        }

        private RectangleF RectF(int x, int y, int width, int height)
        {
            return new RectangleF(Px(x), Px(y), Px(width), Px(height));
        }

        /// <summary>目前版面（已依螢幕高度收斂密度與列數）的邏輯高度。</summary>
        private int ResolveLogicalHeight(UsageSnapshot snapshot)
        {
            // 首次開啟（尚無資料）時三張卡各一列空狀態，面板不預留多餘留白。
            return snapshot == null
                ? CardsLogicalHeight(1, 1, 1, _compact)
                : CardsLogicalHeight(_cardBuckets[0], _cardBuckets[1], _cardBuckets[2], _compact);
        }

        /// <summary>不受螢幕高度限制的自然高度：標準密度、三張卡各自完整列出額度（每卡上限四列）。</summary>
        private static int CalculateLogicalHeight(UsageSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return 690;
            }
            return CardsLogicalHeight(
                NaturalBucketCount(snapshot.Codex),
                NaturalBucketCount(snapshot.Claude),
                NaturalBucketCount(snapshot.Antigravity),
                false);
        }

        private static int CardsLogicalHeight(int codexRows, int claudeRows, int antigravityRows, bool compact)
        {
            var head = compact ? CompactHeaderHeight : ProviderHeaderHeight;
            var row = compact ? CompactBucketHeight : BucketHeight;
            var gap = compact ? CompactCardGap : CardGap;
            var pad = compact ? CompactCardPadBottom : CardPadBottom;
            return HeaderHeight
                + gap + head + codexRows * row + pad
                + gap + head + claudeRows * row + pad
                + gap + head + antigravityRows * row + pad
                + gap + FooterHeight;
        }

        /// <summary>
        /// 依目前螢幕工作區的可用高度決定版面密度與三張卡各顯示幾列額度。
        /// 三家供應商合計最多九列（Codex 3 + Claude 2 + Antigravity 4），
        /// 標準間距在多數螢幕（尤其 125%–200% 縮放）會超出工作區，因此順序是：
        /// 標準密度放得下就用標準；放不下先整體改用緊湊節奏（字級不變，只壓縮間距），
        /// 連緊湊都放不下時才逐列收斂 —— 各卡輪流讓出較次要的額度，永遠保留代表限額。
        /// </summary>
        private void ResolveCardBuckets()
        {
            var natural = new int[3];
            natural[0] = NaturalBucketCount(_snapshot == null ? null : _snapshot.Codex);
            natural[1] = NaturalBucketCount(_snapshot == null ? null : _snapshot.Claude);
            natural[2] = NaturalBucketCount(_snapshot == null ? null : _snapshot.Antigravity);

            // 一律以「實體像素」比對工作區：面板目前的縮放與目標螢幕不同時，
            // 用邏輯像素換算會失準，正是面板被螢幕下緣裁掉的成因。
            var workingArea = LayoutScreen().WorkingArea;
            var scale = LayoutScale(workingArea);
            var availablePhysical = workingArea.Height - 24;
            if (availablePhysical < 200)
            {
                availablePhysical = 200;
            }

            // 第一步：標準密度能完整呈現就維持標準；否則切換緊湊節奏。
            _compact = PhysicalHeight(
                CardsLogicalHeight(natural[0], natural[1], natural[2], false), scale) > availablePhysical;

            for (var i = 0; i < 3; i++)
            {
                _cardBuckets[i] = 1;
            }

            // 第二步：以目前密度逐列輪流補滿，直到放不下為止。
            var rowPhysical = PhysicalHeight(_compact ? CompactBucketHeight : BucketHeight, scale);
            var height = PhysicalHeight(CardsLogicalHeight(1, 1, 1, _compact), scale);
            var grew = true;
            while (grew)
            {
                grew = false;
                for (var i = 0; i < 3; i++)
                {
                    if (_cardBuckets[i] >= natural[i])
                    {
                        continue;
                    }
                    if (height + rowPhysical > availablePhysical)
                    {
                        return;
                    }
                    _cardBuckets[i]++;
                    height += rowPhysical;
                    grew = true;
                }
            }
        }

        /// <summary>
        /// 版面計算要用的螢幕：面板已在畫面上就取它實際所在的那一面；
        /// 尚未定位時取工作列所在的螢幕（面板開啟後會落在那裡）。
        /// 直接用 Location 推算會在面板尚未定位時誤取主螢幕 —— 在主螢幕較高的
        /// 多螢幕環境下會算出過大的可用高度，面板於是在實際顯示的螢幕上被裁掉。
        /// </summary>
        private Screen LayoutScreen()
        {
            if (Visible && IsHandleCreated)
            {
                return Screen.FromControl(this);
            }
            try
            {
                var taskbar = FindWindow("Shell_TrayWnd", null);
                if (taskbar != IntPtr.Zero)
                {
                    return Screen.FromHandle(taskbar);
                }
            }
            catch
            {
            }
            return Screen.PrimaryScreen;
        }

        /// <summary>目標螢幕的真實 DPI 倍率；取不到時退回面板目前的縮放。</summary>
        private float LayoutScale(Rectangle workingArea)
        {
            var scale = WindowChrome.GetPointScale(new Point(
                workingArea.Left + workingArea.Width / 2,
                workingArea.Top + workingArea.Height / 2));
            if (scale > 0)
            {
                return scale;
            }
            return _scale > 0 ? _scale : 1f;
        }

        private static int PhysicalHeight(int logical, float scale)
        {
            return (int)Math.Round(logical * scale);
        }

        /// <summary>顯示後再確認一次版面是否合乎目前螢幕；需要改變就重算並重新對位。</summary>
        private void EnsureFitsCurrentScreen()
        {
            var before = Height;
            ApplyScaledLayout();
            if (Height != before)
            {
                if (_placedAtLanding)
                {
                    ClampToWorkingArea();
                }
                else
                {
                    PositionNearTray();
                }
            }
        }

        private static int NaturalBucketCount(ProviderSnapshot provider)
        {
            return provider == null || !provider.IsAvailable || provider.Buckets.Count == 0
                ? 1
                : Math.Min(MaxBucketsPerCard, provider.Buckets.Count);
        }

        private int DisplayCount(ProviderSnapshot provider)
        {
            var natural = NaturalBucketCount(provider);
            var index = CardIndex(provider);
            return index < 0 ? natural : Math.Max(1, Math.Min(natural, _cardBuckets[index]));
        }

        private int CardIndex(ProviderSnapshot provider)
        {
            if (_snapshot == null || provider == null)
            {
                return -1;
            }
            if (ReferenceEquals(provider, _snapshot.Codex)) return 0;
            if (ReferenceEquals(provider, _snapshot.Claude)) return 1;
            if (ReferenceEquals(provider, _snapshot.Antigravity)) return 2;
            return -1;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        private enum WindowMotion
        {
            None,
            Entering,
            Exiting
        }

        private sealed class IconButton : Button
        {
            private bool _hovered;
            private bool _pressed;

            public IconButton()
            {
                SetStyle(
                    ControlStyles.UserPaint
                    | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.ResizeRedraw,
                    true);
                UpdateStyles();
            }

            public string Glyph { get; set; }
            public float Rotation { get; set; }
            public bool IsSpinning { get; set; }
            public Color HoverBorderColor { get; set; }
            public Color FocusBorderColor { get; set; }
            public Color HoverGlyphColor { get; set; }

            protected override void OnPaint(PaintEventArgs eventArgs)
            {
                var background = _pressed
                    ? FlatAppearance.MouseDownBackColor
                    : _hovered ? FlatAppearance.MouseOverBackColor : BackColor;
                var glyphColor = Enabled ? (_hovered ? HoverGlyphColor : ForeColor) : Palette.MutedText;
                var borderColor = Color.Transparent;
                if (Focused && ShowFocusCues)
                {
                    borderColor = FocusBorderColor;
                }
                else if (_hovered)
                {
                    borderColor = HoverBorderColor;
                }

                eventArgs.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                eventArgs.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

                var scaleUnit = Math.Max(1, (int)Math.Round(Math.Min(Width, Height) / 32f));
                eventArgs.Graphics.Clear(BackColor);
                var buttonBounds = Rectangle.Inflate(ClientRectangle, -scaleUnit, -scaleUnit);
                using (var buttonPath = NativeVisuals.RoundedRectangle(buttonBounds, scaleUnit * 6))
                using (var backgroundBrush = new SolidBrush(background))
                {
                    eventArgs.Graphics.FillPath(backgroundBrush, buttonPath);
                    if (borderColor.A > 0)
                    {
                        using (var borderPen = new Pen(borderColor, scaleUnit))
                        {
                            eventArgs.Graphics.DrawPath(borderPen, buttonPath);
                        }
                    }
                }

                var state = eventArgs.Graphics.Save();
                eventArgs.Graphics.TranslateTransform(Width / 2f, Height / 2f);
                if (IsSpinning)
                {
                    eventArgs.Graphics.RotateTransform(Rotation);
                }
                eventArgs.Graphics.TranslateTransform(-Width / 2f, -Height / 2f);

                if (IsSpinning)
                {
                    DrawSpinner(eventArgs.Graphics, glyphColor, scaleUnit);
                }
                else
                {
                    using (var brush = new SolidBrush(glyphColor))
                    {
                        var format = new StringFormat
                        {
                            Alignment = StringAlignment.Center,
                            LineAlignment = StringAlignment.Center
                        };
                        eventArgs.Graphics.DrawString(Glyph, Font, brush, ClientRectangle, format);
                        format.Dispose();
                    }
                }
                eventArgs.Graphics.Restore(state);
            }

            protected override void OnMouseEnter(EventArgs eventArgs)
            {
                _hovered = true;
                Invalidate();
                base.OnMouseEnter(eventArgs);
            }

            protected override void OnMouseLeave(EventArgs eventArgs)
            {
                _hovered = false;
                _pressed = false;
                Invalidate();
                base.OnMouseLeave(eventArgs);
            }

            protected override void OnMouseDown(MouseEventArgs eventArgs)
            {
                if (eventArgs.Button == MouseButtons.Left)
                {
                    _pressed = true;
                    Invalidate();
                }
                base.OnMouseDown(eventArgs);
            }

            protected override void OnMouseUp(MouseEventArgs eventArgs)
            {
                _pressed = false;
                Invalidate();
                base.OnMouseUp(eventArgs);
            }

            protected override void OnGotFocus(EventArgs eventArgs)
            {
                Invalidate();
                base.OnGotFocus(eventArgs);
            }

            protected override void OnLostFocus(EventArgs eventArgs)
            {
                Invalidate();
                base.OnLostFocus(eventArgs);
            }

            private void DrawSpinner(Graphics graphics, Color color, int scaleUnit)
            {
                var spinnerSize = Math.Max(
                    scaleUnit * 12,
                    (int)Math.Round(Math.Min(Width, Height) * 0.58f));
                var strokeWidth = Math.Max(scaleUnit * 1.5f, spinnerSize / 9f);
                var bounds = new RectangleF(
                    (Width - spinnerSize) / 2f,
                    (Height - spinnerSize) / 2f,
                    spinnerSize,
                    spinnerSize);

                using (var pen = new Pen(color, strokeWidth))
                using (var brush = new SolidBrush(color))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    graphics.DrawArc(pen, bounds, -60, 280);

                    var angle = 220d * Math.PI / 180d;
                    var radius = spinnerSize / 2f - strokeWidth * 0.55f;
                    var centerX = Width / 2f;
                    var centerY = Height / 2f;
                    var dotSize = Math.Max(scaleUnit * 2f, strokeWidth * 1.05f);
                    var dotX = centerX + (float)(Math.Cos(angle) * radius) - dotSize / 2f;
                    var dotY = centerY + (float)(Math.Sin(angle) * radius) - dotSize / 2f;
                    graphics.FillEllipse(brush, dotX, dotY, dotSize, dotSize);
                }
            }

        }
    }
}
