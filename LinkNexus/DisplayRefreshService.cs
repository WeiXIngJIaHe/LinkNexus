using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Animation;

namespace LinkNexus
{
    /// <summary>
    /// 多显示器刷新率实时感知、PerMonitorV2 DPI 换算与窗口屏幕四边磁吸贴合服务 (DisplayRefreshService)
    /// 支持 60Hz / 90Hz / 120Hz / 144Hz / 165Hz / 240Hz+ 高刷屏自适应及跨屏动态切换
    /// </summary>
    public static class DisplayRefreshService
    {
        public const int WM_MOVE = 0x0003;
        public const int WM_SIZE = 0x0005;
        public const int WM_WINDOWPOSCHANGING = 0x0046;
        public const int WM_WINDOWPOSCHANGED = 0x0047;
        public const int WM_DISPLAYCHANGE = 0x007E;
        public const int WM_DPICHANGED = 0x02E0;
        public const int WM_ENTERSIZEMOVE = 0x0231;
        public const int WM_EXITSIZEMOVE = 0x0232;

        private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const int ENUM_CURRENT_SETTINGS = -1;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const int VK_LBUTTON = 0x01;
        private const int VK_CONTROL = 0x11;
        private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        private static int _currentRefreshRateHz = 60;
        private static IntPtr _lastMonitorHandle = IntPtr.Zero;

        /// <summary>
        /// 当前窗口所在显示器的物理刷新率 (Hz，默认兜底 60，支持 60 ~ 240Hz+)
        /// </summary>
        public static int CurrentRefreshRateHz => _currentRefreshRateHz;

        /// <summary>
        /// 当前目标单帧时间间隔 (毫秒)，用于示波器、3D 视口与画布拖拽合帧调度
        /// </summary>
        public static double TargetFrameIntervalMs => 1000.0 / Math.Clamp(_currentRefreshRateHz, 60, 240);

        /// <summary>
        /// 当窗口跨显示器移动或系统更改显示器刷新率时触发
        /// </summary>
        public static event Action<int>? RefreshRateChanged;

        /// <summary>
        /// 基于窗口句柄探测当前所在显示器的真实刷新率，并在变化时通知全站
        /// </summary>
        public static int UpdateRefreshRateForWindow(IntPtr hwnd, bool forceNotify = false)
        {
            int detectedHz = QueryMonitorRefreshRate(hwnd, out IntPtr hMon);
            bool changed = detectedHz != _currentRefreshRateHz || hMon != _lastMonitorHandle;
            _lastMonitorHandle = hMon;

            if (changed || forceNotify)
            {
                _currentRefreshRateHz = detectedHz;
                RefreshRateChanged?.Invoke(_currentRefreshRateHz);
            }

            return _currentRefreshRateHz;
        }

        /// <summary>
        /// 将检测到的刷新率同步至指定 Storyboard 的 DesiredFrameRate
        /// </summary>
        public static void ApplyDesiredFrameRate(Timeline? timeline)
        {
            if (timeline == null) return;
            int targetFps = Math.Clamp(_currentRefreshRateHz, 60, 240);
            Timeline.SetDesiredFrameRate(timeline, targetFps);
        }

        private static int QueryMonitorRefreshRate(IntPtr hwnd, out IntPtr hMonitor)
        {
            hMonitor = IntPtr.Zero;
            try
            {
                if (hwnd == IntPtr.Zero) return 60;

                hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (hMonitor == IntPtr.Zero) return 60;

                var monInfo = new MONITORINFOEX();
                monInfo.cbSize = Marshal.SizeOf<MONITORINFOEX>();
                if (!GetMonitorInfoW(hMonitor, ref monInfo))
                {
                    return 60;
                }

                var devMode = new DEVMODEW();
                devMode.dmSize = (ushort)Marshal.SizeOf<DEVMODEW>();
                if (EnumDisplaySettingsW(monInfo.szDevice, ENUM_CURRENT_SETTINGS, ref devMode))
                {
                    int freq = (int)devMode.dmDisplayFrequency;
                    if (freq >= 30 && freq <= 500)
                    {
                        return freq;
                    }
                }
            }
            catch
            {
            }

            return 60;
        }

