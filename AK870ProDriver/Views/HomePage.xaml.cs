using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ak870Pro.Services;

namespace Ak870Pro.Views
{
    public sealed partial class HomePage : Page
    {
        private bool Busy;
        private bool _loading;

        public HomePage()
        {
            InitializeComponent();
            LoadFromSettings();
            RespondSlider.ValueChanged += (_, e) => RespondText.Text = $"{(int)e.NewValue} 档";
        }

        private void LoadFromSettings()
        {
            _loading = true;
            var s = SettingsService.Current;
            FnAltF4.IsChecked = s.FnAltF4;
            FnAltTab.IsChecked = s.FnAltTab;
            FnSwitch.Value = s.FnSwitch;
            SleepCombo.SelectedIndex = s.SleepIndex;
            RespondSlider.Value = s.RespondLevel;
            _loading = false;
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

        private async void ApplySettings_Click(object sender, RoutedEventArgs e)
        {
            if (!RequireConnected()) return;
            if (Busy) return;
            Busy = true;
            ApplySettingsBtn.IsEnabled = false;
            try
            {
                int sleep = SleepCombo.SelectedIndex switch { 1 => 1, 2 => 5, 3 => 30, _ => 0 };
                bool ok = await AppState.Device.ApplySettingsAsync(
                    fnB: FnAltF4.IsChecked == true,
                    fnC: FnAltTab.IsChecked == true,
                    fnSwitch: (int)FnSwitch.Value,
                    sleepTime: sleep,
                    respondTime: (int)RespondSlider.Value);
                if (ok) Persist();
                ToastService.Show(ok ? "设置已应用" : "应用失败，详见协议日志", ok);
            }
            finally { Busy = false; ApplySettingsBtn.IsEnabled = true; }
        }

        private void Persist()
        {
            var s = SettingsService.Current;
            s.FnAltF4 = FnAltF4.IsChecked == true;
            s.FnAltTab = FnAltTab.IsChecked == true;
            s.FnSwitch = (int)FnSwitch.Value;
            s.SleepIndex = SleepCombo.SelectedIndex;
            s.RespondLevel = (int)RespondSlider.Value;
            SettingsService.Save();
        }

        private bool RequireConnected()
        {
            if (AppState.State == ConnState.Connected) return true;
            ToastService.Show("请先在主页连接设备", false);
            return false;
        }
    }
}
