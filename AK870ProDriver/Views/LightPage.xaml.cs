using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ak870Pro.Services;

namespace Ak870Pro.Views
{
    public sealed partial class LightPage : Page
    {
        private bool Busy;
        private bool _loading;

        public LightPage()
        {
            InitializeComponent();
            BrightSlider.ValueChanged += Slider_Changed;
            SpeedSlider.ValueChanged += Slider_Changed;
            foreach (var m in LayoutService.LightModes) ModeCombo.Items.Add(m);
            foreach (var m in LayoutService.MusicModes) MusicCombo.Items.Add(m);
            LoadFromSettings();
            // SelectionChanged 必须在回填完持久化设置之后才挂接 — 否则进入页面时的
            // 程序化选中会触发"选灯效即应用", 每次打开本页都向键盘重发一次灯效。
            ModeCombo.SelectionChanged += ModeCombo_SelectionChanged;
            MusicCombo.SelectionChanged += MusicCombo_SelectionChanged;
        }

        // 律动运行中切换显示模式: 立即用新 foremode 重发进入帧并重启数据流,
        // 否则新模式要等下次点"开启律动"才生效。
        private async void MusicCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || Busy) return;
            Persist();
            if (!MusicEngine.Running || AppState.State != ConnState.Connected) return;
            int foremode = MusicCombo.SelectedIndex;
            bool ok = await AppState.Device.EnterMusicModeAsync(foremode);
            if (ok) MusicEngine.Start(foremode);
        }

        private void LoadFromSettings()
        {
            _loading = true;
            var s = SettingsService.Current;
            ModeCombo.SelectedIndex = s.LightMode;
            BrightSlider.Value = s.LightBright;
            SpeedSlider.Value = s.LightSpeed;
            MusicCombo.SelectedIndex = s.MusicMode;
            BrightText.Text = ((int)BrightSlider.Value).ToString();
            SpeedText.Text = ((int)SpeedSlider.Value).ToString();
            _loading = false;
        }

        private void Persist()
        {
            var s = SettingsService.Current;
            s.LightMode = ModeCombo.SelectedIndex;
            s.LightBright = (int)BrightSlider.Value;
            s.LightSpeed = (int)SpeedSlider.Value;
            s.MusicMode = MusicCombo.SelectedIndex;
            SettingsService.Save();
        }

        private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            // 选灯效即立即应用（官方行为: 下拉一改就发 05 10 帧）
            _ = ApplyLightAsync();
        }

        private void Slider_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            BrightText.Text = ((int)BrightSlider.Value).ToString();
            SpeedText.Text = ((int)SpeedSlider.Value).ToString();
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
            => await ApplyLightAsync();

        private async Task ApplyLightAsync()
        {
            if (AppState.State != ConnState.Connected) { ToastService.Show("请先在主页连接设备", false); return; }
            if (Busy) return;
            Busy = true; ApplyBtn.IsEnabled = false;
            try
            {
                bool ok = await AppState.Device.SetLightModeAsync(
                    mode: LayoutService.ProtocolModeOf(ModeCombo.SelectedIndex),
                    brightness: (int)BrightSlider.Value,
                    speed: (int)SpeedSlider.Value);
                if (ok) Persist();
                ToastService.Show(ok ? $"灯效已应用: {ModeCombo.SelectedItem}" : "应用失败，详见协议日志", ok);
            }
            finally { Busy = false; ApplyBtn.IsEnabled = true; }
        }

        private async void Music_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.State != ConnState.Connected) { ToastService.Show("请先在主页连接设备", false); return; }
            if (Busy) return;
            Busy = true; MusicBtn.IsEnabled = false;
            try
            {
                // 官方 GetCurSel 返回 0-based 索引 (0..14), 直接作为协议 foremode
                int foremode = MusicCombo.SelectedIndex;
                bool ok = await AppState.Device.EnterMusicModeAsync(foremode);
                if (ok)
                {
                    Persist();
                    // 进入音乐模式只是开关, 键盘动画靠 PC 端 ~33ms 持续推送数据帧 (官方 0x433A00 机制)
                    MusicEngine.Start(foremode);
                }
                ToastService.Show(ok ? $"音乐律动已开启: {MusicCombo.SelectedItem}" : "开启失败，详见协议日志", ok);
            }
            finally { Busy = false; MusicBtn.IsEnabled = true; }
        }

        private async void MusicStop_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.State != ConnState.Connected) { ToastService.Show("请先在主页连接设备", false); return; }
            if (Busy) return;
            Busy = true; MusicStopBtn.IsEnabled = false;
            try
            {
                MusicEngine.Stop();
                // 官方无独立"关音乐"命令: 发一次普通灯效即覆盖退出律动模式
                bool ok = await AppState.Device.SetLightModeAsync(
                    mode: LayoutService.ProtocolModeOf(ModeCombo.SelectedIndex),
                    brightness: (int)BrightSlider.Value,
                    speed: (int)SpeedSlider.Value);
                ToastService.Show(ok ? "音乐律动已停止，已恢复普通灯效" : "停止失败，详见协议日志", ok);
            }
            finally { Busy = false; MusicStopBtn.IsEnabled = true; }
        }
    }
}
