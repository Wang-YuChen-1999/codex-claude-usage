using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexClaudeUsage
{
    internal static class Motion
    {
        private const uint SpiGetClientAreaAnimation = 0x1042;

        public static bool IsEnabled
        {
            get
            {
                return SystemInformation.UIEffectsEnabled
                    && ClientAreaAnimationsEnabled()
                    && !SystemInformation.HighContrast;
            }
        }

        private static bool ClientAreaAnimationsEnabled()
        {
            try
            {
                bool enabled;
                if (SystemParametersInfo(SpiGetClientAreaAnimation, 0, out enabled, 0))
                {
                    return enabled;
                }
            }
            catch
            {
            }

            return SystemInformation.UIEffectsEnabled;
        }

        public static double Clamp01(double value)
        {
            return Math.Max(0, Math.Min(1, value));
        }

        public static double EaseOutQuart(double value)
        {
            var inverse = 1 - Clamp01(value);
            return 1 - inverse * inverse * inverse * inverse;
        }

        public static double EaseInQuart(double value)
        {
            var clamped = Clamp01(value);
            return clamped * clamped * clamped * clamped;
        }

        /// <summary>二次加速（重力感），用於液滴墜落。</summary>
        public static double EaseInQuad(double value)
        {
            var clamped = Clamp01(value);
            return clamped * clamped;
        }

        public static double EaseOutCubic(double value)
        {
            var inverse = 1 - Clamp01(value);
            return 1 - inverse * inverse * inverse;
        }

        /// <summary>帶過衝回彈的緩動（終點附近略微超過 1 再彈回），用於液態形變。</summary>
        public static double EaseOutBack(double value)
        {
            const double c1 = 1.70158;
            const double c3 = c1 + 1;
            var t = Clamp01(value) - 1;
            return 1 + c3 * t * t * t + c1 * t * t;
        }

        public static double SmoothStep(double value)
        {
            var clamped = Clamp01(value);
            return clamped * clamped * (3 - 2 * clamped);
        }

        /// <summary>C2 連續的平滑步進（起訖速度與加速度皆為零），用於液態充盈的無頓挫起步。</summary>
        public static double SmootherStep(double value)
        {
            var t = Clamp01(value);
            return t * t * t * (t * (t * 6 - 15) + 10);
        }

        public static double Lerp(double from, double to, double progress)
        {
            return from + (to - from) * Clamp01(progress);
        }

        public static Color Lerp(Color from, Color to, double progress)
        {
            var amount = Clamp01(progress);
            return Color.FromArgb(
                (int)Math.Round(Lerp(from.A, to.A, amount)),
                (int)Math.Round(Lerp(from.R, to.R, amount)),
                (int)Math.Round(Lerp(from.G, to.G, amount)),
                (int)Math.Round(Lerp(from.B, to.B, amount)));
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(
            uint action,
            uint parameter,
            [MarshalAs(UnmanagedType.Bool)] out bool value,
            uint update);
    }

    internal sealed class AnimatedNumber
    {
        public double Current { get; private set; }
        public double Target { get; private set; }
        private double _from;
        private long _startedAt;
        private int _duration;

        public AnimatedNumber(double value)
        {
            Current = value;
            Target = value;
        }

        public void Jump(double value)
        {
            Current = value;
            Target = value;
            _from = value;
            _duration = 0;
        }

        public void AnimateTo(double value, long now, int duration)
        {
            Update(now);
            _from = Current;
            Target = value;
            _startedAt = now;
            _duration = Math.Max(1, duration);
        }

        public bool Update(long now)
        {
            if (_duration <= 0)
            {
                Current = Target;
                return false;
            }

            var progress = (now - _startedAt) / (double)_duration;
            if (progress >= 1)
            {
                Current = Target;
                _duration = 0;
                return false;
            }

            Current = Motion.Lerp(_from, Target, Motion.SmoothStep(progress));
            return true;
        }
    }
}
