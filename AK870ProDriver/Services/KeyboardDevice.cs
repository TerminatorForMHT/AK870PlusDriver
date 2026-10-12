using System.Diagnostics;
using System.Text;

namespace Ak870Pro.Services
{
    /// <summary>连接状态。</summary>
    public enum ConnState { Disconnected, Connecting, Connected }

    /// <summary>
    /// AK870 PRO 协议层 — 命令序列与传输机制均来自对 DeviceDriver.exe 的反汇编确认。
    ///
    /// 负载格式 (64 字节): payload[0]=类字节 (0x04=命令, 0x00=设置数据, 0x80=灯效方案数据),
    /// payload[1]=命令 ID, 其后为参数。
    ///
    /// 传输 (按通道类型自动选择):
    ///   报告通道 (33/33, usage FF60/0x61 — 2.4G 接收器实际配置通道):
    ///     帧(33B) = [0]=RID 槽(恒 0) + [1..0x20)=payload[0..0x1F) + 校验和@0x20 = sum(帧[1..0x20)) & 0xFF
    ///     WriteFile(33B) 发送, 读应答剥 RID 后 resp[0..2]==payload[0..2], 重试 20x10ms [0x4501C0/0x451ED0]
    ///     连接握手: payload=[02], 期待回显 02 00 00 [0x450360]
    ///   Feature 通道 (FeatureReport=65 — USB 直连形态):
    ///     SetFeature([RID=0] + payload 64B), GetFeature 应答 resp[4]==1 / 0xFF 忙 [0x44FC90/0x452010]
    ///
    /// 命令 (类字节 0x04): payload[1]=命令 ID
    ///   0x18 开始配置会话 | 0x02 应用 | 0xF0 结束 | 0x13 选层(packet[8]=层号)
    ///   0x17 设置子模式 | 0x23 逐键RGB上传入口 | 0x19 宏/方案上传入口
    ///   0x72 自定义灯效方案上传入口 (packet[2]=方案模式, packet[8..9]=数据长度) — 仅上传方案用,
    ///        切换灯效走"灯效参数包" (见下)。
    ///
    /// 灯效参数包 (切换灯效, 官方 0x42B400 / t_light_data 实锤): 类字节 = 模式号本身!
    ///   payload[0]=协议 mode (1..19=灯效, 0=关闭背光; mode=0 时其余字段不填仅发 AA55)
    ///   payload[1..4)=R,G,B | payload[8]=colorful(官方恒 1) | payload[9]=亮度 | payload[10]=速度
    ///   payload[11]=方向 | payload[14]=0xAA | payload[15]=0x55
    /// 官方灯光序列: 04 18(flag=1) → 04 13(packet[8]=1, flag=1) → 参数包(flag=0) → 04 02(flag=1) → 04 F0(flag=0)
    /// 2.4G 报告通道在序列前先发 payload=[0F] 配置进入包 + Sleep(50) [0x4355C0]。
    ///
    /// 数据流分片: 报告通道 32B/帧 (校验和), Feature 通道 64B/包 (RID 0)。
    /// </summary>
    public sealed class KeyboardDevice
    {
        // ---- 协议常量 ----
        public const byte ClassCmd = 0x04;
        public const byte ClassSettings = 0x00;
        public const byte ClassCustomLight = 0x80;

        public const byte CmdBegin = 0x18;
        public const byte CmdApply = 0x02;
        public const byte CmdEnd = 0xF0;
        public const byte CmdSelectLayer = 0x13;
        public const byte CmdSubMode = 0x17;
        public const byte CmdSchemeUpload = 0x72;  // 仅自定义灯效方案上传入口; 切换灯效走"灯效参数包"
        public const byte CmdCustomLightData = 0x23;  // packet[8]: 9=自定义RGB, 3=用户灯光
        public const byte CmdUploadEnter = 0x19;

        /// <summary>命令间隔 ms（config.xml cmd_delaytime=35）。</summary>
        public int CommandDelayMs { get; set; } = 35;

        private HidDeviceInfo? _channel;
        private readonly List<HidDeviceInfo> _opened = new();
        private static readonly SemaphoreSlim ConnGate = new(1, 1);
        public ConnState State { get; private set; } = ConnState.Disconnected;
        public string? ChannelPath => _channel?.Path;
        public string? InterfaceTag => _channel?.InterfaceTag;
        public string? TransportKind => _channel?.Kind.ToString();

