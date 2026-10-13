using System.Runtime.InteropServices;

namespace Ak870Pro.Services
{
    /// <summary>
    /// WASAPI 环回采集 + FFT → 21 列频谱高度 (0..MaxHeight)。
    ///
    /// 实现真实系统音频环回拾音 (WASAPI Loopback) + FFT 频谱分析; 采集不可用时自动退化
    /// 为内置相位旋转假频谱, 保证律动始终可用。
    ///
    /// COM 访问走裸 vtable 委托 (Marshal.GetDelegateForFunctionPointer), 不走 RCW —
    /// 在本进程 (WinUI3/CsWinRT 混部) RCW 接口转换会抛 InvalidCastException。
    /// </summary>
    public sealed class AudioSpectrum : IDisposable
    {
        public const int Columns = 21;
        public int MaxHeight { get; set; } = 6;
        public bool Live => _pCapture != IntPtr.Zero;

        // ---------------- 状态 ----------------
        private IntPtr _pEnumerator, _pDevice, _pClient, _pCapture;
        private IntPtr _mixFormat;
        private int _channels = 2;
        private int _blockAlign = 8;
        private int _sampleRate = 48000;
        private bool _float32 = true;
        private bool _fake;

        private readonly float[] _ring = new float[1024];
        private int _ringPos;
        private readonly float[] _hann = new float[1024];
        private readonly float[] _re = new float[1024];
        private readonly float[] _im = new float[1024];
        private readonly float[] _bandMag = new float[Columns];
        private readonly float[] _smooth = new float[Columns];
        private float _runMax = 1e-3f;
        private float _musicEnv;
        private float _activity;
        private readonly float[] _prevMag = new float[Columns];
        private int _silentFrames;
        private double _fakePhase;
        private int[]? _bandLo, _bandHi;

        // ---------------- 裸 vtable COM ----------------

        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr pUnkOuter, int dwClsContext, ref Guid riid, out IntPtr ppv);
        [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr ptr);