        /// <summary>
        /// 处理 WM_WINDOWPOSCHANGING：实现主窗口拖动时自动磁吸贴合当前显示器上下左右工作区边缘 (rcWork)
        /// 按住 Ctrl 键可临时跳过吸附；拖出阈值平滑脱附
        /// </summary>
        public static void HandleWindowPosChangingSnap(
            IntPtr hwnd,
            IntPtr lParam,
            Window window,
            bool isInteractiveMove,
            double snapThresholdDip = 14.0)
        {
            if (hwnd == IntPtr.Zero || lParam == IntPtr.Zero) return;
            if (window.WindowState != WindowState.Normal) return;

            // 仅在鼠标左键按下交互拖动窗口时生效，按住 Ctrl 键时跳过磁吸
            if (!isInteractiveMove && (GetAsyncKeyState(VK_LBUTTON) & 0x8000) == 0) return;
            if ((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0) return;

            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((pos.flags & SWP_NOMOVE) != 0) return;

            IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero) return;

            var monInfo = new MONITORINFOEX();
            monInfo.cbSize = Marshal.SizeOf<MONITORINFOEX>();
            if (!GetMonitorInfoW(hMonitor, ref monInfo)) return;

            RECT work = monInfo.rcWork;

            // 获取当前窗口尺寸与 DWM 扩展边框补偿量
            if (!GetWindowRect(hwnd, out RECT winRect)) return;

            int width = (pos.flags & SWP_NOSIZE) == 0 && pos.cx > 0
                ? pos.cx
                : (winRect.Right - winRect.Left);
            int height = (pos.flags & SWP_NOSIZE) == 0 && pos.cy > 0
                ? pos.cy
                : (winRect.Bottom - winRect.Top);

            if (width <= 0 || height <= 0) return;

            // 计算 DWM 隐形边框偏移，确保视觉边缘严丝合缝贴合屏幕四边
            int leftMargin = 0, topMargin = 0, rightMargin = 0, bottomMargin = 0;
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT extRect, Marshal.SizeOf<RECT>()) == 0)
            {
                leftMargin = Math.Max(0, extRect.Left - winRect.Left);
                topMargin = Math.Max(0, extRect.Top - winRect.Top);
                rightMargin = Math.Max(0, winRect.Right - extRect.Right);
                bottomMargin = Math.Max(0, winRect.Bottom - extRect.Bottom);
            }

            double dpiScale = GetMonitorDpiScale(hMonitor);
            int snapPx = Math.Max(8, (int)Math.Round(snapThresholdDip * dpiScale));

            bool modified = false;

            // 视觉左/右边缘坐标
            int visualLeft = pos.x + leftMargin;
            int visualRight = pos.x + width - rightMargin;
            int visualTop = pos.y + topMargin;
            int visualBottom = pos.y + height - bottomMargin;

            // 左右磁吸判定
            if (Math.Abs(visualLeft - work.Left) <= snapPx)
            {
                pos.x = work.Left - leftMargin;
                modified = true;
            }
            else if (Math.Abs(visualRight - work.Right) <= snapPx)
            {
                pos.x = work.Right - width + rightMargin;
                modified = true;
            }

            // 上下磁吸判定
            if (Math.Abs(visualTop - work.Top) <= snapPx)
            {
                pos.y = work.Top - topMargin;
                modified = true;
            }
            else if (Math.Abs(visualBottom - work.Bottom) <= snapPx)
            {
                pos.y = work.Bottom - height + bottomMargin;
                modified = true;
            }

            if (modified)
            {
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }

        private static double GetMonitorDpiScale(IntPtr hMonitor)
        {
            try
            {
                if (GetDpiForMonitor(hMonitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                {
                    return dpiX / 96.0;
                }
            }
            catch
            {
            }
            return 1.0;
        }

        #region Win32 Structs & P/Invoke

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODEW
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        /// <summary>
        /// 探测主显示器刷新率并在应用启动阶段解锁 WPF Timeline 默认 60fps 上限
        /// </summary>
        public static void InitializeGlobalTimelineFrameRate()
        {
            try
            {
                var devMode = new DEVMODEW();
                devMode.dmSize = (ushort)Marshal.SizeOf<DEVMODEW>();
                if (EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, ref devMode))
                {
                    int freq = (int)devMode.dmDisplayFrequency;
                    if (freq >= 30 && freq <= 500)
                    {
                        _currentRefreshRateHz = freq;
                    }
                }

                int targetFps = Math.Clamp(_currentRefreshRateHz, 60, 240);
                Timeline.DesiredFrameRateProperty.OverrideMetadata(
                    typeof(Timeline),
                    new FrameworkPropertyMetadata(targetFps));
            }
            catch
            {
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool EnumDisplaySettingsW(string? lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("Shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        #endregion
    }
}
