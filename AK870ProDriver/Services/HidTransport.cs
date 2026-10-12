using System.Runtime.InteropServices;
using System.IO;

namespace Ak870Pro.Services
{
    /// <summary>
    /// 全局日志（协议日志页显示 + 落盘 %LOCALAPPDATA%\Ak870Pro\proto.log）。
    /// </summary>
    public static class AppLog
    {
        public static event Action<string>? LineAdded;
        private static readonly object Gate = new();
        private static readonly List<string> Lines = new();
        private static readonly string LogFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ak870Pro", "proto.log");
        private static bool _sessionHeaderWritten;

        public static void Log(string msg)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            lock (Gate)
            {
                Lines.Add(line);
                if (Lines.Count > 2000) Lines.RemoveRange(0, 500);
                try
                {
                    if (!_sessionHeaderWritten)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(LogFile)!);
                        File.AppendAllText(LogFile,
                            $"===== 会话开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}");
                        _sessionHeaderWritten = true;
                    }
                    File.AppendAllText(LogFile, line + Environment.NewLine);
                }
                catch { }
            }
            LineAdded?.Invoke(msg);
        }

        public static IReadOnlyList<string> Snapshot()
        {
            lock (Gate) { return Lines.ToArray(); }
        }
    }

    /// <summary>通道传输类型。</summary>
    public enum ChannelKind
    {
        None,
        /// <summary>报告通道: Output=33 / Input=33 (WriteFile/ReadFile, 0x451DF0/0x451ED0)。</summary>
        Report33,
        /// <summary>Feature 通道: FeatureReport=65 (HidD_SetFeature / IOCTL_HID_GET_FEATURE, 0x452010/0x4520B0)。</summary>
        Feature65,
    }

    /// <summary>
    /// HID 顶层集合信息（枚举结果）。
    /// caps 字段布局按 hidpi.h: Usage@0, UsagePage@2, Input@4, Output@6, Feature@8 (全 USHORT)。
    /// </summary>
    public sealed class HidDeviceInfo
    {
        public string Path = "";
        public ushort Vid, Pid;
        public ushort UsagePage, Usage;
        public int FeatureReportLength;
        public int InputReportLength;
        public int OutputReportLength;
        public bool WriteOpen;
        public string InterfaceTag = ""; // MI_00 / MI_03
        public ChannelKind Kind;
        public IntPtr Handle;

        // 报告通道异步读写状态 (复刻原驱动 0x451ED0 常驻 IRP 模式)
        internal byte[]? ReadBuf;
        internal System.Threading.NativeOverlapped ReadOv;
        internal System.Threading.NativeOverlapped WriteOv;
        internal IntPtr ReadEvent;
        internal IntPtr WriteEvent;
        internal bool ReadPending;

        public string CapsDesc => $"usage {UsagePage:X2}/{Usage:X2} in={InputReportLength} out={OutputReportLength} feature={FeatureReportLength}";
    }

    /// <summary>
    /// HID 传输层 — 机制与原驱动 (DeviceDriver.exe) 一致:
    ///
    /// 打开: CreateFile(GENERIC_RW, share=RD|WR, OPEN_EXISTING, FILE_FLAG_OVERLAPPED)  [0x451C50]
    /// caps: HidD_GetPreparsedData + HidP_GetCaps (HIDP_STATUS_SUCCESS = 0x00110000)
    /// 分类: FeatureReportByteLength==65 → Feature 通道 | Output==33 且 Input==33 → 报告通道 [0x450640]
    ///
    /// 报告通道帧 (33 字节, [0x4501C0]):
    ///   frame[0..len)  = 负载 (frame[0]=类字节 0x04/0x00/0x80, frame[1]=命令)
    ///   frame[0x20]    = 校验和 = sum(frame[0..len)) & 0xFF
    ///   发送 = WriteFile(33B, OVERLAPPED, 失败补发一次) [0x451DF0 自动零填充到 OutputReportByteLength]
    ///   应答 = ReadFile 常驻 IRP, 校验 resp[0..2] == sent[1..3], 重试 20 次 x 10ms
    /// </summary>
    public static class HidTransport
    {
        public const int ReportFrameSize = 33;  // 报告通道帧长 (Out=In=33): [0]=RID槽(0), [1..0x20)=负载, [0x20]=校验和
        public const int ReportPayloadMax = 31; // 帧内有效负载数 — 帧[0] 是 RID 槽, 帧[0x20] 被校验和占用
        public const int FeaturePacketSize = 65;
        public const int ChunkSize = 64;
        public const ushort TargetVid = 0x05AC;
        public const ushort TargetPid = 0x024F;

        private static readonly Guid GuidDevInterfaceHid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

        // ---------------- Win32 P/Invoke ----------------

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SP_DEVICE_INTERFACE_DETAIL_DATA_W
        {
            public int cbSize;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string DevicePath;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public uint Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        /// <summary>x64 HIDP_CAPS — Usage 在前 (hidpi.h)。</summary>
        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;                    // +0x00
            public ushort UsagePage;                // +0x02
            public ushort InputReportByteLength;    // +0x04
            public ushort OutputReportByteLength;   // +0x06
            public ushort FeatureReportByteLength;  // +0x08
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;               // +0x0A
            public uint NumberLinkCollectionNodes;
            public uint NumberInputButtonCaps;
            public uint NumberInputValueCaps;
            public uint NumberInputDataIndices;
            public uint NumberOutputButtonCaps;
            public uint NumberOutputValueCaps;
            public uint NumberOutputDataIndices;
            public uint NumberFeatureButtonCaps;
            public uint NumberFeatureValueCaps;
            public uint NumberFeatureDataIndices;
        }

        [DllImport("hid.dll")]
        private static extern bool HidD_GetAttributes(IntPtr h, ref HIDD_ATTRIBUTES attrs);
        [DllImport("hid.dll")]
        private static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr preparsed);
        [DllImport("hid.dll")]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsed, ref HIDP_CAPS caps);

        private const int HIDP_STATUS_SUCCESS = 0x00110000;

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetFeature(IntPtr h, byte[] buf, int len);
        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetFeature(IntPtr h, byte[] buf, int len);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(in Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, in Guid ifClass, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifData, IntPtr detailNull, int sizeNull, out int requiredSize, IntPtr devInfoNull);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SP_DEVICE_INTERFACE_DATA ifData, ref SP_DEVICE_INTERFACE_DETAIL_DATA_W detail, int size, IntPtr unused, IntPtr devInfoNull);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr h, byte[] buf, int len, out int written, ref NativeOverlapped ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr h, byte[] buf, int len, out int read, ref NativeOverlapped ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(IntPtr h, ref NativeOverlapped ov, out int bytes, bool wait);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIo(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr h, ref NativeOverlapped ov);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEventW(IntPtr attr, bool manual, bool initial, string? name);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ResetEvent(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr h, int ms);

        private const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint DIGCF_PRESENT = 2, DIGCF_DEVICEINTERFACE = 0x10;
        private const int ERROR_IO_PENDING = 997;
        private const int WAIT_OBJECT_0 = 0;
        private const uint INFINITE = 0xFFFFFFFF;

        public static string ShortPath(string p)
        {
            int i = p.IndexOf('#');
            if (i > 0) p = p[(i + 1)..];
            int j = p.IndexOf('#');
            if (j > 0) p = p[..j];
            return p;
        }

        public static void Delay(uint ms) => Thread.Sleep((int)ms);

        // ---------------- 枚举 ----------------

        /// <summary>
        /// 枚举目标 VID/PID 的所有 HID 顶层集合并打开 (RW, share=3, OVERLAPPED)。
        /// modeTag 可为空 = 不按 MI 过滤 (由 caps 签名决定通道)。
        /// open=false 时仅枚举路径不打开设备 (移除检测用)。
        /// </summary>
        public static List<HidDeviceInfo> Enumerate(string modeTag, bool open = true)
        {
            var result = new List<HidDeviceInfo>();
            IntPtr set = SetupDiGetClassDevs(in GuidDevInterfaceHid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == new IntPtr(-1) || set == IntPtr.Zero)
            {
                AppLog.Log($"[枚举] SetupDiGetClassDevs 失败 err={Marshal.GetLastWin32Error()}");
                return result;
            }

            try
            {
                for (int i = 0; ; i++)
                {
                    var ifData = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, in GuidDevInterfaceHid, i, ref ifData)) break;

                    SetupDiGetDeviceInterfaceDetailW(set, ref ifData, IntPtr.Zero, 0, out int size, IntPtr.Zero);
                    var detail = new SP_DEVICE_INTERFACE_DETAIL_DATA_W { cbSize = 8 }; // x64: int + 首个 wchar 对齐
                    if (!SetupDiGetDeviceInterfaceDetailW(set, ref ifData, ref detail, size, IntPtr.Zero, IntPtr.Zero))
                    {
                        AppLog.Log($"[枚举] GetInterfaceDetail#{i} 失败 err={Marshal.GetLastWin32Error()}");
                        continue;
                    }

                    string path = detail.DevicePath ?? "";
                    if (path.IndexOf($"VID_{TargetVid:X4}", StringComparison.OrdinalIgnoreCase) < 0 ||
                        path.IndexOf($"PID_{TargetPid:X4}", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    var info = new HidDeviceInfo { Path = path };
                    int k = path.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
                    info.InterfaceTag = k > 0 ? path[k..][..6].ToUpperInvariant() : "?";

                    if (!string.IsNullOrEmpty(modeTag) && info.InterfaceTag != modeTag)
                        continue;

                    if (!open)
                    {
                        result.Add(info);
                        continue;
                    }

                    IntPtr h = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                        IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, IntPtr.Zero);
                    if (h == new IntPtr(-1) || h == IntPtr.Zero)
                    {
                        int errW = Marshal.GetLastWin32Error();
                        AppLog.Log($"[打开失败] {info.InterfaceTag} {ShortPath(path)} RW/OVL err={errW}");
                        continue;
                    }
                    info.WriteOpen = true;

                    try
                    {
                        var attrs = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                        if (!HidD_GetAttributes(h, ref attrs))
                        {
                            AppLog.Log($"[枚举] {info.InterfaceTag} GetAttributes 失败 err={Marshal.GetLastWin32Error()}");
                            continue;
                        }
                        info.Vid = attrs.VendorID; info.Pid = attrs.ProductID;

                        if (!HidD_GetPreparsedData(h, out IntPtr pp))
                        {
                            AppLog.Log($"[枚举] {info.InterfaceTag} GetPreparsedData 失败 err={Marshal.GetLastWin32Error()}");
                            continue;
                        }
                        try
                        {
                            var caps = new HIDP_CAPS { Reserved = new ushort[17] };
                            int hr = HidP_GetCaps(pp, ref caps);
                            if (hr != HIDP_STATUS_SUCCESS)
                            {
                                AppLog.Log($"[枚举] {info.InterfaceTag} HidP_GetCaps hr=0x{hr:X8}");
                                continue;
                            }
                            info.UsagePage = caps.UsagePage; info.Usage = caps.Usage;
                            info.FeatureReportLength = caps.FeatureReportByteLength;
                            info.InputReportLength = caps.InputReportByteLength;
                            info.OutputReportLength = caps.OutputReportByteLength;
                        }
                        finally { HidD_FreePreparsedData(pp); }

                        // 按官方驱动规则分类 [0x450640]
                        if (info.FeatureReportLength == FeaturePacketSize)
                            info.Kind = ChannelKind.Feature65;
                        else if (info.OutputReportLength == ReportFrameSize && info.InputReportLength == ReportFrameSize)
                            info.Kind = ChannelKind.Report33;
                        else
                            info.Kind = ChannelKind.None;

                        info.Handle = h;
                        h = IntPtr.Zero;
                        result.Add(info);
                        AppLog.Log($"[枚举] {info.InterfaceTag} {info.CapsDesc} → {info.Kind} ({ShortPath(path)})");
                    }
                    finally
                    {
                        if (h != IntPtr.Zero) CloseHandle(h);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }
            return result;
        }

        /// <summary>
        /// 按官方驱动优先级挑通道: 报告通道 (usage FF60/0x61, 33/33) 优先 — 接收器实际配置通道;
        /// 其次 Feature=65 (USB 直连形态)。
        /// </summary>
        public static HidDeviceInfo? PickChannel(List<HidDeviceInfo> list)
        {
            HidDeviceInfo? best = null;
            foreach (var d in list)
            {
                if (d.Kind == ChannelKind.Report33 && d.UsagePage == 0xFF60 && d.Usage == 0x61)
                    return d;
                if (d.Kind == ChannelKind.Feature65 && best == null) best = d;
            }
            if (best == null)
            {
                foreach (var d in list)
                    if (d.Kind == ChannelKind.Report33) { best = d; break; }
            }
            return best;
        }

        /// <summary>设备仍在系统枚举列表中? (只枚举路径, 不打开设备 — 官方 0x41FD80 移除事件后重扫等价)</summary>
        public static bool IsChannelPresent(string channelPath)
        {
            var list = Enumerate("", open: false);
            return list.Any(d => string.Equals(d.Path, channelPath, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>接口路径 → 模式标签 (config.xml: MI_00=USB, MI_03=2.4G TYPE-A)。</summary>
        public static string ModeTagOf(string interfaceTag) => interfaceTag switch
        {
            "MI_00" => "USB",
            "MI_03" => "2.4G",
            _ => interfaceTag,
        };

        // ---------------- 报告通道传输 (0x451DF0 / 0x451ED0) ----------------

        private static bool EnsureReadArmed(HidDeviceInfo d)
        {
            if (d.ReadBuf == null)
            {
                d.ReadBuf = new byte[d.InputReportLength];
                d.ReadEvent = CreateEventW(IntPtr.Zero, true, false, null);
                if (d.ReadEvent == IntPtr.Zero)
                {
                    AppLog.Log($"[RX] CreateEvent 失败 err={Marshal.GetLastWin32Error()}");
                    return false;
                }
                d.ReadOv = default;
                d.ReadOv.EventHandle = d.ReadEvent;
            }
            if (d.ReadPending) return true;

            Array.Clear(d.ReadBuf);
            ResetEvent(d.ReadEvent);
            if (!ReadFile(d.Handle, d.ReadBuf, d.ReadBuf.Length, out _, ref d.ReadOv))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING)
                {
                    AppLog.Log($"[RX] ReadFile 失败 err={err}");
                    return false;
                }
            }
            d.ReadPending = true;
            return true;
        }

        /// <summary>带超时的异步读。超时返回 0 (IRP 保持挂起, 下次继续等)。</summary>
        public static int ReceiveData(HidDeviceInfo d, byte[] outBuf, int timeoutMs)
        {
            if (!EnsureReadArmed(d)) return -1;
            uint w = WaitForSingleObject(d.ReadEvent, timeoutMs);
            if (w != WAIT_OBJECT_0) return 0; // 超时, IRP 保持

            if (!GetOverlappedResult(d.Handle, ref d.ReadOv, out int bytes, false))
            {
                int err = Marshal.GetLastWin32Error();
                AppLog.Log($"[RX] GetOverlappedResult 失败 err={err}");
                d.ReadPending = false;
                return -1;
            }
            d.ReadPending = false;
            // 官方 0x451ED0: 内部读缓冲[0]==0 (ReportID=0) → 剥掉 RID 字节再拷给调用者
            int n;
            if (bytes > 0 && d.ReadBuf![0] == 0)
            {
                n = Math.Min(bytes - 1, outBuf.Length);
                if (n > 0) Array.Copy(d.ReadBuf, 1, outBuf, 0, n);
            }
            else
            {
                n = Math.Min(bytes, outBuf.Length);
                if (n > 0) Array.Copy(d.ReadBuf, outBuf, n);
            }
            return n;
        }

        /// <summary>WriteFile 发送, 自动补零到 OutputReportByteLength (0x451DF0)。IRP 等待有界(500ms), 超时取消——键盘休眠时 IRP 永不完成, 无限等待会把连接锁永久卡死。</summary>
        public static int SendData(HidDeviceInfo d, byte[] payload, int len)
        {
            byte[] frame;
            if (len >= d.OutputReportLength)
            {
                frame = payload;
                len = d.OutputReportLength;
            }
            else
            {
                frame = new byte[d.OutputReportLength];
                Array.Copy(payload, 0, frame, 0, len);
            }
            if (d.WriteEvent == IntPtr.Zero)
            {
                d.WriteEvent = CreateEventW(IntPtr.Zero, true, false, null);
                if (d.WriteEvent == IntPtr.Zero)
                {
                    AppLog.Log($"[TX] CreateEvent 失败 err={Marshal.GetLastWin32Error()}");
                    return -1;
                }
            }
            ResetEvent(d.WriteEvent);
            d.WriteOv = default;
            d.WriteOv.EventHandle = d.WriteEvent;

            if (!WriteFile(d.Handle, frame, len, out int written, ref d.WriteOv))
            {
                int err = Marshal.GetLastWin32Error();
                if (err != ERROR_IO_PENDING)
                {
                    _lastSendError = err;
                    AppLog.Log($"[TX] WriteFile 失败 err={err}");
                    return -1;
                }
                if (WaitForSingleObject(d.WriteEvent, 500) != WAIT_OBJECT_0)
                {
                    // 设备休眠/断链: IRP 永不完成 → 取消并吸收完成通知, 保证 WriteOv 可安全复用
                    try { CancelIoEx(d.Handle, ref d.WriteOv); } catch { }
                    GetOverlappedResult(d.Handle, ref d.WriteOv, out _, true);
                    _lastSendError = -2;
                    AppLog.Log("[TX] WriteFile 超时 500ms 已取消 (设备可能在休眠)");
                    return -1;
                }
                if (!GetOverlappedResult(d.Handle, ref d.WriteOv, out written, false))
                {
                    _lastSendError = Marshal.GetLastWin32Error();
                    AppLog.Log($"[TX] WriteFile(IRP) 失败 err={_lastSendError}");
                    return -1;
                }
            }
            return written;
        }

        /// <summary>
        /// 报告通道单帧事务 (复刻 0x4501C0/0x450360):
        /// 帧[0]=RID 槽(必须 0), 帧[1..0x20)=payload64[0..0x1F), 帧[0x20]=校验和=sum(帧[1..0x20))&0xFF。
        /// WriteFile(33B) → 失败补发 → Sleep(5) → 读回显 (RID 已剥) resp[0..2]==payload64[0..2], 重试 20x10ms。
        /// 成功后把应答拷回 payload64[0..n]。
        /// </summary>
        public static bool SendAndRecvReport(HidDeviceInfo d, byte[] payload64, int len)
        {
            var frame = new byte[ReportFrameSize];
            int take = Math.Min(Math.Min(len, ReportPayloadMax), payload64.Length);
            Array.Copy(payload64, 0, frame, 1, take); // payload[k] ↔ 帧[k+1], 帧[0]=RID=0

            byte sum = 0;
            for (int i = 1; i < 0x20; i++) sum += frame[i];
            frame[0x20] = sum;

            if (SendData(d, frame, ReportFrameSize) <= 0)
            {
                Delay(10);
                if (SendData(d, frame, ReportFrameSize) <= 0) return false;
            }

            Delay(5);
            var resp = new byte[ReportFrameSize];
            // 官方重试 20x10ms; 实测个别灯效(如 mode 0x13 重峦叠嶂)模式切换耗时 ~600ms 才回显,
            // 期间 IN 报告被占用 → 放宽到 60x10ms, 避免"设置已生效但提示失败"的假报错。
            for (int attempt = 0; attempt < 60; attempt++)
            {
                int n = ReceiveData(d, resp, 5);
                if (n > 0)
                {
                    // 官方回显校验 [0x4501C0 0x4502f5]: esi[1..3] == resp[0..2] → payload64[0..2]
                    byte e0 = take > 0 ? payload64[0] : (byte)0;
                    byte e1 = take > 1 ? payload64[1] : (byte)0;
                    byte e2 = take > 2 ? payload64[2] : (byte)0;
                    if (resp[0] == e0 && resp[1] == e1 && resp[2] == e2)
                    {
                        int cp = Math.Min(n, 0x20);
                        Array.Copy(resp, payload64, cp);
                        return true;
                    }
                    AppLog.Log($"  [回显不符] resp={Hex(resp, 8)} 期望 {e0:X2} {e1:X2} {e2:X2}");
                }
                Delay(10);
            }
            return false;
        }

        // ---------------- Feature 通道传输 (0x452010 / 0x4520B0) ----------------

        public static bool SetFeature(IntPtr h, byte[] packet65)
        {
            bool ok = HidD_SetFeature(h, packet65, FeaturePacketSize);
            if (!ok) _lastSendError = Marshal.GetLastWin32Error();
            return ok;
        }

        public static int LastSendError => _lastSendError;
        private static int _lastSendError;

        public static bool GetFeature(IntPtr h, byte[] packet65) => HidD_GetFeature(h, packet65, FeaturePacketSize);

        public static string Hex(byte[] b, int n)
        {
            var sb = new System.Text.StringBuilder(n * 3);
            for (int i = 0; i < n && i < b.Length; i++) sb.Append(b[i].ToString("X2")).Append(' ');
            return sb.ToString();
        }

        public static void CloseAll(IEnumerable<HidDeviceInfo> list)
        {
            foreach (var d in list)
            {
                if (d.Handle != IntPtr.Zero)
                {
                    if (d.ReadPending) { CancelIo(d.Handle); d.ReadPending = false; }
                    CloseHandle(d.Handle);
                    d.Handle = IntPtr.Zero;
                    d.WriteOpen = false;
                }
                if (d.ReadEvent != IntPtr.Zero) { CloseHandle(d.ReadEvent); d.ReadEvent = IntPtr.Zero; }
                if (d.WriteEvent != IntPtr.Zero) { CloseHandle(d.WriteEvent); d.WriteEvent = IntPtr.Zero; }
            }
        }
    }
}