        /// <summary>模式标签 (config.xml: MI_00=USB, MI_03=2.4G) — 由接口路径自动判定, 无需用户选择。</summary>
        public string? ModeTag => _channel == null ? null : HidTransport.ModeTagOf(_channel.InterfaceTag);

        // ---------------- 连接 ----------------

        /// <summary>
        /// 自动连接 (官方 0x450640 分类流程, 无需指定模式):
        /// 全量枚举 VID/PID → 打开全部接口 → 按 caps 分类 (Feature65 不握手 / 33/33 报告通道立即握手)。
        /// </summary>
        public async Task<bool> ConnectAsync()
        {
            await ConnGate.WaitAsync();
            try
            {
                if (State == ConnState.Connected) return true;
                State = ConnState.Connecting;
                AppState.NotifyStateChanged();

                AppLog.Log($"枚举 HID 设备 VID_{HidTransport.TargetVid:X4} PID_{HidTransport.TargetPid:X4} ...");
                var found = await Task.Run(() => HidTransport.Enumerate(""));

                var pick = HidTransport.PickChannel(found);
                if (pick == null)
                {
                    AppLog.Log("没有可用的协议通道 (需要 33/33 报告通道或 Feature=65 集合)。");
                    HidTransport.CloseAll(found);
                    State = ConnState.Disconnected;
                    return false;
                }

                _opened.AddRange(found.Where(d => d != pick));
                _channel = pick;
                AppLog.Log($"选定通道: [{pick.Kind}] {pick.InterfaceTag} ({HidTransport.ModeTagOf(pick.InterfaceTag)}) {pick.CapsDesc}");

                bool ok;
                if (pick.Kind == ChannelKind.Report33)
                {
                    AppLog.Log("报告通道握手: 发送 02 00 00 (期待回显 02 00 00) ...");
                    var payload = new byte[64];
                    payload[0] = 0x02; // 官方 0x450360: buf[1]=2 → 帧[1]=0x02 (payload[0] ↔ 帧[1])
                    ok = await Task.Run(() => SendAndRecv(ch: pick, payload, 1, "握手"));
                    if (!ok)
                    {
                        AppLog.Log("握手失败。");
                        DisconnectInternal();
                        return false;
                    }
                    AppLog.Log("握手成功, 设备已就绪。");
                }
                else
                {
                    // Feature 通道: 官方分类时不握手 [0x450745-0x450867], 用 04 18 作为连通性探测
                    ok = await Task.Run(() => SendPacket(Cmd(CmdBegin), needResp: true, logTag: "连通探测"));
                    if (!ok)
                    {
                        AppLog.Log("Feature 通道连通探测失败。");
                        DisconnectInternal();
                        return false;
                    }
                }

                State = ConnState.Connected;
                return true;
            }
            finally
            {
                AppState.NotifyStateChanged();
                ConnGate.Release();
            }
        }

        public void Disconnect()
        {
            ConnGate.Wait();
            try { DisconnectInternal(); }
            finally { ConnGate.Release(); }
        }

        private void DisconnectInternal()
        {
            HidTransport.CloseAll(_opened);
            if (_channel != null) HidTransport.CloseAll(new[] { _channel });
            _opened.Clear();
            _channel = null;
            State = ConnState.Disconnected;
            AppLog.Log("已断开。");
        }

        private static string ShortPath(string p)
        {
            int i = p.IndexOf('#');
            return i > 0 ? "hid#" + p[(i + 1)..] : p;
        }

        // ---------------- 命令构造 ----------------

        /// <summary>
        /// 构造 64 字节命令负载: payload[0]=0x04, payload[1]=id; off = 65B 包下标 = payload 下标
        /// (官方 0x413200 的 buf[0]=类字节与 payload[0] 一一对应, 无偏移; 0x413FF0/0x44C4E0/0x4230AD 反汇编实锤)。
        /// </summary>
        private static byte[] Cmd(byte id, params (int off, byte val)[] fields)
        {
            var b = new byte[64];
            b[0] = ClassCmd; b[1] = id;
            foreach (var (off, val) in fields) b[off] = val;
            return b;
        }

