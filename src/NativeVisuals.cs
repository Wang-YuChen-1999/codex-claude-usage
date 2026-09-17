using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CodexClaudeUsage
{
    internal enum BrandIconKind
    {
        Codex,
        Claude,
        Antigravity
    }

    /// <summary>
    /// Windows 11 原生視窗質感：DWM 圓角、系統陰影、深色框架與邊框色。
    /// Windows 10 及更早版本回傳失敗，由呼叫端使用 Region 圓角與 CS_DROPSHADOW 後備。
    /// </summary>
    internal static class WindowChrome
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaBorderColor = 34;
        private const int DwmwcpDoNotRound = 1;
        private const int DwmwcpRound = 2;

        public static readonly bool SupportsNativeRounding = DetectWindows11();

        /// <summary>套用原生圓角彈窗質感；回傳 DWM 是否接受圓角（Windows 11+）。</summary>
        public static bool ApplyRoundedPopupChrome(IntPtr handle, Color borderColor)
        {
            if (handle == IntPtr.Zero)
            {
                return false;
            }

            TrySetAttribute(handle, DwmwaUseImmersiveDarkMode, 1);
            if (!SupportsNativeRounding)
            {
                return false;
            }

            var rounded = TrySetAttribute(handle, DwmwaWindowCornerPreference, DwmwcpRound);
            if (rounded)
            {
                TrySetAttribute(handle, DwmwaBorderColor, ToColorRef(borderColor));
            }
            return rounded;
        }

        /// <summary>
        /// 切換 DWM 圓角：自訂形狀（如圓形 Region）時需關閉，
        /// 否則 DWM 的圓角矩形會蓋過 Region 裁切。
        /// </summary>
        public static void SetCornerRounding(IntPtr handle, bool round)
        {
            if (handle == IntPtr.Zero || !SupportsNativeRounding)
            {
                return;
            }
            TrySetAttribute(handle, DwmwaWindowCornerPreference, round ? DwmwcpRound : DwmwcpDoNotRound);
        }

        /// <summary>
        /// 取得視窗實際所在螢幕的 DPI 縮放（GetDpiForWindow）；
        /// 不可用時回傳 0，呼叫端自行回退。
        /// </summary>
        public static float GetWindowScale(IntPtr handle)
        {
            if (handle == IntPtr.Zero)
            {
                return 0;
            }
            try
            {
                var dpi = GetDpiForWindow(handle);
                if (dpi >= 96)
                {
                    return dpi / 96f;
                }
            }
            catch
            {
            }
            return 0;
        }

        /// <summary>
        /// 依螢幕絕對座標取得該位置所在顯示器的 DPI 縮放；不可用時回傳 0。
        /// </summary>
        public static float GetPointScale(Point pt)
        {
            try
            {
                var hmon = MonitorFromPoint(pt, 2 /* MONITOR_DEFAULTTONEAREST */);
                if (hmon != IntPtr.Zero)
                {
                    uint dx, dy;
                    if (GetDpiForMonitor(hmon, 0 /* MDT_EFFECTIVE_DPI */, out dx, out dy) == 0 && dx >= 96)
                    {
                        return dx / 96f;
                    }
                }
            }
            catch
            {
            }
            return 0;
        }

        private static bool TrySetAttribute(IntPtr handle, int attribute, int value)
        {
            try
            {
                return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)) == 0;
            }
            catch
            {
                return false;
            }
        }

        private static int ToColorRef(Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
        }

        private static bool DetectWindows11()
        {
            try
            {
                var info = new OsVersionInfo();
                info.Size = Marshal.SizeOf(typeof(OsVersionInfo));
                if (RtlGetVersion(ref info) == 0)
                {
                    return info.Major > 10 || (info.Major == 10 && info.Build >= 22000);
                }
            }
            catch
            {
            }
            return false;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OsVersionInfo
        {
            public int Size;
            public int Major;
            public int Minor;
            public int Build;
            public int Platform;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string ServicePack;
        }

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(Point pt, uint dwFlags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
        private static extern int RtlGetVersion(ref OsVersionInfo info);
    }

    internal static class NativeVisuals
    {
        private const string CodexResource = "CodexClaudeUsage.Assets.codex-native.ico";
        private const string ClaudeResource = "CodexClaudeUsage.Assets.claude-native.ico";
        private const string AntigravityResource = "CodexClaudeUsage.Assets.antigravity-native.ico";
        private const string AntigravityPngResource = "CodexClaudeUsage.Assets.antigravity-native.png";
        private static readonly Lazy<Icon> CodexIcon = new Lazy<Icon>(() => LoadBrandIcon(BrandIconKind.Codex));
        private static readonly Lazy<Icon> ClaudeIcon = new Lazy<Icon>(() => LoadBrandIcon(BrandIconKind.Claude));
        private static readonly Lazy<Icon> AntigravityIcon = new Lazy<Icon>(() => LoadBrandIcon(BrandIconKind.Antigravity));
        private static readonly Lazy<Bitmap> CodexIconBitmap = new Lazy<Bitmap>(() => ToIconBitmap(BrandIconKind.Codex));
        private static readonly Lazy<Bitmap> ClaudeIconBitmap = new Lazy<Bitmap>(() => ToIconBitmap(BrandIconKind.Claude));
        private static readonly Lazy<Bitmap> AntigravityIconBitmap = new Lazy<Bitmap>(() => ToIconBitmap(BrandIconKind.Antigravity));
        private static readonly string GlyphFontName = ResolveGlyphFontName();
        private static readonly string UiFontName = ResolveFontName("Segoe UI Variable Text", "Segoe UI");
        private static readonly string DisplayFontName = ResolveFontName("Segoe UI Variable Display", UiFontName);
        private static readonly string SemiboldFontName = ResolveFontName("Segoe UI Semibold", UiFontName);
        private static readonly string ValueFontName = ResolveFontName(
            "Microsoft JhengHei UI",
            "Microsoft JhengHei",
            UiFontName);

        public static Icon GetBrandIcon(BrandIconKind kind)
        {
            if (kind == BrandIconKind.Codex) return CodexIcon.Value;
            if (kind == BrandIconKind.Claude) return ClaudeIcon.Value;
            return AntigravityIcon.Value;
        }

        public static void DrawBrandIcon(Graphics graphics, BrandIconKind kind, Rectangle bounds)
        {
            // 以高品質縮放繪製（DrawIcon 的整數縮放在高 DPI 下會糊/鋸齒）。
            Bitmap bitmap = null;
            if (kind == BrandIconKind.Codex) bitmap = CodexIconBitmap.Value;
            else if (kind == BrandIconKind.Claude) bitmap = ClaudeIconBitmap.Value;
            else if (kind == BrandIconKind.Antigravity) bitmap = AntigravityIconBitmap.Value;

            if (bitmap != null)
            {
                var previousInterpolation = graphics.InterpolationMode;
                var previousOffset = graphics.PixelOffsetMode;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                graphics.DrawImage(bitmap, bounds);
                graphics.InterpolationMode = previousInterpolation;
                graphics.PixelOffsetMode = previousOffset;
                return;
            }

            var accent = kind == BrandIconKind.Codex ? Palette.Codex : (kind == BrandIconKind.Claude ? Palette.Claude : Palette.Antigravity);
            using (var brush = new SolidBrush(accent))
            using (var font = new Font("Segoe UI", Math.Max(8, bounds.Height * 0.4f), FontStyle.Bold, GraphicsUnit.Pixel))
            {
                graphics.FillEllipse(brush, bounds);
                var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                using (var textBrush = new SolidBrush(Palette.Background))
                {
                    string label = kind == BrandIconKind.Codex ? "C" : (kind == BrandIconKind.Claude ? "A" : "G");
                    graphics.DrawString(label, font, textBrush, bounds, format);
                }
                format.Dispose();
            }
        }

        public static Font CreateGlyphFont(float size, GraphicsUnit unit)
        {
            return new Font(GlyphFontName, size, FontStyle.Regular, unit);
        }

        public static Font CreateUiFont(float size, FontStyle style)
        {
            return new Font(UiFontName, size, style, GraphicsUnit.Point);
        }

        public static Font CreateDisplayFont(float size, FontStyle style)
        {
            return new Font(DisplayFontName, size, style, GraphicsUnit.Point);
        }

        public static Font CreateSemiboldFont(float size)
        {
            return new Font(SemiboldFontName, size, FontStyle.Regular, GraphicsUnit.Point);
        }

        public static Font CreateValueFont(float size, FontStyle style)
        {
            return new Font(ValueFontName, size, style, GraphicsUnit.Point);
        }

        public static Bitmap CreateMenuGlyph(string glyph, Color color)
        {
            var bitmap = new Bitmap(24, 24);
            bitmap.SetResolution(96, 96);
            using (var graphics = Graphics.FromImage(bitmap))
            using (var font = CreateGlyphFont(14, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            {
                graphics.Clear(Color.Transparent);
                graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };
                graphics.DrawString(glyph, font, brush, new RectangleF(0, 0, 24, 24), format);
                format.Dispose();
            }
            return bitmap;
        }

        /// <summary>朝白色方向提亮，amount 為 0–1 的比例；用於進度條頂緣光感。</summary>
        public static Color Lighten(Color color, double amount)
        {
            var clamped = Math.Max(0, Math.Min(1, amount));
            return Color.FromArgb(
                color.A,
                (int)Math.Round(color.R + (255 - color.R) * clamped),
                (int)Math.Round(color.G + (255 - color.G) * clamped),
                (int)Math.Round(color.B + (255 - color.B) * clamped));
        }

        public static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            var diameter = Math.Max(2, radius * 2);
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static Bitmap ToIconBitmap(BrandIconKind kind)
        {
            if (kind == BrandIconKind.Antigravity)
            {
                try
                {
                    using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(AntigravityPngResource))
                    {
                        if (stream != null)
                        {
                            return new Bitmap(stream);
                        }
                    }
                }
                catch
                {
                }
            }

            try
            {
                var icon = GetBrandIcon(kind);
                return icon == null ? null : icon.ToBitmap();
            }
            catch
            {
                return null;
            }
        }

        private static Icon LoadBrandIcon(BrandIconKind kind)
        {
            var nativePath = FindInstalledIcon(kind);
            if (!string.IsNullOrEmpty(nativePath))
            {
                try
                {
                    if (nativePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        var assoc = Icon.ExtractAssociatedIcon(nativePath);
                        if (assoc != null)
                        {
                            return (Icon)assoc.Clone();
                        }
                    }
                    else
                    {
                        using (var icon = new Icon(nativePath, new Size(64, 64)))
                        {
                            return (Icon)icon.Clone();
                        }
                    }
                }
                catch
                {
                }
            }

            var resourceName = kind == BrandIconKind.Codex ? CodexResource : (kind == BrandIconKind.Claude ? ClaudeResource : AntigravityResource);
            try
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
                {
                    if (stream == null)
                    {
                        return null;
                    }
                    using (var icon = new Icon(stream, new Size(64, 64)))
                    {
                        return (Icon)icon.Clone();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        private static string FindInstalledIcon(BrandIconKind kind)
        {
            try
            {
                if (kind == BrandIconKind.Antigravity)
                {
                    var localApp = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Programs", "antigravity", "Antigravity.exe");
                    return File.Exists(localApp) ? localApp : null;
                }

                var windowsApps = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "WindowsApps");
                var pattern = kind == BrandIconKind.Codex
                    ? "OpenAI.Codex_*__2p2nqsd0c76g0"
                    : "Claude_*__pzs8sxrjxfjjc";
                var relative = kind == BrandIconKind.Codex
                    ? Path.Combine("app", "resources", "chatgpt-tray-dark.ico")
                    : Path.Combine("app", "resources", "Tray-Win32-Dark.ico");

                return Directory.EnumerateDirectories(windowsApps, pattern)
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => Path.Combine(path, relative))
                    .FirstOrDefault(File.Exists);
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveGlyphFontName()
        {
            try
            {
                using (var font = new Font("Segoe Fluent Icons", 10, FontStyle.Regular, GraphicsUnit.Point))
                {
                    if (string.Equals(font.Name, "Segoe Fluent Icons", StringComparison.OrdinalIgnoreCase))
                    {
                        return "Segoe Fluent Icons";
                    }
                }
            }
            catch
            {
            }
            return "Segoe MDL2 Assets";
        }

        private static string ResolveFontName(params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                try
                {
                    using (var font = new Font(candidate, 10, FontStyle.Regular, GraphicsUnit.Point))
                    {
                        if (string.Equals(font.Name, candidate, StringComparison.OrdinalIgnoreCase))
                        {
                            return candidate;
                        }
                    }
                }
                catch
                {
                }
            }
            return "Segoe UI";
        }
    }
}
