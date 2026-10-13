using System.Runtime.InteropServices;

namespace Ak870Pro.Services
{
    /// <summary>
    /// 音乐律动引擎:
    /// 进入音乐模式后, ~33ms 周期持续向键盘推送动效数据, 停止发送则键盘动画静止。
    ///   2.4G: 0b 1c 频谱帧: buf[4]=显示模式, buf[5]=背景模式, buf[6]=背景亮度, buf[7..27]=21列高度
    ///   USB:  04 20 + 512B 全键 RGB 流 + 04 02, 颜色由 PC 端按显示模式/背景模式渲染
    /// 数据源: AudioSpectrum (WASAPI 环回真实拾音, 失败退化内置假频谱)。
    /// 参数 (显示模式/振幅/背景模式/背景亮度) 运行中可经 UpdateParams 实时更新 —
    /// 每 tick 重读当前参数, 无需重发进入帧。
    /// </summary>
    public static class MusicEngine
    {
        private sealed class Params { public int Fore, Amp, BgMode, BgBright; }

        private static CancellationTokenSource? _cts;
        private static Params? _params;
        public static bool Running => _cts != null;
        public static int CurrentFore => _params?.Fore ?? -1;

        public static void Start(int foremode, int amp, int bgMode, int bgBright)
        {
            Stop();
            var p = new Params { Fore = foremode, Amp = amp, BgMode = bgMode, BgBright = bgBright };
            _params = p;
            var cts = new CancellationTokenSource();
            _cts = cts;
            _ = Task.Run(() => Loop(p, cts));
        }

        /// <summary>运行中实时更新 4 参数 (循环每 tick 重读, 即改即生效, 不重发进入帧)。</summary>
        public static void UpdateParams(int foremode, int amp, int bgMode, int bgBright)
        {
            var p = _params;
            if (p == null) return;
            p.Fore = foremode; p.Amp = amp; p.BgMode = bgMode; p.BgBright = bgBright;
        }

        public static void Stop()
        {
            var c = _cts;
            _cts = null;
            _params = null;
            c?.Cancel();
        }

        private static async Task Loop(Params p, CancellationTokenSource cts)
        {
            var ct = cts.Token;
            AppLog.Log($"音乐律动数据流开始 (foremode={p.Fore}, amp={p.Amp}, bg={p.BgMode}@{p.BgBright}, 33ms 周期)");
            using var spec = new AudioSpectrum();
            spec.Start();
            if (!spec.Live) AppLog.Log("使用内置假频谱");

            var keys = LayoutService.LoadKeys();
            int maxRow = keys.Count > 0 ? keys.Max(k => k.Row) : 5;
            int maxCol = keys.Count > 0 ? keys.Max(k => k.Col) : 20;
            var heights = new byte[AudioSpectrum.Columns];
            ScreenSampler? screen = null;
            int sent = 0, failed = 0, tick = 0;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    if (AppState.State != ConnState.Connected)
                    {
                        AppLog.Log("设备已断开, 音乐律动数据流终止");
                        break;
                    }
                    long t0 = Environment.TickCount64;
                    spec.Update(heights);
                    // 振幅 = PC 端对 21 列高度缩放 (滑块 60 → ×1.0, 实现为真实缩放)。
                    float ampScale = Math.Clamp(p.Amp, 0, 100) / 60f;
                    if (ampScale != 1f)
                        for (int i = 0; i < heights.Length; i++)
                            heights[i] = (byte)Math.Clamp((int)MathF.Round(heights[i] * ampScale), 0, spec.MaxHeight);
                    try
                    {
                        bool ok;
                        if (AppState.Device.IsReportChannel)
                        {
                            ok = await AppState.Device.SendMusicFrame24Async(heights, p.Fore, p.BgMode, p.BgBright);
                        }
                        else
                        {
                            if (p.Fore == 100 && screen == null) screen = new ScreenSampler(AudioSpectrum.Columns, maxRow + 1);
                            var stream = BuildUsbStream(keys, heights, p.Fore, p.BgMode, p.BgBright, maxRow, maxCol, tick, screen);
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
                    tick++;
                    long dt = Environment.TickCount64 - t0;
                    try { await Task.Delay((int)Math.Clamp(33 - dt, 4, 100), ct); }
                    catch (OperationCanceledException) { break; }
                }
            }
            finally { screen?.Dispose(); }

            if (ReferenceEquals(_cts, cts)) { _cts = null; _params = null; }
            cts.Dispose();
            AppLog.Log("音乐律动数据流已停止");
        }

