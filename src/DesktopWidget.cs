using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexClaudeUsage
{
    /// <summary>
    /// 常駐桌面的迷你用量小工具：顯示 Codex 與 Claude 的每週剩餘比例與進度條，
    /// 可拖曳擺放並記住位置。不搶焦點、不出現在工作列與 Alt+Tab；
    /// 雙擊開啟主面板，右鍵提供最上層切換與隱藏。
    /// </summary>
    internal sealed class DesktopWidget : Form
    {
        private int _dynamicWidgetWidth = 240;
        private int WidgetWidth { get { return _dynamicWidgetWidth; } }
        private const int WidgetHeight = 40; // 與圓點同高：膠囊即圓的水平延伸，形變只走單軸。
        private const int DotSize = 40;
        private const int ClickThreshold = 4;
        private const int AutoCollapseDelay = 1500;
        private const int ExpandDuration = 280;     // 進場豐滿（含過衝回彈）。
        private const int CollapseDuration = 220;   // 出場乾脆：exit 快於 enter 是系統動畫的節奏語法。
        private const int ContentSlideDistance = 8; // 內容淡入時的抽屜滑入距離（邏輯 px）。
        private const int MotionIntervalIdle = 16;
        private const int MotionIntervalMorph = 16; // 形變主要由 QueueMorphFrame 自驅動；計時器只當後備節拍。
        private const int WsExNoActivate = 0x08000000;
        private const int WsExToolWindow = 0x00000080;
        private const int WsExLayered = 0x00080000;
        private const int GwlExStyle = -20;
        private const int UlwAlpha = 2;
        private const int CsDropShadow = 0x00020000;

        private readonly WidgetState _state;
        private Font _valueFont;
        private Font _percentFont;
        private readonly ContextMenuStrip _menu;
        private ToolStripMenuItem _summaryItem;
        private readonly ToolStripMenuItem _topMostItem;
        private readonly ToolTip _toolTip;

        private const int UsageAnimationDuration = 360;
        private const int FadeInDuration = 160;

        /// <summary>靜止不透明度：由選單「不透明度」設定並持久化。</summary>
        private double RestOpacity
        {
            get { return _state.Opacity; }
        }

        private readonly Timer _motionTimer;
        private readonly Timer _collapseTimer;
        private readonly Stopwatch _motionClock;
        private readonly AnimatedNumber _codexAnimation = new AnimatedNumber(0);
        private readonly AnimatedNumber _claudeAnimation = new AnimatedNumber(0);
        private readonly AnimatedNumber _antigravityAnimation = new AnimatedNumber(0);
        private readonly bool _animationsEnabled;

        private float _scale;
        private bool _collapsed;
        private double _expandProgress; // 0 = 圓點，1 = 膠囊；圓即最小的膠囊，形狀連續。
        private double _morphWidthProgress;  // 液態形變：寬與高走不同曲線。
        private double _morphHeightProgress;
        private double _morphContentAlpha;   // 內容交叉淡化進度。
        private long _morphStarted = -1;
        private bool _expanding;
        private int _morphAnchorX;
        private int _morphAnchorY; // 圓點（錨定端圓）左上角的位置基準。
        private bool _mirrored;    // 圓點位於螢幕右半：膠囊向左展開、雙環錨於右端。
        private Bitmap _cardSnapshot; // 右側內容快照；左端雙環每幀即時繪製。
        private Bitmap _morphCanvas;      // 形變合成畫布（預乘 ARGB，形變開始建一次、每幀重繪）。
        private IntPtr _morphMemDc;       // UpdateLayeredWindow 來源 DC 與 32bpp DIB。
        private IntPtr _morphDib;
        private IntPtr _morphDibBits;
        private IntPtr _morphOldBitmap;
        private Graphics _morphGraphics; // 形變畫布的繪圖介面：整段動畫共用一個，不逐幀建立。
        private int _morphCanvasW;
        private int _morphCanvasH;
        private bool _layeredMorphActive;
        private int _morphLastWidth = -1;   // 上一幀實際呈現的尺寸與內容透明度，
        private int _morphLastHeight = -1;  // 用來略過與前一幀像素完全相同的重繪。
        private int _morphLastAlpha = -1;
        private bool _morphPumpQueued;      // 自驅動幀排程是否已排入訊息佇列。
        private long _morphFrameCost;       // 上一幀（含等待合成節拍）耗時，用來判斷 DwmFlush 是否真的在節流。
        private bool _usageAnimating;       // 本輪是否仍有用量數值在動（跳幀判斷用）。
        private ImageAttributes _alphaAttributes; // 內容淡入用的 alpha 矩陣，重複使用不逐幀配置。
        private ColorMatrix _alphaMatrix;
        private UsageSnapshot _snapshot;
        private bool _codexHasValue;
        private bool _claudeHasValue;
        private bool _antigravityHasValue;
        private bool _dragging;
        private bool _moved;
        private bool _hovered;
        private bool _pressed; // 圓點按壓態：按下即回饋，不等放開。
        private bool _suppressNextClick; // 雙擊後續的 MouseUp 不觸發單擊切換。
        private bool _morphRetarget;          // 形變中被反向：從當前進度平滑改道。
        private double _morphFromWidth;
        private double _morphFromAlpha;
        private int _morphRetargetDuration;
        private long _fadeStarted = -1;
        private bool _highResTiming;
        private Point _dragStart;

        public DesktopWidget(WidgetState state)
        {
            _state = state;
            AutoScaleMode = AutoScaleMode.None;
            using (var graphics = CreateGraphics())
            {
                _scale = graphics.DpiX / 96f;
            }

            _animationsEnabled = Motion.IsEnabled;
            _motionClock = Stopwatch.StartNew();
            _motionTimer = new Timer { Interval = MotionIntervalIdle };
            _motionTimer.Tick += MotionTick;

            _collapsed = !state.Pinned;
            _expandProgress = _collapsed ? 0 : 1;
            _collapseTimer = new Timer { Interval = AutoCollapseDelay };
            _collapseTimer.Tick += delegate
            {
                _collapseTimer.Stop();
                if (!_state.Pinned && !_collapsed && !_dragging
                    && !ClientRectangle.Contains(PointToClient(Cursor.Position))
                    && (ContextMenuStrip == null || !ContextMenuStrip.Visible))
                {
                    SetCollapsed(true);
                }
            };

            _valueFont = NativeVisuals.CreateValueFont(11.5f, FontStyle.Bold);
            _percentFont = NativeVisuals.CreateValueFont(8f, FontStyle.Regular);
            _toolTip = new ToolTip
            {
                InitialDelay = 400,
                ReshowDelay = 100,
                AutoPopDelay = 6000
            };

            BackColor = Palette.ProviderSurface;
            FormBorderStyle = FormBorderStyle.None;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Codex + Claude + Antigravity 用量小工具";
            TopMost = _state.TopMost;
            DoubleBuffered = true;
            Opacity = _state.Opacity;
            Cursor = Cursors.SizeAll;
            AccessibleName = "Codex、Claude 與 Antigravity 每週用量小工具";
            AccessibleRole = AccessibleRole.Window;

            _topMostItem = new ToolStripMenuItem("保持在最上層")
            {
                CheckOnClick = true,
                Checked = _state.TopMost
            };
            _topMostItem.CheckedChanged += delegate
            {
                TopMost = _topMostItem.Checked;
                _state.TopMost = _topMostItem.Checked;
                _state.Save();
            };

            var pinnedItem = new ToolStripMenuItem("固定展開")
            {
                CheckOnClick = true,
                Checked = _state.Pinned
            };
            pinnedItem.CheckedChanged += delegate
            {
                _state.Pinned = pinnedItem.Checked;
                _state.Save();
                if (pinnedItem.Checked)
                {
                    SetCollapsed(false);
                }
                else if (!ClientRectangle.Contains(PointToClient(Cursor.Position)))
                {
                    SetCollapsed(true);
                }
            };

            var openItem = new ToolStripMenuItem("開啟用量面板")
            {
                Image = NativeVisuals.CreateMenuGlyph("\uE8A7", Palette.PrimaryText)
            };
            openItem.Font = new Font(openItem.Font, FontStyle.Bold);
            openItem.Click += delegate { OpenPanelNearWidget(); };

            var refreshItem = new ToolStripMenuItem("立即更新")
            {
                Image = NativeVisuals.CreateMenuGlyph("\uE72C", Palette.PrimaryText)
            };
            refreshItem.Click += delegate
            {
                var handler = RefreshRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }
            };

            var resetPositionItem = new ToolStripMenuItem("重設位置")
            {
                Image = NativeVisuals.CreateMenuGlyph("\uE81D", Palette.PrimaryText)
            };
            resetPositionItem.Click += delegate
            {
                _state.X = int.MinValue;
                _state.Y = int.MinValue;
                _state.Save();
                PlaceWindow();
                if (!_collapsed)
                {
                    UpdateMirrorFromPosition();
                }
                Invalidate();
            };

            var hideItem = new ToolStripMenuItem("隱藏小工具")
            {
                Image = NativeVisuals.CreateMenuGlyph("\uED1A", Palette.SecondaryText)
            };
            hideItem.Click += delegate { HideByUser(); };

            // 頂部用量摘要：迷你雙環縮圖＋兩組剩餘百分比，開啟選單時即時更新。
            _summaryItem = new ToolStripMenuItem("--")
            {
                Enabled = false,
                Tag = "usage-summary" // DarkMenuRenderer 依此分段上品牌色。
            };

            _menu = new ContextMenuStrip
            {
                BackColor = Palette.Surface,
                ForeColor = Palette.PrimaryText,
                Font = NativeVisuals.CreateUiFont(9f, FontStyle.Regular),
                ShowImageMargin = true,
                ImageScalingSize = new Size(18, 18),
                Padding = new Padding(2, 4, 2, 4),
                Renderer = new DarkMenuRenderer()
            };
            _menu.Items.Add(_summaryItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(openItem);
            _menu.Items.Add(refreshItem);
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(pinnedItem);
            _menu.Items.Add(_topMostItem);
            _menu.Items.Add(CreateOpacityMenu());
            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(resetPositionItem);
            _menu.Items.Add(hideItem);
            _menu.Opening += delegate { UpdateMenuSummary(); };
            MenuChrome.Attach(_menu);
            ContextMenuStrip = _menu;

            _dynamicWidgetWidth = MeasureRequiredExpandedLogicalWidth();
            ApplyScaledLayout();

            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        /// <summary>更新選單頂部的用量摘要（文字＋迷你雙環縮圖）。</summary>
        private void UpdateMenuSummary()
        {
            var codexUsed = _codexHasValue ? _codexAnimation.Target : (double?)null;
            var claudeUsed = _claudeHasValue ? _claudeAnimation.Target : (double?)null;
            var antigravityUsed = _antigravityHasValue ? _antigravityAnimation.Target : (double?)null;
            _summaryItem.Text = string.Format(
                CultureInfo.CurrentCulture,
                "Codex 剩 {0} · Claude 剩 {1} · AG 剩 {2}",
                codexUsed.HasValue ? Math.Round(100 - codexUsed.Value).ToString("0", CultureInfo.CurrentCulture) + "%" : "--",
                claudeUsed.HasValue ? Math.Round(100 - claudeUsed.Value).ToString("0", CultureInfo.CurrentCulture) + "%" : "--",
                antigravityUsed.HasValue ? Math.Round(100 - antigravityUsed.Value).ToString("0", CultureInfo.CurrentCulture) + "%" : "--");

            var previous = _summaryItem.Image;
            var bitmap = new Bitmap(24, 24);
            bitmap.SetResolution(96, 96);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                TrayIconFactory.DrawGaugeRing(
                    graphics, new RectangleF(1.5f, 1.5f, 21f, 21f), 2.2f, codexUsed, Palette.Codex);
                TrayIconFactory.DrawGaugeRing(
                    graphics, new RectangleF(4.5f, 4.5f, 15f, 15f), 2.2f, claudeUsed, Palette.Claude);
                TrayIconFactory.DrawGaugeRing(
                    graphics, new RectangleF(7.5f, 7.5f, 9f, 9f), 2.2f, antigravityUsed, Palette.Antigravity);
            }
            _summaryItem.Image = bitmap;
            if (previous != null)
            {
                previous.Dispose();
            }
        }

        /// <summary>「不透明度」子選單：100／96（預設）／85／75，選擇即生效並持久化。</summary>
        private ToolStripMenuItem CreateOpacityMenu()
        {
            var opacityItem = new ToolStripMenuItem("不透明度")
            {
                Image = NativeVisuals.CreateMenuGlyph("\uE706", Palette.PrimaryText)
            };
            var levels = new[] { 1.0, 0.96, 0.85, 0.75 };
            var labels = new[] { "100%", "96%（預設）", "85%", "75%" };
            for (var i = 0; i < levels.Length; i++)
            {
                var level = levels[i];
                var child = new ToolStripMenuItem(labels[i])
                {
                    Tag = level,
                    Checked = Math.Abs(_state.Opacity - level) < 0.005
                };
                child.Click += delegate(object sender, EventArgs args)
                {
                    var item = (ToolStripMenuItem)sender;
                    var value = (double)item.Tag;
                    _state.Opacity = value;
                    _state.Save();
                    if (_morphStarted < 0 && _fadeStarted < 0)
                    {
                        Opacity = value;
                    }
                    foreach (ToolStripItem sibling in opacityItem.DropDownItems)
                    {
                        var menuItem = sibling as ToolStripMenuItem;
                        if (menuItem != null)
                        {
                            menuItem.Checked = ReferenceEquals(menuItem, item);
                        }
                    }
                };
                opacityItem.DropDownItems.Add(child);
            }
            var dropDown = opacityItem.DropDown;
            dropDown.BackColor = Palette.Surface;
            dropDown.ForeColor = Palette.PrimaryText;
            dropDown.Font = NativeVisuals.CreateUiFont(9f, FontStyle.Regular);
            dropDown.Renderer = new DarkMenuRenderer();
            MenuChrome.Attach(dropDown);
            return opacityItem;
        }

        public event Action<Point> OpenAtRequested; // 於指定螢幕座標開啟詳細面板（液滴落點）。
        public event EventHandler RefreshRequested;   // 選單「立即更新」。
        public event EventHandler HiddenByUser;

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= WsExNoActivate | WsExToolWindow;
                if (!WindowChrome.SupportsNativeRounding)
                {
                    parameters.ClassStyle |= CsDropShadow;
                }
                return parameters;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // 小工具全程使用膠囊 Region 自訂形狀：關閉 DWM 圓角避免其蓋過裁切，
            // 僅保留深色框架設定；邊框由 OnPaint 自繪。
            WindowChrome.ApplyRoundedPopupChrome(Handle, Palette.Border);
            WindowChrome.SetCornerRounding(Handle, false);
            UpdateRoundedRegion();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            _scale = e.DeviceDpiNew / 96f;
            RecreateFonts();
            base.OnDpiChanged(e);
            _dynamicWidgetWidth = MeasureRequiredExpandedLogicalWidth();
            ApplyScaledLayout();
            ClampToScreen();
        }

        /// <summary>依記住的位置（或預設主螢幕右上）放置並顯示，不搶焦點；支援淡入。</summary>
        public void ShowWidget()
        {
            PlaceWindow();
            CheckAndApplyScreenDpi();
            if (!_collapsed)
            {
                UpdateMirrorFromPosition(); // 固定展開啟動：依位置決定內容方向。
                Invalidate();
            }
            if (_animationsEnabled && !Visible)
            {
                Opacity = 0;
                _fadeStarted = _motionClock.ElapsedMilliseconds;
                EnsureMotionTimer();
            }
            else
            {
                Opacity = RestOpacity;
            }
            Show();
            CheckAndApplyScreenDpi();
        }

        /// <summary>
        /// 依視窗所在位置之顯示器實體 DPI 即時校正縮放；若跨螢幕變更則重建字型與版面。
        /// </summary>
        private bool CheckAndApplyScreenDpi()
        {
            var center = new Point(Location.X + Width / 2, Location.Y + Height / 2);
            var actual = WindowChrome.GetPointScale(center);
            if (actual <= 0 && IsHandleCreated)
            {
                actual = WindowChrome.GetWindowScale(Handle);
            }
            if (actual <= 0)
            {
                actual = DeviceDpi / 96f;
            }
            if (actual > 0 && Math.Abs(actual - _scale) > 0.01f)
            {
                _scale = actual;
                RecreateFonts();
                _dynamicWidgetWidth = MeasureRequiredExpandedLogicalWidth();
                ApplyScaledLayout();
                ClampToScreen();
                Invalidate();
                Update();
                return true;
            }
            return false;
        }

        private void SyncDpiScale()
        {
            CheckAndApplyScreenDpi();
        }

        public void SetSnapshot(UsageSnapshot snapshot)
        {
            _snapshot = snapshot;
            var summary = BuildSummary(snapshot);
            if (!string.Equals(AccessibleDescription, summary, StringComparison.Ordinal))
            {
                AccessibleDescription = summary;
                _toolTip.SetToolTip(this, summary);
            }

            _codexHasValue = ScheduleUsageAnimation(
                _codexAnimation, WeeklyBucket(snapshot == null ? null : snapshot.Codex), _codexHasValue);
            _claudeHasValue = ScheduleUsageAnimation(
                _claudeAnimation, WeeklyBucket(snapshot == null ? null : snapshot.Claude), _claudeHasValue);
            _antigravityHasValue = ScheduleUsageAnimation(
                _antigravityAnimation, WeeklyBucket(snapshot == null ? null : snapshot.Antigravity), _antigravityHasValue);

            var oldWidth = _dynamicWidgetWidth;
            _dynamicWidgetWidth = MeasureRequiredExpandedLogicalWidth();
            if (oldWidth != _dynamicWidgetWidth && !_collapsed)
            {
                ApplyScaledLayout();
                ClampToScreen();
            }

            Invalidate();
        }

        /// <summary>資料更新時讓數值與進度條平滑過渡；首次資料或動畫停用時直接跳定。</summary>
        private bool ScheduleUsageAnimation(AnimatedNumber animation, UsageBucket bucket, bool hadValue)
        {
            if (bucket == null)
            {
                animation.Jump(0);
                return false;
            }

            var target = Math.Max(0, Math.Min(100, bucket.UsedPercent));
            if (_animationsEnabled && Visible && hadValue && Math.Abs(animation.Current - target) > 0.01)
            {
                animation.AnimateTo(target, _motionClock.ElapsedMilliseconds, UsageAnimationDuration);
                EnsureMotionTimer();
            }
            else
            {
                animation.Jump(target);
            }
            return true;
        }

        private void MotionTick(object sender, EventArgs eventArgs)
        {
            var now = _motionClock.ElapsedMilliseconds;
            var keepRunning = false;

            // 先推進用量數值，形變幀才會畫到最新的三環讀數。
            var codexActive = _codexAnimation.Update(now);
            var claudeActive = _claudeAnimation.Update(now);
            var antigravityActive = _antigravityAnimation.Update(now);
            _usageAnimating = codexActive || claudeActive || antigravityActive;

            if (_morphStarted >= 0)
            {
                var morphDuration = _expanding ? ExpandDuration : CollapseDuration;
                if (_morphRetarget)
                {
                    morphDuration = _morphRetargetDuration;
                }
                var morphTime = (now - _morphStarted) / (double)morphDuration;
                if (morphTime >= 1)
                {
                    _expandProgress = _expanding ? 1 : 0;
                    _morphWidthProgress = _expandProgress;
                    _morphHeightProgress = _expandProgress;
                    _morphContentAlpha = _expandProgress;
                    _morphStarted = -1;
                    _morphRetarget = false;
                    FinishLayeredMorph();
                }
                else
                {
                    if (_morphRetarget)
                    {
                        // 中斷改道：從凍結的當前進度補間到新目標（無過衝、無延遲）。
                        var target = _expanding ? 1.0 : 0.0;
                        var eased = Motion.EaseOutCubic(morphTime);
                        _morphWidthProgress = _morphFromWidth + (target - _morphFromWidth) * eased;
                        _morphContentAlpha = _morphFromAlpha + (target - _morphFromAlpha) * eased;
                        _morphHeightProgress = _morphWidthProgress;
                        _expandProgress = _morphWidthProgress;
                    }
                    // 液態曲線：展開時寬度帶過衝回彈先行、高度延遲充盈
                    //（圓被「拉」成短膠囊再脹滿）；收合反向 — 高度先收、寬度略遲。
                    else if (_expanding)
                    {
                        _morphWidthProgress = Motion.EaseOutBack(morphTime);
                        // 高度用 C2 連續的 SmootherStep：起步速度為零，
                        // 與「寬度先行」自然銜接、無節奏跳變。
                        _morphHeightProgress = Motion.SmootherStep((morphTime - 0.08) / 0.92);
                        _morphContentAlpha = Motion.SmoothStep((morphTime - 0.05) / 0.60); // 內容較形狀先就位，觀感更利落。
                        _expandProgress = Motion.EaseOutQuart(morphTime);
                    }
                    else
                    {
                        _morphHeightProgress = 1 - Motion.EaseOutQuart(morphTime / 0.88);
                        _morphWidthProgress = 1 - Motion.SmootherStep((morphTime - 0.08) / 0.92);
                        _morphContentAlpha = 1 - Motion.SmoothStep(morphTime / 0.6);
                        _expandProgress = 1 - Motion.EaseOutQuart(morphTime);
                    }
                    keepRunning = true;
                    PresentMorphFrame(); // ULW 原子呈現：大小＋位置＋內容一次交給 DWM。
                    QueueMorphFrame();   // 已對齊合成節拍，立刻排下一幀（不等計時器）。
                }
            }

            if (_fadeStarted >= 0)
            {
                var progress = (now - _fadeStarted) / (double)FadeInDuration;
                if (progress >= 1)
                {
                    Opacity = RestOpacity;
                    _fadeStarted = -1;
                }
                else
                {
                    Opacity = RestOpacity * Motion.EaseOutQuart(progress);
                    keepRunning = true;
                }
            }

            if (_usageAnimating)
            {
                keepRunning = true;
                if (_morphStarted < 0)
                {
                    Invalidate(); // 形變中由 ULW 逐幀呈現，不需要再走 WM_PAINT。
                }
            }

            if (!keepRunning)
            {
                _motionTimer.Stop();
                Invalidate();
            }
        }

        private void EnsureMotionTimer()
        {
            if (!_motionTimer.Enabled)
            {
                _motionTimer.Start();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            DrawCapsuleSurface(graphics);

            if (_morphStarted >= 0 && _cardSnapshot != null)
            {
                // 形變過渡（ULW 後備路徑）：錨定端雙環即時原位，內容淡入滑入。
                if (_mirrored)
                {
                    var state = graphics.Save();
                    graphics.TranslateTransform(ClientSize.Width - ClientSize.Height, 0);
                    DrawDotContent(graphics, ClientSize.Height);
                    graphics.Restore(state);
                }
                else
                {
                    DrawDotContent(graphics, ClientSize.Height);
                }
                var contentAlpha = (float)Motion.Clamp01(_morphContentAlpha);
                var slide = (int)Math.Round(Px(ContentSlideDistance) * (1f - contentAlpha));
                var snapshotX = _mirrored
                    ? ClientSize.Width - _cardSnapshot.Width + slide
                    : -slide;
                DrawImageAlpha(
                    graphics, _cardSnapshot,
                    snapshotX, (ClientSize.Height - _cardSnapshot.Height) / 2, contentAlpha);
                return;
            }

            if (_collapsed)
            {
                DrawDotContent(graphics, Math.Min(ClientSize.Width, ClientSize.Height));
            }
            else
            {
                DrawCardContent(graphics, ClientSize.Width, ClientSize.Height);
            }
        }

        /// <summary>膠囊面（頂部微亮漸層）與環繞邊框；收合時即為圓面。</summary>
        private void DrawCapsuleSurface(Graphics graphics)
        {
            DrawCapsuleSurfaceCore(graphics, ClientSize.Width, ClientSize.Height);
        }

        private void DrawCapsuleSurfaceCore(Graphics graphics, int width, int height)
        {
            var radius = Math.Min(width, height) / 2;
            var surfaceTop = NativeVisuals.Lighten(Palette.ProviderSurface, 0.04);
            var surfaceBottom = Palette.ProviderSurface;
            if (_pressed && _collapsed && _morphStarted < 0)
            {
                // 按壓回饋：按下瞬間表面微暗，等待放開觸發展開的期間即有反應。
                surfaceTop = Darken(surfaceTop, 0.07);
                surfaceBottom = Darken(surfaceBottom, 0.07);
            }
            using (var surfaceBrush = new LinearGradientBrush(
                new Rectangle(0, -1, width, height + 2),
                surfaceTop,
                surfaceBottom,
                LinearGradientMode.Vertical))
            using (var capsule = NativeVisuals.RoundedRectangle(
                new Rectangle(0, 0, width, height), radius))
            {
                graphics.FillPath(surfaceBrush, capsule);
            }

            using (var borderPen = new Pen(_hovered ? Palette.BorderStrong : Palette.Border))
            using (var borderPath = NativeVisuals.RoundedRectangle(
                new Rectangle(0, 0, width - 1, height - 1),
                Math.Max(1, Math.Min(width - 1, height - 1) / 2)))
            {
                graphics.DrawPath(borderPen, borderPath);
            }
        }

        private void DrawImageAlpha(Graphics graphics, Bitmap bitmap, int x, int y, float alpha)
        {
            if (alpha <= 0.01f)
            {
                return;
            }

            var destination = new Rectangle(x, y, bitmap.Width, bitmap.Height);
            var interpolation = graphics.InterpolationMode;
            var pixelOffset = graphics.PixelOffsetMode;
            // 快照是 1:1 貼上的，關掉插值與半像素取樣可省下 GDI+ 的縮放路徑。
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            try
            {
                if (alpha >= 0.99f)
                {
                    graphics.DrawImage(
                        bitmap, destination, 0, 0, bitmap.Width, bitmap.Height, GraphicsUnit.Pixel);
                    return;
                }
                if (_alphaMatrix == null)
                {
                    _alphaMatrix = new ColorMatrix();
                    _alphaAttributes = new ImageAttributes();
                }
                _alphaMatrix.Matrix33 = alpha;
                _alphaAttributes.SetColorMatrix(_alphaMatrix);
                graphics.DrawImage(
                    bitmap,
                    destination,
                    0, 0, bitmap.Width, bitmap.Height,
                    GraphicsUnit.Pixel,
                    _alphaAttributes);
            }
            finally
            {
                graphics.InterpolationMode = interpolation;
                graphics.PixelOffsetMode = pixelOffset;
            }
        }

        /// <summary>
        /// 膠囊內容（與圓同高的單行）：左端保留圓點雙環原位原尺寸 —
        /// 形變中圓的內容完全不動，右側像抽屜拉出兩組品牌色剩餘百分比。
        /// </summary>
        private void DrawCardContent(Graphics graphics, int width, int height)
        {
            if (_mirrored)
            {
                var state = graphics.Save();
                graphics.TranslateTransform(width - height, 0);
                DrawDotContent(graphics, height);
                graphics.Restore(state);
            }
            else
            {
                DrawDotContent(graphics, height);
            }
            DrawCardOverlay(graphics, width, height);
        }

        /// <summary>膠囊右側內容（品牌圖示＋剩餘數字），不含左端雙環 — 形變時獨立滑入淡入。</summary>
        private void DrawCardOverlay(Graphics graphics, int width, int height)
        {
            var codexBucket = WeeklyBucket(_snapshot == null ? null : _snapshot.Codex);
            var claudeBucket = WeeklyBucket(_snapshot == null ? null : _snapshot.Claude);
            var antigravityBucket = WeeklyBucket(_snapshot == null ? null : _snapshot.Antigravity);
            var codexUsed = _codexHasValue ? _codexAnimation.Current : (double?)null;
            var claudeUsed = _claudeHasValue ? _claudeAnimation.Current : (double?)null;
            var antigravityUsed = _antigravityHasValue ? _antigravityAnimation.Current : (double?)null;

            var codexText = RemainingText(codexUsed);
            var claudeText = RemainingText(claudeUsed);
            var antigravityText = RemainingText(antigravityUsed);
            var codexColor = ValueColor(codexBucket, codexUsed, Palette.Codex);
            var claudeColor = ValueColor(claudeBucket, claudeUsed, Palette.Claude);
            var antigravityColor = ValueColor(antigravityBucket, antigravityUsed, Palette.Antigravity);

            var centerY = height / 2f;
            // 圖示高度＝「數字＋條」堆疊總高；條寬＝「數字＋%」實際寬度，兩兩對齊。
            var numberHeight = graphics.MeasureString(
                "0", _valueFont, PointF.Empty, StringFormat.GenericTypographic).Height;
            var barHeight = Math.Max(2, Px(3));
            var barGap = Math.Max(1, Px(2));
            var iconSize = (int)Math.Round(numberHeight + barGap + barHeight);
            var iconGap = Math.Max(3, Px(4));
            var groupGap = Math.Max(6, Px(8));
            var codexBlock = MeasureValueGroup(graphics, codexText, codexUsed.HasValue);
            var claudeBlock = MeasureValueGroup(graphics, claudeText, claudeUsed.HasValue);
            var antigravityBlock = MeasureValueGroup(graphics, antigravityText, antigravityUsed.HasValue);
            var total = iconSize + iconGap + codexBlock + groupGap
                + iconSize + iconGap + claudeBlock + groupGap
                + iconSize + iconGap + antigravityBlock;

            // 緊湊版面：非鏡像時起點緊鄰左端三環（間隔 8px），鏡像時起點保留 14px 內縮；
            // 元素間距統一、膠囊寬度精準包覆內容，無多餘留白。
            float x = _mirrored ? Px(14) : height + Px(8);

            x += DrawBrandGroup(
                graphics, x, centerY, iconSize, iconGap, codexBlock,
                BrandIconKind.Codex, codexText, codexColor, codexUsed, Palette.Codex);
            x += groupGap;
            x += DrawBrandGroup(
                graphics, x, centerY, iconSize, iconGap, claudeBlock,
                BrandIconKind.Claude, claudeText, claudeColor, claudeUsed, Palette.Claude);
            x += groupGap;
            DrawBrandGroup(
                graphics, x, centerY, iconSize, iconGap, antigravityBlock,
                BrandIconKind.Antigravity, antigravityText, antigravityColor, antigravityUsed, Palette.Antigravity);
        }

        /// <summary>品牌圖示（與堆疊同高）＋（剩餘數字上、等寬微型進度條下）為一組；回傳群組寬度。</summary>
        private float DrawBrandGroup(
            Graphics graphics,
            float x,
            float centerY,
            int iconSize,
            int iconGap,
            float blockWidth,
            BrandIconKind kind,
            string numberText,
            Color color,
            double? used,
            Color accent)
        {
            NativeVisuals.DrawBrandIcon(
                graphics,
                kind,
                new Rectangle(
                    (int)Math.Round(x),
                    (int)Math.Round(centerY - iconSize / 2f),
                    iconSize,
                    iconSize));

            var blockX = x + iconSize + iconGap;
            var numberSize = graphics.MeasureString(
                numberText, _valueFont, PointF.Empty, StringFormat.GenericTypographic);
            var barHeight = Math.Max(2, Px(3));
            var barGap = Math.Max(1, Px(2));
            var stackHeight = numberSize.Height + barGap + barHeight;
            var topY = centerY - stackHeight / 2f;

            DrawValueGroupAt(graphics, blockX, topY, numberText, color, used.HasValue);
            DrawMicroProgress(
                graphics,
                new Rectangle(
                    (int)Math.Round(blockX),
                    (int)Math.Round(topY + numberSize.Height + barGap),
                    (int)Math.Round(blockWidth),
                    barHeight),
                used,
                accent);
            return iconSize + iconGap + blockWidth;
        }

        /// <summary>微型進度條：Track 底＋品牌色填充（80% 黃、95% 紅），高 3 邏輯 px。</summary>
        private void DrawMicroProgress(Graphics graphics, Rectangle bounds, double? used, Color accent)
        {
            var radius = Math.Max(1, bounds.Height / 2);
            using (var trackPath = NativeVisuals.RoundedRectangle(bounds, radius))
            using (var trackBrush = new SolidBrush(Palette.Track))
            {
                graphics.FillPath(trackBrush, trackPath);
            }
            if (!used.HasValue)
            {
                return;
            }
            var clamped = Math.Max(0, Math.Min(100, used.Value));
            var fillWidth = (int)Math.Round(bounds.Width * clamped / 100.0);
            if (fillWidth <= 0)
            {
                return;
            }
            var fillColor = clamped >= 95 ? Palette.Critical
                : clamped >= 80 ? Palette.Warning
                : accent;
            var fillBounds = new Rectangle(
                bounds.X, bounds.Y,
                Math.Max(bounds.Height, Math.Min(bounds.Width, fillWidth)),
                bounds.Height);
            using (var fillPath = NativeVisuals.RoundedRectangle(fillBounds, radius))
            using (var fillBrush = new SolidBrush(fillColor))
            {
                graphics.FillPath(fillBrush, fillPath);
            }
        }

        private static string RemainingText(double? used)
        {
            return used.HasValue
                ? Math.Round(100 - Math.Max(0, Math.Min(100, used.Value))).ToString("0", CultureInfo.CurrentCulture)
                : "--";
        }

        private static Color ValueColor(UsageBucket bucket, double? used, Color accent)
        {
            if (!used.HasValue)
            {
                return Palette.MutedText;
            }
            var clamped = Math.Max(0, Math.Min(100, used.Value));
            var exhaustSoon = bucket != null && bucket.ProjectedExhaustAt.HasValue;
            return clamped >= 95 ? Palette.Critical
                : clamped >= 80 || exhaustSoon ? Palette.Warning
                : accent;
        }

        /// <summary>「數字＋%」群組寬度（% 僅在有值時佔位）。</summary>
        private float MeasureValueGroup(Graphics graphics, string numberText, bool hasValue)
        {
            var width = graphics.MeasureString(
                numberText, _valueFont, PointF.Empty, StringFormat.GenericTypographic).Width;
            if (hasValue)
            {
                width += Math.Max(1, Px(1)) + graphics.MeasureString(
                    "%", _percentFont, PointF.Empty, StringFormat.GenericTypographic).Width;
            }
            return width;
        }

        /// <summary>畫「大號數字＋小號 %」（基線對齊，頂緣為基準），回傳群組寬度。</summary>
        private float DrawValueGroupAt(Graphics graphics, float x, float numberY, string numberText, Color color, bool hasValue)
        {
            var numberSize = graphics.MeasureString(
                numberText, _valueFont, PointF.Empty, StringFormat.GenericTypographic);
            using (var valueBrush = new SolidBrush(color))
            {
                graphics.DrawString(
                    numberText, _valueFont, valueBrush,
                    new PointF(x, numberY), StringFormat.GenericTypographic);
            }
            if (!hasValue)
            {
                return numberSize.Width;
            }
            var baselineY = numberY + AscentPixels(graphics, _valueFont);
            var percentSize = graphics.MeasureString(
                "%", _percentFont, PointF.Empty, StringFormat.GenericTypographic);
            var percentY = baselineY - AscentPixels(graphics, _percentFont);
            using (var percentBrush = new SolidBrush(Palette.SecondaryText))
            {
                graphics.DrawString(
                    "%", _percentFont, percentBrush,
                    new PointF(x + numberSize.Width + Math.Max(1, Px(1)), percentY),
                    StringFormat.GenericTypographic);
            }
            return numberSize.Width + Math.Max(1, Px(1)) + percentSize.Width;
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hovered = true;
            _collapseTimer.Stop();
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hovered = false;
            if (!_collapsed && !_state.Pinned)
            {
                _collapseTimer.Stop();
                _collapseTimer.Start(); // 滑鼠離開後延遲自動收回圓點。
            }
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _dragging = true;
                _moved = false;
                _dragStart = e.Location;
                if (_collapsed)
                {
                    _pressed = true;
                    Invalidate();
                    Update(); // 按下當幀即呈現按壓態。
                }
            }
            base.OnMouseDown(e);
        }

        private static Color Darken(Color color, double amount)
        {
            var keep = 1 - Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb(
                color.A,
                (int)Math.Round(color.R * keep),
                (int)Math.Round(color.G * keep),
                (int)Math.Round(color.B * keep));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragging)
            {
                var deltaX = e.X - _dragStart.X;
                var deltaY = e.Y - _dragStart.Y;
                if (_moved || Math.Abs(deltaX) > Px(ClickThreshold) || Math.Abs(deltaY) > Px(ClickThreshold))
                {
                    if (!_moved && _pressed)
                    {
                        _pressed = false; // 進入拖曳即解除按壓態。
                        Invalidate();
                    }
                    _moved = true;
                    var oldScale = _scale;
                    Location = new Point(Location.X + deltaX, Location.Y + deltaY);
                    if (CheckAndApplyScreenDpi() && oldScale > 0)
                    {
                        var ratio = _scale / oldScale;
                        _dragStart = new Point(
                            (int)Math.Round(_dragStart.X * ratio),
                            (int)Math.Round(_dragStart.Y * ratio));
                    }
                }
            }
            base.OnMouseMove(e);
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (!_dragging && IsHandleCreated && !IsDisposed && _morphStarted < 0)
            {
                CheckAndApplyScreenDpi();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (_pressed)
            {
                _pressed = false;
                Invalidate();
            }
            if (_dragging && e.Button == MouseButtons.Left)
            {
                _dragging = false;
                if (_moved)
                {
                    CheckAndApplyScreenDpi();
                    ClampToScreen();
                    _state.X = Location.X;
                    _state.Y = Location.Y;
                    _state.Save();
                }
                else if (!_suppressNextClick)
                {
                    // 單擊切換：圓點展開、膠囊收攏（固定展開時不收）；
                    // 形變中點擊即平滑反向。雙擊後續的 MouseUp 由
                    // _suppressNextClick 吃掉（WinForms 的 MouseUp e.Clicks 恆為 1）。
                    if (_collapsed)
                    {
                        SetCollapsed(false);
                    }
                    else if (!_state.Pinned)
                    {
                        SetCollapsed(true);
                    }
                }
            }
            _suppressNextClick = false;
            base.OnMouseUp(e);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                _suppressNextClick = true; // 雙擊後續的 MouseUp 不再觸發單擊切換。
                // 雙擊一律於小工具附近開啟詳細面板（不再固定托盤右下位）：
                // 誤展開／展開態先平滑收攏（固定展開除外），再以液滴墜落開啟。
                if (!_state.Pinned && (!_collapsed || _morphStarted >= 0))
                {
                    SetCollapsed(true);
                }
                PlayDropletOpen();
            }
            base.OnMouseDoubleClick(e);
        }

        /// <summary>錨定端圓的底部中心（水滴起點基準）。</summary>
        private Point AnchorDotBottomCenter()
        {
            var dotPx = Px(DotSize);
            int dotLeft;
            if (_morphStarted >= 0)
            {
                dotLeft = _morphAnchorX;
            }
            else if (_collapsed)
            {
                dotLeft = Location.X;
            }
            else
            {
                dotLeft = _mirrored ? Location.X + Width - dotPx : Location.X;
            }
            var dotTop = _morphStarted >= 0 ? _morphAnchorY : Location.Y + (Height - dotPx) / 2;
            return new Point(dotLeft + dotPx / 2, dotTop + dotPx - Px(3));
        }

        /// <summary>
        /// 液滴開啟：從錨定端圓底部擠出一滴、重力加速墜落，
        /// 落地處以水波揭示展開詳細面板（OpenAtRequested）。
        /// 系統動畫關閉時直接於落點開啟。
        /// </summary>
        private void PlayDropletOpen()
        {
            var origin = AnchorDotBottomCenter();
            var fall = Px(72);
            var landing = new Point(origin.X, origin.Y + fall + Px(7));
            var self = this;
            if (!_animationsEnabled)
            {
                var direct = OpenAtRequested;
                if (direct != null)
                {
                    direct(landing);
                }
                return;
            }
            var droplet = new DropletWindow(origin, Px(14), fall, 400, delegate
            {
                var handler = self.OpenAtRequested;
                if (handler != null)
                {
                    handler(landing);
                }
            });
            droplet.Play();
        }

        /// <summary>右鍵選單「開啟用量面板」：不播墜落，直接於小工具下方水波揭示。</summary>
        private void OpenPanelNearWidget()
        {
            var origin = AnchorDotBottomCenter();
            var handler = OpenAtRequested;
            if (handler != null)
            {
                handler(new Point(origin.X, origin.Y + Px(10)));
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                EndMorphTiming(); // timeBeginPeriod 配對保險。
                EndLayeredMorph();
                _motionTimer.Dispose();
                _collapseTimer.Dispose();
                _motionClock.Stop();
                DisposeMorphSnapshots();
                if (_valueFont != null) _valueFont.Dispose();
                if (_percentFont != null) _percentFont.Dispose();
                if (_alphaAttributes != null) _alphaAttributes.Dispose();
                _menu.Dispose();
                _toolTip.Dispose();
            }
            base.Dispose(disposing);
        }

        private void RecreateFonts()
        {
            if (_valueFont != null) _valueFont.Dispose();
            if (_percentFont != null) _percentFont.Dispose();
            _valueFont = NativeVisuals.CreateValueFont(11.5f, FontStyle.Bold);
            _percentFont = NativeVisuals.CreateValueFont(8f, FontStyle.Regular);
        }

        private void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)OnDisplayOrResolutionChanged); } catch { }
        }

        private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            if (e.Category == Microsoft.Win32.UserPreferenceCategory.General
                || e.Category == Microsoft.Win32.UserPreferenceCategory.Desktop
                || e.Category == Microsoft.Win32.UserPreferenceCategory.Window)
            {
                try { BeginInvoke((Action)OnDisplayOrResolutionChanged); } catch { }
            }
        }

        private void OnDisplayOrResolutionChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            CheckAndApplyScreenDpi();
            ClampToScreen();
            if (!_collapsed)
            {
                UpdateMirrorFromPosition();
            }
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_DISPLAYCHANGE = 0x007E;
            const int WM_DPICHANGED = 0x02E0;

            if (m.Msg == WM_DISPLAYCHANGE)
            {
                OnDisplayOrResolutionChanged();
            }
            else if (m.Msg == WM_DPICHANGED)
            {
                int newDpi = (short)(m.WParam.ToInt32() & 0xFFFF);
                if (newDpi >= 96)
                {
                    _scale = newDpi / 96f;
                    RecreateFonts();
                }
                OnDisplayOrResolutionChanged();
            }
            base.WndProc(ref m);
        }

        private int MeasureRequiredExpandedLogicalWidth()
        {
            var codexBucket = WeeklyBucket(_snapshot == null ? null : _snapshot.Codex);
            var claudeBucket = WeeklyBucket(_snapshot == null ? null : _snapshot.Claude);
            var antigravityBucket = WeeklyBucket(_snapshot == null ? null : _snapshot.Antigravity);
            var codexUsed = _codexHasValue ? _codexAnimation.Current : (double?)null;
            var claudeUsed = _claudeHasValue ? _claudeAnimation.Current : (double?)null;
            var antigravityUsed = _antigravityHasValue ? _antigravityAnimation.Current : (double?)null;

            var codexText = RemainingText(codexUsed);
            var claudeText = RemainingText(claudeUsed);
            var antigravityText = RemainingText(antigravityUsed);

            using (var bmp = new Bitmap(1, 1))
            {
                var dpi = _scale > 0 ? 96f * _scale : 96f;
                bmp.SetResolution(dpi, dpi);
                using (var g = Graphics.FromImage(bmp))
                {
                    var numberHeight = g.MeasureString(
                        "0", _valueFont, PointF.Empty, StringFormat.GenericTypographic).Height;
                    var barHeight = Math.Max(2, Px(3));
                    var barGap = Math.Max(1, Px(2));
                    var iconSize = (int)Math.Round(numberHeight + barGap + barHeight);
                    var iconGap = Math.Max(3, Px(4));
                    var groupGap = Math.Max(6, Px(8));
                    var codexBlock = MeasureValueGroup(g, codexText, codexUsed.HasValue);
                    var claudeBlock = MeasureValueGroup(g, claudeText, claudeUsed.HasValue);
                    var antigravityBlock = MeasureValueGroup(g, antigravityText, antigravityUsed.HasValue);
                    var total = iconSize + iconGap + codexBlock + groupGap
                        + iconSize + iconGap + claudeBlock + groupGap
                        + iconSize + iconGap + antigravityBlock;

                    // 左端三環直徑 DotSize + 間距 8 + 內容總寬 + 右端圓弧安全內縮 14
                    var totalPhysical = Px(DotSize) + Px(8) + total + Px(14);
                    var logicalNeeded = (int)Math.Ceiling(_scale > 0 ? totalPhysical / _scale : totalPhysical);
                    return Math.Max(230, logicalNeeded);
                }
            }
        }

        [DllImport("winmm.dll")]
        private static extern uint timeBeginPeriod(uint period);

        [DllImport("winmm.dll")]
        private static extern uint timeEndPeriod(uint period);

        [StructLayout(LayoutKind.Sequential)]
        internal struct BitmapInfoHeader
        {
            public int Size;
            public int Width;
            public int Height;
            public short Planes;
            public short BitCount;
            public int Compression;
            public int SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public int ClrUsed;
            public int ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativePoint
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct NativeSize
        {
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        internal struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [DllImport("user32.dll")]
        internal static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr SelectObject(IntPtr hdc, IntPtr handle);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteObject(IntPtr handle);

        [DllImport("gdi32.dll")]
        internal static extern IntPtr CreateDIBSection(
            IntPtr hdc,
            ref BitmapInfoHeader header,
            uint usage,
            out IntPtr bits,
            IntPtr section,
            uint offset);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UpdateLayeredWindow(
            IntPtr hwnd,
            IntPtr hdcDst,
            ref NativePoint destination,
            ref NativeSize size,
            IntPtr hdcSrc,
            ref NativePoint source,
            int colorKey,
            ref BlendFunction blend,
            int flags);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")]
        internal static extern void CopyMemory(IntPtr destination, IntPtr source, uint length);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmFlush();

        /// <summary>收合圓點內容：迷你三環用量儀表（外環 Codex、中環 Claude、內環 Antigravity），與托盤圖示同語彙。</summary>
        private void DrawDotContent(Graphics graphics, int size)
        {
            var codexUsed = _codexHasValue ? _codexAnimation.Current : (double?)null;
            var claudeUsed = _claudeHasValue ? _claudeAnimation.Current : (double?)null;
            var antigravityUsed = _antigravityHasValue ? _antigravityAnimation.Current : (double?)null;
            var outerInset = (int)Math.Round(size * 0.12f);
            var midInset = (int)Math.Round(size * 0.25f);
            var innerInset = (int)Math.Round(size * 0.38f);
            var stroke = Math.Max(1.8f, size * 0.075f);
            TrayIconFactory.DrawGaugeRing(
                graphics,
                new RectangleF(outerInset, outerInset, size - outerInset * 2, size - outerInset * 2),
                stroke,
                codexUsed,
                Palette.Codex);
            TrayIconFactory.DrawGaugeRing(
                graphics,
                new RectangleF(midInset, midInset, size - midInset * 2, size - midInset * 2),
                stroke,
                claudeUsed,
                Palette.Claude);
            TrayIconFactory.DrawGaugeRing(
                graphics,
                new RectangleF(innerInset, innerInset, size - innerInset * 2, size - innerInset * 2),
                stroke,
                antigravityUsed,
                Palette.Antigravity);
        }

        /// <summary>字型在目前 Graphics DPI 下的 ascent 像素值，用於跨字級基線對齊。</summary>
        private static float AscentPixels(Graphics graphics, Font font)
        {
            var family = font.FontFamily;
            var ascent = family.GetCellAscent(font.Style);
            var emHeight = family.GetEmHeight(font.Style);
            return font.SizeInPoints * graphics.DpiY / 72f * ascent / emHeight;
        }

        /// <summary>與托盤儀表共用同一套代表額度選擇（多個每週額度取最吃緊者）。</summary>
        private static UsageBucket WeeklyBucket(ProviderSnapshot provider)
        {
            return UsageSelection.SummaryBucket(provider);
        }

        private static string BuildSummary(UsageSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return "尚未更新用量資料";
            }
            var codex = WeeklyBucket(snapshot.Codex);
            var claude = WeeklyBucket(snapshot.Claude);
            var antigravity = WeeklyBucket(snapshot.Antigravity);
            return string.Format(
                CultureInfo.CurrentCulture,
                "Codex 每週剩餘 {0}，Claude 每週剩餘 {1}，Antigravity 每週剩餘 {2}",
                codex == null ? "未知" : (100 - codex.UsedPercent).ToString("0", CultureInfo.CurrentCulture) + "%",
                claude == null ? "未知" : (100 - claude.UsedPercent).ToString("0", CultureInfo.CurrentCulture) + "%",
                antigravity == null ? "未知" : (100 - antigravity.UsedPercent).ToString("0", CultureInfo.CurrentCulture) + "%");
        }

        private void HideByUser()
        {
            Hide();
            var handler = HiddenByUser;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void PlaceWindow()
        {
            if (_state.HasPosition)
            {
                var saved = new Rectangle(new Point(_state.X, _state.Y), Size);
                foreach (var screen in Screen.AllScreens)
                {
                    var visible = Rectangle.Intersect(screen.WorkingArea, saved);
                    if (visible.Width >= Px(40) && visible.Height >= Px(24))
                    {
                        Location = saved.Location;
                        return;
                    }
                }
            }

            var workingArea = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(
                workingArea.Right - Width - Px(16),
                workingArea.Top + Px(16));
        }

        /// <summary>依目前位置決定展開方向（固定展開啟動時使用）。</summary>
        private void UpdateMirrorFromPosition()
        {
            var workingArea = Screen.FromPoint(Location).WorkingArea;
            _mirrored = Location.X + Width / 2 > workingArea.Left + workingArea.Width / 2;
        }

        /// <summary>
        /// 切換收合圓點與展開膠囊：以左端圓心為錨、尺寸連續插值加內容交叉淡化，
        /// 讓圓在原地「長成」膠囊（或反向縮回）。
        /// </summary>
        private void SetCollapsed(bool collapsed)
        {
            if (_collapsed == collapsed)
            {
                return;
            }
            _collapsed = collapsed;
            _collapseTimer.Stop();

            if (!_animationsEnabled || !Visible)
            {
                _expandProgress = collapsed ? 0 : 1;
                ApplyMorphBounds();
                if (!collapsed)
                {
                    ClampToScreen();
                }
                Invalidate();
                return;
            }

            if (_morphStarted >= 0)
            {
                // 形變進行中改變方向：不重啟、不跳變 —
                // 從目前進度以 EaseOutCubic 平滑改道（retarget），時長按剩餘距離縮放。
                _expanding = !collapsed;
                _morphRetarget = true;
                _morphFromWidth = Motion.Clamp01(_morphWidthProgress);
                _morphFromAlpha = Motion.Clamp01(_morphContentAlpha);
                var target = _expanding ? 1.0 : 0.0;
                var span = Math.Abs(target - _morphFromWidth);
                var fullDuration = _expanding ? ExpandDuration : CollapseDuration;
                _morphRetargetDuration = Math.Max(120, (int)Math.Round(fullDuration * span));
                _morphStarted = _motionClock.ElapsedMilliseconds;
                MotionTick(null, EventArgs.Empty);
                return;
            }

            // 錨點 = 圓點（錨定端圓）的左上角基準；鏡像膠囊收合時圓點在右端。
            _morphAnchorX = collapsed && _mirrored
                ? Location.X + Width - Px(DotSize)
                : Location.X;
            _morphAnchorY = Location.Y + (Height - Px(DotSize)) / 2;
            _expanding = !collapsed;
            if (_expanding)
            {
                // 依圓點所在螢幕半邊決定展開方向：左半向右、右半向左。
                var workingArea = Screen.FromPoint(new Point(_morphAnchorX, _morphAnchorY)).WorkingArea;
                _mirrored = _morphAnchorX + Px(DotSize) / 2 > workingArea.Left + workingArea.Width / 2;
                ClampAnchorForExpansion();
            }

            _morphRetarget = false;
            CaptureMorphSnapshots();
            BeginMorphTiming();
            if (_fadeStarted < 0)
            {
                // 先讓 WinForms 卸下它的半透明 layered 樣式，
                // 形變改由下方的 UpdateLayeredWindow 管線全權接管呈現。
                Opacity = 1.0;
            }
            BeginLayeredMorph();
            _morphStarted = _motionClock.ElapsedMilliseconds;
            EnsureMotionTimer();
            MotionTick(null, EventArgs.Empty); // 立即畫出第 0 幀，不等首個計時器節拍。
            if (_layeredMorphActive)
            {
                ClearWindowRegion(); // 第 0 幀已上屏（與目前外觀一致），此刻清 Region 無縫。
            }
        }

        /// <summary>
        /// 形變改用 UpdateLayeredWindow 呈現：整段動畫不改視窗大小、不重算 Region、
        /// 不走 WM_PAINT — 每幀將預乘 ARGB 畫布原子地交給 DWM 合成（大小、位置、
        /// 內容一次更新），形狀由 alpha 呈現、邊緣抗鋸齒。這與系統自身動畫同一路徑，
        /// 消除逐幀 resize 造成的合成抖動。
        /// </summary>
        private void BeginLayeredMorph()
        {
            var maxWidth = (int)Math.Round(Px(WidgetWidth) * 1.12) + 2; // 含過衝餘量。
            var maxHeight = Px(WidgetHeight) + 2;
            try
            {
                var screenDc = GetDC(IntPtr.Zero);
                _morphMemDc = CreateCompatibleDC(screenDc);
                ReleaseDC(IntPtr.Zero, screenDc);
                var header = new BitmapInfoHeader();
                header.Size = Marshal.SizeOf(typeof(BitmapInfoHeader));
                header.Width = maxWidth;
                header.Height = -maxHeight; // top-down，掃描列與 GDI+ 一致。
                header.Planes = 1;
                header.BitCount = 32;
                _morphDib = CreateDIBSection(_morphMemDc, ref header, 0, out _morphDibBits, IntPtr.Zero, 0);
                if (_morphMemDc == IntPtr.Zero || _morphDib == IntPtr.Zero)
                {
                    ReleaseLayeredMorphResources();
                    return;
                }
                _morphOldBitmap = SelectObject(_morphMemDc, _morphDib);

                // 畫布直接建在 DIB 的像素記憶體上：GDI+ 畫出的每一筆就是 ULW 的來源，
                // 省掉原本每幀一次 LockBits 與整張畫布的記憶體複製（200% DPI 下約 180 KB／幀）。
                _morphCanvas = new Bitmap(
                    maxWidth, maxHeight, maxWidth * 4, PixelFormat.Format32bppPArgb, _morphDibBits);
                var dpi = _scale > 0 ? 96f * _scale : 96f;
                _morphCanvas.SetResolution(dpi, dpi);
                _morphGraphics = Graphics.FromImage(_morphCanvas);
                _morphGraphics.SmoothingMode = SmoothingMode.AntiAlias;
                _morphGraphics.CompositingQuality = CompositingQuality.HighSpeed;
                _morphGraphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                _morphLastWidth = -1;
                _morphLastHeight = -1;
                _morphLastAlpha = -1;
                _morphCanvasW = maxWidth;
                _morphCanvasH = maxHeight;
                // 注意：此處刻意「不」清 Region — 需等第 0 幀 ULW 上屏後再清
                //（先清會讓視窗在間隙中被以直角矩形合成一幀，肉眼可見「先方後圓」）。
                SetWindowLong(Handle, GwlExStyle, GetWindowLong(Handle, GwlExStyle) | WsExLayered);
                _layeredMorphActive = true;
            }
            catch
            {
                ReleaseLayeredMorphResources();
            }
        }

        /// <summary>第 0 幀 ULW 上屏後清除 Region：形狀已由 alpha 呈現，清除無視覺變化。</summary>
        private void ClearWindowRegion()
        {
            var oldRegion = Region;
            if (oldRegion != null)
            {
                Region = null;
                oldRegion.Dispose();
            }
        }

        /// <summary>結束 layered 呈現並釋放 GDI 資源；視窗交還一般繪製管線。</summary>
        private void EndLayeredMorph()
        {
            if (_layeredMorphActive)
            {
                _layeredMorphActive = false;
                try
                {
                    SetWindowLong(Handle, GwlExStyle, GetWindowLong(Handle, GwlExStyle) & ~WsExLayered);
                }
                catch
                {
                }
            }
            ReleaseLayeredMorphResources();
        }

        private void ReleaseLayeredMorphResources()
        {
            // 畫布依附在 DIB 的記憶體上，必須先於 DeleteObject 釋放。
            if (_morphGraphics != null)
            {
                _morphGraphics.Dispose();
                _morphGraphics = null;
            }
            if (_morphCanvas != null)
            {
                _morphCanvas.Dispose();
                _morphCanvas = null;
            }
            if (_morphMemDc != IntPtr.Zero)
            {
                if (_morphOldBitmap != IntPtr.Zero)
                {
                    SelectObject(_morphMemDc, _morphOldBitmap);
                    _morphOldBitmap = IntPtr.Zero;
                }
                DeleteDC(_morphMemDc);
                _morphMemDc = IntPtr.Zero;
            }
            if (_morphDib != IntPtr.Zero)
            {
                DeleteObject(_morphDib);
                _morphDib = IntPtr.Zero;
            }
            _morphDibBits = IntPtr.Zero;
        }

        /// <summary>合成目前形變幀並以 UpdateLayeredWindow 原子呈現（大小＋位置＋內容）。</summary>
        private void PresentMorphFrame()
        {
            if (!_layeredMorphActive)
            {
                // 後備：ULW 不可用時退回逐幀調整視窗的舊路徑。
                ApplyMorphBounds();
                Invalidate();
                Update();
                return;
            }

            var dotPx = Px(DotSize);
            var width = (int)Math.Round(dotPx + (Px(WidgetWidth) - dotPx) * _morphWidthProgress);
            var height = (int)Math.Round(dotPx + (Px(WidgetHeight) - dotPx) * Motion.Clamp01(_morphHeightProgress));
            width = Math.Max(width, height);
            width = Math.Min(width, _morphCanvasW);
            height = Math.Min(height, _morphCanvasH);

            var frameStart = _motionClock.ElapsedMilliseconds;
            var alphaByte = (int)Math.Round(Motion.Clamp01(_morphContentAlpha) * 255);
            if (width == _morphLastWidth
                && height == _morphLastHeight
                && alphaByte == _morphLastAlpha
                && !_usageAnimating)
            {
                // 這一幀在像素上與前一幀完全相同（曲線末段常見）：略過整幀重繪與提交，
                // 只等一個合成節拍，把時間讓給下一個真正會變的幀。
                FlushComposition();
                _morphFrameCost = _motionClock.ElapsedMilliseconds - frameStart;
                return;
            }
            _morphLastWidth = width;
            _morphLastHeight = height;
            _morphLastAlpha = alphaByte;

            var graphics = _morphGraphics;
            graphics.Clear(Color.Transparent);
            DrawCapsuleSurfaceCore(graphics, width, height);
            // 錨定端雙環即時繪製：形變全程原位連續，數值動畫也保持活躍。
            if (_mirrored)
            {
                graphics.TranslateTransform(width - height, 0);
                DrawDotContent(graphics, height);
                graphics.ResetTransform();
            }
            else
            {
                DrawDotContent(graphics, height);
            }
            if (_cardSnapshot != null)
            {
                // 內容：淡入＋自錨定端輕微滑入，像從圓後拉出的抽屜。
                var contentAlpha = alphaByte / 255f;
                var slide = (int)Math.Round(Px(ContentSlideDistance) * (1f - contentAlpha));
                var snapshotX = _mirrored
                    ? width - _cardSnapshot.Width + slide
                    : -slide;
                DrawImageAlpha(
                    graphics, _cardSnapshot,
                    snapshotX, (height - _cardSnapshot.Height) / 2, contentAlpha);
            }
            graphics.Flush(FlushIntention.Sync); // 確保 GDI+ 的繪製已落到 DIB 記憶體再交給 DWM。

            var destination = new NativePoint();
            destination.X = _mirrored ? _morphAnchorX + dotPx - width : _morphAnchorX;
            destination.Y = _morphAnchorY - (height - dotPx) / 2;
            var size = new NativeSize();
            size.Width = width;
            size.Height = height;
            var source = new NativePoint();
            var blend = new BlendFunction();
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = 1; // AC_SRC_ALPHA：使用像素預乘 alpha。
            UpdateLayeredWindow(Handle, IntPtr.Zero, ref destination, ref size, _morphMemDc, ref source, 0, ref blend, UlwAlpha);
            // 等待這一幀真正合成完畢：把更新節奏鎖到 DWM 節拍（vsync），
            // 幀間隔均勻、不跳幀不重複 — 抖動是「不順」感的主因。
            FlushComposition();
            _morphFrameCost = _motionClock.ElapsedMilliseconds - frameStart;
        }

        private static void FlushComposition()
        {
            try
            {
                DwmFlush();
            }
            catch
            {
            }
        }

        /// <summary>
        /// 形變期間的幀排程：DwmFlush 回來時已對齊一個合成節拍，
        /// 立刻以 BeginInvoke 排下一幀。訊息佇列中的委派優先權高於 WM_TIMER，
        /// 也沒有計時器的最小間隔限制 — 幀距因此穩定在一個 vsync，而非時快時慢。
        /// </summary>
        private void QueueMorphFrame()
        {
            if (_morphPumpQueued || _morphStarted < 0 || !IsHandleCreated || IsDisposed)
            {
                return;
            }
            if (_morphFrameCost < 4)
            {
                // DwmFlush 沒有真的等到合成節拍（遠端桌面、DWM 停用等）：
                // 此時自驅動會變成忙迴圈，改讓計時器節拍接手。
                return;
            }
            _morphPumpQueued = true;
            try
            {
                BeginInvoke(new MethodInvoker(delegate
                {
                    _morphPumpQueued = false;
                    if (_morphStarted >= 0 && !IsDisposed)
                    {
                        MotionTick(null, EventArgs.Empty);
                    }
                }));
            }
            catch
            {
                _morphPumpQueued = false; // 排程失敗時退回計時器節拍。
            }
        }

        /// <summary>形變完成：交還一般繪製管線並落定終態（大小、Region、透明度）。</summary>
        private void FinishLayeredMorph()
        {
            // 先落定終態大小與 Region（此刻仍為 layered，同形 Region 裁切終態幀 = 畫面不變），
            // 再移除 layered — 交接瞬間形狀已就位，不會以直角矩形閃現。
            ApplyMorphBounds();
            EndLayeredMorph();
            DisposeMorphSnapshots();
            EndMorphTiming();
            if (_fadeStarted < 0)
            {
                Opacity = RestOpacity;
            }
            Invalidate();
            Update();
        }

        /// <summary>形變開始：提升系統計時器解析度並加密動畫節拍。</summary>
        private void BeginMorphTiming()
        {
            if (!_highResTiming)
            {
                try
                {
                    timeBeginPeriod(1);
                    _highResTiming = true;
                }
                catch
                {
                }
            }
            _motionTimer.Interval = MotionIntervalMorph;
        }

        /// <summary>形變結束：恢復系統計時器解析度與閒置節拍。</summary>
        private void EndMorphTiming()
        {
            if (_highResTiming)
            {
                try
                {
                    timeEndPeriod(1);
                }
                catch
                {
                }
                _highResTiming = false;
            }
            _motionTimer.Interval = MotionIntervalIdle;
        }

        /// <summary>展開前調整錨點，確保膠囊完整落在工作區內。</summary>
        private void ClampAnchorForExpansion()
        {
            var workingArea = Screen.FromPoint(new Point(_morphAnchorX, _morphAnchorY)).WorkingArea;
            var fullWidth = (int)Math.Round(Px(WidgetWidth) * 1.10); // 預留過衝回彈空間。
            var fullHeight = Px(WidgetHeight);
            var dotPx = Px(DotSize);
            if (_mirrored)
            {
                // 向左展開：右端圓固定，左側需容納整支膠囊（含過衝）。
                _morphAnchorX = Math.Max(
                    Math.Min(_morphAnchorX, workingArea.Right - dotPx),
                    Math.Min(workingArea.Right - dotPx, workingArea.Left + fullWidth - dotPx));
            }
            else
            {
                _morphAnchorX = Math.Min(Math.Max(_morphAnchorX, workingArea.Left), Math.Max(workingArea.Left, workingArea.Right - fullWidth));
            }
            var halfGrowth = (fullHeight - dotPx) / 2;
            _morphAnchorY = Math.Min(
                Math.Max(_morphAnchorY, workingArea.Top + halfGrowth),
                Math.Max(workingArea.Top + halfGrowth, workingArea.Bottom - fullHeight + halfGrowth));
        }

        /// <summary>
        /// 過渡期間的右側內容快照（透明底、灰階抗鋸齒）：形變開始渲染一次，
        /// 之後每幀僅做透明度合成＋滑入位移。左端雙環不入快照 —
        /// 由每幀即時繪製，形變全程連續且數值動畫保持活躍。
        /// </summary>
        private void CaptureMorphSnapshots()
        {
            DisposeMorphSnapshots();

            var fullWidth = Px(WidgetWidth);
            var fullHeight = Px(WidgetHeight);
            // 預乘 ARGB：與形變畫布同格式，逐幀 alpha 合成不必再做格式轉換。
            _cardSnapshot = new Bitmap(fullWidth, fullHeight, PixelFormat.Format32bppPArgb);
            var dpi = _scale > 0 ? 96f * _scale : 96f;
            _cardSnapshot.SetResolution(dpi, dpi);
            using (var graphics = Graphics.FromImage(_cardSnapshot))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                // 透明底上用灰階抗鋸齒（ClearType 在透明底會產生黑邊）。
                graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                DrawCardOverlay(graphics, fullWidth, fullHeight);
            }
        }

        private void DisposeMorphSnapshots()
        {
            if (_cardSnapshot != null)
            {
                _cardSnapshot.Dispose();
                _cardSnapshot = null;
            }
        }

        /// <summary>依目前形變進度套用尺寸、位置（左端圓心錨定）與膠囊 Region。</summary>
        private void ApplyMorphBounds()
        {
            // 寬允許過衝（EaseOutBack > 1），高夾在 [0,1]；高不可超過寬（膠囊恆為橫向）。
            var dotPx = Px(DotSize);
            var width = (int)Math.Round(dotPx + (Px(WidgetWidth) - dotPx) * _morphWidthProgress);
            var height = (int)Math.Round(dotPx + (Px(WidgetHeight) - dotPx) * Motion.Clamp01(_morphHeightProgress));
            width = Math.Max(width, height);
            if (_morphStarted >= 0)
            {
                var x = _mirrored ? _morphAnchorX + dotPx - width : _morphAnchorX;
                Location = new Point(x, _morphAnchorY - (height - dotPx) / 2);
            }
            var desired = new Size(width, height);
            if (ClientSize != desired)
            {
                ClientSize = desired;
            }
            UpdateRoundedRegion();
        }

        private void ClampToScreen()
        {
            var workingArea = Screen.FromPoint(Location).WorkingArea;
            Location = new Point(
                Math.Min(Math.Max(Location.X, workingArea.Left), Math.Max(workingArea.Left, workingArea.Right - Width)),
                Math.Min(Math.Max(Location.Y, workingArea.Top), Math.Max(workingArea.Top, workingArea.Bottom - Height)));
        }

        private void ApplyScaledLayout()
        {
            if (_morphStarted < 0)
            {
                _expandProgress = _collapsed ? 0 : 1;
                _morphWidthProgress = _expandProgress;
                _morphHeightProgress = _expandProgress;
                _morphContentAlpha = _expandProgress;
            }
            ApplyMorphBounds();
        }

        private void UpdateRoundedRegion()
        {
            if (_scale <= 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                return;
            }

            // 全程膠囊 Region：圓角半徑 = 高度一半，收合時（寬=高）即為正圓，
            // 因此形變過程中形狀連續、無跳變。
            using (var capsule = NativeVisuals.RoundedRectangle(
                new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
                Math.Min(ClientSize.Width, ClientSize.Height) / 2))
            {
                var previous = Region;
                Region = new Region(capsule);
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
    }

    /// <summary>
    /// 液滴視窗：雙擊圓點時，從圓點底部「擠出」一滴（膠囊面同色），
    /// 以重力加速墜落並隨速度垂直拉伸，落地後回呼開啟詳細面板。
    /// 以 UpdateLayeredWindow 逐幀呈現（透明背景、抗鋸齒邊緣），播畢自毀。
    /// </summary>
    internal sealed class DropletWindow : Form
    {
        private const double SqueezePhase = 0.24; // 前段：水滴自圓點底部長出。

        private readonly Timer _timer;
        private readonly Stopwatch _clock;
        private readonly int _dropletPx;
        private readonly int _fallPx;
        private readonly int _durationMs;
        private readonly Action _landed;
        private readonly int _canvasW;
        private readonly int _canvasH;
        private Bitmap _canvas;
        private IntPtr _memDc;
        private IntPtr _dib;
        private IntPtr _dibBits;
        private IntPtr _oldBitmap;
        private bool _finished;

        public DropletWindow(Point dotBottomCenter, int dropletPx, int fallPx, int durationMs, Action landed)
        {
            _dropletPx = Math.Max(6, dropletPx);
            _fallPx = Math.Max(8, fallPx);
            _durationMs = Math.Max(120, durationMs);
            _landed = landed;
            _canvasW = _dropletPx * 2 + 6;
            _canvasH = _fallPx + _dropletPx * 3;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Location = new Point(dotBottomCenter.X - _canvasW / 2, dotBottomCenter.Y - _dropletPx);

            _timer = new Timer { Interval = 8 };
            _timer.Tick += Tick;
            _clock = new Stopwatch();
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                // LAYERED | TOOLWINDOW | NOACTIVATE | TOPMOST
                parameters.ExStyle |= 0x00080000 | 0x00000080 | 0x08000000 | 0x00000008;
                return parameters;
            }
        }

        public void Play()
        {
            try
            {
                _canvas = new Bitmap(_canvasW, _canvasH, PixelFormat.Format32bppPArgb);
                var screenDc = DesktopWidget.GetDC(IntPtr.Zero);
                _memDc = DesktopWidget.CreateCompatibleDC(screenDc);
                DesktopWidget.ReleaseDC(IntPtr.Zero, screenDc);
                var header = new DesktopWidget.BitmapInfoHeader();
                header.Size = Marshal.SizeOf(typeof(DesktopWidget.BitmapInfoHeader));
                header.Width = _canvasW;
                header.Height = -_canvasH;
                header.Planes = 1;
                header.BitCount = 32;
                _dib = DesktopWidget.CreateDIBSection(_memDc, ref header, 0, out _dibBits, IntPtr.Zero, 0);
                if (_memDc == IntPtr.Zero || _dib == IntPtr.Zero)
                {
                    Finish(true);
                    return;
                }
                _oldBitmap = DesktopWidget.SelectObject(_memDc, _dib);
            }
            catch
            {
                Finish(true);
                return;
            }

            Show();
            PresentFrame(0);
            _clock.Start();
            _timer.Start();
        }

        private void Tick(object sender, EventArgs eventArgs)
        {
            var time = _clock.ElapsedMilliseconds / (double)_durationMs;
            if (time >= 1)
            {
                Finish(false);
                return;
            }
            PresentFrame(time);
        }

        private void PresentFrame(double time)
        {
            // 分段：擠出（頸部相連）→ 斷頸 → 淚滴墜落（尾滴跟隨、高光）→ 落地壓扁。
            var radius = _dropletPx / 2f;
            var centerX = _canvasW / 2f;
            var neckTop = (float)_dropletPx;             // 圓點底緣在畫布中的 y。
            var landBottom = neckTop + _fallPx + radius * 2; // 落地時滴的底緣。

            var grow = (float)Motion.SmoothStep(Motion.Clamp01(time / SqueezePhase));
            var fallTime = (float)Motion.Clamp01((time - 0.30) / 0.62);
            var landTime = (float)Motion.Clamp01((time - 0.92) / 0.08);
            var offsetY = (float)(_fallPx * Motion.EaseInQuad(fallTime));

            using (var graphics = Graphics.FromImage(_canvas))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;

                if (landTime > 0)
                {
                    // 落地：貼地壓扁（squash），面積守恆的扁橢圓。
                    var squashWidth = radius * 2 * (1 + 0.55f * landTime);
                    var squashHeight = Math.Max(2f, radius * 2 * (1 - 0.58f * landTime));
                    var bounds = new RectangleF(
                        centerX - squashWidth / 2, landBottom - squashHeight, squashWidth, squashHeight);
                    FillDropEllipse(graphics, bounds);
                }
                else if (time < 0.30)
                {
                    // 擠出：主滴自圓點底膨出，頸部相連並隨之收窄。
                    var dropRadius = Math.Max(1.5f, radius * grow);
                    var dropCenterY = neckTop + dropRadius * (0.15f + 0.85f * grow);
                    var dropTop = dropCenterY - dropRadius;
                    if (grow > 0.12f && time < 0.27)
                    {
                        var neckWidth = Math.Max(1.5f, radius * (0.85f - 0.62f * grow));
                        using (var neck = new GraphicsPath())
                        using (var neckBrush = new SolidBrush(Palette.ProviderSurface))
                        {
                            var joinY = dropTop + dropRadius * 0.45f;
                            neck.AddBezier(centerX - neckWidth, neckTop - 1, centerX - neckWidth * 0.5f, neckTop + 2,
                                centerX - neckWidth * 0.34f, joinY - 3, centerX - neckWidth * 0.30f, joinY);
                            neck.AddLine(centerX - neckWidth * 0.30f, joinY, centerX + neckWidth * 0.30f, joinY);
                            neck.AddBezier(centerX + neckWidth * 0.30f, joinY, centerX + neckWidth * 0.34f, joinY - 3,
                                centerX + neckWidth * 0.5f, neckTop + 2, centerX + neckWidth, neckTop - 1);
                            neck.CloseFigure();
                            graphics.FillPath(neckBrush, neck);
                        }
                    }
                    var mainBounds = new RectangleF(
                        centerX - dropRadius, dropCenterY - dropRadius, dropRadius * 2, dropRadius * 2);
                    using (var drop = CreateTeardrop(centerX, dropCenterY - dropRadius, dropRadius, 0))
                    {
                        FillDropPath(graphics, drop, mainBounds);
                    }
                }
                else
                {
                    // 墜落：淚滴（尖朝上、尖度隨速度）＋滯後尾滴。
                    var tail = radius * 0.95f * fallTime;
                    var tipY = neckTop + offsetY - tail * 0.4f;
                    var mainBounds = new RectangleF(
                        centerX - radius, tipY, radius * 2, tail + radius * 2);
                    using (var drop = CreateTeardrop(centerX, tipY, radius, tail))
                    {
                        FillDropPath(graphics, drop, mainBounds);
                    }
                    // 高光：左上小橢圓，液面反光。
                    var dropCenterY = tipY + tail + radius;
                    using (var highlight = new SolidBrush(Color.FromArgb(64, 255, 255, 255)))
                    {
                        graphics.FillEllipse(
                            highlight,
                            centerX - radius * 0.52f, dropCenterY - radius * 0.55f,
                            radius * 0.62f, radius * 0.45f);
                    }
                    // 尾滴：滯後於主滴、較小，途中淡出。
                    if (fallTime > 0.18f && fallTime < 0.88f)
                    {
                        var trailAlpha = (int)(150 * (1 - (fallTime - 0.18f) / 0.70f));
                        var trailRadius = radius * 0.30f;
                        var trailY = neckTop + offsetY * 0.52f;
                        using (var trailBrush = new SolidBrush(Color.FromArgb(
                            Math.Max(0, trailAlpha),
                            Palette.ProviderSurface.R, Palette.ProviderSurface.G, Palette.ProviderSurface.B)))
                        {
                            graphics.FillEllipse(
                                trailBrush,
                                centerX - trailRadius, trailY,
                                trailRadius * 2, trailRadius * 2.4f);
                        }
                    }
                }
            }

            var lockBounds = new Rectangle(0, 0, _canvasW, _canvasH);
            var data = _canvas.LockBits(lockBounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var rowBytes = _canvasW * 4;
                if (data.Stride == rowBytes)
                {
                    DesktopWidget.CopyMemory(_dibBits, data.Scan0, (uint)(rowBytes * _canvasH));
                }
                else
                {
                    for (var row = 0; row < _canvasH; row++)
                    {
                        DesktopWidget.CopyMemory(
                            IntPtr.Add(_dibBits, row * rowBytes),
                            IntPtr.Add(data.Scan0, row * data.Stride),
                            (uint)rowBytes);
                    }
                }
            }
            finally
            {
                _canvas.UnlockBits(data);
            }

            var destination = new DesktopWidget.NativePoint();
            destination.X = Location.X;
            destination.Y = Location.Y;
            var size = new DesktopWidget.NativeSize();
            size.Width = _canvasW;
            size.Height = _canvasH;
            var source = new DesktopWidget.NativePoint();
            var blend = new DesktopWidget.BlendFunction();
            blend.SourceConstantAlpha = 255;
            blend.AlphaFormat = 1;
            DesktopWidget.UpdateLayeredWindow(
                Handle, IntPtr.Zero, ref destination, ref size, _memDc, ref source, 0, ref blend, 2);
            try
            {
                DesktopWidget.DwmFlush();
            }
            catch
            {
            }
        }

        /// <summary>淚滴輪廓：上尖（貝塞爾收尖）下圓，tail=0 時即為圓。</summary>
        private static GraphicsPath CreateTeardrop(float centerX, float tipY, float radius, float tail)
        {
            var path = new GraphicsPath();
            var centerY = tipY + tail + radius;
            path.AddBezier(
                centerX, tipY,
                centerX - radius * 0.42f, tipY + tail * 0.5f,
                centerX - radius, centerY - radius * 0.75f,
                centerX - radius, centerY);
            path.AddArc(centerX - radius, centerY - radius, radius * 2, radius * 2, 180, -180);
            path.AddBezier(
                centerX + radius, centerY,
                centerX + radius, centerY - radius * 0.75f,
                centerX + radius * 0.42f, tipY + tail * 0.5f,
                centerX, tipY);
            path.CloseFigure();
            return path;
        }

        /// <summary>液滴填充：面色垂直漸層＋邊框（路徑版）。</summary>
        private static void FillDropPath(Graphics graphics, GraphicsPath path, RectangleF bounds)
        {
            using (var fillBrush = new LinearGradientBrush(
                new RectangleF(bounds.X, bounds.Y - 1, Math.Max(2, bounds.Width), Math.Max(2, bounds.Height) + 2),
                NativeVisuals.Lighten(Palette.ProviderSurface, 0.08),
                Palette.ProviderSurface,
                LinearGradientMode.Vertical))
            {
                graphics.FillPath(fillBrush, path);
            }
            using (var borderPen = new Pen(Palette.BorderStrong))
            {
                graphics.DrawPath(borderPen, path);
            }
        }

        private static void FillDropEllipse(Graphics graphics, RectangleF bounds)
        {
            using (var fillBrush = new LinearGradientBrush(
                new RectangleF(bounds.X, bounds.Y - 1, Math.Max(2, bounds.Width), Math.Max(2, bounds.Height) + 2),
                NativeVisuals.Lighten(Palette.ProviderSurface, 0.08),
                Palette.ProviderSurface,
                LinearGradientMode.Vertical))
            {
                graphics.FillEllipse(fillBrush, bounds);
            }
            using (var borderPen = new Pen(Palette.BorderStrong))
            {
                graphics.DrawEllipse(borderPen, bounds);
            }
        }

        private void Finish(bool aborted)
        {
            if (_finished)
            {
                return;
            }
            _finished = true;
            _timer.Stop();
            var landed = _landed;
            Close();
            if (!aborted && landed != null)
            {
                landed();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
                if (_memDc != IntPtr.Zero)
                {
                    if (_oldBitmap != IntPtr.Zero)
                    {
                        DesktopWidget.SelectObject(_memDc, _oldBitmap);
                        _oldBitmap = IntPtr.Zero;
                    }
                    DesktopWidget.DeleteDC(_memDc);
                    _memDc = IntPtr.Zero;
                }
                if (_dib != IntPtr.Zero)
                {
                    DesktopWidget.DeleteObject(_dib);
                    _dib = IntPtr.Zero;
                }
                if (_canvas != null)
                {
                    _canvas.Dispose();
                    _canvas = null;
                }
            }
            base.Dispose(disposing);
        }
    }
}
