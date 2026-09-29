using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace LinkNexus
{
    /// <summary>
    /// USB 硬件设备连接与驱动状态三态枚举
    /// </summary>
    public enum DeviceState
    {
        /// <summary>
        /// 状态 A: 离线未连接 (Unplugged - 灰色)
        /// </summary>
        Unplugged = 0,

        /// <summary>
        /// 状态 B: 驱动未就绪 / 异常设备 (Warning - 黄色感叹号)
        /// 检测到 USB 物理连接，但驱动异常、缺失或无法正确识别
        /// </summary>
        Warning = 1,

        /// <summary>
        /// 状态 C: 就绪正常 (Ready - 绿色就绪)
        /// 正确安装驱动并识别成功，显示真实枚举到的系统 COM 口号与序列号，点亮绿色就绪灯
        /// </summary>
        Ready = 2
    }

    /// <summary>
    /// 设备功能分类枚举
    /// 用于动态分配独立专属工作台页面
    /// </summary>
    public enum DeviceFunctionType
    {
        Burner_FT2232,    // FT2232 双通道 JTAG/SWD 烧录与内核调试
        Burner_DAPLink,   // CMSIS-DAP / CH32V305 仿真烧录
        Burner_XDS110,    // TI XDS110 / TM4C1294 烧录调试
        Uart_Serial,      // CH343P / 通用 USB-UART / Linux CLI 终端
        Controller_ESP32, // ESP32-S3 核心通信主控 (Debug 模式工作页面)
        MassStorage,      // U盘 / 外部下行大容量存储
        GenericUsb        // 其他通用 USB 外设
    }

    /// <summary>
    /// 动态连接的硬件设备数据模型
    /// 按照 Windows 枚举顺序排列，删去端口序号标识，以 Windows 识别出的真实名字为主名字
    /// 绿色就绪时自动从设备管理器抓取分配序列号与 COM 口
    /// </summary>
    public class PortDeviceModel : INotifyPropertyChanged
    {
        private string _deviceName = string.Empty;
        private string _deviceDescription = string.Empty;
        private DeviceState _state = DeviceState.Ready;
        private DeviceFunctionType _functionType = DeviceFunctionType.GenericUsb;
        private string _vidPid = "--";
        private string _comPort = "--";
        private string _serialNumber = "--";
        private string _hardwarePath = "--";
        private string _statusMessage = "正常就绪 (驱动工作正常)";
        private bool _isSelected = false;

        // 开发者调试模式控制字段
        private bool _isPortEnabled = true;
        private bool _isDebugMode = false;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<PortDeviceModel>? SelectedChanged;

        public ICommand SelectCommand { get; }
        public ICommand TogglePortEnableCommand { get; }
        public ICommand ToggleSimulateStateCommand { get; }

        public PortDeviceModel()
        {
            SelectCommand = new RelayCommand(() => IsSelected = true);
            TogglePortEnableCommand = new RelayCommand(OnTogglePortEnable);
            ToggleSimulateStateCommand = new RelayCommand(OnToggleSimulateState);
        }

        /// <summary>
        /// Windows 设备管理器识别出来的为主名字（彻底去除任何“端口 #xxx”前缀或顺序标识）
        /// </summary>
        public string DeviceName
        {
            get => _deviceName;
            set
            {
                if (_deviceName != value)
                {
                    _deviceName = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FunctionBadgeText));
                }
            }
        }

        public string DeviceDescription
        {
            get => _deviceDescription;
            set { if (_deviceDescription != value) { _deviceDescription = value; OnPropertyChanged(); } }
        }

        public DeviceState State
        {
            get => _state;
            set
            {
                if (_state != value)
                {
                    _state = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsUnplugged));
                    OnPropertyChanged(nameof(IsWarning));
                    OnPropertyChanged(nameof(IsReady));
                    OnPropertyChanged(nameof(StatusBadgeText));
                }
            }
        }

        public bool IsUnplugged => State == DeviceState.Unplugged;
        public bool IsWarning => State == DeviceState.Warning;
        public bool IsReady => State == DeviceState.Ready;

        public string StatusBadgeText => State switch
        {
            DeviceState.Ready => "正常就绪",
            DeviceState.Warning => "驱动未就绪",
            _ => "离线未连接"
        };

        public DeviceFunctionType FunctionType
        {
            get => _functionType;
            set
            {
                if (_functionType != value)
                {
                    _functionType = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FunctionBadgeText));
                    OnPropertyChanged(nameof(IsBurnerDevice));
                    OnPropertyChanged(nameof(IsUartDevice));
                    OnPropertyChanged(nameof(IsStorageDevice));
                }
            }
        }

        private bool _isVirtual = false;

        /// <summary>
        /// 是否为 Debug 模式下弹出的虚拟仿真设备 (免物理硬件在线测试功能)
        /// </summary>
        public bool IsVirtual
        {
            get => _isVirtual;
            set
            {
                if (_isVirtual != value)
                {
                    _isVirtual = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FunctionBadgeText));
                }
            }
        }

        public string FunctionBadgeText => FunctionType switch
        {
            DeviceFunctionType.Burner_FT2232 => IsVirtual ? "FT2232 烧录 (虚拟仿真)" : "FT2232 烧录调试器",
            DeviceFunctionType.Burner_DAPLink => IsVirtual ? "CMSIS-DAP (虚拟仿真)" : "CMSIS-DAP 仿真器",
            DeviceFunctionType.Burner_XDS110 => IsVirtual ? "XDS110 探针 (虚拟仿真)" : "TI XDS110 烧录探针",
            DeviceFunctionType.Uart_Serial => GetUartFunctionBadgeText(),
            DeviceFunctionType.Controller_ESP32 => "ESP32-S3 核心主控 (Debug)",
            DeviceFunctionType.MassStorage => "USB 存储设备 (U盘)",
            _ => "通用 USB 外设"
        };

        private string GetUartFunctionBadgeText()
        {
            string suffix = IsVirtual ? " (虚拟仿真)" : "";
            string devName = DeviceName ?? string.Empty;
            string upper = devName.ToUpperInvariant();

            // 按照用户要求：window识别出来的设备名 + 对应功能，比如 CH340串口-TTL / XDS110 虚拟串口
            if (upper.Contains("XDS110"))
            {
                return $"XDS110 虚拟串口{suffix}";
            }
            if (upper.Contains("CMSIS-DAP") || upper.Contains("DAPLINK"))
            {
                return $"DAP 虚拟串口{suffix}";
            }
            if (upper.Contains("CH340"))
            {
                return $"CH340 串口-TTL{suffix}";
            }
            if (upper.Contains("CH341"))
            {
                return $"CH341 串口-TTL{suffix}";
            }
            if (upper.Contains("CH343"))
            {
                return $"CH343 串口-TTL{suffix}";
            }
            if (upper.Contains("CP210"))
            {
                return $"CP210x 串口-TTL{suffix}";
            }
            if (upper.Contains("FT232"))
            {
                return $"FT232 串口-TTL{suffix}";
            }
            if (upper.Contains("PL2303"))
            {
                return $"PL2303 串口-TTL{suffix}";
            }

            // 若不是以上常见芯片，自动剥离 (COMx) 后缀，以 Windows 识别出的主名字 + 串口-TTL 拼接呈现
            string cleanName = System.Text.RegularExpressions.Regex.Replace(devName, @"\s*\([Cc][Oo][Mm]\d+\)", "").Trim();
            if (string.IsNullOrWhiteSpace(cleanName) || cleanName == "--")
            {
                cleanName = "USB";
            }

            return $"{cleanName} 串口-TTL{suffix}";
        }

        public bool IsBurnerDevice =>
            FunctionType == DeviceFunctionType.Burner_FT2232 ||
            FunctionType == DeviceFunctionType.Burner_DAPLink ||
            FunctionType == DeviceFunctionType.Burner_XDS110;

        public bool IsUartDevice => FunctionType == DeviceFunctionType.Uart_Serial;
        public bool IsEsp32Device => FunctionType == DeviceFunctionType.Controller_ESP32;
        public bool IsStorageDevice => FunctionType == DeviceFunctionType.MassStorage;

        /// <summary>
        /// 是否属于 LinkNexus 硬件基站 (CH338X) 下行的四大物理调试引擎或核心主控
        /// </summary>
        public bool IsCh338xCoreDevice =>
            IsBurnerDevice || IsUartDevice || IsEsp32Device;

        public string VidPid
        {
            get => _vidPid;
            set { if (_vidPid != value) { _vidPid = value; OnPropertyChanged(); } }
        }

        public string ComPort
        {
            get => _comPort;
            set { if (_comPort != value) { _comPort = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// 从 Windows 设备管理器自动抓取分配的硬件序列号
        /// </summary>
        public string SerialNumber
        {
            get => _serialNumber;
            set { if (_serialNumber != value) { _serialNumber = value; OnPropertyChanged(); } }
        }

        public string HardwarePath
        {
            get => _hardwarePath;
            set { if (_hardwarePath != value) { _hardwarePath = value; OnPropertyChanged(); } }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { if (_statusMessage != value) { _statusMessage = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// 是否被用户选中（选中后下方展开该设备的独立工作页面）
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                    if (value)
                    {
                        SelectedChanged?.Invoke(this);
                    }
                }
            }
        }

        #region 开发者调试模式支持

        public bool IsDebugMode
        {
            get => _isDebugMode;
            set { if (_isDebugMode != value) { _isDebugMode = value; OnPropertyChanged(); } }
        }

        public bool IsPortEnabled
        {
            get => _isPortEnabled;
            set
            {
                if (_isPortEnabled != value)
                {
                    _isPortEnabled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PortEnabledButtonText));
                    StatusMessage = _isPortEnabled ? "硬件链路已导通" : "端口已被开发者手动隔离";
                }
            }
        }

        public string PortEnabledButtonText => IsPortEnabled ? "端口已开启" : "端口已关断";

        private void OnTogglePortEnable()
        {
            IsPortEnabled = !IsPortEnabled;
        }

        private void OnToggleSimulateState()
        {
            State = State switch
            {
                DeviceState.Ready => DeviceState.Warning,
                DeviceState.Warning => DeviceState.Ready,
                _ => DeviceState.Ready
            };
        }

        #endregion

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

