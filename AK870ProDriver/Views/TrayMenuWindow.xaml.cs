using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;

namespace Ak870Pro.Views
{
    /// <summary>
    /// 托盘右键菜单宿主 — 纯 WinUI3 MenuFlyout 实现 (替代 Win32 TrackPopupMenu):
    /// 1x1 无边框窗口移到光标处, 以 MenuFlyout.ShowAt 弹出; 菜单关闭即销毁窗口。
    /// </summary>
    public sealed partial class TrayMenuWindow : Window
    {
        private static TrayMenuWindow? _current;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;

        private TrayMenuWindow(Action onOpen, Action onExit, int x, int y)
        {
            InitializeComponent();

            AppWindow.IsShownInSwitchers = false;
            // 明确关掉边框/标题栏/缩放, 并强制 1x1:
            // CreateForContextMenu 在 MSIX full-trust 下可能保留窗口边框, 导致托盘菜单旁
            // 露出一个"黑框+白边"的空窗口。
            var presenter = OverlappedPresenter.Create();
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            AppWindow.SetPresenter(presenter);
            AppWindow.Resize(new SizeInt32(1, 1));
            _pos = new PointInt32(x, y);

            var flyout = new MenuFlyout();
            var miOpen = new MenuFlyoutItem { Text = "打开主页面" };
            miOpen.Click += (_, _) => onOpen();
            var miExit = new MenuFlyoutItem { Text = "退出" };
            miExit.Click += (_, _) => onExit();
            flyout.Items.Add(miOpen);
            flyout.Items.Add(new MenuFlyoutSeparator());
            flyout.Items.Add(miExit);
            flyout.Closed += (_, _) =>
            {
                // 菜单关闭后销毁宿主窗口 (异步避免在 Closed 回调内直接 Close 的时序问题)
                DispatcherQueue.TryEnqueue(CloseOnce);
            };
            _flyout = flyout;
        }

        private readonly MenuFlyout _flyout;
        private readonly PointInt32 _pos;
        private bool _closing;

        private void CloseOnce()
        {
            if (_closing) return;
            _closing = true;
            if (ReferenceEquals(_current, this)) _current = null;
            try { Close(); } catch { /* 应用退出路径上窗口可能已销毁 */ }
        }

        /// <summary>在屏幕物理坐标 (x, y) 处弹出托盘菜单。</summary>
        public static void ShowAt(int x, int y, Action onOpen, Action onExit)
        {
            // 已有菜单开着则先关掉 (重复右键)
            var old = _current;
            _current = null;
            old?.CloseOnce();

            var w = new TrayMenuWindow(onOpen, onExit, x, y);
            _current = w;
            // XamlRoot 要等窗口激活且内容加载完成后才存在，必须在 Loaded 后再 ShowAt，
            // 否则直接抛 "This element does not have a XamlRoot" (未处理异常导致进程崩溃)。
            w.Anchor.Loaded += (_, _) =>
            {
                if (!ReferenceEquals(_current, w)) return;
                // ContextMenu present 下 Activate 前的 AppWindow.Move 不生效，用 Win32 强制到位
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
                SetWindowPos(hwnd, IntPtr.Zero, w._pos.X, w._pos.Y, 1, 1,
                    SWP_NOZORDER | SWP_NOACTIVATE);
                w._flyout.ShowAt(w.Anchor, new Point(0, 0));
            };
            w.Activate();
        }
    }
}