        // ---------------- 底层事务 ----------------

        /// <summary>
        /// 单命令发送+应答。报告通道: 33B 帧 + 回显校验; Feature 通道: SetFeature + resp[4] 状态机。
        /// </summary>
        private bool SendPacket(byte[] payload, bool needResp, string? logTag = null, bool fastSender = false)
        {
            if (_channel == null) throw new InvalidOperationException("未连接设备");
            var ch = _channel;
            string tag = logTag ?? $"cmd {payload[0]:X2} {payload[1]:X2}";

            if (ch.Kind == ChannelKind.Report33)
            {
                bool ok = SendAndRecv(ch, payload, 32, tag);
                if (CommandDelayMs > 0) Thread.Sleep(CommandDelayMs);
                return ok;
            }

            // ---- Feature 通道 (原 0x44FAA0 单包状态机) ----
            int delay = fastSender ? 5 : CommandDelayMs;
            var pkt = new byte[HidTransport.FeaturePacketSize];
            Array.Copy(payload, 0, pkt, 1, 64); // pkt[0]=0 RID 槽

            for (int attempt = 0; attempt <= 10; attempt++)
            {
                HidTransport.Delay((uint)delay);
                bool ok = HidTransport.SetFeature(ch.Handle, pkt);
                LogPacket($"{tag} TX", pkt, ok);
                if (!ok) continue;

                if (!needResp) return true;

                var resp = new byte[HidTransport.FeaturePacketSize];
                Array.Copy(pkt, resp, pkt.Length);
                HidTransport.Delay((uint)delay);
                if (HidTransport.GetFeature(ch.Handle, resp))
                {
                    LogPacket($"{tag} RX", resp, true);
                    byte code = resp[4];
                    if (code == 1) return true;
                    if (code == 0xFF) continue; // 设备忙 → 重发
                    AppLog.Log($"  应答码 0x{code:X2} ≠ 1, 视为失败");
                    return false;
                }
                AppLog.Log($"  {tag} 读取应答失败 err={HidTransport.LastSendError}");
                return false;
            }
            return false;
        }

        /// <summary>报告通道: 校验和帧发送 + 回显应答 (HidTransport.SendAndRecvReport)。</summary>
        private static bool SendAndRecv(HidDeviceInfo ch, byte[] payload, int len, string tag)
        {
            LogFrame($"{tag} TX", payload, len);
            bool ok = HidTransport.SendAndRecvReport(ch, payload, len);
            if (ok) LogFrame($"{tag} RX", payload, 8);
            else AppLog.Log($"  {tag} 无有效回显");
            return ok;
        }

        private static void LogFrame(string tag, byte[] payload, int n)
        {
            AppLog.Log($"{tag} {HidTransport.Hex(payload, Math.Min(n, 16))}...");
        }

        private static void LogPacket(string tag, byte[] pkt, bool ok)
        {
            var sb = new StringBuilder(160);
            sb.Append(tag).Append(ok ? "" : "!(fail)").Append(' ');
            for (int i = 0; i < 16; i++) sb.Append(pkt[i].ToString("X2")).Append(' ');
            sb.Append("...");
            if (!ok) sb.Append($" err={HidTransport.LastSendError}");
            AppLog.Log(sb.ToString());
        }