        // vtable 槽位 (IUnknown: 0=QI 1=AddRef 2=Release):
        //   IMMDeviceEnumerator: 4=GetDefaultAudioEndpoint
        //   IMMDevice: 3=Activate
        //   IAudioClient: 3=Initialize 8=GetMixFormat 10=Start 11=Stop 14=GetService
        //   IAudioCaptureClient: 3=GetBuffer 4=ReleaseBuffer 5=GetNextPacketSize
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetDefaultAudioEndpointD(IntPtr self, int dataFlow, int role, out IntPtr ppDevice);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ActivateD(IntPtr self, ref Guid iid, int clsCtx, IntPtr activationParams, out IntPtr ppInterface);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InitializeD(IntPtr self, int shareMode, uint streamFlags, long hnsBuffer, long hnsPeriod, IntPtr pFormat, IntPtr sessionGuid);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetMixFormatD(IntPtr self, out IntPtr ppFormat);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StartStopD(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetServiceD(IntPtr self, ref Guid riid, out IntPtr ppv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetBufferD(IntPtr self, out IntPtr ppData, out int frames, out int flags, out long devPos, out long qpcPos);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseBufferD(IntPtr self, int frames);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetNextPacketSizeD(IntPtr self, out int frames);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int ReleaseD(IntPtr self);

        private GetNextPacketSizeD? _getNextPacket;
        private GetBufferD? _getBuffer;
        private ReleaseBufferD? _releaseBuffer;

        private static T GetProc<T>(IntPtr obj, int slot) where T : Delegate
        {
            IntPtr vt = Marshal.ReadIntPtr(obj);
            IntPtr fp = Marshal.ReadIntPtr(vt, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(fp);
        }

        private static void ComRelease(ref IntPtr p)
        {
            if (p == IntPtr.Zero) return;
            GetProc<ReleaseD>(p, 2)(p);
            p = IntPtr.Zero;
        }

        private const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
        private const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
        private const int CLSCTX_ALL = 0x17;

        /// <summary>启动环回采集。失败时自动进入假频谱模式并记日志, 不抛异常。</summary>
        public void Start()
        {
            try
            {
                var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"); // MMDeviceEnumerator
                var iidEnum = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
                Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iidEnum, out _pEnumerator));

                var getDefault = GetProc<GetDefaultAudioEndpointD>(_pEnumerator, 4);
                Marshal.ThrowExceptionForHR(getDefault(_pEnumerator, 0 /*eRender*/, 0 /*eConsole*/, out _pDevice));

                var activate = GetProc<ActivateD>(_pDevice, 3);
                var iidClient = new Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2"); // IAudioClient
                Marshal.ThrowExceptionForHR(activate(_pDevice, ref iidClient, CLSCTX_ALL, IntPtr.Zero, out _pClient));

                Marshal.ThrowExceptionForHR(GetProc<GetMixFormatD>(_pClient, 8)(_pClient, out _mixFormat));
                ParseFormat(_mixFormat);

                var init = GetProc<InitializeD>(_pClient, 3);
                Marshal.ThrowExceptionForHR(init(_pClient, 0 /*SHARED*/, AUDCLNT_STREAMFLAGS_LOOPBACK,
                    500_000 /*50ms*/, 0, _mixFormat, IntPtr.Zero));

                var getService = GetProc<GetServiceD>(_pClient, 14);
                var iidCap = new Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317"); // IAudioCaptureClient
                Marshal.ThrowExceptionForHR(getService(_pClient, ref iidCap, out _pCapture));

                Marshal.ThrowExceptionForHR(GetProc<StartStopD>(_pClient, 10)(_pClient));

                _getNextPacket = GetProc<GetNextPacketSizeD>(_pCapture, 5);
                _getBuffer = GetProc<GetBufferD>(_pCapture, 3);
                _releaseBuffer = GetProc<ReleaseBufferD>(_pCapture, 4);
                InitBands();
                AppLog.Log($"音频环回采集已启动: {_sampleRate}Hz {_channels}ch {(_float32 ? "f32" : "i16")}");
            }
            catch (Exception ex)
            {
                AppLog.Log($"音频采集不可用, 退化为内置假频谱: {ex.Message}");
                DisposeCapture();
                _fake = true;
            }
            for (int i = 0; i < 1024; i++)
                _hann[i] = 0.5f - 0.5f * MathF.Cos(2f * MathF.PI * i / 1023f);
        }

        private void ParseFormat(IntPtr p)
        {
            short tag = Marshal.ReadInt16(p, 0);
            _channels = Marshal.ReadInt16(p, 2);
            _sampleRate = Marshal.ReadInt32(p, 4);
            _blockAlign = Marshal.ReadInt16(p, 12);
            int bits = Marshal.ReadInt16(p, 14);
            _float32 = tag == 3;
            if (tag == -2 /*WAVE_FORMAT_EXTENSIBLE*/ && _channels > 0)
                _float32 = Marshal.ReadInt16(p, 24) == 3; // SubFormat 前两字节
            if (_float32 && bits != 32) _float32 = true; // 宽容处理
        }

        private void InitBands()
        {
            _bandLo = new int[Columns];
            _bandHi = new int[Columns];
            float bin = _sampleRate / 1024f;
            for (int i = 0; i < Columns; i++)
            {
                float f0 = 45f * MathF.Pow(15500f / 45f, i / (float)Columns);
                float f1 = 45f * MathF.Pow(15500f / 45f, (i + 1) / (float)Columns);
                _bandLo[i] = Math.Clamp((int)(f0 / bin), 1, 511);
                _bandHi[i] = Math.Clamp(Math.Max(_bandLo[i] + 1, (int)(f1 / bin)), 2, 512);
            }
        }

        /// <summary>采集/生成一帧, 填充 21 列高度 (0..MaxHeight)。</summary>
        public void Update(byte[] heightsOut)
        {
            if (_fake) { UpdateFake(heightsOut); return; }
            try { DrainCapture(); }
            catch (Exception ex)
            {
                AppLog.Log($"音频采集中断, 退化假频谱: {ex.Message}");
                DisposeCapture();
                _fake = true;
                UpdateFake(heightsOut);
                return;
            }
            ComputeBands();
            float peak = 0;
            for (int i = 0; i < Columns; i++) peak = MathF.Max(peak, _bandMag[i]);
            // "停了就灭"判定: 音乐有节奏/动态, 页面底噪和静音是"死"的——
            // 频谱帧间几乎不变。电平法会漏(底噪电平不固定, 0.10~0.23 都出现过),
            // 改为测帧间变化量: 播放时逐帧差异大, 暂停后变化量趋零, 与绝对电平解耦。
            float diff = 0;
            for (int i = 0; i < Columns; i++) diff += MathF.Abs(_bandMag[i] - _prevMag[i]);
            Array.Copy(_bandMag, _prevMag, Columns);
            // 变化量做快攻慢放包络, 得到"活跃度"。慢放系数压低(×0.6)让暂停后
            // 活跃度快速掉下来, 缩短清零延迟。
            if (diff > _activity) _activity = diff; else _activity = _activity * 0.6f + diff * 0.4f;
            bool alive = _activity > 5e-3f;      // 有动态 = 还在放
            bool silent = peak < 2e-3f;          // 或接近全静音
            if (!alive || silent) _silentFrames++; else _silentFrames = 0;
            if (_silentFrames >= 3) // ~100ms 确认
            {
                _runMax = 0;
                _musicEnv = 0;
                _activity = 0;
                Array.Clear(_smooth, 0, _smooth.Length);
                Array.Clear(heightsOut, 0, Columns);
                return;
            }
            if (peak > _musicEnv) _musicEnv = peak; else _musicEnv *= 0.997f;
            for (int i = 0; i < Columns; i++)
            {
                float m = _bandMag[i];
                _runMax = MathF.Max(_runMax * 0.995f, m);
                float v = m / (_runMax + 1e-6f);
                v = MathF.Pow(Math.Clamp(v, 0f, 1f), 0.7f);
                if (m < 1e-4f) v = 0f;
                _smooth[i] = MathF.Max(v, _smooth[i] * 0.6f); // 快攻慢放
                heightsOut[i] = (byte)Math.Clamp((int)MathF.Round(_smooth[i] * MaxHeight), 0, MaxHeight);
            }
        }

        private void DrainCapture()
        {
            if (_pCapture == IntPtr.Zero || _getNextPacket == null || _getBuffer == null || _releaseBuffer == null) return;
            Marshal.ThrowExceptionForHR(_getNextPacket(_pCapture, out int packet));
            while (packet > 0)
            {
                Marshal.ThrowExceptionForHR(_getBuffer(_pCapture, out IntPtr data, out int frames, out int flags, out _, out _));
                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data != IntPtr.Zero)
                    AppendSamples(data, frames);
                Marshal.ThrowExceptionForHR(_releaseBuffer(_pCapture, frames));
                Marshal.ThrowExceptionForHR(_getNextPacket(_pCapture, out packet));
            }
        }

