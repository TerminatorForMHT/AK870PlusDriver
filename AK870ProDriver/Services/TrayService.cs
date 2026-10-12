using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Ak870Pro.Services
{
    /// <summary>系统托盘（Win32 Shell_NotifyIcon 图标 + WinUI3 MenuFlyout 右键菜单）。</summary>
    public static class TrayService
    {
        private const int NIM_ADD = 0x00000000;
        private const int NIM_MODIFY = 0x00000001;
        private const int NIM_DELETE = 0x00000002;
        private const int NIF_MESSAGE = 0x00000001;
        private const int NIF_ICON = 0x00000002;
        private const int NIF_TIP = 0x00000004;
        private const int NIF_SHOWTIP = 0x00000080;
        private const uint WM_USER = 0x0400;
        private const uint WM_TRAYICON = WM_USER + 1;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint WM_RBUTTONUP = 0x0205;
        private const uint WM_RBUTTONDOWN = 0x0204;
        private const int IMAGE_ICON = 1;
        private const int LR_LOADFROMFILE = 0x00000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public IntPtr hBalloonIcon;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);
        [DllImport("user32.dll")]
        private static extern IntPtr LoadImage(IntPtr hInst, string lpszName, int uType, int cxDesired, int cyDesired, int fuLoad);
        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X; public int Y; }

        private static NOTIFYICONDATA _nid;
        private static IntPtr _hIcon;
        private static Window? _window;
        private static IntPtr _hwnd;

        public static void Attach(Window window)
        {
            _window = window;
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);

            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            _hIcon = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
            if (_hIcon == IntPtr.Zero)
                _hIcon = ExtractIconFromExe();

            _nid = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _hIcon,
                szTip = "AK870 Plus 键盘驱动",
            };
            Shell_NotifyIcon(NIM_ADD, ref _nid);
        }

        private static IntPtr ExtractIconFromExe()
        {
            var exePath = Environment.ProcessPath;
            if (exePath != null && File.Exists(exePath))
            {
                IntPtr hIcon = ExtractIcon(_hwnd, exePath, 0);
                if (hIcon != IntPtr.Zero) return hIcon;
            }
            return IntPtr.Zero;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, uint nIconIndex);

        public static void HandleMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg != WM_TRAYICON) return;
            uint mouseMsg = (uint)(lParam.ToInt32() & 0xFFFF);
            if (mouseMsg == WM_LBUTTONUP)
            {
                RestoreWindow();
            }
            else if (mouseMsg == WM_RBUTTONUP)
            {
                // 只响应 RBUTTONUP — DOWN+UP 都弹会导致菜单被连开两次
                ShowContextMenu();
            }
        }

        private static void ShowContextMenu()
        {
            GetCursorPos(out POINT pt);
            AppLog.Log($"托盘右键 → 弹出菜单 @({pt.X},{pt.Y})");
            // WinUI3 MenuFlyout 弹出在光标处 (宿主: 1x1 无边框 TrayMenuWindow)
            Views.TrayMenuWindow.ShowAt(pt.X, pt.Y, RestoreWindow, ExitApp);
        }

        private static void ExitApp()
        {
            Dispose();
            Application.Current.Exit();
        }

        public static void MinimizeToTray()
        {
            if (_window == null) return;
            _window.AppWindow.Hide();
        }

        public static void RestoreWindow()
        {
            if (_window == null) return;
            _window.AppWindow.Show(true);
            SetForegroundWindow(_hwnd);
        }

        public static void Dispose()
        {
            Shell_NotifyIcon(NIM_DELETE, ref _nid);
            if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
        }
    }
}
