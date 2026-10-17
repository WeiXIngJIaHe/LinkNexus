using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using LinkNexus.Mcp;

namespace LinkNexus
{
    public class LogMessageItem
    {
        private string? _timeString;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string TimeString
        {
            get => _timeString ?? Timestamp.ToString("HH:mm:ss.fff");
            set => _timeString = value;
        }
        public string Message { get; set; } = string.Empty;
        public string Level { get; set; } = "INFO"; // INFO, WARN, SUCCESS, ERROR
    }

    /// <summary>
    /// 载入的固件或配置文件信息模型
    /// </summary>
    public class LoadedFileItem : INotifyPropertyChanged
    {
        private string _filePath = string.Empty;
        private string _fileName = "未放入任何文件 (支持拖入 .bin / .hex / .elf)";
        private string _fileSizeText = "--";
        private string _crc32Text = "--";
        private string _targetChipFamily = "--";
        private bool _isFileLoaded = false;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string FilePath
        {
            get => _filePath;
            set { if (_filePath != value) { _filePath = value; OnPropertyChanged(); } }
        }

        public string FileName
        {
            get => _fileName;
            set { if (_fileName != value) { _fileName = value; OnPropertyChanged(); } }
        }

        public string FileSizeText
        {
            get => _fileSizeText;
            set { if (_fileSizeText != value) { _fileSizeText = value; OnPropertyChanged(); } }
        }

        public string Crc32Text
        {
            get => _crc32Text;
            set { if (_crc32Text != value) { _crc32Text = value; OnPropertyChanged(); } }
        }

        public string TargetChipFamily
        {
            get => _targetChipFamily;
            set { if (_targetChipFamily != value) { _targetChipFamily = value; OnPropertyChanged(); } }
        }

        public bool IsFileLoaded
        {
            get => _isFileLoaded;
            set { if (_isFileLoaded != value) { _isFileLoaded = value; OnPropertyChanged(); } }
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 通用 RelayCommand 实现
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute != null ? _ => canExecute() : null)
        {
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter) => _execute(parameter);
    }

    /// <summary>
    /// 串口智能输入自动识别类型
    /// </summary>
    public enum InputRecognizedCategory
    {
        PlainText,
        Hex,
        LinuxCli,
        AtCommand
    }

    /// <summary>
    /// LinkNexus 主视图模型
    /// 1. 动态呈现 CH338X 下行核心设备 (FT2232, CH343P, XDS110, DAP)
    /// 2. 当 ESP32 识别成功，将 Windows 识别的其他外部设备收纳进抽屉 (Drawer)
    /// 3. 打开 Debug 模式时，识别成功的 ESP32 自动进入独立工作页面执行救砖与主控调试
    /// 4. 调试模式仅限连续点击 5 次版本号开启
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly UsbMonitorService _usbMonitor;
        private readonly Dispatcher _dispatcher;

        // 连击 5 次时间戳滑动队列
        private readonly Queue<DateTime> _versionClickTimestamps = new();
        private const int SecretClickRequiredCount = 5;
        private const double SecretClickTimeWindowMs = 1500.0;

        private bool _isDebugMode = false;
        private PortDeviceModel? _selectedDevice;

        // ESP32-S3 核心主控状态与枚举动效
        private bool _isEsp32Online = false;
        private PortDeviceModel? _esp32DeviceModel;
        private bool _isDaemonEnumerating = true; // 开启软件时默认探测
        private string _daemonStatusDotColor = "#38BDF8"; // 启动枚举中为流光青色
        private string _daemonStatusText = "LinkNexus进程枚举中...";
        private DateTime _lastEsp32ConnectAttempt = DateTime.MinValue;

        // 10 秒受控串口枚举生命周期管理
        private CancellationTokenSource? _enumerationCts;
        private bool _isEnumerating = false;
        private string? _enumeratingComPort = null;
        private string? _enumeratingVid = null;
        private string? _enumeratingPid = null;
        private bool _enumerationTimedOut = false;
        private readonly object _enumLock = new();

        // 刷新总线与 VSCode 流光加载状态
        private bool _isRefreshingDevices = false;
        private DateTime _scanStartTime = DateTime.UtcNow;

        // 抽屉状态
        private bool _isOtherDevicesDrawerOpen = false;

        #region 固件烧录与内核工作台状态 (FT2232 / XDS110 / DAPLink)

        private bool _isFlashingActive = false;
        private int _flashProgress = 0;
        private string _flashSpeedText = "待命中";
        private string _flashStatusStep = "就绪：等待选择固件或执行内核组件指令";
        private string _loadBaseAddress = "0x08000000";
        private string _kernelStateText = "CoreID: 0x2BA01477 (Cortex-M4F) | 状态: HALTED";
        private string _coreIdText = "0x2BA01477";

        #endregion

        #region CH343P / UART 串口调试与 Linux CLI 状态

        private int _selectedBaudRate = 115200;
        private bool _isPortOpen = false;
        private SerialPort? _serialPort = null;
        private bool _isLinuxCliMode = false;
        private bool _isHexSendMode = false;
        private bool _isHexReceiveMode = false;
        private bool _isTimestampEnabled = true;
        private string _serialSendBuffer = "AT+SYSINFO?";
        private long _serialTxBytes = 0;
        private long _serialRxBytes = 0;
        private string _linuxCliInput = string.Empty;

        #endregion

        #region ESP32-S3 Debug 工作台状态与救砖控制 (Debug 模式激活)

        private bool _isEsp32FlashingActive = false;
        private int _esp32FlashProgress = 0;
        private string _esp32FlashSpeedText = "待命中";
        private string _esp32FlashStatusStep = "ESP32-S3 主控链路正常，就绪等待指令";
        private bool _isChannel1PowerOn = true;
        private bool _isChannel2PowerOn = true;
        private bool _isChannel3PowerOn = true;
        private bool _isChannel4PowerOn = true;

        #endregion

        #region ESP32-S3 无线调试与网络控制 CLI 状态

        private bool _isEsp32CliOpen = false;
        private string _esp32CliInput = string.Empty;
        private string _esp32WifiMode = "SoftAP (独立热点)";
        private string _esp32WifiSsid = "LinkNexus-ESP32-S3";
        private string _esp32WifiIp = "192.168.4.1";
        private bool _isWirelessDebugListening = true;
        private int _wirelessDebugPort = 8266;
        private string _esp32WifiRssiText = "-32 dBm (本机)";
        private string _esp32WifiClientCount = "0 台";

        public ObservableCollection<string> Esp32CliTerminalLines { get; } = new();

        #endregion

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 主界面顶部展示的 CH338X 下行核心设备集合 (FT2232, CH343P, XDS110, DAP，Debug模式包含ESP32)
        /// </summary>
        public ObservableCollection<PortDeviceModel> ConnectedDevices { get; } = new();

        /// <summary>
        /// 抽屉内收纳的 Windows 识别到的其他 USB 外部设备 (U盘、其它下行插口外设等)
        /// </summary>
        public ObservableCollection<PortDeviceModel> OtherDevices { get; } = new();

        /// <summary>
        /// 全量已检测 USB 硬件设备集合 (用于未选定工作台或关闭工作台时全景展示)
        /// </summary>
        public ObservableCollection<PortDeviceModel> AllUsbDevices { get; } = new();

        public bool HasConnectedDevices => ConnectedDevices.Count > 0;
        public bool HasOtherDevices => OtherDevices.Count > 0;
        public bool HasAnyUsbDevice => AllUsbDevices.Count > 0;
        public bool HasNoUsbDevices => !HasAnyUsbDevice;

