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
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string TimeString => Timestamp.ToString("HH:mm:ss.fff");
        public string Message { get; set; } = string.Empty;
        public string Level { get; set; } = "INFO"; // INFO, WARN, SUCCESS, ERROR
    }

    /// <summary>
    /// 载入的文件信息模型
    /// </summary>
    public class LoadedFileItem : INotifyPropertyChanged
    {
        private string _filePath = string.Empty;
        private string _fileName = "未放入任何文件 (支持拖入 .bin / .hex / .elf / .json)";
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
    /// LinkNexus 主视图模型
    /// 包含：物理端口阵列、真实 USB PnP 监听、VBUS 供电联锁、版本号规范、
    /// 开发者调试模式 (开关端口、文件放入、固件烧录与串口回环功能预览)
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly UsbMonitorService _usbMonitor;
        private readonly Dispatcher _dispatcher;

        // 连击 5 次时间戳滑动队列
        private readonly Queue<DateTime> _versionClickTimestamps = new();
        private const int SecretClickRequiredCount = 5;
        private const double SecretClickTimeWindowMs = 1500.0;

        private bool _isVbusPowerEnabled = false;
        private bool _isDebugMode = false;
        private double _vbusVoltage = 0.0;
        private double _vbusCurrent = 0.0;
        private string _activeOperationStatus = "系统待命就绪";

        // 固件烧录流水线预览状态
        private bool _isFlashingActive = false;
        private int _flashProgress = 0;
        private string _flashSpeedText = "0.0 KB/s";
        private string _flashStatusStep = "待命中 (等待启动烧录预览)";

        // 串口调试回环发送状态
        private string _serialSendBuffer = "AT+SYSINFO?";
        private long _serialTxBytes = 0;
        private long _serialRxBytes = 0;

        // 预览调试选项卡切换索引 (0: 烧录流水线, 1: 串口回环, 2: 动态供电负载, 3: 协议帧校验)
        private int _selectedPreviewTab = 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// 4 个规范排列的物理端口集合 (端口 #1 ~ 端口 #4)
        /// </summary>
        public ObservableCollection<PortDeviceModel> Ports { get; } = new();

        /// <summary>
        /// 实时硬件事件与诊断日志流
        /// </summary>
        public ObservableCollection<LogMessageItem> Logs { get; } = new();

        /// <summary>
        /// 虚拟串口回环监视行
        /// </summary>
        public ObservableCollection<string> SerialMonitorLines { get; } = new();

        /// <summary>
        /// 当前放入或载入的文件信息
        /// </summary>
        public LoadedFileItem CurrentLoadedFile { get; } = new();

        #region 版本号与开发者调试模式属性

        /// <summary>
        /// 严格符合规范的工程版本号格式：vXXxx.YYWW (XXX) -> 例如：v0100.2639 (PRE)
        /// </summary>
        public string VersionString => GenerateVersionString(1, 0, "PRE");

        /// <summary>
        /// 开发者调试模式 (DEBUG MODE) 开关
        /// 开启后允许：开关物理端口、放入固件文件、实时功能预览调试
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
                    OnPropertyChanged(nameof(DebugModeBadgeText));
                    OnPropertyChanged(nameof(DebugModeBadgeColor));

                    // 同步端口卡片内部的调试控制开关可见性
                    foreach (var port in Ports)
                    {
                        port.IsDebugMode = value;
                    }

                    if (value)
                    {
                        AddLog("【开发者调试模式】已激活：已解锁端口物理开关、文件拖拽放入与全功能预览调试控制台。", "SUCCESS");
                    }
                    else
                    {
                        AddLog("【开发者调试模式】已退出，界面恢复极简工业监控状态。", "INFO");
                    }
                }
            }
        }

        public string DebugModeBadgeText => IsDebugMode ? "🛠️ 开发者调试模式 (ON)" : "DEBUG 模式 (OFF)";
        public string DebugModeBadgeColor => IsDebugMode ? "#F59E0B" : "#71717A";

        #endregion

        #region VBUS 下行供电与硬件联锁属性

        /// <summary>
        /// 核心互锁条件：ESP32-S3 主控在线且正常就绪
        /// 只有端口 #4 (ESP32-S3) 状态为 Ready 时，才允许操作下行供电
        /// </summary>
        public bool IsVbusControlUnlocked => Ports.Count > 3 && Ports[3].State == DeviceState.Ready;

        public bool IsVbusPowerEnabled
        {
            get => _isVbusPowerEnabled;
            set
            {
                if (_isVbusPowerEnabled == value) return;

                // 硬件互锁强制校验
                if (value && !IsVbusControlUnlocked)
                {
                    AddLog("【安全互锁】拒绝开启下行供电：ESP32-S3 主控未联通就绪，硬件互锁强制关断 VBUS 输出！", "WARN");
                    _isVbusPowerEnabled = false;
                    OnPropertyChanged();
                    return;
                }

                _isVbusPowerEnabled = value;
                OnPropertyChanged();
                OnVbusToggled(value);
            }
        }

        public double VbusVoltage
        {
            get => _vbusVoltage;
            private set { if (Math.Abs(_vbusVoltage - value) > 0.001) { _vbusVoltage = value; OnPropertyChanged(); } }
        }

        public double VbusCurrent
        {
            get => _vbusCurrent;
            private set { if (Math.Abs(_vbusCurrent - value) > 0.001) { _vbusCurrent = value; OnPropertyChanged(); } }
        }

        public string VbusInterlockStatusText
        {
            get
            {
                if (!IsVbusControlUnlocked)
                {
                    return "🔒 硬件联锁已锁定：ESP32-S3 主控未连接，禁止操作输出";
                }
                return IsVbusPowerEnabled
                    ? "⚡ 供电输出已导通：5.0V / MOS 硬件使能开启"
                    : "⚪ 供电输出已关断：VBUS 处于安全隔离状态";
            }
        }

        public string ActiveOperationStatus
        {
            get => _activeOperationStatus;
            set { if (_activeOperationStatus != value) { _activeOperationStatus = value; OnPropertyChanged(); } }
        }

        #endregion

        #region 调试与功能预览属性

        public int SelectedPreviewTab
        {
            get => _selectedPreviewTab;
            set { if (_selectedPreviewTab != value) { _selectedPreviewTab = value; OnPropertyChanged(); } }
        }

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

        #endregion

        #region 命令定义

        public ICommand VersionClickCommand { get; }
        public ICommand ToggleDebugModeCommand { get; }
        public ICommand ToggleVbusCommand { get; }
        public ICommand BrowseFileCommand { get; }
        public ICommand StartPreviewFlashCommand { get; }
        public ICommand SendSerialTextCommand { get; }
        public ICommand ClearSerialMonitorCommand { get; }
        public ICommand SetSimulatedLoadCommand { get; }
        public ICommand SendTestProtocolFrameCommand { get; }
        public ICommand ReloadConfigCommand { get; }
        public ICommand TriggerScanCommand { get; }
        public ICommand ClearLogsCommand { get; }

        #endregion

        public MainViewModel(UsbMonitorService usbMonitor)
        {
            _usbMonitor = usbMonitor ?? throw new ArgumentNullException(nameof(usbMonitor));
            _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

            InitializePhysicalPorts();

            _usbMonitor.DevicesRefreshed += OnUsbDevicesRefreshed;
            _usbMonitor.LogMessage += msg => AddLog(msg, "INFO");

            // 命令绑定
            VersionClickCommand = new RelayCommand(OnVersionClicked);
            ToggleDebugModeCommand = new RelayCommand(() => IsDebugMode = !IsDebugMode);
            ToggleVbusCommand = new RelayCommand(() => IsVbusPowerEnabled = !IsVbusPowerEnabled);
            BrowseFileCommand = new RelayCommand(OnBrowseFile);
            StartPreviewFlashCommand = new RelayCommand(OnStartPreviewFlash);
            SendSerialTextCommand = new RelayCommand(OnSendSerialText);
            ClearSerialMonitorCommand = new RelayCommand(() => SerialMonitorLines.Clear());
            SetSimulatedLoadCommand = new RelayCommand(p => OnSetSimulatedLoad(p));
            SendTestProtocolFrameCommand = new RelayCommand(p => OnSendTestProtocolFrame(p));
            ReloadConfigCommand = new RelayCommand(OnReloadHardwareConfig);
            TriggerScanCommand = new RelayCommand(() => _usbMonitor.TriggerDebouncedRefresh());
            ClearLogsCommand = new RelayCommand(() => Logs.Clear());

            // 预填充默认串口回环初始提示
            SerialMonitorLines.Add("[系统] 虚拟串口 (VCP) 回环测试控制台已就绪。输入文本或十六进制即可实时预览收发。");

            AddLog($"LinkNexus 工业控制台已启动，加载规范版本号：{VersionString}", "SUCCESS");
            AddLog("硬件监控与 PnP 引擎就绪，等待总线拓扑枚举...", "INFO");
        }

        private void InitializePhysicalPorts()
        {
            Ports.Add(new PortDeviceModel
            {
                PortNumber = 1,
                TargetEngineName = "FT2232HQ 双通道串口/JTAG 调试器",
                EngineShortCode = "FT2232HQ",
                EngineDescription = "双通道 MPSSE JTAG/SWD 协议栈 + 高速虚拟串口 (VCP)",
                IsEsp32Master = false
            });

            Ports.Add(new PortDeviceModel
            {
                PortNumber = 2,
                TargetEngineName = "DAPLink (CH32V305) 仿真器",
                EngineShortCode = "DAPLink",
                EngineDescription = "CMSIS-DAP v2 高速硬件仿真链路 + 隔离调试串口",
                IsEsp32Master = false
            });

            Ports.Add(new PortDeviceModel
            {
                PortNumber = 3,
                TargetEngineName = "XDS110 (TM4C1294) 仿真器",
                EngineShortCode = "XDS110",
                EngineDescription = "TI ARM Cortex-M 专用隔离烧录探针 + JTAG/SWD 调试口",
                IsEsp32Master = false
            });

            Ports.Add(new PortDeviceModel
            {
                PortNumber = 4,
                TargetEngineName = "ESP32-S3 核心通信与控制主控",
                EngineShortCode = "ESP32-S3",
                EngineDescription = "全双工二进制管理总线 + 下行 VBUS 电源联锁保护控制器",
                IsEsp32Master = true
            });

            foreach (var port in Ports)
            {
                port.ResetToUnplugged();
                // 监听端口仿真切换，以便调试模式下切换端口 #4 时实时触发 VBUS 联锁更新
                port.StateToggled += _ => ValidateVbusInterlock();
            }
        }

        private void OnUsbDevicesRefreshed(List<DiscoveredUsbDevice> devices)
        {
            _dispatcher.InvokeAsync(() => UpdatePortsWithDiscoveredDevices(devices));
        }

        private void UpdatePortsWithDiscoveredDevices(List<DiscoveredUsbDevice> devices)
        {
            // 端口 #1: FT2232HQ
            var ftDev = devices.FirstOrDefault(d =>
                d.Vid == "0403" && (d.Pid == "6010" || d.Pid == "6014" || d.Pid == "6001" || d.Name.Contains("FT2232", StringComparison.OrdinalIgnoreCase)));
            ApplyDeviceToPort(Ports[0], ftDev, "FT2232HQ");

            // 端口 #2: DAPLink
            var dapDev = devices.FirstOrDefault(d =>
                (d.Vid == "0D28" && d.Pid == "0204") ||
                (d.Vid == "1A86" && d.Pid == "7523") ||
                d.Name.Contains("DAPLink", StringComparison.OrdinalIgnoreCase) ||
                d.Name.Contains("CMSIS-DAP", StringComparison.OrdinalIgnoreCase));
            ApplyDeviceToPort(Ports[1], dapDev, "DAPLink");

            // 端口 #3: XDS110
            var xdsDev = devices.FirstOrDefault(d =>
                d.Vid == "0451" && (d.Pid == "BEF3" || d.Pid == "BEF2" || d.Pid == "BEF0" || d.Name.Contains("XDS110", StringComparison.OrdinalIgnoreCase)));
            ApplyDeviceToPort(Ports[2], xdsDev, "XDS110");

            // 端口 #4: ESP32-S3
            var espDev = devices.FirstOrDefault(d =>
                d.Vid == "303A" && (d.Pid == "1001" || d.Pid == "0002" || d.Name.Contains("ESP32", StringComparison.OrdinalIgnoreCase)));
            ApplyDeviceToPort(Ports[3], espDev, "ESP32-S3");

            ValidateVbusInterlock();
        }

        private void ApplyDeviceToPort(PortDeviceModel port, DiscoveredUsbDevice? dev, string defaultCode)
        {
            if (dev == null)
            {
                if (port.State != DeviceState.Unplugged)
                {
                    port.ResetToUnplugged();
                    AddLog($"[{port.PortLabel}] 设备已拔出，物理链路进入离线待机状态。", "INFO");
                }
                return;
            }

            if (dev.HasDriverIssue)
            {
                string warnMsg = dev.ConfigManagerErrorCode switch
                {
                    28 => "⚠️ 驱动未安装 (Code 28)",
                    10 => "⚠️ 设备无法启动 (Code 10)",
                    43 => "⚠️ 硬件故障停止 (Code 43)",
                    _ => $"⚠️ 驱动未就绪 / 异常设备 (Code {dev.ConfigManagerErrorCode})"
                };

                if (port.State != DeviceState.Warning || port.VidPid != dev.VidPid)
                {
                    port.SetWarning(dev.VidPid, warnMsg, dev.HardwarePath);
                    AddLog($"[{port.PortLabel}] 发现物理接入但驱动异常: {dev.Name} [{warnMsg}]", "WARN");
                }
            }
            else
            {
                string comStr = string.IsNullOrWhiteSpace(dev.ComPort) ? "已挂载 (USB 复合设备)" : dev.ComPort;
                string serial = string.IsNullOrWhiteSpace(dev.SerialNumber) ? "--" : dev.SerialNumber;

                if (port.State != DeviceState.Ready || port.ComPort != comStr)
                {
                    port.SetReady(dev.VidPid, comStr, serial, dev.Name, dev.HardwarePath);
                    AddLog($"[{port.PortLabel}] 设备驱动就绪: {dev.Name} | {dev.VidPid} | 端口: {comStr}", "SUCCESS");
                }
            }
        }

        private void ValidateVbusInterlock()
        {
            OnPropertyChanged(nameof(IsVbusControlUnlocked));
            OnPropertyChanged(nameof(VbusInterlockStatusText));

            if (!IsVbusControlUnlocked && _isVbusPowerEnabled)
            {
                _isVbusPowerEnabled = false;
                VbusVoltage = 0.0;
                VbusCurrent = 0.0;
                OnPropertyChanged(nameof(IsVbusPowerEnabled));
                OnPropertyChanged(nameof(VbusInterlockStatusText));
                AddLog("【安全联锁保护触发】检测到 ESP32-S3 核心通信主控掉线，已强制切断目标板 VBUS 供电输出！", "WARN");
            }
        }

        private void OnVbusToggled(bool isEnabled)
        {
            OnPropertyChanged(nameof(VbusInterlockStatusText));

            if (isEnabled)
            {
                VbusVoltage = 5.03;
                VbusCurrent = 135.0;
                AddLog("【供电输出】目标板下行供电已开启：VBUS 输出导通 (5.03 V / 135 mA)。", "SUCCESS");
            }
            else
            {
                VbusVoltage = 0.0;
                VbusCurrent = 0.0;
                AddLog("【供电输出】目标板下行供电已关断：VBUS 输出已断开 MOS 管隔离。", "INFO");
            }
        }

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

        #region 文件放入与解析逻辑

        private void OnBrowseFile()
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择固件镜像或硬件配置文件",
                Filter = "固件与配置文件 (*.bin;*.hex;*.elf;*.json)|*.bin;*.hex;*.elf;*.json|固件镜像 (*.bin;*.hex;*.elf)|*.bin;*.hex;*.elf|拓扑配置 (*.json)|*.json|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                LoadFile(dialog.FileName);
            }
        }

        /// <summary>
        /// 外部拖入或选定文件放入时的统一解析入口
        /// </summary>
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

                // 智能识别芯片架构
                string ext = fileInfo.Extension.ToLowerInvariant();
                string nameUpper = fileInfo.Name.ToUpperInvariant();
                if (ext == ".json")
                {
                    CurrentLoadedFile.TargetChipFamily = "LinkNexus CH338X 拓扑配置文件";
                }
                else if (nameUpper.Contains("STM32") || nameUpper.Contains("M4") || nameUpper.Contains("F4"))
                {
                    CurrentLoadedFile.TargetChipFamily = "ARM Cortex-M4F (STM32F4xx)";
                }
                else if (nameUpper.Contains("ESP32") || nameUpper.Contains("S3"))
                {
                    CurrentLoadedFile.TargetChipFamily = "Espressif Xtensa LX7 (ESP32-S3)";
                }
                else if (nameUpper.Contains("TM4C") || nameUpper.Contains("XDS110"))
                {
                    CurrentLoadedFile.TargetChipFamily = "TI Stellaris/Tiva (TM4C1294)";
                }
                else if (nameUpper.Contains("CH32") || nameUpper.Contains("DAP"))
                {
                    CurrentLoadedFile.TargetChipFamily = "WCH QingKe RISC-V (CH32V305)";
                }
                else
                {
                    CurrentLoadedFile.TargetChipFamily = "通用 ARM/RISC-V 32位 二进制镜像";
                }

                CurrentLoadedFile.IsFileLoaded = true;

                AddLog($"【文件放入】成功解析放入文件：{fileInfo.Name} | 大小：{CurrentLoadedFile.FileSizeText} | CRC32：{CurrentLoadedFile.Crc32Text} | 匹配芯片：{CurrentLoadedFile.TargetChipFamily}", "SUCCESS");
            }
            catch (Exception ex)
            {
                AddLog($"【文件放入异常】解析失败: {ex.Message}", "ERROR");
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

        #region 功能的预览调试逻辑

        /// <summary>
        /// 启动固件烧录流水线功能预览
        /// </summary>
        private async void OnStartPreviewFlash()
        {
            if (IsFlashingActive) return;

            IsFlashingActive = true;
            FlashProgress = 0;
            ActiveOperationStatus = "正在执行固件烧录功能预览...";
            AddLog("================ [ 开始执行一键固件烧录流水线预览 ] ================", "WARN");

            // 阶段 1: SWD 协议建立与复位锁定
            FlashStatusStep = "步骤 1/4: SWD 10MHz 握手建立并挂起目标内核...";
            FlashSpeedText = "握手阶段";
            AddLog("SWD 协议链路建立完成，读取目标 CoreID: 0x2BA01477 (Cortex-M4F)。", "INFO");
            await Task.Delay(400);

            // 阶段 2: 扇区快速擦除
            FlashStatusStep = "步骤 2/4: 执行目标扇区快速擦除 (Sector Erase)...";
            FlashProgress = 15;
            AddLog("正在擦除目标扇区基址 (0x08000000 - 0x08040000)...", "INFO");
            await Task.Delay(500);

            // 阶段 3: 高速流式分块写入
            FlashStatusStep = "步骤 3/4: 高速流式写入分块数据...";
            for (int p = 20; p <= 90; p += 5)
            {
                FlashProgress = p;
                FlashSpeedText = $"{218.4 + (p % 4)} KB/s";
                await Task.Delay(50);
            }

            // 阶段 4: CRC32 读出校验与软复位启动
            FlashStatusStep = "步骤 4/4: 读取片上硬件 CRC32 寄存器比对...";
            FlashProgress = 95;
            await Task.Delay(300);

            FlashProgress = 100;
            FlashSpeedText = "烧录完成";
            FlashStatusStep = "烧录校验成功！释放复位线并启动目标程序。";
            AddLog("片上 CRC32 严格匹配本地镜像 (校验通过)，软复位内核完成！", "SUCCESS");
            AddLog("================ [ 固件烧录流水线预览执行成功 ] ================", "SUCCESS");

            ActiveOperationStatus = "固件烧录流水线预览完成 (耗时: 1.85s)";
            IsFlashingActive = false;
        }

        /// <summary>
        /// 虚拟串口回环测试发送
        /// </summary>
        private void OnSendSerialText()
        {
            if (string.IsNullOrWhiteSpace(SerialSendBuffer)) return;

            string text = SerialSendBuffer;
            int bytesCount = System.Text.Encoding.UTF8.GetByteCount(text);
            SerialTxBytes += bytesCount;
            SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [Tx] -> {text}");

            // 仿真自发自收回环响应
            _dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(60);
                string response = text.Trim().ToUpperInvariant() switch
                {
                    "AT+SYSINFO?" => "+SYSINFO: LINKNEXUS-VCP-ENGINE, V0100, BUS=ONLINE, SPEED=3Mbps",
                    "AT+VERSION?" => $"+VERSION: {VersionString}",
                    "PING" => "+PONG: 1ms LATENCY ACK",
                    _ => $"+ECHO: {text}"
                };

                int rxCount = System.Text.Encoding.UTF8.GetByteCount(response);
                SerialRxBytes += rxCount;
                SerialMonitorLines.Add($"[{DateTime.Now:HH:mm:ss.fff}] [Rx] <- {response}");

                while (SerialMonitorLines.Count > 100) SerialMonitorLines.RemoveAt(0);
            });
        }

        /// <summary>
        /// 动态下行供电负载阶跃仿真调试
        /// </summary>
        private void OnSetSimulatedLoad(object? param)
        {
            if (param is string strVal && double.TryParse(strVal, out double mA))
            {
                if (!IsVbusPowerEnabled)
                {
                    AddLog("【供电调试】供电输出当前处于关断状态，请先开启 VBUS 输出开关！", "WARN");
                    return;
                }

                // 仿真压降与电流阶跃
                VbusCurrent = mA;
                VbusVoltage = Math.Max(0.0, 5.03 - (mA / 1000.0) * 0.08); // 仿真 80mΩ 内部等效内阻压降
                AddLog($"【供电调试】动态负载阶跃生效：设定输出负载 {mA:F0} mA，母线电压实时响应为 {VbusVoltage:F2} V。", "INFO");
            }
        }

        /// <summary>
        /// 二进制协议帧校验调试
        /// </summary>
        private void OnSendTestProtocolFrame(object? param)
        {
            string cmdName = param?.ToString() ?? "0x01 心跳包";
            byte cmd = cmdName.Contains("0x10") ? (byte)0x10 : (cmdName.Contains("0x30") ? (byte)0x30 : (byte)0x01);
            byte len = cmd == 0x30 ? (byte)0x02 : (byte)0x01;
            byte payload0 = 0x01;
            byte sum = (byte)((0xAA + 0x55 + cmd + len + payload0) & 0xFF);

            string hexFrame = $"AA 55 {cmd:X2} {len:X2} {payload0:X2} {sum:X2}";
            AddLog($"【协议帧调试】构造发送二进制帧: [{hexFrame}] ({cmdName})", "INFO");
            AddLog($"【协议帧调试】校验和 (Checksum): 0x{sum:X2} | 模拟下位机 ACK 回执: [AA 55 {cmd:X2} 01 06 {(byte)(0xAA+0x55+cmd+1+6):X2}]", "SUCCESS");
        }

        private void OnReloadHardwareConfig()
        {
            AddLog("[底层维护] 正在热重载 HardwareConfig.json 配置文件...", "INFO");
            HardwareConfigManager.Instance.LoadConfig();
            _usbMonitor.TriggerDebouncedRefresh();
            AddLog($"[底层维护] 硬件拓扑与引脚映射热重载成功！版本：{HardwareConfigManager.Instance.CurrentConfig.Version}", "SUCCESS");
        }

        #endregion

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