        /// <summary>
        /// 数据流分片发送。报告通道: 32B/帧 + 校验和@0x20, 包间 2ms (0x44FC90 快速路径时序);
        /// Feature 通道: 64B/包 RID=0, 慢速 35ms 重试 10 次 / 快速 2ms 100ms 总时限。
        /// </summary>
        private bool SendStream(byte[] stream, bool fast = false)
        {
            if (_channel == null) throw new InvalidOperationException("未连接设备");
            var ch = _channel;
            var sw = Stopwatch.StartNew();

            if (ch.Kind == ChannelKind.Report33)
            {
                const int chunk = HidTransport.ReportPayloadMax; // 31: 帧[0]=RID, 帧[1..0x20)=数据, 帧[0x20]=校验和
                int chunks = (stream.Length + chunk - 1) / chunk;
                var frame = new byte[HidTransport.ReportFrameSize];
                for (int i = 0; i < chunks; i++)
                {
                    int n = Math.Min(chunk, stream.Length - i * chunk);
                    Array.Clear(frame);
                    Array.Copy(stream, i * chunk, frame, 1, n);
                    byte sum = 0;
                    for (int k = 1; k < 0x20; k++) sum += frame[k];
                    frame[0x20] = sum;
                    if (HidTransport.SendData(ch, frame, HidTransport.ReportFrameSize) <= 0)
                    {
                        AppLog.Log($"  流帧#{i} 发送失败 err={HidTransport.LastSendError}");
                        return false;
                    }
                    if (i == 0 || i == chunks - 1 || i % 16 == 0)
                        AppLog.Log($"  流帧#{i}/{chunks - 1} {HidTransport.Hex(frame, 8)}...");
                    Thread.Sleep(fast ? 2 : 2);
                    if (sw.ElapsedMilliseconds > 30000) { AppLog.Log("  流发送超时中止"); return false; }
                }
                return true;
            }

            // ---- Feature 通道 ----
            int interDelay = fast ? 2 : CommandDelayMs;
            int chunksF = stream.Length / HidTransport.ChunkSize;
            var pkt = new byte[HidTransport.FeaturePacketSize];
            for (int i = 0; i < chunksF; i++)
            {
                bool ok = false;
                for (int attempt = 0; attempt <= (fast ? 0 : 10); attempt++)
                {
                    Array.Clear(pkt);
                    Array.Copy(stream, i * HidTransport.ChunkSize, pkt, 1, HidTransport.ChunkSize);
                    HidTransport.Delay((uint)interDelay);
                    ok = HidTransport.SetFeature(ch.Handle, pkt);
                    LogPacket($"流#{i} TX", pkt, ok);
                    if (ok) break;
                }
                if (!ok) return false;
                if (fast && sw.ElapsedMilliseconds > 100)
                {
                    AppLog.Log("  快速发送超时(>100ms)中止");
                    return false;
                }
            }
            return true;
        }

        /// <summary>64 字节数据包 (设置/方案包): 报告通道按 2 帧流发送, Feature 通道单包无应答。</summary>
        private bool SendDataPacket(byte[] payload64)
        {
            if (_channel == null) throw new InvalidOperationException("未连接设备");
            if (_channel.Kind == ChannelKind.Report33)
            {
                AppLog.Log($"数据包 {payload64[0]:X2} → 2x32B 流帧");
                return SendStream(payload64, fast: false);
            }
            return SendPacket(payload64, needResp: false);
        }

        // ---------------- 高层序列 ----------------

        /// <summary>04 18 开始会话。</summary>
        public Task<bool> BeginSessionAsync() => Task.Run(() => SendPacket(Cmd(CmdBegin), needResp: true));

        /// <summary>04 02 应用。</summary>
        public Task<bool> ApplyAsync() => Task.Run(() => SendPacket(Cmd(CmdApply), needResp: true));

        /// <summary>04 F0 结束会话。</summary>
        public Task<bool> EndSessionAsync() => Task.Run(() => SendPacket(Cmd(CmdEnd), needResp: false));

        /// <summary>04 13 选择层 (layer 1..3)。</summary>
        public Task<bool> SelectLayerAsync(int layer) =>
            Task.Run(() => SendPacket(Cmd(CmdSelectLayer, (8, (byte)layer)), needResp: true));

