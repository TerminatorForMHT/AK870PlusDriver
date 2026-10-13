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
            AmpSlider.ValueChanged += MusicSlider_Changed;
            BgBrightSlider.ValueChanged += MusicSlider_Changed;
            foreach (var m in LayoutService.LightModes) ModeCombo.Items.Add(m);
            foreach (var m in LayoutService.MusicModes) MusicCombo.Items.Add(m);
            foreach (var m in LayoutService.MusicBgModes) MusicBgCombo.Items.Add(m);
            LoadFromSettings();
            // SelectionChanged 必须在回填完持久化设置之后才挂接 — 否则进入页面时的
            // 程序化选中会触发"选灯效即应用", 每次打开本页都向键盘重发一次灯效。
            ModeCombo.SelectionChanged += ModeCombo_SelectionChanged;
            MusicCombo.SelectionChanged += MusicCombo_SelectionChanged;
            MusicBgCombo.SelectionChanged += MusicBgCombo_SelectionChanged;
        }

        /// <summary>当前律动 4 参数 (UI 索引 → 协议 data 映射, 严禁 SelectedIndex+1)。</summary>
        private (int fore, int amp, int bgMode, int bgBright) CurrentMusicParams()
        {
            int fi = MusicCombo.SelectedIndex; if (fi < 0) fi = 0;
            int bi = MusicBgCombo.SelectedIndex; if (bi < 0) bi = 0;
            return (LayoutService.MusicModeData[fi], (int)AmpSlider.Value,
                    LayoutService.MusicBgModeData[bi], (int)BgBrightSlider.Value);
        }

        /// <summary>屏光联动 (data=100) 选中时隐藏振幅/背景控件。</summary>
        private void UpdateMusicOptionVisibility()
        {
            int fi = MusicCombo.SelectedIndex;
            bool screen = fi >= 0 && LayoutService.MusicModeData[fi] == 100;
            var vis = screen ? Visibility.Collapsed : Visibility.Visible;
            AmpRow.Visibility = vis;
            BgModeRow.Visibility = vis;
            BgBrightRow.Visibility = vis;
        }

        // 律动运行中改任一参数: 实时更新引擎 (循环每 tick 重读参数, 不重发进入帧)。
        private void MusicCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || Busy) return;
            UpdateMusicOptionVisibility();
            // 屏光联动仅 USB 存在 (2.4G 下拉里无此项), 运行中选中则回退
            var p = CurrentMusicParams();
            if (p.fore == 100 && AppState.Device.IsReportChannel && MusicEngine.Running)
            {
                int prev = Array.IndexOf(LayoutService.MusicModeData, MusicEngine.CurrentFore);
                _loading = true; MusicCombo.SelectedIndex = prev >= 0 ? prev : 0; _loading = false;
                UpdateMusicOptionVisibility();
                ToastService.Show("屏光联动仅 USB 连接支持", false);
                return;
            }
            Persist();
            if (MusicEngine.Running && AppState.State == ConnState.Connected)
                MusicEngine.UpdateParams(p.fore, p.amp, p.bgMode, p.bgBright);
        }

        private void MusicBgCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || Busy) return;
            Persist();
            if (!MusicEngine.Running || AppState.State != ConnState.Connected) return;
            var p = CurrentMusicParams();
            MusicEngine.UpdateParams(p.fore, p.amp, p.bgMode, p.bgBright);
        }

        private void MusicSlider_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            AmpText.Text = $"{(int)AmpSlider.Value}%";
            BgBrightText.Text = $"{(int)BgBrightSlider.Value}%";
            if (_loading || Busy) return;
            Persist();
            if (!MusicEngine.Running || AppState.State != ConnState.Connected) return;
            var p = CurrentMusicParams();
            MusicEngine.UpdateParams(p.fore, p.amp, p.bgMode, p.bgBright);
        }

        private void LoadFromSettings()
        {
            _loading = true;
            var s = SettingsService.Current;
            ModeCombo.SelectedIndex = s.LightMode;
            BrightSlider.Value = s.LightBright;
            SpeedSlider.Value = s.LightSpeed;
            MusicCombo.SelectedIndex = s.MusicMode >= 0 && s.MusicMode < LayoutService.MusicModes.Length ? s.MusicMode : 0;
            MusicBgCombo.SelectedIndex = s.MusicBgMode >= 0 && s.MusicBgMode < LayoutService.MusicBgModes.Length ? s.MusicBgMode : 0;
            AmpSlider.Value = Math.Clamp(s.MusicAmp, 0, 100);
            BgBrightSlider.Value = Math.Clamp(s.MusicBgBright, 0, 100);
            BrightText.Text = ((int)BrightSlider.Value).ToString();
            SpeedText.Text = ((int)SpeedSlider.Value).ToString();
            AmpText.Text = $"{(int)AmpSlider.Value}%";
            BgBrightText.Text = $"{(int)BgBrightSlider.Value}%";
            UpdateMusicOptionVisibility();
            _loading = false;
        }

        private void Persist()
        {
            var s = SettingsService.Current;
            s.LightMode = ModeCombo.SelectedIndex;
            s.LightBright = (int)BrightSlider.Value;
            s.LightSpeed = (int)SpeedSlider.Value;
            s.MusicMode = MusicCombo.SelectedIndex;
            s.MusicAmp = (int)AmpSlider.Value;
            s.MusicBgMode = MusicBgCombo.SelectedIndex;
            s.MusicBgBright = (int)BgBrightSlider.Value;
            SettingsService.Save();
        }

        private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            // 选灯效即立即应用（下拉一改就发 05 10 帧）
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
            var p = CurrentMusicParams();
            // 屏光联动仅 USB 存在 (2.4G 下拉无此项)
            if (p.fore == 100 && AppState.Device.IsReportChannel)
            {
                ToastService.Show("屏光联动仅 USB 连接支持", false);
                return;
            }
            Busy = true; MusicBtn.IsEnabled = false;
            try
            {
                bool ok = await AppState.Device.EnterMusicModeAsync(p.fore);
                if (ok)
                {
                    Persist();
                    // 进入音乐模式只是开关, 键盘动画靠 PC 端 ~33ms 持续推送数据帧
                    MusicEngine.Start(p.fore, p.amp, p.bgMode, p.bgBright);
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
                // 无独立"关音乐"命令: 发一次普通灯效即覆盖退出律动模式
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