        /// <summary>USB 流: 每键 4 字节 [灯槽, R, G, B]。点亮键按显示模式取色 (屏光联动用屏幕取色),
        /// 未点亮键按背景模式×背景亮度填静态色 (无=黑)。</summary>
        private static byte[] BuildUsbStream(
            List<Models.KeyInfo> keys, byte[] heights,
            int foremode, int bgMode, int bgBright, int maxRow, int maxCol, int tick, ScreenSampler? screen)
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
                (byte r, byte g, byte b) c;
                if (lit)
                {
                    c = foremode == 100 && screen != null
                        ? screen.ColorAt(k.Row, maxRow, c21)
                        : ModeColor(foremode, c21, tick);
                }
                else
                {
                    c = bgMode == 0 ? default : Scale(ModeColor(bgMode, c21, tick), bgBright);
                }
                stream[i * 4 + 0] = (byte)k.LightIndex;
                stream[i * 4 + 1] = c.r;
                stream[i * 4 + 2] = c.g;
                stream[i * 4 + 3] = c.b;
                i++;
            }
            return stream;
        }

        /// <summary>显示/背景模式 data → 列颜色。
        /// 1 绿黄红(列渐变) 2 彩虹 3 彩虹反向 4 色谱(随时间轮转) 5 频谱循环(列+时间)
        /// 6 白 7 红 8 橙 9 黄 10 蓝 11 绿。</summary>
        private static (byte r, byte g, byte b) ModeColor(int mode, int col, int tick)
        {
            switch (mode)
            {
                case 1: return Hsv(120.0 * (1.0 - col / (double)(AudioSpectrum.Columns - 1)), 1.0, 1.0); // 绿→黄→红
                case 2: return Hsv(col * 360.0 / AudioSpectrum.Columns, 1.0, 1.0);
                case 3: return Hsv(360.0 - col * 360.0 / AudioSpectrum.Columns, 1.0, 1.0);
                case 4: return Hsv(tick * 6.0, 1.0, 1.0);
                case 5: return Hsv(col * 360.0 / AudioSpectrum.Columns + tick * 6.0, 1.0, 1.0);
                case 6: return (255, 255, 255);
                case 7: return (255, 0, 0);
                case 8: return (255, 128, 0);
                case 9: return (255, 255, 0);
                case 10: return (0, 0, 255);
                case 11: return (0, 255, 0);
                default: return (0, 0, 0);
            }
        }

        private static (byte r, byte g, byte b) Scale((byte r, byte g, byte b) c, int bright)
            => ((byte)(c.r * Math.Clamp(bright, 0, 100) / 100),
                (byte)(c.g * Math.Clamp(bright, 0, 100) / 100),
                (byte)(c.b * Math.Clamp(bright, 0, 100) / 100));

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

        /// <summary>
        /// 屏光联动取色 (仅 USB):
        /// 每帧把主屏 StretchBlt 到 21×rows 小位图, 按键行列映射取网格色。
        /// </summary>
        private sealed class ScreenSampler : IDisposable
        {
            private readonly int _cols, _rows;
            private IntPtr _scrDc, _memDc, _bmp, _old;
            private byte[]? _pix;
            private int _w, _h;

            public ScreenSampler(int cols, int rows) { _cols = cols; _rows = rows; }

            /// <summary>取网格色。row=键盘行(0=顶行), col=21 列索引; 键盘顶行=屏幕顶部条带。</summary>
            public (byte r, byte g, byte b) ColorAt(int row, int maxRow, int col)
            {
                try
                {
                    Ensure();
                    StretchBlt(_memDc, 0, 0, _cols, _rows, _scrDc, 0, 0, _w, _h, 0x00CC0020 /*SRCCOPY*/);
                    var bmi = new BITMAPINFO { biSize = 40, biWidth = _cols, biHeight = -_rows, biPlanes = 1, biBitCount = 32 };
                    GetDIBits(_memDc, _bmp, 0, (uint)_rows, _pix!, ref bmi, 0);
                    int gr = Math.Clamp(row * (_rows - 1) / Math.Max(maxRow, 1), 0, _rows - 1);
                    int off = (gr * _cols + Math.Clamp(col, 0, _cols - 1)) * 4;
                    return (_pix![off + 2], _pix[off + 1], _pix[off + 0]); // BGR → RGB
                }
                catch { return (0, 0, 0); }
            }

            private void Ensure()
            {
                int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
                if (_memDc != IntPtr.Zero && w == _w && h == _h) return;
                DisposeGdi();
                _w = w; _h = h;
                _scrDc = GetDC(IntPtr.Zero);
                _memDc = CreateCompatibleDC(_scrDc);
                _bmp = CreateCompatibleBitmap(_scrDc, _cols, _rows);
                _old = SelectObject(_memDc, _bmp);
                _pix = new byte[_cols * _rows * 4];
            }

            private void DisposeGdi()
            {
                if (_old != IntPtr.Zero) { SelectObject(_memDc, _old); _old = IntPtr.Zero; }
                if (_bmp != IntPtr.Zero) { DeleteObject(_bmp); _bmp = IntPtr.Zero; }
                if (_memDc != IntPtr.Zero) { DeleteDC(_memDc); _memDc = IntPtr.Zero; }
                if (_scrDc != IntPtr.Zero) { ReleaseDC(IntPtr.Zero, _scrDc); _scrDc = IntPtr.Zero; }
            }

            public void Dispose() => DisposeGdi();

            [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
            [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
            [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
            [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
            [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
            [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
            [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
            [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
            [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr hdcDest, int x, int y, int w, int h,
                IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, uint rop);
            [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
                [Out] byte[] bits, ref BITMAPINFO bmi, uint usage);

            [StructLayout(LayoutKind.Sequential)]
            private struct BITMAPINFO
            {
                public int biSize, biWidth, biHeight;
                public short biPlanes, biBitCount;
                public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
            }
        }
    }
}