        /// <summary>
        /// 系统设置（0x413FF0 完整序列, buf[i]=payload[i] 一一对应）:
        /// 04 18 → 04 17(payload[2]=1, payload[8]=1) → 00 设置包(流式分片, AA55@0x3E/0x3F, 无应答) → 04 02。
        /// 设置包: payload[1]=1(固定), payload[2..4]=三个复选框, payload[5]=fn_switch,
        ///         payload[6]=sleep_time, payload[7]=0, payload[8]=key_respondtime。
        /// 注意官方此序列**没有 04 F0**。
        /// </summary>
        public async Task<bool> ApplySettingsAsync(bool fnB, bool fnC, int fnSwitch, int sleepTime, int respondTime)
        {
            return await Task.Run(() =>
            {
                if (!SendPacket(Cmd(CmdBegin), true)) return false;
                if (!SendPacket(Cmd(CmdSubMode, (2, (byte)1), (8, (byte)1)), true)) return false;

                var b = new byte[64];
                b[0] = ClassSettings;
                b[1] = 0x01;                 // 固定 1 [0x414088: word 0x0100]
                b[2] = (byte)(fnB ? 1 : 0);  // 复选框1 [0x4140AB-0x4140C4]
                b[3] = (byte)(fnC ? 1 : 0);  // 复选框2 [0x4140C7-0x4140D0]
                b[4] = 0;                    // 复选框3 (官方第三项, 本 UI 无对应)
                b[5] = (byte)fnSwitch;       // "fn_switch" [0x4140F7-0x41412C]
                b[6] = (byte)sleepTime;      // "sleep_time" [0x414139-0x41417E]
                b[8] = (byte)respondTime;    // "key_respondtime" [0x41418B-0x4141D0] (buf[7]=0)
                b[0x3E] = 0xAA; b[0x3F] = 0x55;  // [0x4141D6: word 0x55AA]
                if (!SendDataPacket(b)) return false;
                return SendPacket(Cmd(CmdApply), true);
            });
        }

        /// <summary>
        /// 灯效模式切换。官方按通道分流 (事件分发器 0x434910 event 9, [edi+0x4d8]=连接模式):
        ///  USB (mode=0) 走 0x42B400 SetFeature 序列:
        ///    04 18 → 04 13(payload[8]=1) → 灯效参数包(flag=0) → 04 02 → 04 F0
        ///    参数包: payload[0]=模式号(1..19, 0=关), [1..3]=R,G,B, [8]=colorful, [9]=亮, [10]=速, [11]=方向, AA55@[14..15]
        ///  2.4G (mode=2) 走 0x42B5A0 单帧包 (经 0x4501C0 回显事务), 无 04 18/04 13/04 02/04 F0:
        ///    payload[0..2]=05 10 00, [3]=模式号, [4..6]=R,G,B,
        ///    [11]=colorful [12]=亮 [13]=速 [14]=方向, AA55@[17..18]; mode=0 时仅模式号+AA55。
        ///    注意: 帧通道绝不能发 04 F0 —— 固件收到帧路径的结束命令会复位重启 (实测实锤)。
        /// </summary>
        public async Task<bool> SetLightModeAsync(int mode, int brightness, int speed, byte r = 255, byte g = 0, byte b = 0)
        {
            return await Task.Run(() =>
            {
                if (_channel?.Kind == ChannelKind.Report33)
                {
                    // ---- 2.4G: 0x42B5A0 单帧命令 ----
                    var p = new byte[64];
                    p[0] = 0x05;
                    p[1] = 0x10;
                    p[2] = 0x00;
                    p[3] = (byte)mode;
                    if (mode != 0)
                    {
                        p[4] = r; p[5] = g; p[6] = b;
                        p[11] = 1;                   // colorful
                        p[12] = (byte)brightness;
                        p[13] = (byte)speed;
                        p[14] = 0;                   // direction
                    }
                    p[17] = 0xAA; p[18] = 0x55;
                    return SendPacket(p, true, "灯效帧 05 10");
                }

                // ---- USB Feature: 0x42B400 序列 ----
                if (!SendPacket(Cmd(CmdBegin), true)) return false;
                if (!SendPacket(Cmd(CmdSelectLayer, (8, 1)), true)) return false;

                var pk = new byte[64];
                if (mode != 0)
                {
                    pk[0] = (byte)mode;
                    pk[1] = r; pk[2] = g; pk[3] = b;
                    pk[8] = 1;                  // colorful
                    pk[9] = (byte)brightness;
                    pk[10] = (byte)speed;
                    pk[11] = 0;                 // direction
                }
                pk[14] = 0xAA; pk[15] = 0x55;

                if (!SendPacket(pk, needResp: false, logTag: "灯效参数包")) return false;
                if (!SendPacket(Cmd(CmdApply), true)) return false;
                SendPacket(Cmd(CmdEnd), false);
                return true;
            });
        }

        /// <summary>当前通道是否为 2.4G 报告通道。</summary>
        public bool IsReportChannel => _channel?.Kind == ChannelKind.Report33;

