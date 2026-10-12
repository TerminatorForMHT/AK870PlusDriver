using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Ak870Pro.Views;

namespace Ak870Pro
{
    public sealed partial class MainWindow : Window
    {
        private bool _wasConnected;

        public MainWindow()
        {
            InitializeComponent();
            SystemBackdrop = new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt };
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Tall;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1180, 760));
            Nav.SelectedItem = Nav.MenuItems[0];
            Services.ToastService.Attach(ToastBorder, ToastText);
            ApplyDebugVisibility();
            Services.SettingsService.DebugModeChanged += _ => DispatcherQueue.TryEnqueue(ApplyDebugVisibility);
            Services.AppState.StateChanged += s => DispatcherQueue.TryEnqueue(() =>
            {
                UpdateStatus();
                if (s == Services.ConnState.Connected)
                    Services.ToastService.Show("设备已连接", true, 2000);
                else if (s == Services.ConnState.Disconnected && _wasConnected)
                    Services.ToastService.Show("设备已断开", false, 2000);
                _wasConnected = s == Services.ConnState.Connected;
            });
            UpdateStatus();
            // 官方机制: 窗口建立后即注册 WM_DEVICECHANGE 监视, 启动自动扫描握手,
            // 设备插入/拔出自动重连 (DeviceMonitor → 0x4321C0/0x41FD80/0x41F690 等价实现)。
            Services.DeviceMonitor.Start(this);
            Services.TrayService.Attach(this);
            SetWindowIcon();
            AppWindow.Closing += OnClosing;
        }

        private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            try
            {
                if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
                {
                    ContentFrame.Content = tag switch
                    {
                        "home" => new HomePage(),
                        "light" => new LightPage(),
                        "keys" => new KeysPage(),
                        "macro" => new MacroPage(),
                        "log" => new LogPage(),
                        "settings" => new SettingsPage(),
                        _ => null
                    };
                }
            }
            catch (Exception ex)
            {
                App.Log($"导航页面构造失败: {ex}");
                Services.AppLog.Log($"页面加载失败: {ex.Message}");
            }
        }

        /// <summary>DEBUG 模式控制协议日志页可见性; 关闭时若正停留在该页则切回主页。</summary>
        private void ApplyDebugVisibility()
        {
            bool on = Services.SettingsService.Current.DebugMode;
            LogNavItem.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on && Nav.SelectedItem is NavigationViewItem sel && ReferenceEquals(sel, LogNavItem))
                Nav.SelectedItem = Nav.MenuItems[0];
        }

        private void UpdateStatus()
        {
            var d = Services.AppState.Device;
            StatusText.Text = Services.AppState.State switch
            {
                Services.ConnState.Connected => $"已连接 ({d.ModeTag ?? d.InterfaceTag})",
                Services.ConnState.Connecting => "正在连接...",
                _ => "未连接"
            };
        }

        private void SetWindowIcon()
        {
            try
            {
                var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
                if (System.IO.File.Exists(iconPath))
                    AppWindow.SetIcon(iconPath);
            }
            catch { /* 图标设置失败不影响运行 */ }
        }

        private void OnClosing(object sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs e)
        {
            e.Cancel = true; // 最小化到托盘，不退出
            Services.TrayService.MinimizeToTray();
        }
    }
}
