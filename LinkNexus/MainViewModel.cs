using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;

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

        // ESP32-S3 核心主控状态
        private bool _isEsp32Online = false;
        private PortDeviceModel? _esp32DeviceModel;
        private string _daemonStatusDotColor = "#EF4444"; // 默认未连接为红色
        private string _daemonStatusText = "LinkNexus 物理拓扑守护服务离线 (ESP32-S3 核心主控未连接)";

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

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 主界面顶部展示的 CH338X 下行核心设备集合 (FT2232, CH343P, XDS110, DAP，Debug模式包含ESP32)
        /// </summary>
        public ObservableCollection<PortDeviceModel> ConnectedDevices { get; } = new();

        /// <summary>
        /// 抽屉内收纳的 Windows 识别到的其他 USB 外部设备 (U盘、其它下行插口外设等)
        /// </summary>
        public ObservableCollection<PortDeviceModel> OtherDevices { get; } = new();

        public bool HasConnectedDevices => ConnectedDevices.Count > 0;
        public bool HasOtherDevices => OtherDevices.Count > 0;

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
                }
            }
        }

        public string OtherDevicesDrawerButtonText =>
            $"📂 系统其他设备 ({OtherDevices.Count}) {(IsOtherDevicesDrawerOpen ? "▴ 收起抽屉" : "▾ 展开抽屉")}";

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
                    }
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsBurnerWorkspaceVisible));
                    OnPropertyChanged(nameof(IsUartWorkspaceVisible));
                    OnPropertyChanged(nameof(IsEsp32WorkspaceVisible));
                    OnPropertyChanged(nameof(IsGenericWorkspaceVisible));
                    OnPropertyChanged(nameof(IsLogsWorkspaceVisible));
                }
            }
        }

        public bool IsBurnerWorkspaceVisible => SelectedDevice?.IsBurnerDevice ?? false;
        public bool IsUartWorkspaceVisible => SelectedDevice?.IsUartDevice ?? false;
        public bool IsEsp32WorkspaceVisible => SelectedDevice?.IsEsp32Device ?? false;
        public bool IsGenericWorkspaceVisible => SelectedDevice != null && !SelectedDevice.IsBurnerDevice && !SelectedDevice.IsUartDevice && !SelectedDevice.IsEsp32Device;
        public bool IsLogsWorkspaceVisible => SelectedDevice == null;

        #endregion

        #region ESP32 守护服务与版本号

        public bool IsEsp32Online
        {
            get => _isEsp32Online;
            set { if (_isEsp32Online != value) { _isEsp32Online = value; OnPropertyChanged(); } }
        }

        public string VersionString => GenerateVersionString(1, 0, "PRE");

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
            ? "🛑 断开虚拟硬件接入 (收起虚拟设备)"
            : "🚀 开启虚拟硬件接入 (弹出 4 大核心引擎)";

        public string VirtualDevicesStatusText => IsVirtualDevicesEnabled
            ? "🟢 虚拟硬件已就绪：已接入 FT2232、CH343P、XDS110、DAPLink 仿真设备 (可直接测试全套功能)"
            : "⚪ 虚拟硬件未接入：点击按钮弹出 4 大核心硬件进行免接线在线功能测试";

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
            set { if (_selectedBaudRate != value) { _selectedBaudRate = value; OnPropertyChanged(); } }
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
        private string _detectedInputType = "🤖 智能识别：请输入 Shell 命令、HEX 数据或字符串...";
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

        #endregion

        public MainViewModel(UsbMonitorService usbMonitor)
        {
            _usbMonitor = usbMonitor ?? throw new ArgumentNullException(nameof(usbMonitor));
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            _usbMonitor.DevicesRefreshed += OnUsbDevicesRefreshed;
            _usbMonitor.LogMessage += msg => AddLog(msg, "INFO");

            // 基础命令绑定
            VersionClickCommand = new RelayCommand(OnVersionClicked);
            TriggerScanCommand = new RelayCommand(() => _usbMonitor.TriggerDebouncedRefresh());
            ClearLogsCommand = new RelayCommand(() => Logs.Clear());
            BrowseFileCommand = new RelayCommand(OnBrowseFile);
            StartPreviewFlashCommand = new RelayCommand(OnStartPreviewFlash);
            ReadSectorCommand = new RelayCommand(OnReadSector);
            ChipEraseCommand = new RelayCommand(OnChipErase);
            ReadCoreIdCommand = new RelayCommand(OnReadCoreId);
            ResetTargetCommand = new RelayCommand(OnResetTarget);

            ToggleSerialPortCommand = new RelayCommand(OnToggleSerialPort);
            SendSerialTextCommand = new RelayCommand(OnSendSerialText);
            ClearSerialMonitorCommand = new RelayCommand(() => SerialMonitorLines.Clear());
            SendLinuxCliCommand = new RelayCommand(OnSendLinuxCli);
            InjectCliPresetCommand = new RelayCommand(p => OnInjectCliPreset(p?.ToString()));
            SelectDeviceCommand = new RelayCommand(p =>
            {
                if (p is PortDeviceModel dev) SelectedDevice = dev;
            });
            ReloadConfigCommand = new RelayCommand(OnReloadHardwareConfig);
            ToggleOtherDevicesDrawerCommand = new RelayCommand(() => IsOtherDevicesDrawerOpen = !IsOtherDevicesDrawerOpen);
            ExitDebugModeCommand = new RelayCommand(() => IsDebugMode = false);
            ToggleVirtualDevicesCommand = new RelayCommand(OnToggleVirtualDevices);
            SelectVirtualDeviceCommand = new RelayCommand(p =>
            {
                if (p is PortDeviceModel dev) SelectedVirtualDevice = dev;
            });

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

            InitDefaultDumpsAndCli();

            AddLog($"LinkNexus 工业控制台已启动，加载规范版本号：{VersionString}", "SUCCESS");
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
                    IsEsp32Online = false;
                    DaemonStatusDotColor = "#F59E0B"; // 黄色：枚举异常 / 驱动未就绪
                    DaemonStatusText = $"LinkNexus 物理拓扑守护服务异常 (ESP32 主控驱动未就绪 Code {espDev.ConfigManagerErrorCode})";
                }
                else
                {
                    IsEsp32Online = true;
                    DaemonStatusDotColor = "#10B981"; // 绿色：正常就绪
                    DaemonStatusText = $"LinkNexus 物理拓扑守护服务正常运行中 (ESP32-S3 主控在线 | {espDev.ComPort})";
                }

                _esp32DeviceModel = new PortDeviceModel
                {
                    DeviceName = string.IsNullOrWhiteSpace(espDev.Name) ? "ESP32-S3 核心通信主控" : espDev.Name,
                    DeviceDescription = espDev.Description,
                    VidPid = espDev.VidPid,
                    ComPort = string.IsNullOrWhiteSpace(espDev.ComPort) ? "COM9" : espDev.ComPort,
                    SerialNumber = string.IsNullOrWhiteSpace(espDev.SerialNumber) ? "ESP32-S3-MAIN-001" : espDev.SerialNumber,
                    HardwarePath = espDev.HardwarePath,
                    State = espDev.HasDriverIssue ? DeviceState.Warning : DeviceState.Ready,
                    FunctionType = DeviceFunctionType.Controller_ESP32,
                    StatusMessage = "ESP32-S3 核心主控链路正常，管理协议栈就绪"
                };
            }
            else
            {
                IsEsp32Online = false;
                _esp32DeviceModel = null;
                DaemonStatusDotColor = "#EF4444"; // 红色：未连接
                DaemonStatusText = "LinkNexus 物理拓扑守护服务离线 (ESP32-S3 核心主控未连接)";
            }

            // 2. 分离设备：
            // A) CH338X 下行的核心设备 (FT2232, CH343P, XDS110, DAP)
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

            OnPropertyChanged(nameof(HasConnectedDevices));
            OnPropertyChanged(nameof(HasOtherDevices));
            OnPropertyChanged(nameof(OtherDevicesDrawerButtonText));

            // 保持当前选中的设备或默认选中第一个
            if (SelectedDevice == null || (!ConnectedDevices.Contains(SelectedDevice) && !OtherDevices.Contains(SelectedDevice)))
            {
                SelectedDevice = ConnectedDevices.FirstOrDefault() ?? OtherDevices.FirstOrDefault();
            }
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
                    ? $"⚠️ 驱动未就绪 (Code {dev.ConfigManagerErrorCode})"
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
                card.FunctionType = DeviceFunctionType.Burner_DAPLink;
            }
            // 3. XDS110 (TM4C1294) 仿真器 (CH338X 下行)
            else if ((vid == "0451" && (pid == "BEF3" || pid == "BEF2" || pid == "BEF0")) || nameUpper.Contains("XDS110") || nameUpper.Contains("TM4C"))
            {
                card.FunctionType = DeviceFunctionType.Burner_XDS110;
            }
            // 4. CH343P / 板载高速串口 (CH338X 下行)
            else if ((vid == "1A86" && (pid == "55D3" || pid == "7523" || pid == "5523")) ||
                     nameUpper.Contains("CH343") || nameUpper.Contains("CH340") || nameUpper.Contains("CP210"))
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
                    DeviceName = "Texas Instruments XDS110 Probe (虚拟仿真)",
                    DeviceDescription = "TM4C1294 ARM Cortex-M 仿真烧录探针",
                    VidPid = "0451:BEF3",
                    ComPort = "COM5",
                    SerialNumber = "XDS110-VIRT-003",
                    HardwarePath = @"USB\VID_0451&PID_BEF3\XDS110-VIRT-003",
                    State = DeviceState.Ready,
                    FunctionType = DeviceFunctionType.Burner_XDS110,
                    IsVirtual = true,
                    StatusMessage = "虚拟探针正常就绪 (仿真模式，支持 CoreID 读取/Flash 快速擦除)"
                };
                xds110Virt.SelectedChanged += OnVirtualCardSelected;

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
                VirtualDevices.Add(dapVirt);

                IsVirtualDevicesEnabled = true;
                SelectedVirtualDevice = ft2232Virt;

                AddLog("【虚拟接入功能】成功弹出 4 个虚拟核心调试引擎：FT2232、CH343P、XDS110、DAPLink！", "SUCCESS");
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
            IsPortOpen = !IsPortOpen;
            if (IsPortOpen)
            {
                AddLog($"【串口服务】已打开 {SelectedDevice?.ComPort ?? "COM4"} | 波特率: {SelectedBaudRate} 8-N-1 | DTR: {(IsDtrEnable ? 1 : 0)} RTS: {(IsRtsEnable ? 1 : 0)}", "SUCCESS");
                SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [系统] 串口 {SelectedDevice?.ComPort ?? "COM4"} 已打开，波特率：{SelectedBaudRate}，硬件流控引脚已初始化就绪。");
            }
            else
            {
                AddLog($"【串口服务】已关闭 {SelectedDevice?.ComPort ?? "COM4"}", "INFO");
                SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [系统] 串口已关闭。");
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
                await Task.Delay(100);
                // 释放复位电平
                IsDtrEnable = true;
                IsRtsEnable = false;
                await Task.Delay(50);
                IsRtsEnable = true;
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
                DetectedInputType = "🤖 智能识别：请输入 Shell 命令、HEX 数据或字符串...";
                DetectedInputBadgeColor = "#71717A";
                _detectedCategory = InputRecognizedCategory.PlainText;
                return;
            }

            string text = _smartInputBuffer.Trim();

            if (_inputModeOverrideIndex == 1)
            {
                DetectedInputType = "🔢 强制模式：十六进制数据帧 (HEX Mode)";
                DetectedInputBadgeColor = "#F59E0B";
                _detectedCategory = InputRecognizedCategory.Hex;
                return;
            }
            if (_inputModeOverrideIndex == 2)
            {
                DetectedInputType = "🐧 强制模式：Linux 卡片机 CLI 指令 (Shell Mode)";
                DetectedInputBadgeColor = "#38BDF8";
                _detectedCategory = InputRecognizedCategory.LinuxCli;
                return;
            }
            if (_inputModeOverrideIndex == 3)
            {
                DetectedInputType = "📝 强制模式：标准 ASCII 文本 (Raw Text Mode)";
                DetectedInputBadgeColor = "#A1A1AA";
                _detectedCategory = InputRecognizedCategory.PlainText;
                return;
            }

            // 智能自动识别
            if (text.StartsWith("HEX:", StringComparison.OrdinalIgnoreCase) || IsHexString(text))
            {
                DetectedInputType = "🔢 自动识别：十六进制数据帧 (HEX Frame) ➜ 将按原始二进制字节流发送";
                DetectedInputBadgeColor = "#F59E0B";
                _detectedCategory = InputRecognizedCategory.Hex;
            }
            else if (text.StartsWith("AT", StringComparison.OrdinalIgnoreCase))
            {
                DetectedInputType = "📡 自动识别：AT 调制解调指令 (Modem Command) ➜ 自动追加 \\r\\n 握手";
                DetectedInputBadgeColor = "#10B981";
                _detectedCategory = InputRecognizedCategory.AtCommand;
            }
            else if (IsLinuxCommand(text))
            {
                DetectedInputType = "🐧 自动识别：Linux 卡片机 CLI 指令 ➜ 将执行 Shell 终端交互与回显";
                DetectedInputBadgeColor = "#38BDF8";
                _detectedCategory = InputRecognizedCategory.LinuxCli;
            }
            else
            {
                DetectedInputType = "📝 自动识别：标准 ASCII 串口字符串 ➜ 普通串口双向透传";
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

            string hexFormatted = string.Join(" ", byteList.Select(b => b.ToString("X2")));
            int count = byteList.Count;
            SerialTxBytes += count;

            string prefix = IsTimestampEnabled ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
            SerialMonitorLines.Add($"{prefix}[Tx HEX] -> {hexFormatted} ({count} 字节)");
            AddLog($"[串口通信] [HEX 发送] {hexFormatted} (共 {count} 字节)", "INFO");

            // 仿真下位机响应
            _dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(40);
                string rxPrefix = IsTimestampEnabled ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
                string respHex = byteList[0] == 0xAA ? "AA 55 06 00 00 05" : $"06 {hexFormatted}";
                int rxCount = respHex.Split(' ').Length;
                SerialRxBytes += rxCount;
                SerialMonitorLines.Add($"{rxPrefix}[Rx HEX] <- {respHex} (ACK OK)");
                while (SerialMonitorLines.Count > 150) SerialMonitorLines.RemoveAt(0);
            });
        }

        private void ProcessSendLinuxCli(string cmd)
        {
            int bytesCount = System.Text.Encoding.UTF8.GetByteCount(cmd) + 1;
            SerialTxBytes += bytesCount;

            LinuxCliTerminalLines.Add($"root@linknexus-board:~# {cmd}");
            string prefix = IsTimestampEnabled ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
            SerialMonitorLines.Add($"{prefix}[Tx CLI] -> {cmd}");

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

        private void ProcessSendAtCommand(string cmd)
        {
            int bytesCount = System.Text.Encoding.UTF8.GetByteCount(cmd) + 2;
            SerialTxBytes += bytesCount;

            string prefix = IsTimestampEnabled ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
            SerialMonitorLines.Add($"{prefix}[Tx AT] -> {cmd}\\r\\n");

            _dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(40);
                string rxPrefix = IsTimestampEnabled ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
                string response = cmd.Trim().ToUpperInvariant() switch
                {
                    "AT" => "OK",
                    "AT+SYSINFO?" => "+SYSINFO: CH343P-UART-CONTROLLER, BAUD=115200, FLOW=RTS/CTS, DTR=1",
                    "AT+VERSION?" => $"+VERSION: {VersionString}",
                    "AT+RST" => "OK\r\n[SYSTEM REBOOTING...]",
                    _ => "OK"
                };
                SerialRxBytes += System.Text.Encoding.UTF8.GetByteCount(response);
                SerialMonitorLines.Add($"{rxPrefix}[Rx AT] <- {response}");
                while (SerialMonitorLines.Count > 150) SerialMonitorLines.RemoveAt(0);
            });
        }

        private void ProcessSendPlainText(string text)
        {
            int bytesCount = System.Text.Encoding.UTF8.GetByteCount(text);
            SerialTxBytes += bytesCount;

            string prefix = IsTimestampEnabled ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
            SerialMonitorLines.Add($"{prefix}[Tx] -> {text}");

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

        private void OnReloadHardwareConfig()
        {
            AddLog("[底层维护] 正在热重载 HardwareConfig.json 配置文件...", "INFO");
            HardwareConfigManager.Instance.LoadConfig();
            _usbMonitor.TriggerDebouncedRefresh();
            AddLog($"[底层维护] 硬件拓扑与引脚映射热重载成功！版本：{HardwareConfigManager.Instance.CurrentConfig.Version}", "SUCCESS");
        }

        public static string GenerateVersionString(int major = 1, int minor = 0, string stage = "PRE")
        {
            var now = DateTime.Now;
            string xxMajor = (major % 100).ToString("D2");
            string xxMinor = (minor % 100).ToString("D2");
            string yy = (now.Year % 100).ToString("D2");
            var cal = CultureInfo.InvariantCulture.Calendar;
            int ww = cal.GetWeekOfYear(now, CalendarWeekRule.FirstFourDayWeek, DayOfWeek.Monday);

            return $"v{xxMajor}{xxMinor}.{yy}{ww:D2} ({stage})";
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
            });
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
