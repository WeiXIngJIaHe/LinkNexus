using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus
{
    /// <summary>
    /// 扫描发现的真实 USB / PnP 实体对象
    /// </summary>
    public class DiscoveredUsbDevice
    {
        public string DeviceId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Vid { get; set; } = string.Empty;
        public string Pid { get; set; } = string.Empty;
        public string VidPid => (!string.IsNullOrEmpty(Vid) && !string.IsNullOrEmpty(Pid)) ? $"VID:{Vid}  PID:{Pid}" : "--";
        public string SerialNumber { get; set; } = string.Empty;
        public string ComPort { get; set; } = string.Empty;
        public uint ConfigManagerErrorCode { get; set; } = 0;
        public string Status { get; set; } = "OK";
        public bool HasDriverIssue => ConfigManagerErrorCode != 0 || Status.Equals("Error", StringComparison.OrdinalIgnoreCase);
        public string HardwarePath { get; set; } = string.Empty;
    }

    /// <summary>
    /// USB 设备监控服务
    /// 封装 Win32 RegisterDeviceNotification 注册底层热插拔事件，
    /// 并通过 WMI Win32_PnPEntity 获取操作系统真实的硬件状态与驱动代码
    /// </summary>
    public class UsbMonitorService : IDisposable
    {
        #region Win32 API 与常量定义

        public const int WM_DEVICECHANGE = 0x0219;
        public const int DBT_DEVICEARRIVAL = 0x8000;
        public const int DBT_DEVICEREMOVECOMPLETE = 0x8004;
        public const int DBT_DEVNODES_CHANGED = 0x0007;

        private const int DBT_DEVTYP_DEVICEINTERFACE = 0x00000005;
        private const int DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

        // USB 设备接口类 GUID: {A5DCBF10-6530-11D2-901F-00C04FB951ED}
        private static readonly Guid GuidDevinterfaceUsbDevice = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
        // 串口设备接口类 GUID: {86E0D1E0-8089-11D0-9CE4-08003E301F73}
        private static readonly Guid GuidDevinterfaceComport = new("86E0D1E0-8089-11D0-9CE4-08003E301F73");

        [StructLayout(LayoutKind.Sequential)]
        private struct DEV_BROADCAST_DEVICEINTERFACE
        {
            public int dbcc_size;
            public int dbcc_devicetype;
            public int dbcc_reserved;
            public Guid dbcc_classguid;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string dbcc_name;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr RegisterDeviceNotification(IntPtr hRecipient, IntPtr notificationFilter, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterDeviceNotification(IntPtr handle);

        #endregion

        private IntPtr _hUsbNotify = IntPtr.Zero;
        private IntPtr _hComNotify = IntPtr.Zero;
        private readonly Timer _debounceTimer;
        private bool _disposed = false;

        private static readonly Regex VidRegex = new(@"VID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PidRegex = new(@"PID_([0-9A-Fa-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ComPortRegex = new(@"(COM\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// 当检测到底层设备变动并完成真实枚举时触发
        /// </summary>
        public event Action<List<DiscoveredUsbDevice>>? DevicesRefreshed;

        /// <summary>
        /// 诊断日志通知事件
        /// </summary>
        public event Action<string>? LogMessage;

        public UsbMonitorService()
        {
            // 防抖定时器 (250ms)，复合设备插拔会连续触发多次 WM_DEVICECHANGE，防抖合并为单次刷新
            _debounceTimer = new Timer(_ =>
            {
                Task.Run(() => ScanAndNotify());
            }, null, Timeout.Infinite, Timeout.Infinite);
        }

        /// <summary>
        /// 注册监听主窗口句柄
        /// </summary>
        public void Register(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return;

            try
            {
                // 注册 USB 设备类接口
                var usbFilter = new DEV_BROADCAST_DEVICEINTERFACE
                {
                    dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
                    dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                    dbcc_classguid = GuidDevinterfaceUsbDevice
                };

                IntPtr pUsbFilter = Marshal.AllocHGlobal(usbFilter.dbcc_size);
                Marshal.StructureToPtr(usbFilter, pUsbFilter, false);
                _hUsbNotify = RegisterDeviceNotification(hWnd, pUsbFilter, DEVICE_NOTIFY_WINDOW_HANDLE);
                Marshal.FreeHGlobal(pUsbFilter);

                // 注册 COM 串口类接口
                var comFilter = new DEV_BROADCAST_DEVICEINTERFACE
                {
                    dbcc_size = Marshal.SizeOf<DEV_BROADCAST_DEVICEINTERFACE>(),
                    dbcc_devicetype = DBT_DEVTYP_DEVICEINTERFACE,
                    dbcc_classguid = GuidDevinterfaceComport
                };

                IntPtr pComFilter = Marshal.AllocHGlobal(comFilter.dbcc_size);
                Marshal.StructureToPtr(comFilter, pComFilter, false);
                _hComNotify = RegisterDeviceNotification(hWnd, pComFilter, DEVICE_NOTIFY_WINDOW_HANDLE);
                Marshal.FreeHGlobal(pComFilter);

                LogMessage?.Invoke("[PnP 服务] 已成功注册 Windows Win32 设备广播监听 (USB/COM 接口类)。");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[PnP 服务] 注册设备广播监听异常: {ex.Message}");
            }

            // 初始化立即异步扫描一次
            TriggerDebouncedRefresh();
        }

        /// <summary>
        /// WndProc 窗口消息过滤器接入
        /// </summary>
        public IntPtr HwndHandler(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DEVICECHANGE)
            {
                int eventType = wParam.ToInt32();
                if (eventType == DBT_DEVICEARRIVAL)
                {
                    LogMessage?.Invoke("[PnP 事件] 检测到底层 USB 硬件插入 (DBT_DEVICEARRIVAL)，触发去抖扫描...");
                    TriggerDebouncedRefresh();
                }
                else if (eventType == DBT_DEVICEREMOVECOMPLETE)
                {
                    LogMessage?.Invoke("[PnP 事件] 检测到底层 USB 硬件拔出 (DBT_DEVICEREMOVECOMPLETE)，触发去抖扫描...");
                    TriggerDebouncedRefresh();
                }
                else if (eventType == DBT_DEVNODES_CHANGED)
                {
                    TriggerDebouncedRefresh();
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>
        /// 触发防抖扫描
        /// </summary>
        public void TriggerDebouncedRefresh()
        {
            if (_disposed) return;
            _debounceTimer.Change(250, Timeout.Infinite);
        }

        /// <summary>
        /// 同步/异步全量查询操作系统真实枚举的 USB 与串口实体
        /// </summary>
        public List<DiscoveredUsbDevice> ScanCurrentDevices()
        {
            var result = new List<DiscoveredUsbDevice>();

            try
            {
                // 查询 Win32_PnPEntity 中所有当前连接的 USB/串口/仿真器设备
                using var searcher = new ManagementObjectSearcher(
                    "SELECT DeviceID, Name, Description, Status, ConfigManagerErrorCode, PNPDeviceID " +
                    "FROM Win32_PnPEntity " +
                    "WHERE DeviceID LIKE '%USB%' OR DeviceID LIKE '%FTDIBUS%' OR Service = 'usbser' OR ClassGuid = '{4d36e978-e325-11ce-bfc1-08002be10318}'");

                using var collection = searcher.Get();
                foreach (ManagementObject obj in collection)
                {
                    string pnpId = obj["PNPDeviceID"]?.ToString() ?? obj["DeviceID"]?.ToString() ?? string.Empty;
                    string name = obj["Name"]?.ToString() ?? string.Empty;
                    string desc = obj["Description"]?.ToString() ?? string.Empty;
                    string status = obj["Status"]?.ToString() ?? "OK";
                    uint errorCode = 0;
                    if (obj["ConfigManagerErrorCode"] is uint code)
                    {
                        errorCode = code;
                    }
                    else if (obj["ConfigManagerErrorCode"] != null && uint.TryParse(obj["ConfigManagerErrorCode"].ToString(), out var parsedCode))
                    {
                        errorCode = parsedCode;
                    }

                    // 提取 VID 和 PID
                    string vid = string.Empty;
                    string pid = string.Empty;
                    var vidMatch = VidRegex.Match(pnpId);
                    if (vidMatch.Success) vid = vidMatch.Groups[1].Value.ToUpperInvariant();

                    var pidMatch = PidRegex.Match(pnpId);
                    if (pidMatch.Success) pid = pidMatch.Groups[1].Value.ToUpperInvariant();

                    // 提取 COM 端口号
                    string comPort = string.Empty;
                    var comMatch = ComPortRegex.Match(name);
                    if (comMatch.Success)
                    {
                        comPort = comMatch.Groups[1].Value.ToUpperInvariant();
                    }

                    // 提取序列号 (PnP ID 末尾段)
                    string serial = string.Empty;
                    var segments = pnpId.Split('\\');
                    if (segments.Length > 2)
                    {
                        string candidate = segments[^1];
                        if (!candidate.Contains("&") && candidate.Length >= 4)
                        {
                            serial = candidate;
                        }
                    }

                    result.Add(new DiscoveredUsbDevice
                    {
                        DeviceId = pnpId,
                        Name = name,
                        Description = desc,
                        Vid = vid,
                        Pid = pid,
                        SerialNumber = serial,
                        ComPort = comPort,
                        ConfigManagerErrorCode = errorCode,
                        Status = status,
                        HardwarePath = pnpId
                    });
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[PnP 扫描] WMI 设备实体扫描异常: {ex.Message}");
            }

            return result;
        }

        private void ScanAndNotify()
        {
            var devices = ScanCurrentDevices();
            DevicesRefreshed?.Invoke(devices);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _debounceTimer.Dispose();

            if (_hUsbNotify != IntPtr.Zero)
            {
                UnregisterDeviceNotification(_hUsbNotify);
                _hUsbNotify = IntPtr.Zero;
            }
            if (_hComNotify != IntPtr.Zero)
            {
                UnregisterDeviceNotification(_hComNotify);
                _hComNotify = IntPtr.Zero;
            }
        }
    }
}

