using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Ak870Pro.Services;

namespace Ak870Pro.Views
{
    public sealed partial class LogPage : Page
    {
        private bool _busy;
        private bool _disposed;

        public LogPage()
        {
            InitializeComponent();
            foreach (var line in AppLog.Snapshot()) Append(line);
            AppLog.LineAdded += OnLineAdded;
            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _disposed = true;
            AppLog.LineAdded -= OnLineAdded;
        }

        private void OnLineAdded(string line)
        {
            if (_disposed) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_disposed) return;
                try { Append(line); } catch { /* 页面已销毁 */ }
            });
        }

        private void Append(string line)
        {
            LogText.Text += line + Environment.NewLine;
            LogScroll.UpdateLayout();
            LogScroll.ScrollToVerticalOffset(LogScroll.ScrollableHeight);
        }

        private byte[]? ParseHex()
        {
            try
            {
                var bytes = RawBox.Text.Split(new[] { ' ', ',', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? t[2..] : t)
                    .Select(t => Convert.ToByte(t, 16)).ToArray();
                if (bytes.Length == 0) return null;
                var pkt = new byte[HidTransport.FeaturePacketSize];
                Array.Copy(bytes, pkt, Math.Min(bytes.Length, pkt.Length));
                return pkt;
            }
            catch (Exception ex)
            {
                AppLog.Log($"hex 解析失败: {ex.Message}");
                return null;
            }
        }

        private async void Send_Click(object sender, RoutedEventArgs e) => await SendRawAsync(needResp: true);
        private async void SendNoResp_Click(object sender, RoutedEventArgs e) => await SendRawAsync(needResp: false);

        private async Task SendRawAsync(bool needResp)
        {
            if (AppState.State != ConnState.Connected) { AppLog.Log("未连接设备"); return; }
            var pkt = ParseHex();
            if (pkt == null || _busy) return;
            _busy = true; SendBtn.IsEnabled = SendNoRespBtn.IsEnabled = false;
            try
            {
                bool ok = await AppState.Device.SendRawAsync(pkt, needResp);
                AppLog.Log(ok ? "== 原始发送成功" : "== 原始发送失败");
            }
            finally { _busy = false; SendBtn.IsEnabled = SendNoRespBtn.IsEnabled = true; }
        }

        private void Clear_Click(object sender, RoutedEventArgs e) => LogText.Text = "";

        private void SaveLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    $"ak870_proto_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.WriteAllText(path, LogText.Text);
                AppLog.Log($"日志已保存: {path}");
            }
            catch (Exception ex) { AppLog.Log($"保存失败: {ex.Message}"); }
        }
    }
}
