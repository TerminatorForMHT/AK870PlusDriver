namespace Ak870Pro.Services
{
    /// <summary>
    /// 音乐律动引擎 — 复刻官方 30ms 工作线程行为 (0x45B020 → 0x433A00):
    /// 进入音乐模式后, ~33ms 周期持续向键盘推送动效数据, 停止发送则键盘动画静止。
    ///   2.4G: 0b 1c 频谱帧 (0x4330C0)
    ///   USB:  04 20 + 512B 全键 RGB 流 + 04 02 (0x432E80)
    /// 数据源: AudioSpectrum (WASAPI 环回真实拾音, 失败退化官方同款假频谱)。
    /// </summary>
    public static class MusicEngine
    {
        private static CancellationTokenSource? _cts;
        public static bool Running => _cts != null;

        public static void Start(int foremode)
        {
            Stop();
            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = Task.Run(() => Loop(foremode, cts));
        }

        public static void Stop()
        {
            var c = _cts;
            _cts = null;
            c?.Cancel();
        }

        private static async Task Loop(int foremode, CancellationTokenSource cts)
        {
            var ct = cts.Token;
            AppLog.Log($"音乐律动数据流开始 (foremode={foremode}, 33ms 周期)");
            using var spec = new AudioSpectrum();
            spec.Start();
            if (!spec.Live) AppLog.Log("使用内置假频谱 (官方同款行为)");

            var keys = LayoutService.LoadKeys();
            int maxRow = keys.Count > 0 ? keys.Max(k => k.Row) : 5;
            int maxCol = keys.Count > 0 ? keys.Max(k => k.Col) : 20;
            var palette = BuildPalette();
            var heights = new byte[AudioSpectrum.Columns];
            int sent = 0, failed = 0;

            while (!ct.IsCancellationRequested)
            {
                if (AppState.State != ConnState.Connected)
                {
                    AppLog.Log("设备已断开, 音乐律动数据流终止");
                    break;
                }
                long t0 = Environment.TickCount64;
                spec.Update(heights);
                try
                {
                    bool ok;
                    if (AppState.Device.IsReportChannel)
                    {
                        ok = await AppState.Device.SendMusicFrame24Async(heights, foremode);
                    }
                    else
                    {
                        var stream = BuildUsbStream(keys, heights, palette, maxRow, maxCol);
                        ok = await AppState.Device.SendMusicFrameUsbAsync(stream);
                    }
                    if (ok) sent++; else failed++;
                    if ((sent + failed) % 100 == 1 && sent > 0)
                        AppLog.Log($"律动流: 已发 {sent} 帧" + (failed > 0 ? $", 失败 {failed}" : ""));
                }
                catch (Exception ex)
                {
                    AppLog.Log($"律动帧异常, 数据流终止: {ex.Message}");
                    break;
                }
                long dt = Environment.TickCount64 - t0;
                try { await Task.Delay((int)Math.Clamp(33 - dt, 4, 100), ct); }
                catch (OperationCanceledException) { break; }
            }

            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
            AppLog.Log("音乐律动数据流已停止");
        }

        /// <summary>USB 流: 每键 4 字节 [灯槽, R, G, B], 列高度≥键所在行(自下而上)即点亮。</summary>
        private static byte[] BuildUsbStream(
            List<Models.KeyInfo> keys, byte[] heights,
            (byte r, byte g, byte b)[] palette, int maxRow, int maxCol)
        {
            var stream = new byte[512];
            int i = 0;
            foreach (var k in keys)
            {
                if (i >= 128) break;
                int c21 = maxCol > 0
                    ? Math.Min(k.Col * AudioSpectrum.Columns / (maxCol + 1), AudioSpectrum.Columns - 1)
                    : 0;
                bool lit = (maxRow - k.Row) < heights[c21];
                var c = lit ? palette[c21] : default;
                stream[i * 4 + 0] = (byte)k.LightIndex;
                stream[i * 4 + 1] = c.r;
                stream[i * 4 + 2] = c.g;
                stream[i * 4 + 3] = c.b;
                i++;
            }
            return stream;
        }

        /// <summary>21 列彩虹调色板 (列→色相均布)。</summary>
        private static (byte r, byte g, byte b)[] BuildPalette()
        {
            var p = new (byte, byte, byte)[AudioSpectrum.Columns];
            for (int i = 0; i < p.Length; i++) p[i] = Hsv(i * 360.0 / p.Length, 1.0, 1.0);
            return p;
        }

        private static (byte r, byte g, byte b) Hsv(double h, double s, double v)
        {
            h = (h % 360 + 360) % 360;
            int sector = (int)(h / 60) % 6;
            double f = h / 60 - Math.Floor(h / 60);
            double p1 = v * (1 - s), p2 = v * (1 - f * s), p3 = v * (1 - (1 - f) * s);
            (double r, double g, double b) = sector switch
            {
                0 => (v, p3, p1), 1 => (p2, v, p1), 2 => (p1, v, p3),
                3 => (p1, p2, v), 4 => (p3, p1, v), _ => (v, p1, p2),
            };
            return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }
    }
}
