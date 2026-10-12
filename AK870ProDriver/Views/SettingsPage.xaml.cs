using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using Ak870Pro.Services;

namespace Ak870Pro.Views
{
    /// <summary>设置页: 功能设置 (自主页迁移) + 开机自启动 + 关于。版本号连点 7 次切换 DEBUG 模式
    /// (对齐 Android 开发者模式交互), DEBUG 开启后侧边栏才显示协议日志页。</summary>
    public sealed partial class SettingsPage : Page
    {
        private const int TapThreshold = 7;
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunValue = "AK870Plus";
        private bool Busy;
        private bool _suppressAutoStart;
        private int _verTaps;

        public SettingsPage()
        {
            InitializeComponent();
            LoadFromSettings();
            RespondSlider.ValueChanged += (_, e) => RespondText.Text = $"{(int)e.NewValue} 档";
        }

        private void LoadFromSettings()
        {
            var s = SettingsService.Current;
            FnAltF4.IsChecked = s.FnAltF4;
            FnAltTab.IsChecked = s.FnAltTab;
            FnWinKey.IsChecked = s.FnWinKey;
            SleepCombo.SelectedIndex = s.SleepIndex;
            RespondSlider.Value = s.RespondLevel;
            DebugStateText.Visibility = s.DebugMode ? Visibility.Visible : Visibility.Collapsed;
            // 自启动状态以注册表为唯一事实来源
            _suppressAutoStart = true;
            AutoStartToggle.IsOn = GetAutoStart();
            _suppressAutoStart = false;
        }

        // ---------------- 开机自启动 (HKCU Run) ----------------

        private static bool GetAutoStart()
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string path && path == Environment.ProcessPath;
        }

        private static void SetAutoStart(bool on)
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey);
            if (on)
            {
                var exe = Environment.ProcessPath
                    ?? throw new InvalidOperationException("无法获取程序路径");
                k.SetValue(RunValue, $"\"{exe}\"");
            }
            else if (k.GetValue(RunValue) != null)
                k.DeleteValue(RunValue);
        }

        private void AutoStart_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressAutoStart) return;
            try
            {
                SetAutoStart(AutoStartToggle.IsOn);
                ToastService.Show(AutoStartToggle.IsOn ? "已开启开机自启动" : "已关闭开机自启动", true);
            }
            catch (Exception ex)
            {
                AppLog.Log($"自启动设置失败: {ex.Message}");
                ToastService.Show("自启动设置失败，详见协议日志", false);
                _suppressAutoStart = true;
                AutoStartToggle.IsOn = GetAutoStart();
                _suppressAutoStart = false;
            }
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
                    disAltTab: FnAltTab.IsChecked == true,
                    disAltF4: FnAltF4.IsChecked == true,
                    disWinKey: FnWinKey.IsChecked == true,
                    sleepTime: sleep,
                    respondTime: (int)RespondSlider.Value);
                if (ok)
                {
                    var s = SettingsService.Current;
                    s.FnAltF4 = FnAltF4.IsChecked == true;
                    s.FnAltTab = FnAltTab.IsChecked == true;
                    s.FnWinKey = FnWinKey.IsChecked == true;
                    s.SleepIndex = SleepCombo.SelectedIndex;
                    s.RespondLevel = (int)RespondSlider.Value;
                    SettingsService.Save();
                }
                ToastService.Show(ok ? "设置已应用" : "应用失败，详见协议日志", ok);
            }
            finally { Busy = false; ApplySettingsBtn.IsEnabled = true; }
        }

        private void Version_Click(object sender, RoutedEventArgs e)
        {
            _verTaps++;
            if (_verTaps >= TapThreshold)
            {
                _verTaps = 0;
                bool on = !SettingsService.Current.DebugMode;
                SettingsService.SetDebugMode(on);
                DebugStateText.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                ToastService.Show(on ? "DEBUG 模式已开启，协议日志已显示" : "DEBUG 模式已关闭", on);
                return;
            }
            int remain = TapThreshold - _verTaps;
            if (remain <= 3)
                ToastService.Show($"再点 {remain} 次开启 DEBUG 模式", true, 1500);
        }

        private bool RequireConnected()
        {
            if (AppState.State == ConnState.Connected) return true;
            ToastService.Show("请先在主页连接设备", false);
            return false;
        }
    }
}