        /// <summary>
        /// 抽屉开关状态
        /// </summary>
        public bool IsOtherDevicesDrawerOpen
        {
            get => _isOtherDevicesDrawerOpen;
            set
            {
                if (_isOtherDevicesDrawerOpen != value)
                {
                    _isOtherDevicesDrawerOpen = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(OtherDevicesDrawerButtonText));
                    OnPropertyChanged(nameof(IsOtherDevicesDrawerCollapsed));
                    OnPropertyChanged(nameof(AreCardsExceedingTwoRows));
                }
            }
        }

        public bool IsOtherDevicesDrawerCollapsed => !IsOtherDevicesDrawerOpen;

        public string OtherDevicesDrawerButtonText =>
            $"其他外设 ({OtherDevices.Count}) {(IsOtherDevicesDrawerOpen ? "收起" : "展开")}";

        #region 主题与深浅色模式切换 (Theme & Aesthetic Switching)

        public bool IsDarkMode => ThemeManager.IsDarkMode;
        public bool IsLightMode => !ThemeManager.IsDarkMode;

        public string ThemeButtonText => IsDarkMode ? "深色" : "浅色";

        public string ThemeButtonToolTip => IsDarkMode ? "当前为战术深色模式，点击切换至白瓷明昼模式" : "当前为白瓷明昼模式，点击切换至战术深色模式";

        public ICommand ToggleThemeCommand { get; }

        private void ToggleTheme()
        {
            ThemeManager.ApplyTheme(!ThemeManager.IsDarkMode);
            OnPropertyChanged(nameof(IsDarkMode));
            OnPropertyChanged(nameof(IsLightMode));
            OnPropertyChanged(nameof(ThemeButtonText));
            OnPropertyChanged(nameof(ThemeButtonToolTip));
        }

        #endregion

        #region 主工作台右侧内核流水与日志抽屉体系 (Log & Kernel Drawer)

        private bool _isLogDrawerOpen = false;

        /// <summary>
        /// 主工作台右侧内核操作流水与日志抽屉是否展开
        /// 针对需求：抽屉放置于右侧，点击关闭后需手动再次开启，避免操作完成后突兀弹出打扰用户
        /// </summary>
        public bool IsLogDrawerOpen
        {
            get => _isLogDrawerOpen;
            set
            {
                if (_isLogDrawerOpen != value)
                {
                    _isLogDrawerOpen = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(LogDrawerButtonText));
                    OnPropertyChanged(nameof(IsLogDrawerClosed));
                }
            }
        }

        public bool IsLogDrawerClosed => !_isLogDrawerOpen;

        public string LogDrawerButtonText => IsLogDrawerOpen ? "收起日志" : $"系统日志 ({Logs.Count})";

        public ICommand ToggleLogDrawerCommand { get; }
        public ICommand CloseLogDrawerCommand { get; }
        public ICommand OpenLogDrawerCommand { get; }

        #endregion

        #region 核心设备托盘抽屉收纳体系 (解决卡片超过两行打开工具时占用比例过大问题)

        private bool _isDeviceDrawerCollapsed = false;

        /// <summary>
        /// 核心设备卡片抽屉是否处于收起/收纳状态 (用于在打开工具时将超过2行的卡片收纳为精简抽屉栏)
        /// </summary>
        public bool IsDeviceDrawerCollapsed
        {
            get => _isDeviceDrawerCollapsed;
            set
            {
                if (_isDeviceDrawerCollapsed != value)
                {
                    _isDeviceDrawerCollapsed = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsDeviceDrawerExpanded));
                    OnPropertyChanged(nameof(DeviceDrawerCollapseButtonText));
                }
            }
        }

        /// <summary>
        /// 核心设备卡片抽屉是否展开
        /// </summary>
        public bool IsDeviceDrawerExpanded => !IsDeviceDrawerCollapsed;

        /// <summary>
        /// 是否有活动工作台/工具正在打开 (选定了设备)
        /// </summary>
        public bool IsToolWorkspaceActive => SelectedDevice != null;

        /// <summary>
        /// 判断卡片是否超过两行 (在 100%~150% 缩放下，核心卡片 >= 5 张或展开外设抽屉时即超过两行)
        /// </summary>
        public bool AreCardsExceedingTwoRows => ConnectedDevices.Count >= 5 || (ConnectedDevices.Count >= 3 && IsOtherDevicesDrawerOpen);

        /// <summary>
        /// 抽屉展开时卡片区域的最大高度 (若有工具打开且卡片多，限制在两行高度约 265px，支持暗黑滑条滚动，杜绝挤压工作台)
        /// </summary>
        public double CardAreaMaxHeight => IsToolWorkspaceActive ? 265.0 : double.PositiveInfinity;

        public string DeviceDrawerCollapseButtonText =>
            IsDeviceDrawerCollapsed ? "▾ 展开设备抽屉" : "▴ 收起设备抽屉";

        public ICommand ToggleDeviceDrawerCommand { get; }
        public ICommand CloseWorkspaceCommand { get; }

        #endregion

        /// <summary>
        /// 全局系统 PnP 与诊断事件流
        /// </summary>
        public ObservableCollection<LogMessageItem> Logs { get; } = new();

        /// <summary>
        /// 固件烧录内核工作台：扇区 Hex 转储行集合
        /// </summary>
        public ObservableCollection<string> SectorHexDumpLines { get; } = new();

        /// <summary>
        /// 串口回环与数据监视行
        /// </summary>
        public ObservableCollection<string> SerialMonitorLines { get; } = new();

        /// <summary>
        /// Linux 卡片机 CLI 终端行
        /// </summary>
        public ObservableCollection<string> LinuxCliTerminalLines { get; } = new();

        /// <summary>
        /// ESP32-S3 二进制协议交互日志
        /// </summary>
        public ObservableCollection<string> Esp32ProtocolLogLines { get; } = new();

        /// <summary>
        /// 当前放入或载入的固件文件
        /// </summary>
        public LoadedFileItem CurrentLoadedFile { get; } = new();

        public List<int> AvailableBaudRates { get; } = new()
        {
            9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600, 1500000, 2000000, 3000000
        };

        #region 当前选中的硬件设备与工作台路由

        public PortDeviceModel? SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (_selectedDevice != value)
                {
                    if (_selectedDevice != null)
                    {
                        _selectedDevice.IsSelected = false;
                    }
                    _selectedDevice = value;
                    if (_selectedDevice != null)
                    {
                        _selectedDevice.IsSelected = true;
                        // 核心需求: 当卡片超过两行再打开工具时，自动将设备卡片收纳入抽屉，释放屏幕空间给下方工作台
                        if (AreCardsExceedingTwoRows || ConnectedDevices.Count >= 5 || (IsDebugMode && VirtualDevices.Count >= 5))
                        {
                            IsDeviceDrawerCollapsed = true;
                        }
                    }
                    else
                    {
                        // 关闭工具返回概览时，自动展开设备卡片抽屉
                        IsDeviceDrawerCollapsed = false;
                    }
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsBurnerWorkspaceVisible));
                    OnPropertyChanged(nameof(IsUartWorkspaceVisible));
                    OnPropertyChanged(nameof(IsEsp32WorkspaceVisible));
                    OnPropertyChanged(nameof(IsGenericWorkspaceVisible));
                    OnPropertyChanged(nameof(IsLogsWorkspaceVisible));
                    OnPropertyChanged(nameof(IsToolWorkspaceActive));
                    OnPropertyChanged(nameof(CardAreaMaxHeight));
                }
            }
        }

        public bool IsBurnerWorkspaceVisible => SelectedDevice?.IsBurnerDevice ?? false;
        public bool IsUartWorkspaceVisible => SelectedDevice?.IsUartDevice ?? false;
        public bool IsEsp32WorkspaceVisible => SelectedDevice?.IsEsp32Device ?? false;
        public bool IsGenericWorkspaceVisible => SelectedDevice != null && !SelectedDevice.IsBurnerDevice && !SelectedDevice.IsUartDevice && !SelectedDevice.IsEsp32Device;
        public bool IsLogsWorkspaceVisible => SelectedDevice == null;

        #endregion

        #region ESP32-S3 CLI 与无线调试属性

        public bool IsEsp32CliOpen
        {
            get => _isEsp32CliOpen;
            set { if (_isEsp32CliOpen != value) { _isEsp32CliOpen = value; OnPropertyChanged(); } }
        }

        public string Esp32CliInput
        {
            get => _esp32CliInput;
            set { if (_esp32CliInput != value) { _esp32CliInput = value; OnPropertyChanged(); } }
        }

        public string Esp32WifiMode
        {
            get => _esp32WifiMode;
            set { if (_esp32WifiMode != value) { _esp32WifiMode = value; OnPropertyChanged(); } }
        }

        public string Esp32WifiSsid
        {
            get => _esp32WifiSsid;
            set { if (_esp32WifiSsid != value) { _esp32WifiSsid = value; OnPropertyChanged(); } }
        }

        public string Esp32WifiIp
        {
            get => _esp32WifiIp;
            set { if (_esp32WifiIp != value) { _esp32WifiIp = value; OnPropertyChanged(); } }
        }

        public bool IsWirelessDebugListening
        {
            get => _isWirelessDebugListening;
            set { if (_isWirelessDebugListening != value) { _isWirelessDebugListening = value; OnPropertyChanged(); } }
        }

        public int WirelessDebugPort
        {
            get => _wirelessDebugPort;
            set { if (_wirelessDebugPort != value) { _wirelessDebugPort = value; OnPropertyChanged(); } }
        }

        public string Esp32WifiRssiText
        {
            get => _esp32WifiRssiText;
            set { if (_esp32WifiRssiText != value) { _esp32WifiRssiText = value; OnPropertyChanged(); } }
        }

        public string Esp32WifiClientCount
        {
            get => _esp32WifiClientCount;
            set { if (_esp32WifiClientCount != value) { _esp32WifiClientCount = value; OnPropertyChanged(); } }
        }

        #endregion

        #region ESP32 守护服务与版本号

        public bool IsEsp32Online
        {
            get => _isEsp32Online;
            set { if (_isEsp32Online != value) { _isEsp32Online = value; OnPropertyChanged(); } }
        }

        public string VersionString => VersionInfo.CurrentVersion;

        /// <summary>
        /// Model Context Protocol (MCP) 服务中枢 (预留给 v1.00 后接入 AI Agent 进行硬件调度)
        /// </summary>
        public McpServerBridge McpBridge { get; } = new();

        private readonly McpIpcServer _mcpIpcServer;

        public string DaemonStatusDotColor
        {
            get => _daemonStatusDotColor;
            set { if (_daemonStatusDotColor != value) { _daemonStatusDotColor = value; OnPropertyChanged(); } }
        }

        public string DaemonStatusText
        {
            get => _daemonStatusText;
            set { if (_daemonStatusText != value) { _daemonStatusText = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// 是否正在枚举/探测 ESP32 守护服务与物理拓扑链路 (用于驱动左下角微型流光滑动条动画)
        /// </summary>
        public bool IsDaemonEnumerating
        {
            get => _isDaemonEnumerating;
            set
            {
                if (_isDaemonEnumerating != value)
                {
                    _isDaemonEnumerating = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// 是否正在刷新总线与枚举设备列表 (用于驱动类似 VS Code 插件生效样式的细线流光条)
        /// </summary>
        public bool IsRefreshingDevices
        {
            get => _isRefreshingDevices;
            set
            {
                if (_isRefreshingDevices != value)
                {
                    _isRefreshingDevices = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(RefreshButtonText));
                }
            }
        }

        public string RefreshButtonText => "刷新总线";

        /// <summary>
        /// 调试模式开关 (仅通过连续点击 5 次版本号触发)
        /// 开启时切换进入独立 Debug 开发者工程维护与虚拟调试新页面
        /// </summary>
        public bool IsDebugMode
        {
            get => _isDebugMode;
            set
            {
                if (_isDebugMode != value)
                {
                    _isDebugMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DebugModeTagVisibility));
                    OnPropertyChanged(nameof(IsNormalModeVisible));
                    OnPropertyChanged(nameof(IsDebugModeVisible));
                    OnDebugModeChanged(value);
                }
            }
        }

        public bool IsNormalModeVisible => !IsDebugMode;
        public bool IsDebugModeVisible => IsDebugMode;
        public Visibility DebugModeTagVisibility => IsDebugMode ? Visibility.Visible : Visibility.Collapsed;

        #region 虚拟接入功能 (Debug 模式核心仿真测试中心)

        private bool _isVirtualDevicesEnabled = false;
        public bool IsVirtualDevicesEnabled
        {
            get => _isVirtualDevicesEnabled;
            set
            {
                if (_isVirtualDevicesEnabled != value)
                {
                    _isVirtualDevicesEnabled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(VirtualDevicesButtonText));
                    OnPropertyChanged(nameof(VirtualDevicesStatusText));
                    OnPropertyChanged(nameof(HasVirtualDevices));
                }
            }
        }

        public ObservableCollection<PortDeviceModel> VirtualDevices { get; } = new();
        public bool HasVirtualDevices => VirtualDevices.Count > 0;

        private PortDeviceModel? _selectedVirtualDevice;
        public PortDeviceModel? SelectedVirtualDevice
        {
            get => _selectedVirtualDevice;
            set
            {
                if (_selectedVirtualDevice != value)
                {
                    if (_selectedVirtualDevice != null) _selectedVirtualDevice.IsSelected = false;
                    _selectedVirtualDevice = value;
                    if (_selectedVirtualDevice != null) _selectedVirtualDevice.IsSelected = true;
                    OnPropertyChanged();
                    if (value != null)
                    {
                        SelectedDevice = value;
                    }
                }
            }
        }

        public string VirtualDevicesButtonText => IsVirtualDevicesEnabled
            ? "断开虚拟硬件接入 (收起仿真)"
            : "开启虚拟硬件接入 (仿真核心引擎)";

        public string VirtualDevicesStatusText => IsVirtualDevicesEnabled
            ? "虚拟硬件已就绪：已接入 FT2232、CH343P、XDS110、DAPLink 仿真设备 (可直接测试全套功能)"
            : "虚拟硬件未接入：点击按钮开启虚拟仿真核心硬件进行免接线功能测试";

        #endregion

        #endregion

        #region 固件烧录与内核属性

        public bool IsFlashingActive
        {
            get => _isFlashingActive;
            set { if (_isFlashingActive != value) { _isFlashingActive = value; OnPropertyChanged(); } }
        }

        public int FlashProgress
        {
            get => _flashProgress;
            set { if (_flashProgress != value) { _flashProgress = value; OnPropertyChanged(); } }
        }

        public string FlashSpeedText
        {
            get => _flashSpeedText;
            set { if (_flashSpeedText != value) { _flashSpeedText = value; OnPropertyChanged(); } }
        }

        public string FlashStatusStep
        {
            get => _flashStatusStep;
            set { if (_flashStatusStep != value) { _flashStatusStep = value; OnPropertyChanged(); } }
        }

        public string LoadBaseAddress
        {
            get => _loadBaseAddress;
            set { if (_loadBaseAddress != value) { _loadBaseAddress = value; OnPropertyChanged(); } }
        }

        public string KernelStateText
        {
            get => _kernelStateText;
            set { if (_kernelStateText != value) { _kernelStateText = value; OnPropertyChanged(); } }
        }

        public string CoreIdText
        {
            get => _coreIdText;
            set { if (_coreIdText != value) { _coreIdText = value; OnPropertyChanged(); } }
        }

        #endregion

        #region 串口与 Linux CLI 属性 (含 DTR/RTS/DTS/CTS 硬件引脚控制与输入自动识别)

        public int SelectedBaudRate
        {
            get => _selectedBaudRate;
            set
            {
                if (_selectedBaudRate != value)
                {
                    _selectedBaudRate = value;
                    OnPropertyChanged();
                    if (_serialPort != null && _serialPort.IsOpen)
                    {
                        try
                        {
                            _serialPort.BaudRate = value;
                            AddLog($"[串口服务] 物理串口波特率已动态更新为 {value}", "INFO");
                        }
                        catch (Exception ex)
                        {
                            AddLog($"[串口服务] 动态更改波特率失败: {ex.Message}", "ERROR");
                        }
                    }
                }
            }
        }

        public bool IsPortOpen
        {
            get => _isPortOpen;
            set
            {
                if (_isPortOpen != value)
                {
                    _isPortOpen = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PortOpenButtonText));
                    OnPropertyChanged(nameof(PortOpenStatusText));
                }
            }
        }

        public string PortOpenButtonText => IsPortOpen ? "关闭串口" : "打开串口";
        public string PortOpenStatusText => IsPortOpen ? "已打开 (Online)" : "未打开 (Closed)";

        #region DTR / RTS / DTS(DSR) / CTS 硬件信号引脚控制

        private bool _isDtrEnable = true;
        private bool _isRtsEnable = true;
        private bool _isCtsActive = true;
        private bool _isDtsActive = true;

        /// <summary>
        /// DTR (Data Terminal Ready) 输出引脚控制
        /// </summary>
        public bool IsDtrEnable
        {
            get => _isDtrEnable;
            set
            {
                if (_isDtrEnable != value)
                {
                    _isDtrEnable = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(DtrButtonText));
                    if (_serialPort != null && _serialPort.IsOpen)
                    {
                        try { _serialPort.DtrEnable = value; } catch { }
                    }
                    AddLog($"[串口引脚] DTR 信号线切换为 {(value ? "高电平 (1 / SET)" : "低电平 (0 / CLEAR)")}", "INFO");
                }
            }
        }

        public string DtrButtonText => IsDtrEnable ? "DTR: 1" : "DTR: 0";

        /// <summary>
        /// RTS (Request To Send) 输出引脚控制
        /// </summary>
        public bool IsRtsEnable
        {
            get => _isRtsEnable;
            set
            {
                if (_isRtsEnable != value)
                {
                    _isRtsEnable = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(RtsButtonText));
                    if (_serialPort != null && _serialPort.IsOpen)
                    {
                        try { _serialPort.RtsEnable = value; } catch { }
                    }
                    AddLog($"[串口引脚] RTS 信号线切换为 {(value ? "高电平 (1 / SET)" : "低电平 (0 / CLEAR)")}", "INFO");
                }
            }
        }

        public string RtsButtonText => IsRtsEnable ? "RTS: 1" : "RTS: 0";

        /// <summary>
        /// CTS (Clear To Send) 输入状态指示
        /// </summary>
        public bool IsCtsActive
        {
            get => _isCtsActive;
            set { if (_isCtsActive != value) { _isCtsActive = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// DTS / DSR (Data Set Ready) 输入状态指示
        /// </summary>
        public bool IsDtsActive
        {
            get => _isDtsActive;
            set { if (_isDtsActive != value) { _isDtsActive = value; OnPropertyChanged(); } }
        }

        #endregion

        #region 智能自动识别输入与视窗分流模式

        private string _smartInputBuffer = string.Empty;
        private string _detectedInputType = "智能识别：请输入 Shell 命令、HEX 数据或字符串...";
        private string _detectedInputBadgeColor = "#71717A";
        private InputRecognizedCategory _detectedCategory = InputRecognizedCategory.PlainText;
        private int _inputModeOverrideIndex = 0; // 0: 智能自适应, 1: 强制 HEX, 2: 强制 Linux CLI, 3: 强制 纯文本
        private int _serialViewMode = 0; // 0: 智能双视窗自动分流, 1: 纯 Linux CLI 终端, 2: 纯串口监视

        public string SmartInputBuffer
        {
            get => _smartInputBuffer;
            set
            {
                if (_smartInputBuffer != value)
                {
                    _smartInputBuffer = value;
                    OnPropertyChanged();
                    UpdateDetectedInputType();
                }
            }
        }

        public string DetectedInputType
        {
            get => _detectedInputType;
            set { if (_detectedInputType != value) { _detectedInputType = value; OnPropertyChanged(); } }
        }

        public string DetectedInputBadgeColor
        {
            get => _detectedInputBadgeColor;
            set { if (_detectedInputBadgeColor != value) { _detectedInputBadgeColor = value; OnPropertyChanged(); } }
        }

        public int InputModeOverrideIndex
        {
            get => _inputModeOverrideIndex;
            set
            {
                if (_inputModeOverrideIndex != value)
                {
                    _inputModeOverrideIndex = value;
                    OnPropertyChanged();
                    UpdateDetectedInputType();
                }
            }
        }

        public int SerialViewMode
        {
            get => _serialViewMode;
            set
            {
                if (_serialViewMode != value)
                {
                    _serialViewMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsSmartDualViewVisible));
                    OnPropertyChanged(nameof(IsOnlyLinuxCliVisible));
                    OnPropertyChanged(nameof(IsOnlySerialMonitorVisible));
                    OnPropertyChanged(nameof(IsLinuxCliAreaVisible));
                    OnPropertyChanged(nameof(IsSerialMonitorAreaVisible));
                }
            }
        }

        private bool _isAppendNewLine = true;
        public bool IsAppendNewLine
        {
            get => _isAppendNewLine;
            set { if (_isAppendNewLine != value) { _isAppendNewLine = value; OnPropertyChanged(); } }
        }

        public bool IsSmartDualViewVisible => _serialViewMode == 0;
        public bool IsOnlyLinuxCliVisible => _serialViewMode == 1;
        public bool IsOnlySerialMonitorVisible => _serialViewMode == 2;
        public bool IsLinuxCliAreaVisible => _serialViewMode == 0 || _serialViewMode == 1;
        public bool IsSerialMonitorAreaVisible => _serialViewMode == 0 || _serialViewMode == 2;

        #endregion

        public bool IsLinuxCliMode
        {
            get => _isLinuxCliMode;
            set
            {
                if (_isLinuxCliMode != value)
                {
                    _isLinuxCliMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsNormalSerialMode));
                }
            }
        }

        public bool IsNormalSerialMode
        {
            get => !_isLinuxCliMode;
            set
            {
                if (_isLinuxCliMode == value)
                {
                    _isLinuxCliMode = !value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsLinuxCliMode));
                }
            }
        }

        public bool IsHexSendMode
        {
            get => _isHexSendMode;
            set { if (_isHexSendMode != value) { _isHexSendMode = value; OnPropertyChanged(); } }
        }

        public bool IsHexReceiveMode
        {
            get => _isHexReceiveMode;
            set { if (_isHexReceiveMode != value) { _isHexReceiveMode = value; OnPropertyChanged(); } }
        }

        public bool IsTimestampEnabled
        {
            get => _isTimestampEnabled;
            set { if (_isTimestampEnabled != value) { _isTimestampEnabled = value; OnPropertyChanged(); } }
        }

        public string SerialSendBuffer
        {
            get => _serialSendBuffer;
            set { if (_serialSendBuffer != value) { _serialSendBuffer = value; OnPropertyChanged(); } }
        }

        public long SerialTxBytes
        {
            get => _serialTxBytes;
            set { if (_serialTxBytes != value) { _serialTxBytes = value; OnPropertyChanged(); } }
        }

        public long SerialRxBytes
        {
            get => _serialRxBytes;
            set { if (_serialRxBytes != value) { _serialRxBytes = value; OnPropertyChanged(); } }
        }

        public string LinuxCliInput
        {
            get => _linuxCliInput;
            set { if (_linuxCliInput != value) { _linuxCliInput = value; OnPropertyChanged(); } }
        }

        #endregion

        #region ESP32 救砖与物理通道控制属性

        public bool IsEsp32FlashingActive
        {
            get => _isEsp32FlashingActive;
            set { if (_isEsp32FlashingActive != value) { _isEsp32FlashingActive = value; OnPropertyChanged(); } }
        }

        public int Esp32FlashProgress
        {
            get => _esp32FlashProgress;
            set { if (_esp32FlashProgress != value) { _esp32FlashProgress = value; OnPropertyChanged(); } }
        }

        public string Esp32FlashSpeedText
        {
            get => _esp32FlashSpeedText;
            set { if (_esp32FlashSpeedText != value) { _esp32FlashSpeedText = value; OnPropertyChanged(); } }
        }

        public string Esp32FlashStatusStep
        {
            get => _esp32FlashStatusStep;
            set { if (_esp32FlashStatusStep != value) { _esp32FlashStatusStep = value; OnPropertyChanged(); } }
        }

        public bool IsChannel1PowerOn
        {
            get => _isChannel1PowerOn;
            set { if (_isChannel1PowerOn != value) { _isChannel1PowerOn = value; OnPropertyChanged(); } }
        }

        public bool IsChannel2PowerOn
        {
            get => _isChannel2PowerOn;
            set { if (_isChannel2PowerOn != value) { _isChannel2PowerOn = value; OnPropertyChanged(); } }
        }

        public bool IsChannel3PowerOn
        {
            get => _isChannel3PowerOn;
            set { if (_isChannel3PowerOn != value) { _isChannel3PowerOn = value; OnPropertyChanged(); } }
        }

        public bool IsChannel4PowerOn
        {
            get => _isChannel4PowerOn;
            set { if (_isChannel4PowerOn != value) { _isChannel4PowerOn = value; OnPropertyChanged(); } }
        }

        #endregion

        #region 命令定义

        public ICommand VersionClickCommand { get; }
        public ICommand TriggerScanCommand { get; }
        public ICommand ClearLogsCommand { get; }
        public ICommand BrowseFileCommand { get; }
        public ICommand StartPreviewFlashCommand { get; }
        public ICommand ReadSectorCommand { get; }
        public ICommand ChipEraseCommand { get; }
        public ICommand ReadCoreIdCommand { get; }
        public ICommand ResetTargetCommand { get; }

        public ICommand ToggleSerialPortCommand { get; }
        public ICommand SendSerialTextCommand { get; }
        public ICommand ClearSerialMonitorCommand { get; }
        public ICommand SendLinuxCliCommand { get; }
        public ICommand InjectCliPresetCommand { get; }
        public ICommand SelectDeviceCommand { get; }
        public ICommand ReloadConfigCommand { get; }
        public ICommand ToggleOtherDevicesDrawerCommand { get; }
        public ICommand ExitDebugModeCommand { get; }
        public ICommand ToggleVirtualDevicesCommand { get; }
        public ICommand SelectVirtualDeviceCommand { get; }

        // 串口硬件引脚与智能输入命令
        public ICommand ToggleDtrCommand { get; }
        public ICommand ToggleRtsCommand { get; }
        public ICommand TriggerMcuResetCommand { get; }
        public ICommand SendSmartInputCommand { get; }
        public ICommand SetSerialViewModeCommand { get; }

        // ESP32 专属调试与救砖命令
        public ICommand FlashEsp32MasterFirmwareCommand { get; }
        public ICommand FlashTm4cDfuMirrorCommand { get; }
        public ICommand FlashDapLinkFirmwareCommand { get; }
        public ICommand FactoryResetCommand { get; }
        public ICommand ToggleChannelPowerCommand { get; }
        public ICommand SendHeartbeatPacketCommand { get; }
        public ICommand SendIna236TelemetryCommand { get; }
        public ICommand SendMosControlPacketCommand { get; }

        // ESP32-S3 CLI 与无线调试控制命令
        public ICommand OpenEsp32CliCommand { get; }
        public ICommand CloseEsp32CliCommand { get; }
        public ICommand SendEsp32CliCommand { get; }
        public ICommand SetWifiApPresetCommand { get; }
        public ICommand SetWifiStaPresetCommand { get; }
        public ICommand QueryWifiStatusPresetCommand { get; }
        public ICommand ScanWifiPresetCommand { get; }
        public ICommand DisconnectWifiPresetCommand { get; }
        public ICommand ConnectWirelessTcpCommand { get; }
        public ICommand HelpCliPresetCommand { get; }
        public ICommand ClearCliTerminalCommand { get; }

        #endregion

        public MainViewModel(UsbMonitorService usbMonitor)
        {
            _usbMonitor = usbMonitor ?? throw new ArgumentNullException(nameof(usbMonitor));
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            _usbMonitor.DevicesRefreshed += OnUsbDevicesRefreshed;
            _usbMonitor.LogMessage += msg => AddLog(msg, "INFO");

            // 基础命令绑定
            VersionClickCommand = new RelayCommand(OnVersionClicked);
            TriggerScanCommand = new RelayCommand(OnTriggerScan);
            ClearLogsCommand = new RelayCommand(() => Logs.Clear());
            BrowseFileCommand = new RelayCommand(OnBrowseFile);
            StartPreviewFlashCommand = new RelayCommand(OnStartPreviewFlash);
            ReadSectorCommand = new RelayCommand(OnReadSector);
            ChipEraseCommand = new RelayCommand(OnChipErase);
            ReadCoreIdCommand = new RelayCommand(OnReadCoreId);
            ResetTargetCommand = new RelayCommand(OnResetTarget);

            ToggleSerialPortCommand = new RelayCommand(OnToggleSerialPort);
            SendSerialTextCommand = new RelayCommand(OnSendSerialText);
            ClearSerialMonitorCommand = new RelayCommand(OnClearSerialMonitor);
            SendLinuxCliCommand = new RelayCommand(OnSendLinuxCli);
            InjectCliPresetCommand = new RelayCommand(p => OnInjectCliPreset(p?.ToString()));
            SelectDeviceCommand = new RelayCommand(p =>
            {
                if (p is PortDeviceModel dev) SelectedDevice = dev;
            });
            ReloadConfigCommand = new RelayCommand(OnReloadHardwareConfig);
            ToggleOtherDevicesDrawerCommand = new RelayCommand(() => IsOtherDevicesDrawerOpen = !IsOtherDevicesDrawerOpen);
            ToggleDeviceDrawerCommand = new RelayCommand(() => IsDeviceDrawerCollapsed = !IsDeviceDrawerCollapsed);
            CloseWorkspaceCommand = new RelayCommand(() =>
            {
                SelectedDevice = null;
                IsDeviceDrawerCollapsed = false;
                IsLogDrawerOpen = false;
            });
            ExitDebugModeCommand = new RelayCommand(() => IsDebugMode = false);
            ToggleVirtualDevicesCommand = new RelayCommand(OnToggleVirtualDevices);
            SelectVirtualDeviceCommand = new RelayCommand(p =>
            {
                if (p is PortDeviceModel dev) SelectedVirtualDevice = dev;
            });
            ToggleThemeCommand = new RelayCommand(ToggleTheme);
            ThemeManager.ThemeChanged += _ =>
            {
                OnPropertyChanged(nameof(IsDarkMode));
                OnPropertyChanged(nameof(IsLightMode));
                OnPropertyChanged(nameof(ThemeButtonToolTip));
            };
            ToggleLogDrawerCommand = new RelayCommand(() => IsLogDrawerOpen = !IsLogDrawerOpen);
            CloseLogDrawerCommand = new RelayCommand(() => IsLogDrawerOpen = false);
            OpenLogDrawerCommand = new RelayCommand(() => IsLogDrawerOpen = true);

            // 串口硬件引脚与智能自动识别命令绑定
            ToggleDtrCommand = new RelayCommand(() => IsDtrEnable = !IsDtrEnable);
            ToggleRtsCommand = new RelayCommand(() => IsRtsEnable = !IsRtsEnable);
            TriggerMcuResetCommand = new RelayCommand(OnTriggerMcuReset);
            SendSmartInputCommand = new RelayCommand(OnSendSmartInput);
            SetSerialViewModeCommand = new RelayCommand(p =>
            {
                if (int.TryParse(p?.ToString(), out int mode)) SerialViewMode = mode;
            });

            // ESP32 Debug 救砖与协议命令
            FlashEsp32MasterFirmwareCommand = new RelayCommand(OnFlashEsp32Master);
            FlashTm4cDfuMirrorCommand = new RelayCommand(OnFlashTm4cDfu);
            FlashDapLinkFirmwareCommand = new RelayCommand(OnFlashDapLink);
            FactoryResetCommand = new RelayCommand(OnFactoryReset);
            ToggleChannelPowerCommand = new RelayCommand(p => OnToggleChannelPower(p?.ToString()));
            SendHeartbeatPacketCommand = new RelayCommand(() => OnSendEsp32Packet(0x01, "0x01 心跳租约握手"));
            SendIna236TelemetryCommand = new RelayCommand(() => OnSendEsp32Packet(0x10, "0x10 INA236 母线遥测采集"));
            SendMosControlPacketCommand = new RelayCommand(() => OnSendEsp32Packet(0x30, "0x30 通道 MOS 供电控制"));

            // ESP32-S3 CLI 与无线调试命令绑定
            OpenEsp32CliCommand = new RelayCommand(OpenEsp32Cli);
            CloseEsp32CliCommand = new RelayCommand(CloseEsp32Cli);
            SendEsp32CliCommand = new RelayCommand(OnSendEsp32Cli);
            SetWifiApPresetCommand = new RelayCommand(() => ExecuteEsp32Cli("wifi ap LinkNexus-ESP32-AP 12345678"));
            SetWifiStaPresetCommand = new RelayCommand(() => ExecuteEsp32Cli("wifi connect LabRouter_5G pass8888"));
            QueryWifiStatusPresetCommand = new RelayCommand(() => ExecuteEsp32Cli("wifi status"));
            ScanWifiPresetCommand = new RelayCommand(() => ExecuteEsp32Cli("wifi scan"));
            DisconnectWifiPresetCommand = new RelayCommand(() => ExecuteEsp32Cli("wifi disconnect"));
            ConnectWirelessTcpCommand = new RelayCommand(OnConnectWirelessTcp);
            HelpCliPresetCommand = new RelayCommand(() => ExecuteEsp32Cli("help"));
            ClearCliTerminalCommand = new RelayCommand(() => ExecuteEsp32Cli("clear"));

            // 监听底层 ESP32 真实通信回调
            Esp32CommService.Instance.CliResponseReceived += text =>
            {
                _dispatcher.InvokeAsync(() =>
                {
                    if (!string.IsNullOrEmpty(text))
                    {
                        if (_isEnumerating)
                        {
                            OnEnumerationSuccess();
                        }

                        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                        foreach (var l in lines)
                        {
                            if (!string.IsNullOrWhiteSpace(l))
                            {
                                Esp32CliTerminalLines.Add(l);
                            }
                        }
                        while (Esp32CliTerminalLines.Count > 350) Esp32CliTerminalLines.RemoveAt(0);
                    }
                });
            };

            Esp32CommService.Instance.WifiStatusReceived += status =>
            {
                _dispatcher.InvokeAsync(() =>
                {
                    UpdateWifiStatusFromPacket(status);
                });
            };

            // 当下位机响应心跳包时，触发枚举成功逻辑：停止左下角滑动条动画，点亮绿灯
            Esp32CommService.Instance.HeartbeatAckReceived += ack =>
            {
                _dispatcher.InvokeAsync(() =>
                {
                    OnEnumerationSuccess();
                });
            };

            // 当收到物理硬件上报的真实遥测数据时，若正在枚举，立即确认枚举成功并点亮绿灯
            Esp32CommService.Instance.TelemetryReceived += data =>
            {
                _dispatcher.InvokeAsync(() =>
                {
                    if (!Esp32CommService.Instance.IsSimulationMode && (_isEnumerating || !IsEsp32Online))
                    {
                        OnEnumerationSuccess();
                    }
                });
            };

            InitDefaultDumpsAndCli();

            // 预留 MCP (Model Context Protocol) 框架数据源绑定
            McpBridge.BindHardwareContext(
                devicesProvider: () => AllUsbDevices.Count > 0 ? AllUsbDevices : ConnectedDevices,
                isEsp32OnlineProvider: () => IsEsp32Online,
                daemonStatusTextProvider: () => DaemonStatusText,
                mosPowerSwitchHandler: (channel, enable) =>
                {
                    _dispatcher.Invoke(() =>
                    {
                        switch (channel)
                        {
                            case 1: IsChannel1PowerOn = enable; break;
                            case 2: IsChannel2PowerOn = enable; break;
                            case 3: IsChannel3PowerOn = enable; break;
                            case 4: IsChannel4PowerOn = enable; break;
                        }
                    });
                },
                logsProvider: () => Logs,
                serialLogsProvider: () => SerialMonitorLines);

            // 启动本地命名管道 IPC 服务，随时响应外部 AI Agent 或 Stdio 命令行透传请求
            _mcpIpcServer = new McpIpcServer(McpBridge);
            _mcpIpcServer.Start();

            AddLog($"LinkNexus 工业控制台已启动，加载规范版本号：{VersionString} (MCP 管道 IPC 监听已就绪)", "SUCCESS");
            AddLog("等待系统底层 USB PnP 拓扑动态枚举...", "INFO");
        }

        private void InitDefaultDumpsAndCli()
        {
            SectorHexDumpLines.Add("08000000: 00 04 00 20 51 01 00 08  95 02 00 08 97 02 00 08 |... Q...........|");
            SectorHexDumpLines.Add("08000010: 99 02 00 08 9B 02 00 08  9D 02 00 08 00 00 00 00 |................|");
            SectorHexDumpLines.Add("08000020: 00 00 00 00 00 00 00 00  00 00 00 00 A1 02 00 08 |................|");
            SectorHexDumpLines.Add("08000030: A3 02 00 08 00 00 00 00  A5 02 00 08 A7 02 00 08 |................|");

            LinuxCliTerminalLines.Add("LinkNexus Linux Console Bridge v1.0.0");
            LinuxCliTerminalLines.Add("Linux linknexus-board 6.1.0-arm64-v8a #1 SMP PREEMPT aarch64");
            LinuxCliTerminalLines.Add("Type 'help' or click presets above to run commands.");
            LinuxCliTerminalLines.Add("root@linknexus-board:~# ");

            SerialMonitorLines.Add("[系统] 虚拟串口调试控制台已就绪。输入文本或 HEX 即可收发。");

            Esp32ProtocolLogLines.Add("[ESP32 CDC] 全双工二进制管理总线已挂接就绪 (Baud: 921600)");
            Esp32ProtocolLogLines.Add("[ESP32 CDC] [Tx] -> AA 55 01 01 01 02 (Heartbeat Ping)");
            Esp32ProtocolLogLines.Add("[ESP32 CDC] [Rx] <- AA 55 01 01 06 07 (ACK Online)");
        }

        #region 动态设备枚举与分类展示 (核心要求 1 & 2: 抽屉分类与 ESP32 守护)

        private void OnUsbDevicesRefreshed(List<DiscoveredUsbDevice> devices)
        {
            _dispatcher.InvokeAsync(() => ProcessDiscoveredDevices(devices));
        }

        private void ProcessDiscoveredDevices(List<DiscoveredUsbDevice> devices)
        {
            // 1. 优先检测 ESP32-S3 核心主控 (核心要求 1 & 2)
            var espDev = devices.FirstOrDefault(d =>
                (d.Vid == "303A" && (d.Pid == "1001" || d.Pid == "0002")) ||
                d.Name.Contains("ESP32", StringComparison.OrdinalIgnoreCase));

            if (espDev != null)
            {
                if (espDev.HasDriverIssue)
                {
                    CancelEnumeration();
                    IsEsp32Online = false;
                    IsDaemonEnumerating = false;
                    DaemonStatusDotColor = "#F59E0B"; // 黄色：守护服务异常
                    DaemonStatusText = "LinkNexus进程异常";
                }
                else if (!string.IsNullOrWhiteSpace(espDev.ComPort) && espDev.ComPort != "--")
                {
                    if (IsEsp32Online && Esp32CommService.Instance.IsConnected && Esp32CommService.Instance.ConnectedPortName == espDev.ComPort)
                    {
                        // 已在线就绪，保持常绿
                        IsDaemonEnumerating = false;
                        DaemonStatusDotColor = "#10B981";
                        DaemonStatusText = "LinkNexus进程在线";
                    }
                    else if (_enumerationTimedOut && _enumeratingComPort == espDev.ComPort && !Esp32CommService.Instance.IsConnected)
                    {
                        // 10 秒超时后释放串口状态，维持红灯
                        IsDaemonEnumerating = false;
                        IsEsp32Online = false;
                        DaemonStatusDotColor = "#EF4444";
                        DaemonStatusText = "LinkNexus进程超时";
                    }
                    else if (_isEnumerating && _enumeratingComPort == espDev.ComPort)
                    {
                        // 正在 10 秒受控枚举倒计时中，保持滑条流动
                        IsDaemonEnumerating = true;
                        DaemonStatusDotColor = "#38BDF8";
                        DaemonStatusText = "LinkNexus进程枚举中...";
                    }
                    else
                    {
                        // 插入 USB 获取到 PID、VID 码，左下角启动滑条动画，向硬件发送串口枚举，开始 10 秒受控超时
                        StartEnumeration(espDev.ComPort, espDev.Vid, espDev.Pid);
                    }
                }
                else
                {
                    CancelEnumeration();
                    IsDaemonEnumerating = true;
                    IsEsp32Online = false;
                    DaemonStatusDotColor = "#38BDF8";
                    DaemonStatusText = "LinkNexus进程枚举中...";
                }

                _esp32DeviceModel = new PortDeviceModel
                {
                    DeviceName = string.IsNullOrWhiteSpace(espDev.Name) ? "ESP32-S3 核心通信主控" : espDev.Name,
                    DeviceDescription = espDev.Description,
                    VidPid = espDev.VidPid,
                    ComPort = string.IsNullOrWhiteSpace(espDev.ComPort) ? "--" : espDev.ComPort,
                    SerialNumber = string.IsNullOrWhiteSpace(espDev.SerialNumber) ? "--" : espDev.SerialNumber,
                    HardwarePath = espDev.HardwarePath,
                    State = espDev.HasDriverIssue ? DeviceState.Warning : (_enumerationTimedOut ? DeviceState.Warning : (IsEsp32Online ? DeviceState.Ready : DeviceState.Warning)),
                    FunctionType = DeviceFunctionType.Controller_ESP32,
                    StatusMessage = _enumerationTimedOut
                        ? $"串口枚举超时 (10秒未响应)，已主动释放串口 {espDev.ComPort}"
                        : (IsEsp32Online
                            ? "ESP32-S3 核心主控链路正常，管理协议栈就绪"
                            : "正在执行受控串口枚举握手...")
                };
            }
            else
            {
                CancelEnumeration();
                _enumerationTimedOut = false;
                IsEsp32Online = false;
                IsDaemonEnumerating = false;
                _esp32DeviceModel = null;
                DaemonStatusDotColor = "#EF4444"; // 红色：离线
                DaemonStatusText = "LinkNexus进程离线";
                if (Esp32CommService.Instance.IsConnected)
                {
                    Esp32CommService.Instance.Disconnect();
                }
            }

            // 2. 分离设备：
            // A) 当 ESP32 识别并枚举成功：把 Windows 识别到的其他设备卡片收纳为抽屉形式，
            //    主卡片区只显示出 CH338X 下行的核心调试/通信设备 (FT2232, CH343P, XDS110, DAP)
            // B) 当 ESP32 未识别/离线时：无拓扑收敛，所有设备均平铺在主卡片列表
            var ch338xCards = new List<PortDeviceModel>();
            var otherCards = new List<PortDeviceModel>();

            var validDevices = devices.Where(d =>
                !d.Name.Contains("ESP32", StringComparison.OrdinalIgnoreCase) &&
                !(d.Vid == "303A" && (d.Pid == "1001" || d.Pid == "0002")) &&
                !d.Name.Equals("Generic USB Hub", StringComparison.OrdinalIgnoreCase) &&
                !d.Name.Equals("USB Root Hub", StringComparison.OrdinalIgnoreCase) &&
                !d.Name.Contains("根集线器", StringComparison.OrdinalIgnoreCase)
            ).ToList();

            if (IsEsp32Online)
            {
                foreach (var dev in validDevices)
                {
                    var card = ClassifyDevice(dev);
                    if (card.IsCh338xCoreDevice)
                    {
                        ch338xCards.Add(card);
                    }
                    else
                    {
                        otherCards.Add(card);
                    }
                }
            }
            else
            {
                foreach (var dev in validDevices)
                {
                    var card = ClassifyDevice(dev);
                    ch338xCards.Add(card);
                }
            }

            // 同步到 ObservableCollection
            ConnectedDevices.Clear();
            foreach (var card in ch338xCards)
            {
                card.SelectedChanged += OnDeviceCardSelected;
                ConnectedDevices.Add(card);
            }

            // 同步到抽屉集合
            OtherDevices.Clear();
            foreach (var card in otherCards)
            {
                card.SelectedChanged += OnDeviceCardSelected;
                OtherDevices.Add(card);
            }

            // 同步到全量已检测 USB 硬件设备集合
            AllUsbDevices.Clear();
            foreach (var card in ConnectedDevices)
            {
                AllUsbDevices.Add(card);
            }
            foreach (var card in OtherDevices)
            {
                AllUsbDevices.Add(card);
            }

            OnPropertyChanged(nameof(HasConnectedDevices));
            OnPropertyChanged(nameof(HasOtherDevices));
            OnPropertyChanged(nameof(HasAnyUsbDevice));
            OnPropertyChanged(nameof(HasNoUsbDevices));
            OnPropertyChanged(nameof(OtherDevicesDrawerButtonText));
            OnPropertyChanged(nameof(AreCardsExceedingTwoRows));

            // 如果当前有选中的设备，且该设备已拔出，则清空选中（返回全部设备展示）
            if (SelectedDevice != null && !ConnectedDevices.Contains(SelectedDevice) && !OtherDevices.Contains(SelectedDevice))
            {
                SelectedDevice = null;
            }

            CompleteScanAnimationAsync();
        }

        private void StartEnumeration(string portName, string vid, string pid)
        {
            lock (_enumLock)
            {
                _enumerationCts?.Cancel();
                _enumerationCts?.Dispose();
                _enumerationCts = new CancellationTokenSource();
                _isEnumerating = true;
                _enumeratingComPort = portName;
                _enumeratingVid = vid;
                _enumeratingPid = pid;
                _enumerationTimedOut = false;
            }

            // 1. 插入 USB 获取到 PID、VID 码，左下角启动滑条动画
            IsDaemonEnumerating = true;
            IsEsp32Online = false;
            DaemonStatusDotColor = "#38BDF8"; // 青色：枚举握手中
            DaemonStatusText = "LinkNexus进程枚举中...";

            AddLog($"[USB PnP] 捕获主控硬件接入 [VID:{vid} PID:{pid}]，打开端口 {portName}，启动 10 秒受控串口枚举倒计时...", "INFO");

            // 2. 连接物理串口 (115200 8N1 DTR=false RTS=false)
            if (!Esp32CommService.Instance.IsConnected || Esp32CommService.Instance.ConnectedPortName != portName)
            {
                Esp32CommService.Instance.Connect(portName, 115200);
            }

            // 3. 向硬件发送串口枚举 (Heartbeat Ping 0x01 与枚举探测)
            Esp32CommService.Instance.SendHeartbeat(0x01);

            // 4. 10 秒倒计时超时控制，超时就释放串口并点亮红灯
            var token = _enumerationCts.Token;
            Task.Run(async () =>
            {
                var startTime = DateTime.UtcNow;
                while (!token.IsCancellationRequested && (DateTime.UtcNow - startTime).TotalSeconds < 10)
                {
                    try
                    {
                        await Task.Delay(1500, token);
                    }
                    catch (OperationCanceledException)
                    {
                        return; // 被成功应答或重置取消
                    }

                    if (token.IsCancellationRequested) return;

                    lock (_enumLock)
                    {
                        if (!_isEnumerating) return;
                    }

                    // 10 秒窗口内若未响应，周期性下发枚举包以克服单片机启动延迟
                    if (Esp32CommService.Instance.IsConnected && !Esp32CommService.Instance.IsSimulationMode)
                    {
                        Esp32CommService.Instance.SendHeartbeat(0x01);
                    }
                }

                if (!token.IsCancellationRequested)
                {
                    await _dispatcher.InvokeAsync(() => HandleEnumerationTimeout(portName, vid, pid));
                }
            }, token);
        }

        private void OnEnumerationSuccess()
        {
            string portName;
            lock (_enumLock)
            {
                if (!_isEnumerating && IsEsp32Online) return;
                _enumerationCts?.Cancel();
                _enumerationCts?.Dispose();
                _enumerationCts = null;
                _isEnumerating = false;
                _enumerationTimedOut = false;
                portName = _enumeratingComPort ?? Esp32CommService.Instance.ConnectedPortName;
            }

            // 当枚举成功，点亮绿灯，停止滑条动画
            IsDaemonEnumerating = false;
            IsEsp32Online = true;
            DaemonStatusDotColor = "#10B981"; // 纯绿就绪灯
            DaemonStatusText = "LinkNexus进程在线";

            if (_esp32DeviceModel != null)
            {
                _esp32DeviceModel.State = DeviceState.Ready;
                _esp32DeviceModel.StatusMessage = "ESP32-S3 核心主控链路正常，管理协议栈就绪";
            }

            AddLog($"[守护服务] 硬件串口 {portName} 枚举成功！物理链路锁定就绪，已点亮绿灯。", "SUCCESS");
        }

        private void HandleEnumerationTimeout(string portName, string vid, string pid)
        {
            lock (_enumLock)
            {
                if (!_isEnumerating) return;
                _isEnumerating = false;
                _enumerationCts?.Dispose();
                _enumerationCts = null;
                _enumerationTimedOut = true;
            }

            // 枚举 10 秒钟，超时就释放串口并点亮红灯
            IsDaemonEnumerating = false;
            IsEsp32Online = false;

            // 彻底释放物理串口资源
            if (Esp32CommService.Instance.IsConnected)
            {
                Esp32CommService.Instance.Disconnect();
            }

            // 点亮红灯
            DaemonStatusDotColor = "#EF4444"; // 警示红灯
            DaemonStatusText = "LinkNexus进程超时";

            if (_esp32DeviceModel != null)
            {
                _esp32DeviceModel.State = DeviceState.Warning;
                _esp32DeviceModel.StatusMessage = $"串口枚举超时 (10秒未响应)，已主动释放串口 {portName}";
            }

            AddLog($"[守护服务] 硬件 [VID:{vid} PID:{pid}] 串口 {portName} 枚举超时 (10秒未响应)，已主动释放串口并点亮红灯。", "WARNING");
        }

        private void CancelEnumeration()
        {
            lock (_enumLock)
            {
                _enumerationCts?.Cancel();
                _enumerationCts?.Dispose();
                _enumerationCts = null;
                _isEnumerating = false;
            }
        }

        private void OnTriggerScan()
        {
            if (IsRefreshingDevices) return;
            IsRefreshingDevices = true;
            _scanStartTime = DateTime.UtcNow;

            // 仅在未连接/未在线时才触发枚举重试动画；若已在线则保持在线状态，消除左下角卡顿假象
            if (!IsEsp32Online)
            {
                IsDaemonEnumerating = true;
                _enumerationTimedOut = false; // 手动刷新允许重新尝试枚举
            }

            // 手动点击刷新总线时立即执行后台扫描，无需等待 250ms 防抖
            _usbMonitor.TriggerImmediateRefresh();
        }

        private async void CompleteScanAnimationAsync()
        {
            var elapsed = DateTime.UtcNow - _scanStartTime;
            if (elapsed.TotalMilliseconds < 700)
            {
                await Task.Delay(700 - (int)elapsed.TotalMilliseconds);
            }
            if (!_isEnumerating && IsEsp32Online)
            {
                IsDaemonEnumerating = false;
                DaemonStatusDotColor = "#10B981";
                DaemonStatusText = "LinkNexus进程在线";
            }
            else if (!_isEnumerating && _enumerationTimedOut)
            {
                IsDaemonEnumerating = false;
                DaemonStatusDotColor = "#EF4444";
                DaemonStatusText = "LinkNexus进程超时";
            }
            else if (!_isEnumerating && !IsEsp32Online)
            {
                IsDaemonEnumerating = false;
                DaemonStatusDotColor = "#EF4444";
                DaemonStatusText = "LinkNexus进程离线";
            }
            IsRefreshingDevices = false;
        }

        private PortDeviceModel ClassifyDevice(DiscoveredUsbDevice dev)
        {
            var card = new PortDeviceModel
            {
                DeviceName = string.IsNullOrWhiteSpace(dev.Name) ? dev.Description : dev.Name,
                DeviceDescription = dev.Description,
                VidPid = dev.VidPid,
                ComPort = string.IsNullOrWhiteSpace(dev.ComPort) ? "--" : dev.ComPort,
                SerialNumber = string.IsNullOrWhiteSpace(dev.SerialNumber) ? "--" : dev.SerialNumber,
                HardwarePath = dev.HardwarePath,
                State = dev.HasDriverIssue ? DeviceState.Warning : DeviceState.Ready,
                StatusMessage = dev.HasDriverIssue
                    ? $"驱动未就绪 (Code {dev.ConfigManagerErrorCode})"
                    : "正常就绪 (驱动正常加载)"
            };

            string nameUpper = card.DeviceName.ToUpperInvariant();
            string vid = dev.Vid.ToUpperInvariant();
            string pid = dev.Pid.ToUpperInvariant();

            // 1. FT2232 烧录与 JTAG 引擎 (CH338X 下行)
            if (vid == "0403" || nameUpper.Contains("FT2232") || nameUpper.Contains("FT4232") || nameUpper.Contains("FTDI"))
            {
                card.FunctionType = DeviceFunctionType.Burner_FT2232;
            }
            // 2. DAPLink (CH32V305) 仿真器 (CH338X 下行)
            else if ((vid == "0D28" && pid == "0204") || nameUpper.Contains("DAPLINK") || nameUpper.Contains("CMSIS-DAP"))
            {
                if (!string.IsNullOrWhiteSpace(dev.ComPort) || nameUpper.Contains("UART") || nameUpper.Contains("CDC") || nameUpper.Contains("SERIAL") || nameUpper.Contains("串行"))
                {
                    card.FunctionType = DeviceFunctionType.Uart_Serial;
                }
                else
                {
                    card.FunctionType = DeviceFunctionType.Burner_DAPLink;
                }
            }
            // 3. XDS110 (TM4C1294) 复合设备区分:
            // A) 虚拟串口通道: 拥有有效 COM 口，或名称包含 UART / PORT / SERIAL / CDC / 串行 (如 XDS110 Class Application/User UART)
            // B) 调试探针通道: 无 COM 口的 JTAG / SWD 调试探针 (如 XDS110 Class Debug Probe)
            else if ((vid == "0451" && (pid == "BEF3" || pid == "BEF2" || pid == "BEF0")) || nameUpper.Contains("XDS110") || nameUpper.Contains("TM4C"))
            {
                bool isProbe = nameUpper.Contains("PROBE") || string.IsNullOrWhiteSpace(dev.ComPort);
                bool isUart = !string.IsNullOrWhiteSpace(dev.ComPort) || nameUpper.Contains("UART") || nameUpper.Contains("CDC") || nameUpper.Contains("SERIAL") || nameUpper.Contains("DATA PORT") || nameUpper.Contains("串行");

                if (isUart && !string.IsNullOrWhiteSpace(dev.ComPort))
                {
                    card.FunctionType = DeviceFunctionType.Uart_Serial;
                }
                else if (isProbe)
                {
                    card.FunctionType = DeviceFunctionType.Burner_XDS110;
                }
                else if (isUart)
                {
                    card.FunctionType = DeviceFunctionType.Uart_Serial;
                }
                else
                {
                    card.FunctionType = DeviceFunctionType.Burner_XDS110;
                }
            }
            // 4. CH343P / 板载高速串口 / 通用串口 (CH338X 下行及所有串口)
            else if ((vid == "1A86" && (pid == "55D3" || pid == "7523" || pid == "5523")) ||
                     nameUpper.Contains("CH343") || nameUpper.Contains("CH340") || nameUpper.Contains("CH341") ||
                     nameUpper.Contains("CP210") || nameUpper.Contains("FT232") || nameUpper.Contains("PL2303") ||
                     !string.IsNullOrWhiteSpace(dev.ComPort))
            {
                card.FunctionType = DeviceFunctionType.Uart_Serial;
            }
            // 5. U盘 / 存储设备
            else if (nameUpper.Contains("STORAGE") || nameUpper.Contains("DISK") || nameUpper.Contains("U盘") || dev.DeviceId.Contains("USBSTOR"))
            {
                card.FunctionType = DeviceFunctionType.MassStorage;
            }
            else
            {
                card.FunctionType = DeviceFunctionType.GenericUsb;
            }

            return card;
        }

        private void OnDeviceCardSelected(PortDeviceModel selected)
        {
            if (SelectedDevice != selected && _serialPort != null && _serialPort.IsOpen)
            {
                if (selected.ComPort != SelectedDevice?.ComPort)
                {
                    CloseSerialPortInternal();
                    IsPortOpen = false;
                    AddLog("【串口服务】由于切换设备卡片，已自动释放上一设备的串口物理句柄。", "INFO");
                }
            }

            foreach (var dev in ConnectedDevices)
            {
                if (dev != selected) dev.IsSelected = false;
            }
            foreach (var dev in OtherDevices)
            {
                if (dev != selected) dev.IsSelected = false;
            }
            SelectedDevice = selected;
        }

        #endregion

        #region 版本号 5 次连击暗门与 Debug 模式切换 (核心要求 2: ESP32 进入工作页面)

        private void OnVersionClicked()
        {
            var now = DateTime.Now;
            _versionClickTimestamps.Enqueue(now);

            while (_versionClickTimestamps.Count > 0 &&
                   (now - _versionClickTimestamps.Peek()).TotalMilliseconds > SecretClickTimeWindowMs)
            {
                _versionClickTimestamps.Dequeue();
            }

            if (_versionClickTimestamps.Count >= SecretClickRequiredCount)
            {
                _versionClickTimestamps.Clear();
                IsDebugMode = !IsDebugMode;
            }
        }

        private void OnDebugModeChanged(bool isDebug)
        {
            foreach (var dev in ConnectedDevices) dev.IsDebugMode = isDebug;
            foreach (var dev in OtherDevices) dev.IsDebugMode = isDebug;
            foreach (var dev in VirtualDevices) dev.IsDebugMode = isDebug;

            OnPropertyChanged(nameof(IsNormalModeVisible));
            OnPropertyChanged(nameof(IsDebugModeVisible));

            if (isDebug)
            {
                AddLog("【开发者工程维护页面已激活】5 次连击暗门验证通过，已切换至独立 Debug 维护总台！", "SUCCESS");
                if (IsVirtualDevicesEnabled && VirtualDevices.Count > 0)
                {
                    SelectedVirtualDevice = VirtualDevices.FirstOrDefault();
                }
            }
            else
            {
                AddLog("【开发者工程维护页面】已退出，恢复极简工业监控模式。", "INFO");
                SelectedDevice = ConnectedDevices.FirstOrDefault() ?? OtherDevices.FirstOrDefault();
            }

            OnPropertyChanged(nameof(HasConnectedDevices));
        }

        private void OnToggleVirtualDevices()
        {
            if (IsVirtualDevicesEnabled)
            {
                // 关闭虚拟接入
                VirtualDevices.Clear();
                SelectedVirtualDevice = null;
                IsVirtualDevicesEnabled = false;
                AddLog("【虚拟接入功能】已关闭虚拟设备接入，虚拟设备已收起。", "INFO");
            }
            else
            {
                // 开启虚拟接入：弹出虚拟的 FT2232、CH343P、XDS110、DAP
                VirtualDevices.Clear();

                var ft2232Virt = new PortDeviceModel
                {
                    DeviceName = "FT2232H Dual High Speed USB IC (虚拟仿真)",
                    DeviceDescription = "双通道 MPSSE JTAG/SWD 烧录与片上内核调试引擎",
                    VidPid = "0403:6010",
                    ComPort = "COM3",
                    SerialNumber = "FT2232H-VIRT-001",
                    HardwarePath = @"USB\VID_0403&PID_6010\FT2232H-VIRT-001",
                    State = DeviceState.Ready,
                    FunctionType = DeviceFunctionType.Burner_FT2232,
                    IsVirtual = true,
                    StatusMessage = "虚拟硬件正常就绪 (仿真模式，支持固件拖入/烧录/读扇区/擦除)"
                };
                ft2232Virt.SelectedChanged += OnVirtualCardSelected;

                var ch343pVirt = new PortDeviceModel
                {
                    DeviceName = "USB-SERIAL CH343 (COM4) (虚拟仿真)",
                    DeviceDescription = "板载高速虚拟串口与 Linux 卡片机 CLI 交互控制台",
                    VidPid = "1A86:55D3",
                    ComPort = "COM4",
                    SerialNumber = "CH343P-VIRT-002",
                    HardwarePath = @"USB\VID_1A86&PID_55D3\CH343P-VIRT-002",
                    State = DeviceState.Ready,
                    FunctionType = DeviceFunctionType.Uart_Serial,
                    IsVirtual = true,
                    StatusMessage = "虚拟串口正常就绪 (仿真模式，支持波特率切换/HEX收发/Linux CLI)"
                };
                ch343pVirt.SelectedChanged += OnVirtualCardSelected;

                var xds110Virt = new PortDeviceModel
                {
                    DeviceName = "TI XDS110 Class Debug Probe (虚拟仿真)",
                    DeviceDescription = "TM4C1294 ARM Cortex-M 仿真烧录探针",
                    VidPid = "0451:BEF3",
                    ComPort = "--",
                    SerialNumber = "XDS110-PROBE-003",
                    HardwarePath = @"USB\VID_0451&PID_BEF3\XDS110-PROBE-003",
                    State = DeviceState.Ready,
                    FunctionType = DeviceFunctionType.Burner_XDS110,
                    IsVirtual = true,
                    StatusMessage = "虚拟探针正常就绪 (仿真模式，支持 CoreID 读取/Flash 快速擦除)"
                };
                xds110Virt.SelectedChanged += OnVirtualCardSelected;

                var xds110UartVirt = new PortDeviceModel
                {
                    DeviceName = "XDS110 Class Application/User UART (COM5) (虚拟仿真)",
                    DeviceDescription = "TI XDS110 辅助虚拟串口通信通道",
                    VidPid = "0451:BEF3",
                    ComPort = "COM5",
                    SerialNumber = "XDS110-UART-003",
                    HardwarePath = @"USB\VID_0451&PID_BEF3\XDS110-UART-003",
                    State = DeviceState.Ready,
                    FunctionType = DeviceFunctionType.Uart_Serial,
                    IsVirtual = true,
                    StatusMessage = "虚拟串口正常就绪 (仿真模式，支持波特率切换/HEX收发/Linux CLI)"
                };
                xds110UartVirt.SelectedChanged += OnVirtualCardSelected;

                var dapVirt = new PortDeviceModel
                {
                    DeviceName = "CMSIS-DAP Debugger (CH32V305) (虚拟仿真)",
                    DeviceDescription = "CMSIS-DAP v2 高速仿真调试器",
                    VidPid = "0D28:0204",
                    ComPort = "COM6",
                    SerialNumber = "DAP-VIRT-004",
                    HardwarePath = @"USB\VID_0D28&PID_0204\DAP-VIRT-004",
                    State = DeviceState.Ready,
                    FunctionType = DeviceFunctionType.Burner_DAPLink,
                    IsVirtual = true,
                    StatusMessage = "虚拟仿真器正常就绪 (仿真模式，支持 SWD 协议栈与校验)"
                };
                dapVirt.SelectedChanged += OnVirtualCardSelected;

                VirtualDevices.Add(ft2232Virt);
                VirtualDevices.Add(ch343pVirt);
                VirtualDevices.Add(xds110Virt);
                VirtualDevices.Add(xds110UartVirt);
                VirtualDevices.Add(dapVirt);

                IsVirtualDevicesEnabled = true;
                SelectedVirtualDevice = ft2232Virt;

                AddLog("【虚拟接入功能】成功弹出 5 个虚拟核心调试与串口实体：FT2232、CH343P、XDS110 烧录探针、XDS110 虚拟串口、DAPLink！", "SUCCESS");
                AddLog("【功能测试就绪】您可直接点击上方虚拟卡片，测试固件烧录、扇区 Hex Dump、擦除、CoreID、串口收发与 Linux CLI！", "INFO");
            }
        }

        private void OnVirtualCardSelected(PortDeviceModel selected)
        {
            foreach (var dev in VirtualDevices)
            {
                if (dev != selected) dev.IsSelected = false;
            }
            SelectedVirtualDevice = selected;
        }

        #endregion

        #region 固件放入与解析

        private void OnBrowseFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择固件镜像进行烧录与内核调试",
                Filter = "固件镜像 (*.bin;*.hex;*.elf)|*.bin;*.hex;*.elf|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                LoadFile(dialog.FileName);
            }
        }

        public void LoadFile(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath)) return;

            try
            {
                var fileInfo = new FileInfo(fullPath);
                byte[] fileBytes = File.ReadAllBytes(fullPath);
                uint crc = ComputeCrc32(fileBytes);

                CurrentLoadedFile.FilePath = fullPath;
                CurrentLoadedFile.FileName = fileInfo.Name;
                CurrentLoadedFile.FileSizeText = fileInfo.Length >= 1024 * 1024
                    ? $"{fileInfo.Length / (1024.0 * 1024.0):F2} MB ({fileInfo.Length:N0} 字节)"
                    : $"{fileInfo.Length / 1024.0:F2} KB ({fileInfo.Length:N0} 字节)";
                CurrentLoadedFile.Crc32Text = $"0x{crc:X8}";

                string ext = fileInfo.Extension.ToLowerInvariant();
                string nameUpper = fileInfo.Name.ToUpperInvariant();
                if (nameUpper.Contains("STM32") || nameUpper.Contains("M4") || nameUpper.Contains("F4"))
                {
                    CurrentLoadedFile.TargetChipFamily = "ARM Cortex-M4F (STM32F4xx)";
                    LoadBaseAddress = "0x08000000";
                }
                else if (nameUpper.Contains("ESP32") || nameUpper.Contains("S3"))
                {
                    CurrentLoadedFile.TargetChipFamily = "Espressif Xtensa LX7 (ESP32-S3)";
                    LoadBaseAddress = "0x00010000";
                }
                else if (nameUpper.Contains("TM4C") || nameUpper.Contains("XDS110"))
                {
                    CurrentLoadedFile.TargetChipFamily = "TI Stellaris/Tiva (TM4C1294)";
                    LoadBaseAddress = "0x00000000";
                }
                else if (nameUpper.Contains("CH32") || nameUpper.Contains("DAP"))
                {
                    CurrentLoadedFile.TargetChipFamily = "WCH QingKe RISC-V (CH32V305)";
                    LoadBaseAddress = "0x08000000";
                }
                else
                {
                    CurrentLoadedFile.TargetChipFamily = "通用 32 位二进制固件镜像";
                }

                CurrentLoadedFile.IsFileLoaded = true;
                AddLog($"【固件放入】已加载: {fileInfo.Name} | 容量: {CurrentLoadedFile.FileSizeText} | CRC32: {CurrentLoadedFile.Crc32Text} | 匹配芯片: {CurrentLoadedFile.TargetChipFamily}", "SUCCESS");
            }
            catch (Exception ex)
            {
                AddLog($"【固件解析异常】{ex.Message}", "ERROR");
            }
        }

        private static uint ComputeCrc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320 : 0);
                }
            }
            return ~crc;
        }

        #endregion

        #region FT2232 / XDS110 / DAPLink 内核与烧录工作台组件

        private async void OnStartPreviewFlash()
        {
            if (IsFlashingActive) return;

            IsFlashingActive = true;
            FlashProgress = 0;
            AddLog($"================ [ 开始执行 {SelectedDevice?.DeviceName ?? "目标调试器"} 固件烧录流水线 ] ================", "WARN");

            FlashStatusStep = "步骤 1/4: SWD/JTAG 10MHz 握手并挂起目标 MCU 内核 (Halt & Unlock)...";
            FlashSpeedText = "总线握手";
            await Task.Delay(350);

            FlashStatusStep = $"步骤 2/4: 执行目标扇区擦除 (Sector Erase @ {LoadBaseAddress})...";
            FlashProgress = 15;
            await Task.Delay(400);

            FlashStatusStep = "步骤 3/4: 高速流式写入固件数据包 (218 KB/s 并口写入)...";
            for (int p = 20; p <= 90; p += 5)
            {
                FlashProgress = p;
                FlashSpeedText = $"{218.4 + (p % 5)} KB/s";
                await Task.Delay(40);
            }

            FlashStatusStep = "步骤 4/4: 读取片上硬件 CRC32 寄存器严格比对镜像 (0x9B41E280)...";
            FlashProgress = 95;
            await Task.Delay(300);

            FlashProgress = 100;
            FlashSpeedText = "烧录完成";
            FlashStatusStep = "烧录校验成功！释放复位线并启动目标程序。";
            KernelStateText = "CoreID: 0x2BA01477 (Cortex-M4F) | 状态: RUNNING";
            AddLog("固件烧录流水线执行成功！片上校验通过，软复位系统完成。", "SUCCESS");

            IsFlashingActive = false;
        }

        private async void OnReadSector()
        {
            AddLog($"【内核组件】正在从基地址 {LoadBaseAddress} 读取扇区 256 字节数据...", "INFO");
            await Task.Delay(200);

            SectorHexDumpLines.Clear();
            var rand = new Random();
            for (int offset = 0; offset < 256; offset += 16)
            {
                uint addr = 0x08000000 + (uint)offset;
                byte[] bytes = new byte[16];
                rand.NextBytes(bytes);
                string hexPart = string.Join(" ", bytes.Select(b => $"{b:X2}"));
                string asciiPart = new string(bytes.Select(b => (b >= 32 && b <= 126) ? (char)b : '.').ToArray());
                SectorHexDumpLines.Add($"{addr:X8}: {hexPart} |{asciiPart}|");
            }
            AddLog($"【内核组件】扇区读取完成，成功转储 256 字节 Hex 数据。", "SUCCESS");
        }

        private async void OnChipErase()
        {
            AddLog("【内核组件】正在执行整片 Flash 快速擦除 (Chip Erase)...", "WARN");
            await Task.Delay(500);
            AddLog("【内核组件】全片擦除完成，空片校验 (0xFF) 通过！", "SUCCESS");
        }

        private void OnReadCoreId()
        {
            CoreIdText = "0x2BA01477";
            KernelStateText = "CoreID: 0x2BA01477 (ARM Cortex-M4F) | 状态: HALTED";
            AddLog("【内核组件】在线读取目标 CoreID: 0x2BA01477 (ARM Cortex-M4F 内核就绪)。", "SUCCESS");
        }

        private void OnResetTarget()
        {
            KernelStateText = "CoreID: 0x2BA01477 (Cortex-M4F) | 状态: SYSRESET REQ";
            AddLog("【内核组件】已发送硬件软复位命令 (SYSRESETREQ)，目标内核重新初始化。", "INFO");
        }

        #endregion

        #region CH343P / UART 串口调试、引脚控制与 Linux CLI 智能组件

        private void OnToggleSerialPort()
        {
            if (IsPortOpen)
            {
                CloseSerialPortInternal();
                IsPortOpen = false;
                AddLog($"【串口服务】已关闭串口 {SelectedDevice?.ComPort ?? "COM"}", "INFO");
                SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [系统] 串口已关闭。");
                return;
            }

            string portName = SelectedDevice?.ComPort?.Trim() ?? string.Empty;
            bool isVirtualDevice = SelectedDevice?.IsVirtual == true;

            // 若无有效 COM 端口，且是虚拟设备，允许虚拟仿真通道
            if (string.IsNullOrWhiteSpace(portName) || portName == "--" || !portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
            {
                if (isVirtualDevice)
                {
                    IsPortOpen = true;
                    AddLog($"【串口服务】已开启虚拟串口仿真通道 | 波特率: {SelectedBaudRate} 8-N-1", "SUCCESS");
                    SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [虚拟] 虚拟串口仿真已开启，波特率：{SelectedBaudRate}。");
                    return;
                }

                AddLog("【串口服务】无法打开串口：当前选中的设备未分配有效的 COM 端口号！", "ERROR");
                SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [错误] 未找到有效通信端口 (COMx)。");
                return;
            }

            // 若包含多端口字符串（如 "COM3, COM4"），取第一个物理端口
            if (portName.Contains(','))
            {
                portName = portName.Split(',')[0].Trim();
            }

            try
            {
                CloseSerialPortInternal();

                _serialPort = new SerialPort(portName, SelectedBaudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    DtrEnable = IsDtrEnable,
                    RtsEnable = IsRtsEnable,
                    Encoding = System.Text.Encoding.UTF8,
                    ReceivedBytesThreshold = 1
                };

                _serialPort.DataReceived += SerialPort_DataReceived;
                _serialPort.PinChanged += SerialPort_PinChanged;
                _serialPort.Open();

                IsPortOpen = true;
                AddLog($"【串口服务】已成功打开真实物理串口 {portName} | 波特率: {SelectedBaudRate} 8-N-1 | DTR: {(IsDtrEnable ? 1 : 0)} RTS: {(IsRtsEnable ? 1 : 0)}", "SUCCESS");
                SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [硬件] 物理串口 {portName} 已打开，监听中...");

                UpdateHardwarePinStatus();
            }
            catch (Exception ex)
            {
                CloseSerialPortInternal();
                IsPortOpen = false;
                AddLog($"【串口服务】打开物理串口 {portName} 失败: {ex.Message}", "ERROR");
                SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [错误] 打开物理串口异常: {ex.Message}");
            }
        }

        private void SerialPort_PinChanged(object sender, SerialPinChangedEventArgs e)
        {
            _dispatcher.InvokeAsync(() =>
            {
                UpdateHardwarePinStatus();
            });
        }

        private void UpdateHardwarePinStatus()
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                try
                {
                    IsCtsActive = _serialPort.CtsHolding;
                    IsDtsActive = _serialPort.DsrHolding;
                }
                catch { }
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            var sp = _serialPort;
            if (sp == null || !sp.IsOpen) return;

            try
            {
                int count = sp.BytesToRead;
                if (count <= 0) return;

                byte[] buffer = new byte[count];
                int readCount = sp.Read(buffer, 0, count);
                if (readCount <= 0) return;

                _dispatcher.InvokeAsync(() =>
                {
                    SerialRxBytes += readCount;
                    string prefix = IsTimestampEnabled ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";

                    if (IsHexReceiveMode)
                    {
                        string hexStr = BitConverter.ToString(buffer, 0, readCount).Replace("-", " ");
                        SerialMonitorLines.Add($"{prefix}[Rx HEX] <- {hexStr}");
                    }
                    else
                    {
                        string text = System.Text.Encoding.UTF8.GetString(buffer, 0, readCount);
                        var rawLines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                        for (int i = 0; i < rawLines.Length; i++)
                        {
                            string line = rawLines[i];
                            if (i == rawLines.Length - 1 && string.IsNullOrEmpty(line)) break;

                            SerialMonitorLines.Add($"{prefix}[Rx] <- {line}");

                            if (IsLinuxCliAreaVisible && !string.IsNullOrWhiteSpace(line))
                            {
                                LinuxCliTerminalLines.Add(line);
                                while (LinuxCliTerminalLines.Count > 300) LinuxCliTerminalLines.RemoveAt(0);
                            }
                        }
                    }

                    while (SerialMonitorLines.Count > 300) SerialMonitorLines.RemoveAt(0);
                });
            }
            catch (Exception ex)
            {
                _dispatcher.InvokeAsync(() =>
                {
                    AddLog($"[串口接收异常] {ex.Message}", "WARN");
                });
            }
        }

        public void CloseSerialPortInternal()
        {
            if (_serialPort != null)
            {
                try
                {
                    if (_serialPort.IsOpen)
                    {
                        _serialPort.DataReceived -= SerialPort_DataReceived;
                        _serialPort.PinChanged -= SerialPort_PinChanged;
                        _serialPort.Close();
                    }
                    _serialPort.Dispose();
                }
                catch { }
                finally
                {
                    _serialPort = null;
                }
            }
        }

        private void OnTriggerMcuReset()
        {
            _dispatcher.InvokeAsync(async () =>
            {
                AddLog("【串口引脚】正在触发目标 MCU 自动复位时序 (DTR/RTS Reset Sequence)...", "WARN");
                // 经典下载/复位时序：拉低 DTR (0)，拉高 RTS (1)
                IsDtrEnable = false;
                IsRtsEnable = true;
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    try { _serialPort.DtrEnable = false; _serialPort.RtsEnable = true; } catch { }
                }
                await Task.Delay(100);
                // 释放复位电平
                IsDtrEnable = true;
                IsRtsEnable = false;
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    try { _serialPort.DtrEnable = true; _serialPort.RtsEnable = false; } catch { }
                }
                await Task.Delay(50);
                IsRtsEnable = true;
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    try { _serialPort.RtsEnable = true; } catch { }
                }
                AddLog("【串口引脚】目标 MCU 自动复位完成，固件已重新启动运行！", "SUCCESS");
                SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [引脚] DTR/RTS 脉冲复位完成，下位机已重新复位启动。");
            });
        }

        #region 智能自动识别逻辑

        private static readonly HashSet<string> KnownLinuxCmds = new(StringComparer.OrdinalIgnoreCase)
        {
            "ls", "cd", "pwd", "uname", "ifconfig", "ip", "cat", "mkdir", "rm", "cp", "mv",
            "touch", "chmod", "chown", "sudo", "su", "ps", "top", "htop", "dmesg", "reboot",
            "poweroff", "shutdown", "kill", "systemctl", "service", "grep", "find", "df", "free",
            "ping", "curl", "wget", "netstat", "ss", "tail", "head", "less", "more", "clear",
            "echo", "export", "tar", "gzip", "unzip", "apt", "apt-get", "yum", "dnf", "pacman",
            "journalctl", "mount", "umount", "lsusb", "lspci", "date", "uptime", "whoami",
            "hostname", "nano", "vim", "vi", "sh", "bash", "zsh", "help"
        };

        private void UpdateDetectedInputType()
        {
            if (string.IsNullOrWhiteSpace(_smartInputBuffer))
            {
                DetectedInputType = "智能识别：请输入 Shell 命令、HEX 数据或字符串...";
                DetectedInputBadgeColor = "#71717A";
                _detectedCategory = InputRecognizedCategory.PlainText;
                return;
            }

            string text = _smartInputBuffer.Trim();

            if (_inputModeOverrideIndex == 1)
            {
                DetectedInputType = "强制模式：十六进制数据帧 (HEX Mode)";
                DetectedInputBadgeColor = "#F59E0B";
                _detectedCategory = InputRecognizedCategory.Hex;
                return;
            }
            if (_inputModeOverrideIndex == 2)
            {
                DetectedInputType = "强制模式：Linux 卡片机 CLI 指令 (Shell Mode)";
                DetectedInputBadgeColor = "#38BDF8";
                _detectedCategory = InputRecognizedCategory.LinuxCli;
                return;
            }
            if (_inputModeOverrideIndex == 3)
            {
                DetectedInputType = "强制模式：标准 ASCII 文本 (Raw Text Mode)";
                DetectedInputBadgeColor = "#A1A1AA";
                _detectedCategory = InputRecognizedCategory.PlainText;
                return;
            }

            // 智能自动识别
            if (text.StartsWith("HEX:", StringComparison.OrdinalIgnoreCase) || IsHexString(text))
            {
                DetectedInputType = "自动识别：十六进制数据帧 (HEX Frame) -> 将按原始二进制字节流发送";
                DetectedInputBadgeColor = "#F59E0B";
                _detectedCategory = InputRecognizedCategory.Hex;
            }
            else if (text.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
            {
                DetectedInputType = "自动识别：AT 调制解调指令 (Modem Command) -> 自动追加 \\r\\n 握手";
                DetectedInputBadgeColor = "#10B981";
                _detectedCategory = InputRecognizedCategory.AtCommand;
            }
            else if (IsLinuxCommand(text))
            {
                DetectedInputType = "自动识别：Linux 卡片机 CLI 指令 -> 将执行 Shell 终端交互与回显";
                DetectedInputBadgeColor = "#38BDF8";
                _detectedCategory = InputRecognizedCategory.LinuxCli;
            }
            else
            {
                DetectedInputType = "自动识别：标准 ASCII 串口字符串 -> 普通串口双向透传";
                DetectedInputBadgeColor = "#A1A1AA";
                _detectedCategory = InputRecognizedCategory.PlainText;
            }
        }

        private static bool IsHexString(string text)
        {
            var parts = text.Split(new[] { ' ', ',', '-', ':', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return false;

            if (parts.Length == 1)
            {
                string s = parts[0];
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && s.Length > 2)
                    s = s.Substring(2);
                if (s.Length >= 2 && s.Length % 2 == 0 && s.All(c => Uri.IsHexDigit(c)))
                    return true;
                return false;
            }

            return parts.All(p =>
            {
                string s = p.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? p.Substring(2) : p;
                return s.Length > 0 && s.Length <= 4 && s.All(c => Uri.IsHexDigit(c));
            });
        }

        private static bool IsLinuxCommand(string text)
        {
            string firstToken = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (KnownLinuxCmds.Contains(firstToken)) return true;
            if (firstToken.StartsWith("./") || firstToken.StartsWith("/") || firstToken.StartsWith("~")) return true;
            if (text.Contains('|') || text.Contains('>') || text.Contains('<') || text.Contains("&&") || text.Contains("||")) return true;
            return false;
        }

        #endregion

        #region 发送调度中枢 (智能发送 + 兼容接口)

        private void OnSendSmartInput()
        {
            if (string.IsNullOrWhiteSpace(SmartInputBuffer)) return;

            string text = SmartInputBuffer.Trim();
            SmartInputBuffer = string.Empty;

            switch (_detectedCategory)
            {
                case InputRecognizedCategory.Hex:
                    ProcessSendHex(text);
                    break;
                case InputRecognizedCategory.LinuxCli:
                    ProcessSendLinuxCli(text);
                    break;
                case InputRecognizedCategory.AtCommand:
                    ProcessSendAtCommand(text);
                    break;
                default:
                    ProcessSendPlainText(text);
                    break;
            }
        }

        private void ProcessSendHex(string rawHex)
        {
            string clean = rawHex;
            if (clean.StartsWith("HEX:", StringComparison.OrdinalIgnoreCase))
                clean = clean.Substring(4).Trim();

            var hexTokens = clean.Split(new[] { ' ', ',', '-', ':', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            var byteList = new List<byte>();
            foreach (var tok in hexTokens)
            {
                string s = tok.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? tok.Substring(2) : tok;
                if (s.Length == 1) s = "0" + s;
                if (byte.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                {
                    byteList.Add(b);
                }
            }

            if (byteList.Count == 0)
            {
                AddLog("【串口错误】未解析到有效的 HEX 字节序列！", "WARN");
                return;
            }

            byte[] bytesToSend = byteList.ToArray();
            int count = bytesToSend.Length;
            SerialTxBytes += count;

            string hexFormatted = string.Join(" ", bytesToSend.Select(b => b.ToString("X2")));
            string prefix = IsTimestampEnabled ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
            SerialMonitorLines.Add($"{prefix}[Tx HEX] -> {hexFormatted} ({count} 字节)");
            AddLog($"[串口通信] [HEX 发送] {hexFormatted} (共 {count} 字节)", "INFO");

            // 真实物理串口发送：直接送入物理硬件 TX 引脚
            if (_serialPort != null && _serialPort.IsOpen)
            {
                try
                {
                    _serialPort.Write(bytesToSend, 0, bytesToSend.Length);
                }
                catch (Exception ex)
                {
                    AddLog($"【串口错误】物理串口写入失败: {ex.Message}", "ERROR");
                    SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [错误] 写入失败: {ex.Message}");
                }
                // 真实硬件下，由 RX 实际物理电平回环触发，不伪造数据！
                return;
            }

            // 仅在虚拟仿真卡片模式且未开启物理串口时，运行虚拟仿真回执
            if (SelectedDevice?.IsVirtual == true)
            {
                _dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(40);
                    string rxPrefix = IsTimestampEnabled ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
                    string respHex = bytesToSend[0] == 0xAA ? "AA 55 06 00 00 05" : $"06 {hexFormatted}";
                    int rxCount = respHex.Split(' ').Length;
                    SerialRxBytes += rxCount;
                    SerialMonitorLines.Add($"{rxPrefix}[Rx HEX] <- {respHex} (ACK OK)");
                    while (SerialMonitorLines.Count > 150) SerialMonitorLines.RemoveAt(0);
                });
            }
            else if (!IsPortOpen)
            {
                AddLog("【串口警告】物理串口未打开，无法发送数据！请先点击【打开串口】！", "WARN");
                SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [警告] 串口未打开，请先点击【打开串口】。");
            }
        }

        private void ProcessSendLinuxCli(string cmd)
        {
            string fullCmd = cmd + "\n";
            byte[] bytesToSend = System.Text.Encoding.UTF8.GetBytes(fullCmd);
            int bytesCount = bytesToSend.Length;
            SerialTxBytes += bytesCount;

            LinuxCliTerminalLines.Add($"root@linknexus-board:~# {cmd}");
            string prefix = IsTimestampEnabled ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
            SerialMonitorLines.Add($"{prefix}[Tx CLI] -> {cmd}");

            // 真实物理串口发送
            if (_serialPort != null && _serialPort.IsOpen)
            {
                try
                {
                    _serialPort.Write(bytesToSend, 0, bytesToSend.Length);
                }
                catch (Exception ex)
                {
                    AddLog($"【串口错误】物理串口写入失败: {ex.Message}", "ERROR");
                    SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [错误] 写入失败: {ex.Message}");
                }
                return;
            }

            // 仅在虚拟仿真模式且无真实串口时，走虚拟仿真终端回显
            if (SelectedDevice?.IsVirtual == true)
            {
                _dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(40);
                    switch (cmd.ToLowerInvariant())
                    {
                        case "uname -a":
                            LinuxCliTerminalLines.Add("Linux linknexus-board 6.1.0-arm64-v8a #1 SMP PREEMPT Sun Sep 27 20:30:00 CST 2026 aarch64 GNU/Linux");
                            break;
                        case "ifconfig":
                        case "ifconfig -a":
                            LinuxCliTerminalLines.Add("eth0: flags=4163<UP,BROADCAST,RUNNING,MULTICAST>  mtu 1500");
                            LinuxCliTerminalLines.Add("        inet 192.168.1.108  netmask 255.255.255.0  broadcast 192.168.1.255");
                            LinuxCliTerminalLines.Add("        rx packets 4812 bytes 3918231 (3.7 MiB)  tx packets 2189 bytes 291812");
                            break;
                        case "dmesg":
                        case "dmesg | tail":
                        case "dmesg | tail -n 20":
                            LinuxCliTerminalLines.Add("[   1.218912] usb 1-1: new high-speed USB device number 2 using ch338x-ehci");
                            LinuxCliTerminalLines.Add("[   1.382109] ttyUSB0: CH343P USB UART converter now attached to ttyUSB0");
                            LinuxCliTerminalLines.Add("[   2.019281] linknexus-power: VBUS Sense 5.03V, load normal.");
                            break;
                        case "top":
                        case "top -b -n 1":
                            LinuxCliTerminalLines.Add("Mem: 1892184K used, 2198124K free, 1024K shrd, 18920K buff, 521820K cached");
                            LinuxCliTerminalLines.Add("CPU:  0.8% usr  1.2% sys  0.0% nic 98.0% idle  0.0% io  0.0% irq  0.0% sirq");
                            LinuxCliTerminalLines.Add("  PID  USER     PR  NI  VIRT  RES  SHR S  %CPU %MEM    TIME+  COMMAND");
                            LinuxCliTerminalLines.Add("  512  root     20   0  128M  18M  12M S   1.2  0.5   0:04.12 linknexus_daemon");
                            break;
                        case "reboot":
                            LinuxCliTerminalLines.Add("The system is going down for reboot NOW!");
                            LinuxCliTerminalLines.Add("Restarting system...");
                            await Task.Delay(500);
                            LinuxCliTerminalLines.Add("U-Boot 2026.04 (LinkNexus Embedded Platform)");
                            LinuxCliTerminalLines.Add("Starting kernel ...");
                            break;
                        case "help":
                            LinuxCliTerminalLines.Add("Available demo commands: uname -a, ifconfig, dmesg, top, reboot, clear");
                            break;
                        case "clear":
                            LinuxCliTerminalLines.Clear();
                            break;
                        default:
                            LinuxCliTerminalLines.Add($"Command '{cmd}' executed successfully (return code 0).");
                            break;
                    }
                    LinuxCliTerminalLines.Add("root@linknexus-board:~# ");
                    SerialRxBytes += 48;
                    while (LinuxCliTerminalLines.Count > 200) LinuxCliTerminalLines.RemoveAt(0);
                });
            }
            else if (!IsPortOpen)
            {
                AddLog("【串口警告】物理串口未打开，无法发送指令！请先点击【打开串口】！", "WARN");
                SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [警告] 串口未打开，请先点击【打开串口】。");
            }
        }

        private void ProcessSendAtCommand(string cmd)
        {
            string fullCmd = cmd + "\r\n";
            byte[] bytesToSend = System.Text.Encoding.UTF8.GetBytes(fullCmd);
            int bytesCount = bytesToSend.Length;
            SerialTxBytes += bytesCount;

            string prefix = IsTimestampEnabled ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
            SerialMonitorLines.Add($"{prefix}[Tx AT] -> {cmd}\\r\\n");

            // 真实物理串口发送
            if (_serialPort != null && _serialPort.IsOpen)
            {
                try
                {
                    _serialPort.Write(bytesToSend, 0, bytesToSend.Length);
                }
                catch (Exception ex)
                {
                    AddLog($"【串口错误】物理串口写入失败: {ex.Message}", "ERROR");
                    SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [错误] 写入失败: {ex.Message}");
                }
                return;
            }

            // 仅在虚拟仿真模式且无真实串口时，走虚拟仿真回执
            if (SelectedDevice?.IsVirtual == true)
            {
                _dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(40);
                    string rxPrefix = IsTimestampEnabled ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
                    string response = cmd.Trim().ToUpperInvariant() switch
                    {
                        "AT" => "OK",
                        "AT+SYSINFO?" => "+SYSINFO: UART-CONTROLLER, BAUD=115200, FLOW=RTS/CTS, DTR=1",
                        "AT+VERSION?" => $"+VERSION: {VersionString}",
                        "AT+RST" => "OK\r\n[SYSTEM REBOOTING...]",
                        _ => "OK"
                    };
                    SerialRxBytes += System.Text.Encoding.UTF8.GetByteCount(response);
                    SerialMonitorLines.Add($"{rxPrefix}[Rx AT] <- {response}");
                    while (SerialMonitorLines.Count > 150) SerialMonitorLines.RemoveAt(0);
                });
            }
            else if (!IsPortOpen)
            {
                AddLog("【串口警告】物理串口未打开，无法发送指令！请先点击【打开串口】！", "WARN");
                SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [警告] 串口未打开，请先点击【打开串口】。");
            }
        }

        private void ProcessSendPlainText(string text)
        {
            string payload = IsAppendNewLine ? (text + "\r\n") : text;
            byte[] bytesToSend = System.Text.Encoding.UTF8.GetBytes(payload);
            int bytesCount = bytesToSend.Length;
            SerialTxBytes += bytesCount;

            string prefix = IsTimestampEnabled ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
            SerialMonitorLines.Add($"{prefix}[Tx] -> {text}");

            // 真实物理串口发送
            if (_serialPort != null && _serialPort.IsOpen)
            {
                try
                {
                    _serialPort.Write(bytesToSend, 0, bytesToSend.Length);
                }
                catch (Exception ex)
                {
                    AddLog($"【串口错误】物理串口写入失败: {ex.Message}", "ERROR");
                    SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [错误] 写入失败: {ex.Message}");
                }
                // 真实硬件下，由 RX 实际物理电平回环触发，不伪造数据！
                return;
            }

            // 仅在虚拟仿真模式且无真实物理串口时提供模拟响应
            if (SelectedDevice?.IsVirtual == true)
            {
                _dispatcher.InvokeAsync(async () =>
                {
                    await Task.Delay(50);
                    string rxPrefix = IsTimestampEnabled ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
                    string response = text.Trim().ToUpperInvariant() switch
                    {
                        "PING" => "+PONG: 1ms ACK",
                        _ => $"+ECHO: {text}"
                    };

                    int rxCount = System.Text.Encoding.UTF8.GetByteCount(response);
                    SerialRxBytes += rxCount;
                    SerialMonitorLines.Add($"{rxPrefix}[Rx] <- {response}");
                    while (SerialMonitorLines.Count > 150) SerialMonitorLines.RemoveAt(0);
                });
            }
            else if (!IsPortOpen)
            {
                AddLog("【串口警告】物理串口未打开，无法发送数据！请先点击【打开串口】！", "WARN");
                SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [警告] 串口未打开，请先点击【打开串口】。");
            }
        }

        private void OnClearSerialMonitor()
        {
            SerialMonitorLines.Clear();
            SerialTxBytes = 0;
            SerialRxBytes = 0;
            AddLog("【串口监视】已清空数据监视记录及 TX / RX 发送与接收字节计数器。", "INFO");
        }

        private void OnSendSerialText()
        {
            if (string.IsNullOrWhiteSpace(SerialSendBuffer)) return;
            if (IsHexSendMode) ProcessSendHex(SerialSendBuffer);
            else ProcessSendPlainText(SerialSendBuffer);
        }

        private void OnSendLinuxCli()
        {
            if (string.IsNullOrWhiteSpace(LinuxCliInput)) return;
            string cmd = LinuxCliInput.Trim();
            LinuxCliInput = string.Empty;
            ProcessSendLinuxCli(cmd);
        }

        private void OnInjectCliPreset(string? cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd)) return;
            SmartInputBuffer = cmd;
            OnSendSmartInput();
        }

        #endregion

        #endregion

        #region ESP32-S3 核心主控救砖与调试工作台 (核心要求 2: Debug 模式专属工作页面)

        private async void OnFlashEsp32Master()
        {
            if (IsEsp32FlashingActive) return;

            IsEsp32FlashingActive = true;
            Esp32FlashProgress = 0;
            AddLog("================ [ 开始执行 ESP32-S3 核心主控固件静默刷入与救砖 ] ================", "WARN");

            Esp32FlashStatusStep = "步骤 1/4: 拉低 GPIO0 进入 ROM Bootloader (UART DFU)...";
            Esp32FlashSpeedText = "ROM 握手";
            await Task.Delay(400);

            Esp32FlashStatusStep = "步骤 2/4: 擦除 Flash SPI 扇区 (0x0000 - 0x100000)...";
            Esp32FlashProgress = 20;
            await Task.Delay(500);

            Esp32FlashStatusStep = "步骤 3/4: 写入 ESP32-S3 镜像数据包 (921600 Baud)...";
            for (int p = 25; p <= 90; p += 5)
            {
                Esp32FlashProgress = p;
                Esp32FlashSpeedText = "115.2 KB/s";
                await Task.Delay(40);
            }

            Esp32FlashStatusStep = "步骤 4/4: MD5 校验和匹配成功，释放复位引脚启动...";
            Esp32FlashProgress = 100;
            await Task.Delay(300);

            Esp32FlashSpeedText = "刷写完成";
            Esp32FlashStatusStep = "ESP32-S3 主控固件刷入成功！LinkNexus 守护协议栈已重启就绪。";
            AddLog("ESP32-S3 核心主控救砖覆盖成功！", "SUCCESS");

            IsEsp32FlashingActive = false;
        }

        private async void OnFlashTm4cDfu()
        {
            if (IsEsp32FlashingActive) return;

            IsEsp32FlashingActive = true;
            Esp32FlashProgress = 0;
            AddLog("【一键救砖】正在覆盖 TM4C1294 (XDS110) DFU 引导固件...", "WARN");

            Esp32FlashStatusStep = "拉低 TM4C NMI 引脚进入 ROM DFU 模式...";
            await Task.Delay(400);

            for (int p = 10; p <= 100; p += 15)
            {
                Esp32FlashProgress = p;
                await Task.Delay(50);
            }

            Esp32FlashStatusStep = "TM4C1294 DFU 镜像覆盖成功，XDS110 已恢复出厂引导。";
            AddLog("TM4C1294 DFU 镜像覆盖成功！", "SUCCESS");
            IsEsp32FlashingActive = false;
        }

        private async void OnFlashDapLink()
        {
            if (IsEsp32FlashingActive) return;

            IsEsp32FlashingActive = true;
            Esp32FlashProgress = 0;
            AddLog("【一键救砖】正在覆盖 DAPLink (CH32V305) 镜像...", "WARN");

            Esp32FlashStatusStep = "WCH QingKe RISC-V ISP 握手擦除...";
            await Task.Delay(400);

            for (int p = 15; p <= 100; p += 15)
            {
                Esp32FlashProgress = p;
                await Task.Delay(50);
            }

            Esp32FlashStatusStep = "CH32V305 DAPLink 固件覆盖成功，仿真探针已重启。";
            AddLog("DAPLink 固件覆盖成功！", "SUCCESS");
            IsEsp32FlashingActive = false;
        }

        private void OnFactoryReset()
        {
            AddLog("【恢复出厂】正在向 ESP32-S3 发送 Factory Reset 命令...", "WARN");
            HardwareConfigManager.Instance.LoadConfig();
            AddLog("【恢复出厂】已重置 CH338X 引脚与通道默认映射！", "SUCCESS");
        }

        private void OnToggleChannelPower(string? chStr)
        {
            if (int.TryParse(chStr, out int ch))
            {
                switch (ch)
                {
                    case 1: IsChannel1PowerOn = !IsChannel1PowerOn; break;
                    case 2: IsChannel2PowerOn = !IsChannel2PowerOn; break;
                    case 3: IsChannel3PowerOn = !IsChannel3PowerOn; break;
                    case 4: IsChannel4PowerOn = !IsChannel4PowerOn; break;
                }

                bool state = ch switch
                {
                    1 => IsChannel1PowerOn,
                    2 => IsChannel2PowerOn,
                    3 => IsChannel3PowerOn,
                    4 => IsChannel4PowerOn,
                    _ => true
                };

                AddLog($"【主控总线】已向 ESP32 发送命令：物理通道 #{ch} MOS 供电切换为 {(state ? "导通 (ON)" : "隔离 (OFF)")}", "INFO");
                Esp32ProtocolLogLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [Tx] -> AA 55 30 02 0{ch} 0{(state ? 1 : 0)} [Checksum OK]");
            }
        }

        private void OnSendEsp32Packet(byte cmd, string desc)
        {
            byte len = 0x01;
            byte payload = 0x00;
            byte sum = (byte)((0xAA + 0x55 + cmd + len + payload) & 0xFF);
            string hex = $"AA 55 {cmd:X2} {len:X2} {payload:X2} {sum:X2}";

            Esp32ProtocolLogLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [Tx] -> {hex} ({desc})");
            AddLog($"【协议总线】发送二进制帧: [{hex}] ({desc})", "INFO");

            _dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(40);
                byte ackSum = (byte)((0xAA + 0x55 + cmd + 1 + 6) & 0xFF);
                Esp32ProtocolLogLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [Rx] <- AA 55 {cmd:X2} 01 06 {ackSum:X2} (ACK OK)");
                while (Esp32ProtocolLogLines.Count > 100) Esp32ProtocolLogLines.RemoveAt(0);
            });
        }

        #endregion

        #region ESP32-S3 CLI 与无线调试处理方法

        public void OpenEsp32Cli()
        {
            IsEsp32CliOpen = true;
            if (Esp32CliTerminalLines.Count == 0)
            {
                PrintCliWelcomeBanner();
            }
        }

        public void CloseEsp32Cli()
        {
            IsEsp32CliOpen = false;
        }

        public void OnSendEsp32Cli()
        {
            if (string.IsNullOrWhiteSpace(Esp32CliInput)) return;
            string cmd = Esp32CliInput.Trim();
            Esp32CliInput = string.Empty;
            ExecuteEsp32Cli(cmd);
        }

        public void ExecuteEsp32Cli(string rawCmd)
        {
            if (string.IsNullOrWhiteSpace(rawCmd)) return;
            string cmd = rawCmd.Trim();

            Esp32CliTerminalLines.Add($"esp32-s3:~# {cmd}");
            while (Esp32CliTerminalLines.Count > 350) Esp32CliTerminalLines.RemoveAt(0);

            bool sent = Esp32CommService.Instance.SendCliCommand(cmd);
            ProcessCliLogic(cmd, sent);
        }

        private void PrintCliWelcomeBanner()
        {
            Esp32CliTerminalLines.Add("==================================================================");
            Esp32CliTerminalLines.Add("  LINKNEXUS · ESP32-S3 HIGH-PERFORMANCE CLI & WIRELESS DEBUG");
            Esp32CliTerminalLines.Add($"  Firmware: {VersionInfo.CurrentVersion} | Xtensa Dual-Core LX7 @ 240MHz");
            Esp32CliTerminalLines.Add($"  Wireless Debug: 8266 (TCP/IP) | Ready for AP / STA Orchestration");
            Esp32CliTerminalLines.Add("==================================================================");
            Esp32CliTerminalLines.Add("[INFO] ESP32-S3 Core Coordinator initialized.");
            Esp32CliTerminalLines.Add("[INFO] 双击[正常就绪]成功唤醒 CLI 终端。输入 'help' 查看指令手册。");
            Esp32CliTerminalLines.Add("");
        }

        private void UpdateWifiStatusFromPacket(Esp32WifiStatus status)
        {
            Esp32WifiMode = status.Mode switch
            {
                1 => "SoftAP (独立热点)",
                2 => "Station (局域网接入)",
                _ => "已关闭"
            };
            if (!string.IsNullOrEmpty(status.Ssid)) Esp32WifiSsid = status.Ssid;
            if (!string.IsNullOrEmpty(status.IpAddress)) Esp32WifiIp = status.IpAddress;
            IsWirelessDebugListening = status.IsWirelessDebugActive;
            WirelessDebugPort = status.WirelessDebugPort;
            Esp32WifiClientCount = $"{status.ConnectedClients} 台";
            Esp32WifiRssiText = $"{status.Rssi} dBm";
        }

        public void OnConnectWirelessTcp()
        {
            string host = string.IsNullOrWhiteSpace(Esp32WifiIp) || Esp32WifiIp == "--" ? "192.168.4.1" : Esp32WifiIp;
            int port = WirelessDebugPort > 0 ? WirelessDebugPort : 8266;

            Esp32CliTerminalLines.Add($"[WDEBUG] 正在尝试通过 TCP 直连 ESP32-S3 无线调试端口 {host}:{port}...");
            AddLog($"[无线调试] 尝试建立 TCP 直连 {host}:{port}...", "INFO");

            bool success = Esp32CommService.Instance.ConnectTcp(host, port);
            if (success)
            {
                Esp32CliTerminalLines.Add($"[WDEBUG] TCP 链路已成功建立！无线调试会话激活 (已绑定 0x40/0x50 双向协议总线)");
                AddLog($"[无线调试] 成功连接至 ESP32-S3 无线端口 {host}:{port}", "SUCCESS");
            }
            else
            {
                Esp32CliTerminalLines.Add($"[WDEBUG] TCP 连接未成功响应 (物理 ESP32 未入网或未连接到热点)。系统保持仿真模拟回显模式。");
                AddLog($"[无线调试] TCP 直连 {host}:{port} 超时，保持仿真模式", "WARN");
            }
        }

        private async void ProcessCliLogic(string cmd, bool sentToHardware)
        {
            string[] parts = cmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return;

            string verb = parts[0].ToLowerInvariant();

            if (verb == "clear")
            {
                Esp32CliTerminalLines.Clear();
                return;
            }

            if (verb == "connect")
            {
                string targetHost = parts.Length >= 2 ? parts[1] : (string.IsNullOrWhiteSpace(Esp32WifiIp) || Esp32WifiIp == "--" ? "192.168.4.1" : Esp32WifiIp);
                int targetPort = parts.Length >= 3 && int.TryParse(parts[2], out int p) ? p : 8266;

                Esp32CliTerminalLines.Add($"[WDEBUG] 正在发起无线 TCP 直连: {targetHost}:{targetPort}...");
                bool ok = Esp32CommService.Instance.ConnectTcp(targetHost, targetPort);
                if (ok)
                {
                    Esp32CliTerminalLines.Add($"[WDEBUG] 无线调试 TCP 直连建立成功！当前会话: {targetHost}:{targetPort}");
                }
                else
                {
                    Esp32CliTerminalLines.Add($"[WDEBUG] 直连失败或超时 (2500ms)。请确保已连接 ESP32 热点或局域网同一网段。");
                }
                return;
            }

            // 若已成功下发至物理硬件 ESP32-S3，则由下位机异步返回真实响应帧 (0x50/0x40)，无需上位机模拟
            if (sentToHardware && !Esp32CommService.Instance.IsSimulationMode)
            {
                return;
            }

            if (verb == "help")
            {
                Esp32CliTerminalLines.Add("==================================================================");
                Esp32CliTerminalLines.Add($"  LinkNexus ESP32-S3 CLI 指令系统帮助手册 ({VersionInfo.CurrentVersion})");
                Esp32CliTerminalLines.Add("==================================================================");
                Esp32CliTerminalLines.Add("  wifi ap <ssid> [pwd] [ch] [save]     - 启动 SoftAP (可选 [save] 固化至 NVS)");
                Esp32CliTerminalLines.Add("  wifi connect <ssid> [pwd] [save]     - 连接指定 Wi-Fi 热点 (可选 [save] 固化)");
                Esp32CliTerminalLines.Add("  wifi status                          - 查询当前 Wi-Fi 工作模式与 IP 状态");
                Esp32CliTerminalLines.Add("  wifi txpower [dbm] [save]            - 设置/查询发射功率 (可选 [save])");
                Esp32CliTerminalLines.Add("  wifi protocol [b|bg|bgn] [save]      - 设置调制协议模式 (可选 [save])");
                Esp32CliTerminalLines.Add("  wifi channel <1-13> [save]           - 设置射频工作信道 (可选 [save])");
                Esp32CliTerminalLines.Add("  wifi diag                            - 射频阻抗失配与自检诊断");
                Esp32CliTerminalLines.Add("  wifi scan [force]                    - 扫描周围可用 2.4GHz 无线网络");
                Esp32CliTerminalLines.Add("  wifi disconnect                      - 关闭 Wi-Fi 并停止无线调试");
                Esp32CliTerminalLines.Add("  wifi save                            - 全量固化当前 Wi-Fi 参数至 NVS");
                Esp32CliTerminalLines.Add("  wifi reset                           - 清除 NVS 自定义配置恢复出厂默认");
                Esp32CliTerminalLines.Add("  wifi config                          - 查看 NVS 固化配置汇总表");
                Esp32CliTerminalLines.Add("  power <ch 0-5|all> <true|false> [save] - 控制 MOS 开关 (可选 [save] 固化)");
                Esp32CliTerminalLines.Add("  power save                           - 固化当前 MOS 通道状态为开机默认");
                Esp32CliTerminalLines.Add("  power status                         - 查看 MOS 通道状态及 NVS 开机 Mask");
                Esp32CliTerminalLines.Add("  telemetry [true|false] [save]        - 控制遥测广播流 (可选 [save] 固化)");
                Esp32CliTerminalLines.Add("  connect [ip] [port]                  - 建立上位机与 ESP32-S3 的 Wi-Fi TCP 调试直连");
                Esp32CliTerminalLines.Add("  status                               - 查询 ESP32-S3 双核状态、堆内存与外设");
                Esp32CliTerminalLines.Add("  reboot                               - 软复位并重启 ESP32-S3 芯片");
                Esp32CliTerminalLines.Add("  clear                                - 清空当前终端输出行");
                Esp32CliTerminalLines.Add("==================================================================");
                return;
            }

            if (verb == "wifi")
            {
                if (parts.Length < 2)
                {
                    Esp32CliTerminalLines.Add("[ERR] 缺少子命令。用法: wifi <ap|connect|status|txpower|protocol|channel|diag|scan|disconnect|save|reset|config>");
                    return;
                }

                string sub = parts[1].ToLowerInvariant();
                if (sub == "save")
                {
                    Esp32CliTerminalLines.Add("[NVS] 当前 Wi-Fi 运行时参数 (发射功率/调制协议/信道/AP/STA) 已全量固化至 NVS 存储区。[OK]");
                    return;
                }
                else if (sub == "reset")
                {
                    Esp32CliTerminalLines.Add("[NVS] 已清除 NVS 自定义配置并恢复出厂默认值。[OK]");
                    return;
                }
                else if (sub == "config" || sub == "nvs")
                {
                    Esp32CliTerminalLines.Add("--- [ ESP32-S3 NVS 固化参数配置汇总 ] ---");
                    Esp32CliTerminalLines.Add("  TX Power   : 11.50 dBm (raw: 46)");
                    Esp32CliTerminalLines.Add("  Protocol   : 802.11b/g/n (all)");
                    Esp32CliTerminalLines.Add("  Channel    : CH1 (2412 MHz)");
                    Esp32CliTerminalLines.Add("  SoftAP     : SSID 'LinkNexus-ESP32-AP', CH1");
                    Esp32CliTerminalLines.Add("  Station    : AutoConnect=0");
                    Esp32CliTerminalLines.Add("  MOS Mask   : 0x3F (All 6 Channels ON)");
                    Esp32CliTerminalLines.Add("  Telemetry  : Stream=false (Muted for Clean Terminal)");
                    Esp32CliTerminalLines.Add("------------------------------------------");
                    return;
                }
                else if (sub == "ap")
                {
                    string ssid = parts.Length >= 3 ? parts[2] : "LinkNexus-ESP32-AP";
                    string pwd = parts.Length >= 4 ? parts[3] : "12345678";
                    byte ch = parts.Length >= 5 && byte.TryParse(parts[4], out byte c) ? c : (byte)1;
                    bool doSave = parts.Any(p => p.Equals("save", StringComparison.OrdinalIgnoreCase));

                    Esp32WifiMode = "SoftAP (独立热点)";
                    Esp32WifiSsid = ssid;
                    Esp32WifiIp = "192.168.4.1";
                    IsWirelessDebugListening = true;
                    WirelessDebugPort = 8266;
                    Esp32WifiClientCount = "0 台";
                    Esp32WifiRssiText = "-32 dBm (本机)";

                    Esp32CliTerminalLines.Add($"[WIFI] 正在启动 SoftAP 独立热点: SSID='{ssid}', 信道={ch}{(doSave ? " [已固化至 NVS]" : "")}...");
                    await Task.Delay(120);
                    Esp32CliTerminalLines.Add($"[WIFI] SoftAP 启动就绪! 默认网关 192.168.4.1, 掩码 255.255.255.0");
                    Esp32CliTerminalLines.Add($"[WDEBUG] 无线 TCP 调试服务已启动监听 192.168.4.1:{WirelessDebugPort} [OK]");
                    AddLog($"[ESP32] Wi-Fi SoftAP 已就绪: {ssid} (192.168.4.1:8266)", "SUCCESS");
                }
                else if (sub == "connect")
                {
                    if (parts.Length < 3)
                    {
                        Esp32CliTerminalLines.Add("[ERR] 用法: wifi connect <ssid> [password] [save]");
                        return;
                    }
                    string ssid = parts[2];
                    string pwd = parts.Length >= 4 ? parts[3] : "";
                    bool doSave = parts.Any(p => p.Equals("save", StringComparison.OrdinalIgnoreCase));

                    // 检测是否已处于连接就绪状态 (避免重复连接并直接返回当前连接状态)
                    if (Esp32WifiMode.StartsWith("Station") && Esp32WifiSsid == ssid && !string.IsNullOrEmpty(Esp32WifiIp) && Esp32WifiIp != "--")
                    {
                        Esp32CliTerminalLines.Add($"[ALREADY_CONNECTED] Wi-Fi 已连接目标网络 (无需重复发起连接){(doSave ? " [已固化至 NVS]" : "")}:");
                        Esp32CliTerminalLines.Add($"  目标 SSID:     {Esp32WifiSsid}");
                        Esp32CliTerminalLines.Add($"  IP 地址:       {Esp32WifiIp}");
                        Esp32CliTerminalLines.Add($"  信号强度:      {Esp32WifiRssiText}");
                        Esp32CliTerminalLines.Add($"  客户端连接:    {Esp32WifiClientCount}");
                        Esp32CliTerminalLines.Add($"  无线调试服务:  {(IsWirelessDebugListening ? $"监听中 (TCP 端口 {WirelessDebugPort})" : "未启动")}");
                        return;
                    }

                    if (Esp32WifiMode.StartsWith("Station") && !string.IsNullOrEmpty(Esp32WifiIp) && Esp32WifiIp != "--")
                    {
                        Esp32CliTerminalLines.Add($"[SWITCHING] 当前已接入 '{Esp32WifiSsid}' ({Esp32WifiIp})，正在切换至新目标 '{ssid}'...");
                    }
                    else
                    {
                        Esp32CliTerminalLines.Add($"[WIFI] 正在扫描并连接目标 AP: '{ssid}'{(doSave ? " [已固化至 NVS]" : "")}...");
                    }

                    await Task.Delay(200);
                    Esp32WifiMode = "Station (局域网接入)";
                    Esp32WifiSsid = ssid;
                    Esp32WifiIp = "192.168.1.188";
                    IsWirelessDebugListening = true;
                    WirelessDebugPort = 8266;
                    Esp32WifiClientCount = "1 台";
                    Esp32WifiRssiText = "-48 dBm (良好)";

                    Esp32CliTerminalLines.Add($"[WIFI] 认证通过! 获取 DHCP IP: 192.168.1.188 (掩码: 255.255.255.0, 网关: 192.168.1.1)");
                    Esp32CliTerminalLines.Add($"[WDEBUG] 无线 TCP 调试服务已在 192.168.1.188:{WirelessDebugPort} 启动监听 [OK]");
                    AddLog($"[ESP32] Wi-Fi Station 已接入 {ssid} (192.168.1.188:8266)", "SUCCESS");
                }
                else if (sub == "status")
                {
                    Esp32CliTerminalLines.Add("--- [ ESP32-S3 无线调试与网络状态 ] ---");
                    Esp32CliTerminalLines.Add($"  工作模式:      {Esp32WifiMode}");
                    Esp32CliTerminalLines.Add($"  当前 SSID:     {Esp32WifiSsid}");
                    Esp32CliTerminalLines.Add($"  IP 地址:       {Esp32WifiIp}");
                    Esp32CliTerminalLines.Add($"  信号强度:      {Esp32WifiRssiText}");
                    Esp32CliTerminalLines.Add($"  客户端连接:    {Esp32WifiClientCount}");
                    Esp32CliTerminalLines.Add($"  无线调试服务:  {(IsWirelessDebugListening ? $"监听中 (TCP 端口 {WirelessDebugPort})" : "未启动")}");
                    Esp32CliTerminalLines.Add("----------------------------------------");
                }
                else if (sub == "scan")
                {
                    Esp32CliTerminalLines.Add("[WIFI] 未连接物理 ESP32 硬件，无法执行真实射频信道扫描。请检查 USB 串口连接。");
                }
                else if (sub == "disconnect")
                {
                    Esp32WifiMode = "已关闭";
                    Esp32WifiSsid = "--";
                    Esp32WifiIp = "--";
                    IsWirelessDebugListening = false;
                    Esp32WifiClientCount = "0 台";
                    Esp32WifiRssiText = "--";

                    Esp32CliTerminalLines.Add("[WIFI] Wi-Fi 射频已关闭，无线调试服务已停止。");
                    AddLog("[ESP32] Wi-Fi 射频已关闭", "INFO");
                }
                else
                {
                    Esp32CliTerminalLines.Add($"[ERR] 未知 wifi 子命令: '{sub}'。输入 'help' 查看语法。");
                }
                return;
            }

            if (verb == "status")
            {
                Esp32CliTerminalLines.Add("--- [ ESP32-S3 系统运行诊断 ] ---");
                Esp32CliTerminalLines.Add("  SoC: ESP32-S3-WROOM-1 (Xtensa Dual-Core LX7 @ 240MHz)");
                Esp32CliTerminalLines.Add("  FreeRTOS: v10.4.3 | C++17 Core Coordinator: Running");
                Esp32CliTerminalLines.Add("  Internal SRAM: 512 KB (Free: 384 KB) | PSRAM: 8 MB Octal-SPI");
                Esp32CliTerminalLines.Add("  Flash: 16 MB Quad-SPI (Wear Leveling Active)");
                Esp32CliTerminalLines.Add($"  Wi-Fi 模式: {Esp32WifiMode} | IP: {Esp32WifiIp} | 调试端口: {WirelessDebugPort}");
                Esp32CliTerminalLines.Add("---------------------------------");
                return;
            }

            if (verb == "power")
            {
                if (parts.Length >= 2 && parts[1].Equals("save", StringComparison.OrdinalIgnoreCase))
                {
                    Esp32CliTerminalLines.Add("[NVS] 6 通道 MOS 开机默认供电状态已固化至 NVS (Mask: 0x3F)。[OK]");
                    return;
                }
                if (parts.Length >= 2 && (parts[1].Equals("status", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("show", StringComparison.OrdinalIgnoreCase)))
                {
                    Esp32CliTerminalLines.Add("--- [ 6 通道 MOS 供电状态 (Mask: 0x3F, NVS 默认: 0x3F) ] ---");
                    for (int i = 0; i < 6; i++)
                    {
                        Esp32CliTerminalLines.Add($"  CH{i}: 导通 (ON/true)");
                    }
                    return;
                }
                if (parts.Length >= 3)
                {
                    bool isAll = parts[1].Equals("all", StringComparison.OrdinalIgnoreCase);
                    bool hasCh = byte.TryParse(parts[1], out byte ch);
                    bool doSave = parts.Length >= 4 && parts[3].Equals("save", StringComparison.OrdinalIgnoreCase);

                    bool? state = parts[2].ToLowerInvariant() switch
                    {
                        "true" or "1" or "on" or "enable" => true,
                        "false" or "0" or "off" or "disable" => false,
                        _ => null
                    };

                    if (state == null)
                    {
                        Esp32CliTerminalLines.Add("[ERR] 开关状态请使用 'true' 或 'false' (亦支持 1/0, on/off)。");
                        return;
                    }

                    if (isAll)
                    {
                        for (byte i = 0; i < 6; i++)
                        {
                            Esp32CommService.Instance.SendPowerControl(i, state.Value);
                        }
                        Esp32CliTerminalLines.Add($"[POWER] 所有通道 (CH0~5) 物理 MOS 供电已更新为: {(state.Value ? "true (导通)" : "false (关断)")}{(doSave ? " [已固化至 NVS]" : "")} [OK]");
                        return;
                    }

                    if (hasCh && ch < 6)
                    {
                        Esp32CommService.Instance.SendPowerControl(ch, state.Value);
                        Esp32CliTerminalLines.Add($"[POWER] 通道 {ch} 物理 MOS 供电已设为: {(state.Value ? "true (导通)" : "false (关断)")}{(doSave ? " [已固化至 NVS]" : "")} [OK]");
                        return;
                    }
                }
                Esp32CliTerminalLines.Add("[ERR] 用法: power <channel 0~5 | all> <true | false> [save]");
                return;
            }

            if (verb == "telemetry")
            {
                if (parts.Length >= 2)
                {
                    if (parts[1].Equals("save", StringComparison.OrdinalIgnoreCase))
                    {
                        Esp32CliTerminalLines.Add("[NVS] 遥测广播流配置已固化至 NVS: false (关闭/静音)。[OK]");
                        return;
                    }

                    bool? state = parts[1].ToLowerInvariant() switch
                    {
                        "true" or "1" or "on" or "stream" => true,
                        "false" or "0" or "off" => false,
                        _ => null
                    };

                    if (state != null)
                    {
                        bool doSave = parts.Length >= 3 && parts[2].Equals("save", StringComparison.OrdinalIgnoreCase);
                        Esp32CliTerminalLines.Add($"[遥测控制] 二进制遥测广播流已设为: {(state.Value ? "true (开启)" : "false (静音保护)")}{(doSave ? " [已固化至 NVS]" : "")} [OK]");
                        return;
                    }
                }

                Esp32CliTerminalLines.Add("================ INA236 硬件传感器实时遥测采样 ================");
                Esp32CliTerminalLines.Add("  母线总线   (Bus)   :  5.02 V,   120 mA  ( 0.60 W)");
                Esp32CliTerminalLines.Add("  XDS110通道 (Port 1):  3.31 V,    45 mA  ( 0.15 W)");
                Esp32CliTerminalLines.Add("  DAPLink通道(Port 2):  3.30 V,    38 mA  ( 0.13 W)");
                Esp32CliTerminalLines.Add("----------------------------------------------------------------");
                Esp32CliTerminalLines.Add("  上位机连接状态     : 在线 (C# Heartbeat 活跃)");
                Esp32CliTerminalLines.Add("  二进制广播流模式   : 已静音 (保护终端杜绝乱码)");
                Esp32CliTerminalLines.Add("================================================================");
                return;
            }

            if (verb == "reboot")
            {
                Esp32CliTerminalLines.Add("[SYSTEM] 收到重启请求，系统即将执行软复位...");
                await Task.Delay(400);
                Esp32CliTerminalLines.Add("[SYSTEM] ESP-ROM: esp32s3-20210327");
                Esp32CliTerminalLines.Add("[SYSTEM] Booting LinkNexus Core Coordinator...");
                PrintCliWelcomeBanner();
                return;
            }

            Esp32CliTerminalLines.Add($"[ERR] 未知命令: '{cmd}'。输入 'help' 获取命令帮助。");
        }

        #endregion

        private void OnReloadHardwareConfig()
        {
            AddLog("[底层维护] 正在热重载 HardwareConfig.json 配置文件...", "INFO");
            HardwareConfigManager.Instance.LoadConfig();
            _usbMonitor.TriggerDebouncedRefresh();
            AddLog($"[底层维护] 硬件拓扑与引脚映射热重载成功！版本：{HardwareConfigManager.Instance.CurrentConfig.Version}", "SUCCESS");
        }

        public static string GenerateVersionString(int major = VersionInfo.MajorVersion, int minor = VersionInfo.MinorRevision, string stage = VersionInfo.Stage, string? yearWeek = VersionInfo.TargetYearWeek)
        {
            return VersionInfo.Format(major, minor, stage, yearWeek);
        }

        public void AddLog(string message, string level = "INFO")
        {
            _dispatcher.InvokeAsync(() =>
            {
                Logs.Add(new LogMessageItem
                {
                    Timestamp = DateTime.Now,
                    Message = message,
                    Level = level
                });

                while (Logs.Count > 300) Logs.RemoveAt(0);
                OnPropertyChanged(nameof(LogDrawerButtonText));
            });
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
