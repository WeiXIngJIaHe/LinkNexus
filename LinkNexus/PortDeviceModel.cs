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
        /// 未插入任何设备时，卡片压暗置灰，端口/扇区/串口信息显示为 --
        /// </summary>
        Unplugged = 0,

        /// <summary>
        /// 状态 B: 驱动未就绪 / 异常设备 (Warning - 黄色感叹号)
        /// 检测到 USB 物理连接，但驱动异常、缺失或无法正确识别出规范的 VID/PID，卡片高亮淡黄色
        /// </summary>
        Warning = 1,

        /// <summary>
        /// 状态 C: 就绪正常 (Ready - 绿色就绪)
        /// 正确安装驱动并识别成功，显示真实枚举到的系统 COM 口号或序列号，点亮绿色就绪灯
        /// </summary>
        Ready = 2
    }

    /// <summary>
    /// 物理端口设备模型
    /// 彻底废弃旧有“CH通道”命名，严格按物理端口序号（端口 #1、端口 #2……）绑定硬件引擎
    /// 支持在开发者调试模式下开关端口、注入异常与仿真状态切换
    /// </summary>
    public class PortDeviceModel : INotifyPropertyChanged
    {
        private int _portNumber;
        private string _targetEngineName = string.Empty;
        private string _engineShortCode = string.Empty;
        private string _engineDescription = string.Empty;
        private DeviceState _state = DeviceState.Unplugged;
        private string _vidPid = "--";
        private string _comPort = "--";
        private string _serialNumber = "--";
        private string _deviceFriendlyName = "--";
        private string _hardwarePath = "--";
        private string _statusMessage = "未接入设备";
        private bool _isEsp32Master = false;

        // 开发者调试模式控制字段
        private bool _isPortEnabled = true;
        private bool _isDebugMode = false;

        public event PropertyChangedEventHandler? PropertyChanged;
        public event Action<PortDeviceModel>? StateToggled;

        public ICommand TogglePortEnableCommand { get; }
        public ICommand ToggleSimulateStateCommand { get; }

        public PortDeviceModel()
        {
            TogglePortEnableCommand = new RelayCommand(OnTogglePortEnable);
            ToggleSimulateStateCommand = new RelayCommand(OnToggleSimulateState);
        }

        /// <summary>
        /// 物理端口序号 (1, 2, 3, 4)
        /// </summary>
        public int PortNumber
        {
            get => _portNumber;
            set
            {
                if (_portNumber != value)
                {
                    _portNumber = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(PortLabel));
                }
            }
        }

        /// <summary>
        /// 界面展示的规范端口标签（严格符合“端口 #1”、“端口 #2”规范）
        /// </summary>
        public string PortLabel => $"端口 #{PortNumber}";

        /// <summary>
        /// 物理引擎名称 (如: FT2232HQ 双通道串口/JTAG 调试器)
        /// </summary>
        public string TargetEngineName
        {
            get => _targetEngineName;
            set { if (_targetEngineName != value) { _targetEngineName = value; OnPropertyChanged(); } }
        }

        public string EngineShortCode
        {
            get => _engineShortCode;
            set { if (_engineShortCode != value) { _engineShortCode = value; OnPropertyChanged(); } }
        }

        public string EngineDescription
        {
            get => _engineDescription;
            set { if (_engineDescription != value) { _engineDescription = value; OnPropertyChanged(); } }
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
                    StateToggled?.Invoke(this);
                }
            }
        }

        public bool IsUnplugged => State == DeviceState.Unplugged;
        public bool IsWarning => State == DeviceState.Warning;
        public bool IsReady => State == DeviceState.Ready;

        public string StatusBadgeText => State switch
        {
            DeviceState.Ready => "正常就绪",
            DeviceState.Warning => "⚠️ 驱动未就绪 / 异常设备",
            _ => "离线未连接"
        };

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

        public string SerialNumber
        {
            get => _serialNumber;
            set { if (_serialNumber != value) { _serialNumber = value; OnPropertyChanged(); } }
        }

        public string DeviceFriendlyName
        {
            get => _deviceFriendlyName;
            set { if (_deviceFriendlyName != value) { _deviceFriendlyName = value; OnPropertyChanged(); } }
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

        public bool IsEsp32Master
        {
            get => _isEsp32Master;
            set { if (_isEsp32Master != value) { _isEsp32Master = value; OnPropertyChanged(); } }
        }

        #region 开发者调试模式控制

        /// <summary>
        /// 是否处于 Debug 调试模式
        /// </summary>
        public bool IsDebugMode
        {
            get => _isDebugMode;
            set { if (_isDebugMode != value) { _isDebugMode = value; OnPropertyChanged(); } }
        }

        /// <summary>
        /// 端口硬件 MOS 开关状态 (True=开启供电/使能，False=物理关断/隔离)
        /// </summary>
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
                    StatusMessage = _isPortEnabled ? "端口 MOS 已导通 (在线)" : "⚠️ 端口已被开发者手动关断 (隔离)";
                }
            }
        }

        public string PortEnabledButtonText => IsPortEnabled ? "端口已开启" : "端口已关断";

        private void OnTogglePortEnable()
        {
            IsPortEnabled = !IsPortEnabled;
        }

        /// <summary>
        /// 调试模式下循环模拟设备三态切换 (Unplugged -> Ready -> Warning -> Unplugged)
        /// </summary>
        private void OnToggleSimulateState()
        {
            switch (State)
            {
                case DeviceState.Unplugged:
                    // 模拟接入就绪
                    SetReady(
                        PortNumber switch
                        {
                            1 => "VID:0403  PID:6010",
                            2 => "VID:0D28  PID:0204",
                            3 => "VID:0451  PID:BEF3",
                            4 => "VID:303A  PID:1001",
                            _ => "VID:1234  PID:5678"
                        },
                        PortNumber switch
                        {
                            1 => "COM3, COM4",
                            2 => "COM5",
                            3 => "COM7 (User UART)",
                            4 => "COM9 (ESP CDC)",
                            _ => "COM10"
                        },
                        $"DEV-SIM-{PortNumber:D2}-OK",
                        TargetEngineName,
                        $"Hub #01 Port #{PortNumber} [仿真测试]"
                    );
                    StatusMessage = "调试模式：模拟设备已就绪接入";
                    break;

                case DeviceState.Ready:
                    // 模拟驱动异常 / 缺失
                    SetWarning(
                        VidPid,
                        "⚠️ 调试模式：模拟驱动异常 (Code 28)",
                        HardwarePath
                    );
                    break;

                case DeviceState.Warning:
                default:
                    // 模拟拔出
                    ResetToUnplugged();
                    StatusMessage = "调试模式：模拟设备已拔出断开";
                    break;
            }
        }

        #endregion

        public void ResetToUnplugged()
        {
            State = DeviceState.Unplugged;
            VidPid = "--";
            ComPort = "--";
            SerialNumber = "--";
            DeviceFriendlyName = "--";
            HardwarePath = "--";
            StatusMessage = "等待物理链路接入...";
        }

        public void SetReady(string vidPid, string comPort, string serialNumber, string friendlyName, string hwPath)
        {
            State = DeviceState.Ready;
            VidPid = string.IsNullOrWhiteSpace(vidPid) ? "--" : vidPid;
            ComPort = string.IsNullOrWhiteSpace(comPort) ? "--" : comPort;
            SerialNumber = string.IsNullOrWhiteSpace(serialNumber) ? "--" : serialNumber;
            DeviceFriendlyName = string.IsNullOrWhiteSpace(friendlyName) ? TargetEngineName : friendlyName;
            HardwarePath = string.IsNullOrWhiteSpace(hwPath) ? $"Hub Port #{PortNumber}" : hwPath;
            StatusMessage = "硬件链路已同步，协议栈就绪";
        }

        public void SetWarning(string vidPid, string warningMsg, string hwPath)
        {
            State = DeviceState.Warning;
            VidPid = string.IsNullOrWhiteSpace(vidPid) ? "--" : vidPid;
            ComPort = "--";
            SerialNumber = "--";
            DeviceFriendlyName = "未识别的 USB 设备";
            HardwarePath = string.IsNullOrWhiteSpace(hwPath) ? $"Hub Port #{PortNumber}" : hwPath;
            StatusMessage = string.IsNullOrWhiteSpace(warningMsg) ? "⚠️ 驱动未就绪 / 异常设备" : warningMsg;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

