using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CodexClaudeUsage
{
    internal enum GlobalHotkeyAction
    {
        ToggleUsage,
        Refresh
    }

    internal sealed class GlobalHotkeyManager : NativeWindow, IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const uint ModAlt = 0x0001;
        private const uint ModShift = 0x0004;
        private const uint ModWin = 0x0008;
        private const int ToggleUsageId = 1;
        private const int RefreshId = 2;
        private const uint ToggleUsageKey = 0x55; // U
        private const uint RefreshKey = 0x52; // R

        private bool _toggleUsageRegistered;
        private bool _refreshRegistered;
        private bool _disposed;
        private bool _handleDestroyed;

        public GlobalHotkeyManager()
        {
            CreateHandle(new CreateParams());
        }

        public event Action<GlobalHotkeyAction> Pressed;

        public bool Configure(bool enabled, out string error)
        {
            error = null;
            if (_disposed)
            {
                error = "快捷鍵管理器已關閉。";
                return false;
            }

            if (!enabled)
            {
                Disable();
                return true;
            }

            Disable();
            var toggleFallback = false;
            var refreshFallback = false;
            _toggleUsageRegistered = RegisterHotKey(Handle, ToggleUsageId, ModWin | ModAlt, ToggleUsageKey);
            if (!_toggleUsageRegistered)
            {
                _toggleUsageRegistered = RegisterHotKey(Handle, ToggleUsageId, ModWin | ModAlt | ModShift, ToggleUsageKey);
                toggleFallback = _toggleUsageRegistered;
            }
            _refreshRegistered = RegisterHotKey(Handle, RefreshId, ModWin | ModAlt, RefreshKey);
            if (!_refreshRegistered)
            {
                _refreshRegistered = RegisterHotKey(Handle, RefreshId, ModWin | ModAlt | ModShift, RefreshKey);
                refreshFallback = _refreshRegistered;
            }

            if (!_toggleUsageRegistered && !_refreshRegistered)
            {
                error = "全域快捷鍵已被其他程式使用。";
                return false;
            }

            var notes = new System.Collections.Generic.List<string>();
            if (toggleFallback) notes.Add("面板改用 Win+Alt+Shift+U");
            else if (!_toggleUsageRegistered) notes.Add("面板快捷鍵無法註冊");
            if (refreshFallback) notes.Add("更新改用 Win+Alt+Shift+R");
            else if (!_refreshRegistered) notes.Add("更新快捷鍵無法註冊");
            error = notes.Count == 0 ? null : string.Join("；", notes);
            return true;
        }

        public void Disable()
        {
            if (_disposed)
            {
                return;
            }
            UnregisterRegisteredHotkeys();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey)
            {
                var id = m.WParam.ToInt32();
                if (id == ToggleUsageId)
                {
                    var pressed = Pressed;
                    if (pressed != null)
                    {
                        pressed(GlobalHotkeyAction.ToggleUsage);
                    }
                }
                else if (id == RefreshId)
                {
                    var pressed = Pressed;
                    if (pressed != null)
                    {
                        pressed(GlobalHotkeyAction.Refresh);
                    }
                }
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            UnregisterRegisteredHotkeys();
            if (!_handleDestroyed && Handle != IntPtr.Zero)
            {
                _handleDestroyed = true;
                DestroyHandle();
            }
            Pressed = null;
            GC.SuppressFinalize(this);
        }

        private void UnregisterRegisteredHotkeys()
        {
            if (Handle == IntPtr.Zero)
            {
                _toggleUsageRegistered = false;
                _refreshRegistered = false;
                return;
            }

            if (_toggleUsageRegistered)
            {
                UnregisterHotKey(Handle, ToggleUsageId);
                _toggleUsageRegistered = false;
            }
            if (_refreshRegistered)
            {
                UnregisterHotKey(Handle, RefreshId);
                _refreshRegistered = false;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }
}