        /// <summary>
        /// 进入音乐律动 (官方 0x434FE0 按连接模式分流, 事件 0xA):
        ///  2.4G: 单帧 payload=[05 01 00 foremode], AA55@[17..18] (0x4501C0 回显事务, 一次性)
        ///  USB:  04 18 → 04 13(层1) → 参数包(payload[0]=foremode, AA55@[14..15], 无应答) → 04 02 → 04 F0 (0x4346B0)
        /// 注意: 此命令仅是"进入音乐显示模式"开关, 之后必须 ~33ms 周期持续调用
        /// SendMusicFrame24Async / SendMusicFrameUsbAsync 推送动效数据, 否则键盘无动画 (官方 0x433A00 分流实锤)。
        /// </summary>
        public async Task<bool> EnterMusicModeAsync(int foremode)
        {
            return await Task.Run(() =>
            {
                if (_channel?.Kind == ChannelKind.Report33)
                {
                    var p = new byte[64];
                    p[0] = 0x05; p[1] = 0x01; p[2] = 0x00; p[3] = (byte)foremode;
                    p[17] = 0xAA; p[18] = 0x55;
                    return SendPacket(p, true, "音乐进入 05 01");
                }

                // ---- USB Feature: 0x4346B0 序列 ----
                if (!SendPacket(Cmd(CmdBegin), true)) return false;
                if (!SendPacket(Cmd(CmdSelectLayer, (8, 1)), true)) return false;
                var pk = new byte[64];
                pk[0] = (byte)foremode;
                pk[14] = 0xAA; pk[15] = 0x55;
                if (!SendPacket(pk, needResp: false, logTag: "音乐参数包")) return false;
                if (!SendPacket(Cmd(CmdApply), true)) return false;
                SendPacket(Cmd(CmdEnd), false);
                return true;
            });
        }

        /// <summary>
        /// 2.4G 音乐律动数据帧 (官方 0x4330C0 栈帧逐字节实锤, 经 0x4501C0 发送 len=0x21):
        ///   buf 直接含 ReportID 槽: buf[0]=0, buf[1]=0x0b, buf[2]=0x1c, buf[3]=0,
        ///   buf[4]=GetCurData(颜色)=foremode, buf[5]=GetCurData(副下拉), buf[6]=状态字节,
        ///   buf[7..27]=21 列频谱高度, buf[32]=校验和 (0x4501C0 覆盖 buf[0..31] 求和)。
        /// 动画帧允许丢帧 → fire-and-forget (不做回显等待, 保持 33ms 节奏)。
        /// </summary>
        public Task<bool> SendMusicFrame24Async(byte[] heights21, int foremode)
        {
            return Task.Run(() =>
            {
                if (_channel == null || _channel.Kind != ChannelKind.Report33) return false;
                var frame = new byte[HidTransport.ReportFrameSize];
                frame[1] = 0x0b; frame[2] = 0x1c;   // buf[1..2]
                frame[4] = (byte)foremode;          // buf[4]=颜色模式下拉数据
                Array.Copy(heights21, 0, frame, 7, Math.Min(AudioSpectrum.Columns, heights21.Length)); // buf[7..27]=21列频谱
                byte sum = 0;
                for (int k = 1; k < 0x20; k++) sum += frame[k];
                frame[0x20] = sum;
                return HidTransport.SendData(_channel, frame, HidTransport.ReportFrameSize) > 0;
            });
        }

        /// <summary>
        /// USB 音乐律动数据帧 (官方 0x432E80):
        ///   04 20 (payload[8]=8, 逐键RGB模式) → 512B 数据流 ([灯槽,R,G,B]×128 四元组) → 04 02, 全快速路径。
        /// </summary>
        public Task<bool> SendMusicFrameUsbAsync(byte[] stream512)
        {
            return Task.Run(() =>
            {
                if (_channel == null || _channel.Kind == ChannelKind.Report33) return false;
                if (!SendPacket(Cmd(0x20, (8, (byte)8)), needResp: false, logTag: "音乐 04 20", fastSender: true)) return false;
                if (!SendStream(stream512, fast: true)) return false;
                return SendPacket(Cmd(CmdApply), needResp: false, logTag: "音乐 04 02", fastSender: true);
            });
        }

