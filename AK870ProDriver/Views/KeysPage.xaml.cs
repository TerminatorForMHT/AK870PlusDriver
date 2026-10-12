using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Ak870Pro.Models;
using Ak870Pro.Services;
using Microsoft.UI;
using Color = Windows.UI.Color;

namespace Ak870Pro.Views
{
    public sealed partial class KeysPage : Page
    {
        private KeyInfo? _selected;
        private bool _busy;
        private readonly Dictionary<KeyInfo, Border> _visuals = new();

        public KeysPage()
        {
            InitializeComponent();
            ModeRgb.Checked += Mode_Changed;
            ModeUser.Checked += Mode_Changed;
            RSlider.ValueChanged += Color_Changed;
            GSlider.ValueChanged += Color_Changed;
            BSlider.ValueChanged += Color_Changed;
            BuildKeyboard();
        }

        private void BuildKeyboard()
        {
            // XML 坐标基于官方 800x300 布局图; 统一平移到 (0,0) 后等比映射进 760x420 Canvas (外层 Viewbox 再自适应)
            int minL = AppState.Keys.Min(k => k.Left);
            int minT = AppState.Keys.Min(k => k.Top);
            int maxR = AppState.Keys.Max(k => k.Left + k.Width);
            int maxB = AppState.Keys.Max(k => k.Top + k.Height);
            const double canvasW = 760, canvasH = 420, margin = 6;
            double scale = Math.Min((canvasW - 2 * margin) / (maxR - minL), (canvasH - 2 * margin) / (maxB - minT));

            foreach (var k in AppState.Keys)
            {
                var border = new Border
                {
                    Width = k.Width * scale,
                    Height = k.Height * scale,
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128)),
                    Background = new SolidColorBrush(Colors.Transparent),
                    Tag = k,
                };
                var tb = new TextBlock
                {
                    Text = k.Name,
                    FontSize = Math.Max(8, 10 * scale),
                    Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(1),
                };
                border.Child = tb;
                Canvas.SetLeft(border, margin + (k.Left - minL) * scale);
                Canvas.SetTop(border, margin + (k.Top - minT) * scale);
                border.PointerPressed += Key_PointerPressed;
                KeyCanvas.Children.Add(border);
                _visuals[k] = border;
            }
        }

        private void Key_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b && b.Tag is KeyInfo k)
                SelectKey(k);
        }

        private void SelectKey(KeyInfo k)
        {
            _selected = k;
            SelKeyText.Text = $"{k.Name}  (key_index={k.KeyIndex}, light_index={k.LightIndex})";
            RSlider.Value = k.R; GSlider.Value = k.G; BSlider.Value = k.B;
            foreach (var kv in _visuals)
                kv.Value.BorderBrush = new SolidColorBrush(kv.Key == k
                    ? Color.FromArgb(255, 0, 160, 255)
                    : Color.FromArgb(90, 128, 128, 128));
        }

        private Color CurrentColor => Color.FromArgb(255, (byte)RSlider.Value, (byte)GSlider.Value, (byte)BSlider.Value);

        private void Color_Changed(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
            => ColorPreview.Background = new SolidColorBrush(CurrentColor);

        private void Mode_Changed(object sender, RoutedEventArgs e)
        {
            // 用户灯光模式预览: 开=白, 关=透明
            if (ModeUser.IsChecked == true)
                foreach (var kv in _visuals)
                    kv.Value.Background = new SolidColorBrush(kv.Key.UserOn ? Colors.White : Colors.Transparent);
            else
                foreach (var kv in _visuals)
                    kv.Value.Background = new SolidColorBrush(kv.Key.HasColor
                        ? Color.FromArgb(255, kv.Key.R, kv.Key.G, kv.Key.B)
                        : Colors.Transparent);
        }

        private void SetKey_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) { Show(false, "请先点击一个按键"); return; }
            var c = CurrentColor;
            if (ModeUser.IsChecked == true)
            {
                _selected.UserOn = c.R + c.G + c.B > 0;
                _visuals[_selected].Background = new SolidColorBrush(_selected.UserOn ? Colors.White : Colors.Transparent);
            }
            else
            {
                _selected.R = c.R; _selected.G = c.G; _selected.B = c.B;
                _visuals[_selected].Background = new SolidColorBrush(c);
            }
        }

        private void ClearKey_Click(object sender, RoutedEventArgs e)
        {
            if (_selected == null) return;
            _selected.R = _selected.G = _selected.B = 0;
            _selected.UserOn = false;
            _visuals[_selected].Background = new SolidColorBrush(Colors.Transparent);
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var k in AppState.Keys) { k.R = k.G = k.B = 0; k.UserOn = false; }
            foreach (var v in _visuals.Values) v.Background = new SolidColorBrush(Colors.Transparent);
        }

        private void FillAll_Click(object sender, RoutedEventArgs e)
        {
            var c = CurrentColor;
            foreach (var k in AppState.Keys)
            {
                if (ModeUser.IsChecked == true) { k.UserOn = true; _visuals[k].Background = new SolidColorBrush(Colors.White); }
                else { k.R = c.R; k.G = c.G; k.B = c.B; _visuals[k].Background = new SolidColorBrush(c); }
            }
        }

        private async void Apply_Click(object sender, RoutedEventArgs e)
        {
            if (AppState.State != ConnState.Connected) { Show(false, "请先在主页连接设备"); return; }
            if (_busy) return;
            _busy = true; ApplyBtn.IsEnabled = false;
            try
            {
                bool ok;
                if (ModeUser.IsChecked == true)
                {
                    var on = AppState.Keys.Where(k => k.UserOn).Select(k => k.LightIndex).ToList();
                    ok = await AppState.Device.ApplyUserLightAsync(on);
                }
                else
                {
                    var colors = AppState.Keys.Where(k => k.HasColor)
                        .ToDictionary(k => k.LightIndex, k => (k.R, k.G, k.B));
                    if (colors.Count == 0) { Show(false, "没有已设置的按键颜色"); return; }
                    ok = await AppState.Device.ApplyPerKeyRgbAsync(colors);
                }
                Show(ok, ok ? "已应用到键盘" : "应用失败，详见协议日志");
            }
            finally { _busy = false; ApplyBtn.IsEnabled = true; }
        }

        private static void Show(bool ok, string msg) => ToastService.Show(msg, ok);
    }
}
