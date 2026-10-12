using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Ak870Pro.Services
{
    /// <summary>全局顶部浮窗提示（复用 MainWindow 顶部浮层，替代各页底部 InfoBar）。</summary>
    public static class ToastService
    {
        private static TextBlock? _text;
        private static Border? _border;
        private static DispatcherQueueTimer? _timer;

        public static void Attach(Border border, TextBlock text)
        {
            _border = border;
            _text = text;
        }

        public static void Show(string message, bool success = true, int durationMs = 3000)
        {
            if (_border == null || _text == null) return;
            var dq = _border.DispatcherQueue;
            dq.TryEnqueue(() =>
            {
                _text.Text = message;
                _border.Background = new SolidColorBrush(success
                    ? Windows.UI.Color.FromArgb(230, 16, 124, 16)
                    : Windows.UI.Color.FromArgb(230, 180, 30, 30));
                _border.Visibility = Visibility.Visible;
                _border.Opacity = 0;
                // 简单淡入：用 Composition 动画太沉，直接 Opacity 渐变
                var fade = dq.CreateTimer();
                fade.Interval = TimeSpan.FromMilliseconds(16);
                double o = 0;
                fade.Tick += (_, _) =>
                {
                    o += 0.15;
                    if (o >= 1) { _border.Opacity = 1; fade.Stop(); }
                    else _border.Opacity = o;
                };
                fade.Start();

                if (_timer == null)
                {
                    _timer = dq.CreateTimer();
                    _timer.IsRepeating = false;
                }
                _timer.Interval = TimeSpan.FromMilliseconds(durationMs);
                _timer.Tick += (_, _) =>
                {
                    _border.Visibility = Visibility.Collapsed;
                };
                _timer.Start();
            });
        }
    }
}