        private byte[] _packetBuf = new byte[8192];

        private void AppendSamples(IntPtr data, int frames)
        {
            int bytes = frames * _blockAlign;
            if (_packetBuf.Length < bytes) _packetBuf = new byte[bytes];
            Marshal.Copy(data, _packetBuf, 0, bytes);
            var buf = _packetBuf;
            for (int f = 0; f < frames; f++)
            {
                float mono = 0;
                int baseOff = f * _blockAlign;
                for (int c = 0; c < _channels; c++)
                {
                    float s = _float32
                        ? BitConverter.ToSingle(buf, baseOff + c * 4)
                        : BitConverter.ToInt16(buf, baseOff + c * 2) / 32768f;
                    mono += s;
                }
                mono /= _channels;
                _ring[_ringPos] = mono;
                _ringPos = (_ringPos + 1) & 1023;
            }
        }

        private void ComputeBands()
        {
            int start = _ringPos; // 最旧样本
            for (int i = 0; i < 1024; i++)
            {
                _re[i] = _ring[(start + i) & 1023] * _hann[i];
                _im[i] = 0;
            }
            Fft(_re, _im);
            for (int b = 0; b < Columns; b++)
            {
                float sum = 0;
                for (int k = _bandLo![b]; k < _bandHi![b]; k++)
                    sum += MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
                _bandMag[b] = sum / (_bandHi[b] - _bandLo[b]) / 128f;
            }
        }

        /// <summary>1024 点迭代基-2 FFT (就地)。</summary>
        private static void Fft(float[] re, float[] im)
        {
            const int n = 1024;
            for (int i = 1, j = 0; i < n; i++)
            {
                int bit = n >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
            }
            for (int len = 2; len <= n; len <<= 1)
            {
                float ang = -2f * MathF.PI / len;
                float wr = MathF.Cos(ang), wi = MathF.Sin(ang);
                int half = len >> 1;
                for (int i = 0; i < n; i += len)
                {
                    float cwr = 1f, cwi = 0f;
                    for (int k = 0; k < half; k++)
                    {
                        int a = i + k, b = i + k + half;
                        float tr = re[b] * cwr - im[b] * cwi;
                        float ti = re[b] * cwi + im[b] * cwr;
                        re[b] = re[a] - tr; im[b] = im[a] - ti;
                        re[a] += tr; im[a] += ti;
                        float nwr = cwr * wr - cwi * wi;
                        cwi = cwr * wi + cwi * wr;
                        cwr = nwr;
                    }
                }
            }
        }

        /// <summary>内置假频谱 (查表相位旋转, 采集不可用时的退化动画)。</summary>
        private void UpdateFake(byte[] heightsOut)
        {
            _fakePhase += 0.35;
            for (int i = 0; i < Columns; i++)
            {
                double s = 0.5 + 0.5 * Math.Sin(_fakePhase + i * 0.62) * Math.Sin(_fakePhase * 0.63 + i * 0.31);
                heightsOut[i] = (byte)Math.Clamp((int)Math.Round(Math.Pow(s, 1.5) * MaxHeight), 0, MaxHeight);
            }
        }

        private void DisposeCapture()
        {
            try { if (_pClient != IntPtr.Zero) GetProc<StartStopD>(_pClient, 11)(_pClient); } catch { }
            ComRelease(ref _pCapture);
            ComRelease(ref _pClient);
            ComRelease(ref _pDevice);
            ComRelease(ref _pEnumerator);
            _getNextPacket = null; _getBuffer = null; _releaseBuffer = null;
            if (_mixFormat != IntPtr.Zero) { CoTaskMemFree(_mixFormat); _mixFormat = IntPtr.Zero; }
        }

        public void Dispose() => DisposeCapture();
    }
}
