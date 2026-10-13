using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;
using Ak870Pro.Services;

namespace Ak870Pro.Services
{
    /// <summary>
    /// 设备自动连接监视器:
    ///
    ///   WM_DEVICECHANGE(0x219, DBT_DEVICEARRIVAL/DBT_DEVICEREMOVECOMPLETE,
    ///   DBT_DEVTYP_DEVICEINTERFACE=5) 事件入队 → 工作线程全量重扫 →
    ///   枚举+白名单+打开+分类 (Feature65 不握手 / 33/33 报告通道立即握手)。
    ///
    /// 行为: 无需用户选择连接模式, 设备插上即自动枚举、自动握手、自动就绪;
    /// USB(接口路径含 MI_00) 与 2.4G(MI_03) 按路径自动判定。
    /// 实现: 子类化主窗口 HWND 接收 WM_DEVICECHANGE + RegisterDeviceNotification(HID 接口 GUID),
    /// 到达/移除事件去抖后触发全量重扫 (断开失效通道 → 自动重连)。
    /// </summary>
    public static class DeviceMonitor
    {
        private const int WM_DEVICECHANGE = 0x0219;
        private const int DBT_DEVICEARRIVAL = 0x8000;
        private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        private const int DBT_DEVTYP_DEVICEINTERFACE = 5;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0;
        private static readonly Guid GuidDevInterfaceHid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

        private const int GWLP_WNDPROC = -4;

        private static IntPtr _oldWndProc;
        private static WndProcDelegate? _wndProc;      // 防 GC
        private static IntPtr _notifyHandle;
        private static DispatcherQueueTimer? _debounce;
        private static DispatcherQueueTimer? _poll;
        private static bool _installed;
        private static bool _rescanQueued;
        private static int _scanActive;
        private static readonly object Gate = new();

        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProcW(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr RegisterDeviceNotificationW(IntPtr hRecipient, IntPtr notificationFilter, int flags);
        [DllImport("user32.dll")]
        private static extern bool UnregisterDeviceNotification(IntPtr handle);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEV_BROADCAST_DEVICEINTERFACE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
            public Guid dbcc_classguid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
            public string dbcc_name;
        }

        /// <summary>在窗口句柄创建后调用 (Window 构造完成即可)。返回 false 表示已装过。</summary>
        public static bool Start(Window window)
        {
            if (_installed) return false;
            try
            {
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                _wndProc = WndProc;
                _oldWndProc = SetWindowLongPtrW(hwnd, GWLP_WNDPROC,
                    Marshal.GetFunctionPointerForDelegate(_wndProc));
                if (_oldWndProc == IntPtr.Zero)
                {
                    AppLog.Log($"[监视] 子类化窗口失败 err={Marshal.GetLastWin32Error()}");
                    return false;
                }

                var filter = new DEV_BROADCAST_DEVICEINTERFACE
                {
                    dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
                    dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                    dbcc_classguid = GuidDevInterfaceHid,
                    dbcc_name = "",
                };
                IntPtr buf = Marshal.AllocHGlobal(filter.dbcc_size);
                try
                {
                    Marshal.StructureToPtr(filter, buf, false);
                    _notifyHandle = RegisterDeviceNotificationW(hwnd, buf, DEVICE_NOTIFY_WINDOW_HANDLE);
                }
                finally { Marshal.FreeHGlobal(buf); }

                if (_notifyHandle == IntPtr.Zero)
                    AppLog.Log($"[监视] RegisterDeviceNotification 失败 err={Marshal.GetLastWin32Error()}");

                _debounce = window.DispatcherQueue.CreateTimer();
                _debounce.Interval = TimeSpan.FromMilliseconds(600);
                _debounce.IsRepeating = false;
                _debounce.Tick += (_, _) => { _rescanQueued = false; _ = RescanAsync("设备变动"); };

                // 兜底轮询: 仅靠设备事件重扫, 键盘无线休眠唤醒不产生 DEVICECHANGE,
                // 因此未连接时每 3s 重扫一次 (已连接时 Tick 直接跳过, 零开销)。
                _poll = window.DispatcherQueue.CreateTimer();
                _poll.Interval = TimeSpan.FromSeconds(3);
                _poll.IsRepeating = true;
                _poll.Tick += (_, _) =>
                {
                    if (AppState.Device.State == ConnState.Disconnected && !_rescanQueued)
                        _ = RescanAsync("轮询");
                };

                _installed = true;
                AppLog.Log("[监视] WM_DEVICECHANGE 监视已启用 (HID 接口), 启动即自动扫描连接。");
                _ = RescanAsync("启动扫描");
                _poll.Start();
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Log($"[监视] 启动失败: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            TrayService.HandleMessage(msg, wParam, lParam);
            if (msg == WM_DEVICECHANGE)
            {
                int evt = (int)wParam;
                if (evt == DBT_DEVICEARRIVAL || evt == DBT_DEVICEREMOVECOMPLETE)
                {
                    // dbch_devicetype @ +4 == DBT_DEVTYP_DEVICEINTERFACE (只处理此类型)
                    int devType = lParam != IntPtr.Zero ? Marshal.ReadInt32(lParam, 4) : 0;
                    if (devType == DBT_DEVTYP_DEVICEINTERFACE)
                    {
                        AppLog.Log($"[监视] WM_DEVICECHANGE {(evt == DBT_DEVICEARRIVAL ? "到达" : "移除")} (HID 接口)");
                        QueueRescan();
                    }
                }
            }
            return CallWindowProcW(_oldWndProc, hWnd, msg, wParam, lParam);
        }

        private static void QueueRescan()
        {
            lock (Gate)
            {
                if (_rescanQueued || _debounce == null) return;
                _rescanQueued = true;
                _debounce.Start(); // 一次性, 到期后 Rescan
            }
        }

        /// <summary>
        /// 全量重扫: 已连接时先确认通道是否仍存在 (移除则断开),
        /// 然后未连接即自动连接 (枚举 → 选通道 → 握手)。
        /// </summary>
        public static async Task RescanAsync(string reason)
        {
            // 重入守卫: 轮询期间若上一次扫描还没结束(如设备休眠 IRP 超时中), 直接跳过,
            // 避免多个扫描在 ConnGate 上排队 — 用户点"重新扫描"时最多只等一个在途扫描。
            if (Interlocked.Exchange(ref _scanActive, 1) != 0) return;
            bool quiet = reason == "轮询";
            try
            {
                var dev = AppState.Device;
                if (!quiet) AppLog.Log($"[监视] 全量重扫 ({reason}) ...");

                if (dev.State == ConnState.Connected)
                {
                    bool stillThere = await Task.Run(() => HidTransport.IsChannelPresent(dev.ChannelPath!));
                    if (!stillThere)
                    {
                        AppLog.Log("[监视] 当前通道已消失, 断开。");
                        dev.Disconnect();
                        AppState.NotifyStateChanged();
                    }
                    else if (!quiet)
                    {
                        AppLog.Log("[监视] 通道仍在, 无需处理。");
                    }
                    if (stillThere) return;
                }

                if (dev.State == ConnState.Disconnected)
                {
                    bool ok = await dev.ConnectAsync();
                    AppState.NotifyStateChanged();
                    if (ok) AppLog.Log("[监视] 自动连接成功。");
                    else if (!quiet) AppLog.Log("[监视] 自动连接未找到设备 (等待插入)。");
                }
            }
            catch (Exception ex)
            {
                AppLog.Log($"[监视] 重扫异常: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _scanActive, 0);
            }
        }
    }
}