        /// <summary>
        /// 清除自定义灯效方案（0x44C4E0）:
        /// 04 18 → 04 13(层1) → 0x80 方案包(payload[8]=方案号, AA55@0x0E, 全零=清除, 无应答) → 04 02 → 04 F0。
        /// </summary>
        public async Task<bool> ClearCustomSchemeAsync(int profile = 0)
        {
            return await Task.Run(() =>
            {
                if (!SendPacket(Cmd(CmdBegin), true)) return false;
                if (!SendPacket(Cmd(CmdSelectLayer, (8, 1)), true)) return false;

                var b = new byte[64];
                b[0] = ClassCustomLight;
                b[7] = 0;
                b[8] = (byte)profile;
                b[0x0D] = 0xAA; b[0x0E] = 0x55;
                if (!SendPacket(b, needResp: false)) return false;

                if (!SendPacket(Cmd(CmdApply), true)) return false;
                SendPacket(Cmd(CmdEnd), false);
                return true;
            });
        }

        /// <summary>
        /// 逐键自定义 RGB（0x44C5F0 完整序列）:
        /// 清除方案 → 04 18 → 04 23(packet[8]=9) → 576B 数据流(9x64B, 尾部 AA55) → 04 02 → 04 F0。
        /// 数据流: 每个 LED 槽 4 字节 @ slot*4 = [slot号, R, G, B]。
        /// </summary>
        public async Task<bool> ApplyPerKeyRgbAsync(IReadOnlyDictionary<int, (byte R, byte G, byte B)> colors, int profile = 0)
        {
            return await Task.Run(async () =>
            {
                if (!await ClearCustomSchemeAsync(profile)) return false;

                var stream = new byte[576];
                foreach (var (slot, rgb) in colors)
                {
                    if (slot < 1 || slot * 4 + 3 >= stream.Length) continue;
                    stream[slot * 4 + 0] = (byte)slot;
                    stream[slot * 4 + 1] = rgb.R;
                    stream[slot * 4 + 2] = rgb.G;
                    stream[slot * 4 + 3] = rgb.B;
                }
                stream[0x23E] = 0xAA; stream[0x23F] = 0x55; // 流结束标记

                bool ok;
                ok = SendPacket(Cmd(CmdBegin), true)
                  && SendPacket(Cmd(CmdCustomLightData, (8, 9)), true)
                  && SendStream(stream, fast: false)
                  && SendPacket(Cmd(CmdApply), true);
                SendPacket(Cmd(CmdEnd), false);
                return ok;
            });
        }

        /// <summary>
        /// 用户灯光（单色开关位图）:
        /// 04 18 → 04 23(packet[8]=3) → 192B 数据流(stream[slot]=0xFF=亮, 尾部 AA55@0xBE) → 04 02 → 04 F0。
        /// </summary>
        public async Task<bool> ApplyUserLightAsync(IReadOnlyCollection<int> onSlots, int profile = 0)
        {
            return await Task.Run(async () =>
            {
                if (!await ClearCustomSchemeAsync(profile)) return false;

                var stream = new byte[192];
                foreach (var s in onSlots)
                    if (s >= 0 && s < stream.Length) stream[s] = 0xFF;
                stream[0xBE] = 0xAA; stream[0xBF] = 0x55;

                bool ok = SendPacket(Cmd(CmdBegin), true)
                       && SendPacket(Cmd(CmdCustomLightData, (8, 3)), true)
                       && SendStream(stream, fast: false)
                       && SendPacket(Cmd(CmdApply), true);
                SendPacket(Cmd(CmdEnd), false);
                return ok;
            });
        }

        /// <summary>日志页: 发送任意原始包 (65 字节 hex, [0]=类字节)。报告通道按回显事务/流帧发送。</summary>
        public Task<bool> SendRawAsync(byte[] packet65, bool needResp) =>
            Task.Run(() =>
            {
                var payload = new byte[64];
                Array.Copy(packet65, payload, Math.Min(64, packet65.Length));
                if (needResp) return SendPacket(payload, needResp: true, logTag: "RAW");
                return SendDataPacket(payload);
            });

        /// <summary>日志页: 发送原始数据流（按通道分片）。</summary>
        public Task<bool> SendRawStreamAsync(byte[] stream) =>
            Task.Run(() => SendStream(stream, fast: false));
    }
}
