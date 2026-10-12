using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ak870Pro.Services;

namespace Ak870Pro.Views
{
    public sealed partial class HomePage : Page
    {
        private bool Busy;

        public HomePage()
        {
            InitializeComponent();
        }

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (Busy) return;
            Busy = true;
            var dev = AppState.Device;
            ConnectBtn.IsEnabled = false;
            AppState.NotifyStateChanged();
            try
            {
                AppLog.Log("用户点击重新扫描连接。");
                bool ok = await dev.ConnectAsync();
                ToastService.Show(ok ? "连接成功" : "未找到设备，请确认键盘已连接（无线模式请按一下按键唤醒）", ok);
            }
            catch (Exception ex)
            {
                AppLog.Log($"连接异常: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                App.Log($"连接异常: {ex}");
                ToastService.Show("连接出现异常，详见协议日志", false);
            }
            finally
            {
                Busy = false;
                ConnectBtn.IsEnabled = true;
                DisconnectBtn.IsEnabled = dev.State == ConnState.Connected;
                AppState.NotifyStateChanged();
            }
        }

        private void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            AppState.Device.Disconnect();
            DisconnectBtn.IsEnabled = false;
            AppState.NotifyStateChanged();
        }

        private async void Layer_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireConnected()) return;
            if (Busy) return;
            Busy = true;
            try
            {
                int layer = int.Parse(((Button)sender).Tag.ToString()!);
                bool ok = await AppState.Device.SelectLayerAsync(layer);
                ToastService.Show(ok ? $"已选择层 {layer}" : "选择层失败", ok);
            }
            finally { Busy = false; }
        }

        private bool RequireConnected()
        {
            if (AppState.State == ConnState.Connected) return true;
            ToastService.Show("请先在主页连接设备", false);
            return false;
        }
    }
}
