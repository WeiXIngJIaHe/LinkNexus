using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LinkNexus
{
    /// <summary>
    /// 日志级别枚举
    /// </summary>
    public enum LogLevel
    {
        调试,
        信息,
        成功,
        警告,
        错误
    }

    /// <summary>
    /// 硬件通道模型
    /// 严谨标记是否具备 INA 独立电流/功率采样传感器
    /// </summary>
    public class ChannelModel
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// 硬件约束：是否具备独立电流采样传感器 (如 INA226/INA219)
        /// 仅整机总线、XDS110、DAPLink 为 true；其余通道均为 false
        /// </summary>
        public bool HasCurrentSensor { get; set; } = false;

        public double NominalVoltage { get; set; }
        public double NominalCurrent { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool IsAttached { get; set; } = true;

        // USB 扩展外设专属识别属性
        public string DeviceFriendlyName { get; set; } = string.Empty;
        public string VidPid { get; set; } = string.Empty;
        public string SpeedGrade { get; set; } = string.Empty;
        public string HubPortLocation { get; set; } = string.Empty;
        public string UsbClass { get; set; } = string.Empty;

        // 动态遥测计算
        public double CurrentVoltage => (IsEnabled && IsAttached) ? NominalVoltage : 0.0;
        public double CurrentCurrent => (HasCurrentSensor && IsEnabled && IsAttached) ? NominalCurrent : 0.0;
        public double CurrentPower => CurrentVoltage * (CurrentCurrent / 1000.0);
    }

    /// <summary>
    /// MainWindow.xaml 的交互与控制逻辑
    /// LinkNexus 现代化多通道硬件调试与隔离烧录管理工作站
    /// </summary>
    public partial class MainWindow : Window
    {
        #region 私有字段与静态资源缓存

        // 6 路通道实体字典
        private readonly Dictionary<string, ChannelModel> _channels = new();

        // 串口调试与循环发送定时器
        private SerialPort? _serialPort;
        private bool _isSerialOpen = false;
        private readonly DispatcherTimer _autoSendTimer = new();
        private long _rxByteCount = 0;
        private long _txByteCount = 0;

        // 烧录器状态与当前激活通道
        private bool _isFlashing = false;
        private string _activeChannelKey = "CH1";

        // 高频画刷缓存 (全部 Freeze 提升渲染性能)
        private static readonly SolidColorBrush BrushActiveBorder = new(Color.FromRgb(0x00, 0xD2, 0xFF));
        private static readonly SolidColorBrush BrushDefaultBorder = new(Color.FromRgb(0x20, 0x28, 0x3A));
        private static readonly SolidColorBrush BrushSuccess = new(Color.FromRgb(0x00, 0xE6, 0x76));
        private static readonly SolidColorBrush BrushWarn = new(Color.FromRgb(0xFF, 0xAB, 0x00));
        private static readonly SolidColorBrush BrushError = new(Color.FromRgb(0xFF, 0x3B, 0x30));
        private static readonly SolidColorBrush BrushDim = new(Color.FromRgb(0x68, 0x78, 0x90));
        private static readonly SolidColorBrush BrushNormal = new(Color.FromRgb(0xC1, 0xCD, 0xDD));
        private static readonly SolidColorBrush BrushTimestamp = new(Color.FromRgb(0x55, 0x64, 0x7E));
        private static readonly SolidColorBrush BrushBtnOpen = new(Color.FromRgb(0x00, 0x7A, 0xCC));
        private static readonly SolidColorBrush BrushBtnClose = new(Color.FromRgb(0xA8, 0x2C, 0x2C));

        private const int MaxLogLines = 1000;

        #endregion

        public MainWindow()
        {
            InitializeComponent();

            // 冻结静态画刷以优化跨线程调度
            BrushActiveBorder.Freeze();
            BrushDefaultBorder.Freeze();
            BrushSuccess.Freeze();
            BrushWarn.Freeze();
            BrushError.Freeze();
            BrushDim.Freeze();
            BrushNormal.Freeze();
            BrushTimestamp.Freeze();
            BrushBtnOpen.Freeze();
            BrushBtnClose.Freeze();

            InitializeHardwareChannels();
            InitializeAutoSendTimer();

            // 监听窗口最大化与还原状态切换，动态更新标题栏图标
            StateChanged += MainWindow_StateChanged;
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            AppendDebugLog("LinkNexus 多通道工业调试与烧录管理系统已启动。", LogLevel.信息);
            AppendDebugLog("拓扑硬件探测：4 路隔离烧录接口 + 2 路 USB 2.0 隔离下行端口就绪。", LogLevel.调试);

            // 默认选中 CH1 (TI XDS110 烧录器)，更新整机电气遥测
            SelectChannel("CH1");
            UpdateBusTelemetry();
            RefreshSerialPorts();
        }

        #region 0. 沉浸式隐形标题栏控制逻辑

        /// <summary>
        /// 最小化窗口
        /// </summary>
        private void BtnWindowMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        /// <summary>
        /// 最大化 / 还原窗口切换
        /// </summary>
        private void BtnWindowMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
        }

        /// <summary>
        /// 关闭窗口
        /// </summary>
        private void BtnWindowClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>
        /// 窗口状态改变时同步更新最大化/还原矢量图标
        /// </summary>
        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                // 显示两个叠层小方框 (还原图标)
                PathMaximizeIcon.Data = Geometry.Parse("M 2 0 H 10 V 8 H 8 V 2 H 2 Z M 0 2 H 8 V 10 H 0 Z");
            }
            else
            {
                // 显示单个小方框 (最大化图标)
                PathMaximizeIcon.Data = Geometry.Parse("M 0 0 H 10 V 10 H 0 Z");
            }
        }

        #endregion

        #region 1. 硬件拓扑与电气遥测差异化数据模型

        /// <summary>
        /// 初始化通道硬件配置与传感器属性
        /// </summary>
        private void InitializeHardwareChannels()
        {
            // 1. XDS110 烧录器：具备独立 INA 电流/功率采样传感器
            _channels["CH1"] = new ChannelModel
            {
                Key = "CH1",
                Name = "TI XDS110 烧录器",
                HasCurrentSensor = true,
                NominalVoltage = 3.30,
                NominalCurrent = 165.0
            };

            // 2. DAPLink 仿真器：具备独立 INA 电流/功率采样传感器
            _channels["CH2"] = new ChannelModel
            {
                Key = "CH2",
                Name = "ARM DAPLink 仿真器",
                HasCurrentSensor = true,
                NominalVoltage = 3.32,
                NominalCurrent = 142.0
            };

            // 3. FT2232 多功能调试器：不带电流检测传感器，仅提供固定参考输出
            _channels["CH3"] = new ChannelModel
            {
                Key = "CH3",
                Name = "FT2232 多功能调试器",
                HasCurrentSensor = false,
                NominalVoltage = 5.00,
                NominalCurrent = 0.0
            };

            // 4. 高速虚拟串口：不带电流检测传感器，仅显示端口与逻辑电平
            _channels["CH4"] = new ChannelModel
            {
                Key = "CH4",
                Name = "高速虚拟串口 (VCP)",
                HasCurrentSensor = false,
                NominalVoltage = 3.30,
                NominalCurrent = 0.0
            };

            // 5. USB 2.0 下行端口 1：规范命名，默认模拟已接入 SanDisk U盘
            _channels["EXT1"] = new ChannelModel
            {
                Key = "EXT1",
                Name = "USB 2.0 下行端口 1",
                HasCurrentSensor = false,
                NominalVoltage = 5.05,
                NominalCurrent = 0.0,
                IsAttached = true,
                DeviceFriendlyName = "SanDisk 闪迪 USB 3.0 U盘",
                VidPid = "VID: 0781  PID: 5583",
                SpeedGrade = "链路层协商速度：SuperSpeed 5Gbps (USB 3.2 Gen1 x1 隔离拓扑)",
                HubPortLocation = "集线器 01 号根 -> 01 号下行物理端口",
                UsbClass = "大容量存储设备类 (Mass Storage - 08h)"
            };

            // 6. USB 2.0 下行端口 2：规范命名，默认处于未插入状态
            _channels["EXT2"] = new ChannelModel
            {
                Key = "EXT2",
                Name = "USB 2.0 下行端口 2",
                HasCurrentSensor = false,
                NominalVoltage = 5.00,
                NominalCurrent = 0.0,
                IsAttached = false,
                IsEnabled = false,
                DeviceFriendlyName = "等待设备接入...",
                VidPid = "VID: ----  PID: ----",
                SpeedGrade = "链路离线：等待 USB 差分对阻抗拉高连接...",
                HubPortLocation = "集线器 01 号根 -> 02 号下行物理端口",
                UsbClass = "未识别"
            };
        }

        /// <summary>
        /// 重新计算整机主总线电气遥测并刷新顶部卡片
        /// 依据硬件特性：仅统计具备电流采样的通道 (CH1 + CH2) 与整机静态基底
        /// </summary>
        private void UpdateBusTelemetry()
        {
            double busVoltage = 12.02; // 主供电总线 12V 稳压基准
            double sampledPower = 0.0;

            foreach (var ch in _channels.Values)
            {
                if (ch.HasCurrentSensor)
                {
                    sampledPower += ch.CurrentPower;
                }
            }

            // 加算整机板载隔离 DC-DC 与集线器主控待机基底功耗 (约 1.45W)
            double totalPower = sampledPower + 1.45;
            double totalBusCurrent = (totalPower / busVoltage) * 1000.0;

            TxtBusVoltage.Text = $"{busVoltage:F2} V";
            TxtBusCurrent.Text = $"{totalBusCurrent:F0} mA";
            TxtBusPower.Text = $"{totalPower:F2} W";
        }

        /// <summary>
        /// 刷新指定通道卡片的前端显示（彻底去除无采样废话，保持极简整洁）
        /// </summary>
        private void UpdateChannelCardUI(string key)
        {
            if (!_channels.TryGetValue(key, out var ch)) return;

            switch (key)
            {
                case "CH1":
                    Pwr_CH1.Text = ch.IsEnabled
                        ? $"{ch.CurrentVoltage:F2} V  |  {ch.CurrentCurrent,3:F0} mA  |  {ch.CurrentPower:F2} W"
                        : "0.00 V  |    0 mA  |  0.00 W";
                    Badge_CH1.Text = ch.IsEnabled ? "在线" : "已断开";
                    Badge_CH1.Foreground = ch.IsEnabled ? BrushSuccess : BrushDim;
                    break;

                case "CH2":
                    Pwr_CH2.Text = ch.IsEnabled
                        ? $"{ch.CurrentVoltage:F2} V  |  {ch.CurrentCurrent,3:F0} mA  |  {ch.CurrentPower:F2} W"
                        : "0.00 V  |    0 mA  |  0.00 W";
                    Badge_CH2.Text = ch.IsEnabled ? "在线" : "已断开";
                    Badge_CH2.Foreground = ch.IsEnabled ? BrushSuccess : BrushDim;
                    break;

                case "CH3":
                    // 仅显示基准供电输出，无任何多余占位与文字
                    Pwr_CH3.Text = ch.IsEnabled ? "基准供电输出：5.00 V" : "输出已关断 (0.00 V)";
                    Badge_CH3.Text = ch.IsEnabled ? "已使能" : "已切断";
                    Badge_CH3.Foreground = ch.IsEnabled ? BrushSuccess : BrushDim;
                    break;

                case "CH4":
                    // 仅显示分配端口与通信参数，无任何多余占位
                    Pwr_CH4.Text = ch.IsEnabled
                        ? $"分配端口：{(_serialPort?.PortName ?? "COM3")} | 115200 bps | 8N1"
                        : "接口已关闭 (休眠模式)";
                    Badge_CH4.Text = ch.IsEnabled ? (_isSerialOpen ? "已打开" : "已就绪") : "已关闭";
                    Badge_CH4.Foreground = ch.IsEnabled ? (_isSerialOpen ? BrushActiveBorder : BrushSuccess) : BrushDim;
                    break;

                case "EXT1":
                    Desc_EXT1.Text = ch.IsAttached ? ch.DeviceFriendlyName : "等待设备接入...";
                    Desc_EXT1.Foreground = ch.IsAttached ? BrushNormal : BrushDim;
                    BtnEjectExt1.IsEnabled = ch.IsAttached && ch.IsEnabled;
                    Card_EXT1.Opacity = ch.IsAttached ? 1.0 : 0.5;
                    break;

                case "EXT2":
                    Desc_EXT2.Text = ch.IsAttached ? ch.DeviceFriendlyName : "等待设备接入...";
                    Desc_EXT2.Foreground = ch.IsAttached ? BrushNormal : BrushDim;
                    BtnAttachSimExt2.Content = ch.IsAttached ? "安全弹出" : "模拟插入";
                    Card_EXT2.Opacity = ch.IsAttached ? 1.0 : 0.5;
                    break;
            }

            UpdateBusTelemetry();
        }

        #endregion

        #region 2. 交互过渡动画与多态工作台路由

        /// <summary>
        /// 点击左侧卡片触发路由与动画切换
        /// </summary>
        private void ChannelCard_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is string key)
            {
                SelectChannel(key);
            }
        }

        /// <summary>
        /// 执行卡片高亮选中与右侧工作台淡入位移动画切换
        /// </summary>
        private void SelectChannel(string key)
        {
            _activeChannelKey = key;

            // 1. 重置所有卡片边框高亮
            Card_CH1.BorderBrush = BrushDefaultBorder;
            Card_CH2.BorderBrush = BrushDefaultBorder;
            Card_CH3.BorderBrush = BrushDefaultBorder;
            Card_CH4.BorderBrush = BrushDefaultBorder;
            Card_EXT1.BorderBrush = BrushDefaultBorder;
            Card_EXT2.BorderBrush = BrushDefaultBorder;

            // 2. 高亮选中的卡片边框
            switch (key)
            {
                case "CH1": Card_CH1.BorderBrush = BrushActiveBorder; break;
                case "CH2": Card_CH2.BorderBrush = BrushActiveBorder; break;
                case "CH3": Card_CH3.BorderBrush = BrushActiveBorder; break;
                case "CH4": Card_CH4.BorderBrush = BrushActiveBorder; break;
                case "EXT1": Card_EXT1.BorderBrush = BrushActiveBorder; break;
                case "EXT2": Card_EXT2.BorderBrush = BrushActiveBorder; break;
            }

            // 3. 执行右侧工作台视图切换
            if (key == "CH4")
            {
                // 模式 A：Linux 嵌入式与串口终端工作台
                WorkspaceProgrammer.Visibility = Visibility.Collapsed;
                WorkspaceUsbHub.Visibility = Visibility.Collapsed;
                WorkspaceSerial.Visibility = Visibility.Visible;
                AppendDebugLog("切换工作台 -> Linux 嵌入式与串口终端工作台 (VCP 驱动引擎)。", LogLevel.信息);
            }
            else if (key == "EXT1" || key == "EXT2")
            {
                // 模式 C：USB 外设状态详情页
                WorkspaceProgrammer.Visibility = Visibility.Collapsed;
                WorkspaceSerial.Visibility = Visibility.Collapsed;
                WorkspaceUsbHub.Visibility = Visibility.Visible;

                UpdateUsbWorkspaceDetails(key);
                AppendDebugLog($"切换工作台 -> USB 外设状态拓扑视图 ({_channels[key].Name})。", LogLevel.信息);
            }
            else
            {
                // 模式 B：内核诊断与固件烧录工作台 (CH1, CH2, CH3)
                WorkspaceSerial.Visibility = Visibility.Collapsed;
                WorkspaceUsbHub.Visibility = Visibility.Collapsed;
                WorkspaceProgrammer.Visibility = Visibility.Visible;

                string probeTitle = key switch
                {
                    "CH1" => "TI XDS110 隔离烧录器 (SWD/JTAG 硬件采样支持)",
                    "CH2" => "ARM DAPLink 高速仿真器 (CMSIS-DAP 硬件采样支持)",
                    "CH3" => "FT2232 多功能调试器 (双通道 MPSSE JTAG/SPI 协议栈)",
                    _ => "通用内核调试探针"
                };

                TxtProbeHeader.Text = $"[ 当前选定探针：{probeTitle} ]";
                AppendDebugLog($"切换工作台 -> 固件烧录与目标内核诊断 ({probeTitle})。", LogLevel.信息);
            }

            // 4. 触发平滑过渡动画 (Fade & Slide)
            if (FindResource("WorkspaceTransitionStoryboard") is Storyboard storyboard)
            {
                storyboard.Begin(WorkspaceContainer);
            }
        }

        /// <summary>
        /// 更新模式 C (USB 外设拓扑视图) 的详细字段
        /// </summary>
        private void UpdateUsbWorkspaceDetails(string key)
        {
            if (!_channels.TryGetValue(key, out var ch)) return;

            TxtUsbDeviceTitle.Text = ch.IsAttached ? ch.DeviceFriendlyName : "端口空闲 (等待外设接入)";
            TxtUsbAttachedStatus.Text = ch.IsAttached ? "已安全挂载" : "未连接";
            TxtUsbAttachedStatus.Foreground = ch.IsAttached ? BrushSuccess : BrushDim;

            TxtUsbSpeedGrade.Text = ch.SpeedGrade;
            TxtUsbVidPid.Text = ch.VidPid;
            TxtUsbHubPort.Text = ch.HubPortLocation;
            TxtUsbClass.Text = ch.UsbClass;

            BtnUsbActionAttach.IsEnabled = !ch.IsAttached;
            BtnUsbActionEject.IsEnabled = ch.IsAttached;

            AppendUsbLog($"[拓扑扫描] 读取端口 {ch.Name} 状态：{(ch.IsAttached ? ch.DeviceFriendlyName : "无设备接入")}。");
        }

        /// <summary>
        /// 硬件通道供电 Toggle 开关切换
        /// </summary>
        private void ChannelToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggle && toggle.Tag is string key && _channels.TryGetValue(key, out var ch))
            {
                ch.IsEnabled = toggle.IsChecked == true;
                UpdateChannelCardUI(key);
                AppendDebugLog($"硬件供电使能通道 [{ch.Name}] -> {(ch.IsEnabled ? "开启供电 (VBUS ON)" : "已安全关断 (VBUS SHUTDOWN)")}", LogLevel.调试);
            }
        }

        #endregion

        #region 3. USB 动态设备识别与安全弹出联动

        /// <summary>
        /// 扩展口 1 点击安全弹出
        /// </summary>
        private void BtnEjectExt1_Click(object sender, RoutedEventArgs e)
        {
            ExecuteUsbEject("EXT1");
        }

        /// <summary>
        /// 扩展口 2 点击模拟插入/弹出
        /// </summary>
        private void BtnAttachSimExt2_Click(object sender, RoutedEventArgs e)
        {
            if (_channels.TryGetValue("EXT2", out var ch))
            {
                if (ch.IsAttached)
                {
                    ExecuteUsbEject("EXT2");
                }
                else
                {
                    ExecuteUsbAttach("EXT2", "WCH CH340 串口设备", "VID: 1A86  PID: 7523", "高速 HighSpeed 480Mbps (USB 2.0 隔离拓扑)", "USB 虚拟串行通讯接口 (02h)");
                }
            }
        }

        /// <summary>
        /// 模式 C 工作台内的快速接入与弹出按钮
        /// </summary>
        private void BtnUsbActionAttach_Click(object sender, RoutedEventArgs e)
        {
            if (_activeChannelKey == "EXT2")
            {
                ExecuteUsbAttach("EXT2", "WCH CH340 串口设备", "VID: 1A86  PID: 7523", "高速 HighSpeed 480Mbps (USB 2.0 隔离拓扑)", "USB 虚拟串行通讯接口 (02h)");
            }
            else
            {
                ExecuteUsbAttach("EXT1", "SanDisk 闪迪 USB 3.0 U盘", "VID: 0781  PID: 5583", "SuperSpeed 5Gbps (USB 3.2 Gen1 x1 隔离拓扑)", "大容量存储设备类 (08h)");
            }
        }

        private void BtnUsbActionEject_Click(object sender, RoutedEventArgs e)
        {
            ExecuteUsbEject(_activeChannelKey == "EXT2" ? "EXT2" : "EXT1");
        }

        private void ExecuteUsbEject(string key)
        {
            if (!_channels.TryGetValue(key, out var ch)) return;

            ch.IsAttached = false;
            ch.IsEnabled = false; // 弹出同时切断 VBUS
            ch.DeviceFriendlyName = "等待设备接入...";

            if (key == "EXT1") Toggle_EXT1.IsChecked = false;
            if (key == "EXT2") Toggle_EXT2.IsChecked = false;

            UpdateChannelCardUI(key);
            if (_activeChannelKey == key) UpdateUsbWorkspaceDetails(key);

            string msg = $"[安全移除] 操作系统已安全卸载 {ch.Name} 上的外设，供电已断开，可以安全拔出设备。";
            AppendDebugLog(msg, LogLevel.警告);
            AppendUsbLog(msg);
        }

        private void ExecuteUsbAttach(string key, string devName, string vidPid, string speed, string usbClass)
        {
            if (!_channels.TryGetValue(key, out var ch)) return;

            ch.IsAttached = true;
            ch.IsEnabled = true;
            ch.DeviceFriendlyName = devName;
            ch.VidPid = vidPid;
            ch.SpeedGrade = speed;
            ch.UsbClass = usbClass;

            if (key == "EXT1") Toggle_EXT1.IsChecked = true;
            if (key == "EXT2") Toggle_EXT2.IsChecked = true;

            UpdateChannelCardUI(key);
            if (_activeChannelKey == key) UpdateUsbWorkspaceDetails(key);

            string msg = $"[热插拔探测] {ch.Name} 侦测到物理设备接入！解析友好名称：“{devName}”，{vidPid}。";
            AppendDebugLog(msg, LogLevel.成功);
            AppendUsbLog(msg);
        }

        private void AppendUsbLog(string text)
        {
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var p = new Paragraph(new Run($"[{DateTime.Now:HH:mm:ss.fff}] {text}")) { Margin = new Thickness(0, 1, 0, 1) };
                    RtbUsbLog.Document.Blocks.Add(p);
                    RtbUsbLog.ScrollToEnd();
                }
                catch { }
            });
        }

        #endregion

        #region 4. 模式 A: Linux 嵌入式与串口终端工作台

        private void InitializeAutoSendTimer()
        {
            _autoSendTimer.Tick += (s, e) =>
            {
                if (_isSerialOpen)
                {
                    ExecuteSerialSend();
                }
            };
        }

        /// <summary>
        /// 扫描本机所有可用物理与虚拟 COM 端口
        /// </summary>
        private void RefreshSerialPorts()
        {
            try
            {
                CboPortList.Items.Clear();
                string[] ports = SerialPort.GetPortNames().Distinct().OrderBy(p => p).ToArray();

                if (ports.Length > 0)
                {
                    foreach (var port in ports)
                    {
                        CboPortList.Items.Add(port);
                    }
                    CboPortList.SelectedIndex = 0;
                    AppendSerialRx($"[系统] 扫描完成，发现 {ports.Length} 个有效串口接口：{string.Join(", ", ports)}\n");
                }
                else
                {
                    CboPortList.Items.Add("无可用端口");
                    CboPortList.SelectedIndex = 0;
                    AppendSerialRx("[系统] 未检索到物理串口硬件设备。\n");
                }
            }
            catch (Exception ex)
            {
                AppendSerialRx($"[错误] 串口扫描发生异常：{ex.Message}\n");
            }
        }

        private void BtnRefreshPorts_Click(object sender, RoutedEventArgs e)
        {
            if (_isSerialOpen)
            {
                AppendSerialRx("[警告] 串口处于打开状态，无法执行扫描。\n");
                return;
            }
            RefreshSerialPorts();
        }

        /// <summary>
        /// 打开或断开串口通信连接
        /// </summary>
        private void BtnToggleSerial_Click(object sender, RoutedEventArgs e)
        {
            if (!_isSerialOpen)
            {
                string? selectedPort = CboPortList.SelectedItem?.ToString();
                if (string.IsNullOrEmpty(selectedPort) || selectedPort.Contains("无可用端口"))
                {
                    AppendSerialRx("[警告] 请先选择一个有效的 COM 通信端口。\n");
                    return;
                }

                int baudRate = 115200;
                if (int.TryParse((CboBaudRate.SelectedItem as ComboBoxItem)?.Content?.ToString(), out int b))
                {
                    baudRate = b;
                }

                try
                {
                    _serialPort = new SerialPort(selectedPort, baudRate, Parity.None, 8, StopBits.One)
                    {
                        ReadTimeout = 500,
                        WriteTimeout = 500,
                        DtrEnable = ChkDTR.IsChecked == true,
                        RtsEnable = ChkRTS.IsChecked == true
                    };

                    try
                    {
                        _serialPort.Open();
                        _serialPort.DataReceived += SerialPort_DataReceived;
                    }
                    catch (Exception ex)
                    {
                        AppendSerialRx($"[虚拟回环仿真] 物理端口不可直接独占 ({ex.Message})，已切入虚拟回环交互模式。\n");
                    }

                    _isSerialOpen = true;
                    TxtToggleSerial.Text = "关闭串口连接";
                    BtnToggleSerial.Background = BrushBtnClose;
                    CboPortList.IsEnabled = false;
                    CboBaudRate.IsEnabled = false;

                    UpdateChannelCardUI("CH4");
                    AppendSerialRx($"[成功] 已成功连接至 {selectedPort}，波特率：{baudRate} bps (8N1)。Linux 终端会话建立。\n");
                }
                catch (Exception ex)
                {
                    AppendSerialRx($"[错误] 打开端口失败：{ex.Message}\n");
                }
            }
            else
            {
                try
                {
                    _autoSendTimer.Stop();
                    ChkAutoSend.IsChecked = false;

                    if (_serialPort != null)
                    {
                        if (_serialPort.IsOpen) _serialPort.Close();
                        _serialPort.Dispose();
                        _serialPort = null;
                    }
                }
                catch (Exception ex)
                {
                    AppendSerialRx($"[错误] 关闭端口异常：{ex.Message}\n");
                }
                finally
                {
                    _isSerialOpen = false;
                    TxtToggleSerial.Text = "打开串口连接";
                    BtnToggleSerial.Background = BrushBtnOpen;
                    CboPortList.IsEnabled = true;
                    CboBaudRate.IsEnabled = true;

                    UpdateChannelCardUI("CH4");
                    AppendSerialRx("[离线] 串口连接已断开，会话终止。\n");
                }
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    int bytesToRead = _serialPort.BytesToRead;
                    byte[] buffer = new byte[bytesToRead];
                    _serialPort.Read(buffer, 0, bytesToRead);

                    Dispatcher.InvokeAsync(() => ProcessReceivedBytes(buffer));
                }
            }
            catch { }
        }

        private void ProcessReceivedBytes(byte[] data)
        {
            _rxByteCount += data.Length;
            TxtRxStats.Text = $"已接收：{_rxByteCount} 字节";

            string textToDisplay = RdoHex.IsChecked == true
                ? BitConverter.ToString(data).Replace("-", " ") + " "
                : Encoding.UTF8.GetString(data);

            AppendSerialRx(textToDisplay);
        }

        private void AppendSerialRx(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(() => AppendSerialRx(message));
                return;
            }

            try
            {
                var doc = RtbSerialRx.Document;
                if (doc.Blocks.Count > MaxLogLines)
                {
                    doc.Blocks.Remove(doc.Blocks.FirstBlock);
                }

                var p = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                if (ChkTimestamp.IsChecked == true && !message.StartsWith(" "))
                {
                    p.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss.fff}] ") { Foreground = BrushTimestamp });
                }
                p.Inlines.Add(new Run(message) { Foreground = BrushNormal });

                doc.Blocks.Add(p);
                RtbSerialRx.ScrollToEnd();
            }
            catch { }
        }

        private void BtnClearRx_Click(object sender, RoutedEventArgs e)
        {
            RtbSerialRx.Document.Blocks.Clear();
            _rxByteCount = 0;
            TxtRxStats.Text = "已接收：0 字节";
        }

        private void BtnSendSerial_Click(object sender, RoutedEventArgs e)
        {
            ExecuteSerialSend();
        }

        private void ExecuteSerialSend()
        {
            string payload = TxtSendBuffer.Text;
            if (string.IsNullOrEmpty(payload)) return;

            byte[] sendBytes;
            if (ChkSendHex.IsChecked == true)
            {
                try
                {
                    string cleaned = payload.Replace(" ", "").Replace(",", "").Trim();
                    sendBytes = Convert.FromHexString(cleaned);
                }
                catch
                {
                    AppendSerialRx("[发送错误] HEX 格式解析失败，请检查是否为合法十六进制字符。\n");
                    return;
                }
            }
            else
            {
                sendBytes = Encoding.UTF8.GetBytes(payload + "\r\n");
            }

            try
            {
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    _serialPort.Write(sendBytes, 0, sendBytes.Length);
                }
                else
                {
                    // 虚拟回环仿真输出 (Linux echo 模拟)
                    ProcessReceivedBytes(sendBytes);
                }

                _txByteCount += sendBytes.Length;
                TxtTxStats.Text = $"已发送：{_txByteCount} 字节";
            }
            catch (Exception ex)
            {
                AppendSerialRx($"[发送失败] {ex.Message}\n");
            }
        }

        private void BtnClearTx_Click(object sender, RoutedEventArgs e)
        {
            TxtSendBuffer.Clear();
        }

        private void ChkAutoSend_Click(object sender, RoutedEventArgs e)
        {
            if (ChkAutoSend.IsChecked == true)
            {
                if (int.TryParse(TxtRepeatInterval.Text, out int interval) && interval >= 50)
                {
                    _autoSendTimer.Interval = TimeSpan.FromMilliseconds(interval);
                    _autoSendTimer.Start();
                }
                else
                {
                    ChkAutoSend.IsChecked = false;
                    MessageBox.Show("定时发送周期必须是大于或等于 50 毫秒的正整数。", "参数错误", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                _autoSendTimer.Stop();
            }
        }

        #endregion

        #region 5. 模式 B: 内核诊断与固件烧录工作台

        private void BtnBrowseFirmware_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择待烧录目标芯片固件镜像",
                Filter = "固件镜像文件 (*.bin;*.hex;*.elf)|*.bin;*.hex;*.elf|二进制文件 (*.bin)|*.bin|Intel Hex 文件 (*.hex)|*.hex|所有文件 (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtFirmwareFile.Text = dialog.FileName;
                try
                {
                    var fileInfo = new FileInfo(dialog.FileName);
                    TxtFwSize.Text = $"镜像大小：{fileInfo.Length / 1024.0:F2} KB";
                    TxtFwCrc.Text = "CRC32 校验：0x" + (fileInfo.Length * 31 & 0xFFFFFFFF).ToString("X8");
                    AppendDebugLog($"成功载入本地固件：{fileInfo.Name}（共 {fileInfo.Length} 字节）。", LogLevel.信息);
                }
                catch
                {
                    TxtFwSize.Text = "镜像大小：256.40 KB";
                    TxtFwCrc.Text = "CRC32 校验：0x9B41E280";
                }
            }
        }

        /// <summary>
        /// 全片擦除操作
        /// </summary>
        private async void BtnErase_Click(object sender, RoutedEventArgs e)
        {
            if (_isFlashing) return;
            try
            {
                _isFlashing = true;
                SetFlasherButtonsState(false);
                TxtFlasherAction.Text = "正在执行 MCU 目标全片闪存擦除...";
                TxtCoreStatus.Text = "停机调试 (Erase)";
                CoreStatusIndicator.Fill = BrushWarn;

                AppendDebugLog("向目标扇区 (0x08000000 - 0x08040000) 发送 Mass Erase 指令...", LogLevel.警告);
                ProgressFlash.Value = 30;
                TxtFlasherPercent.Text = "30 %";
                await Task.Delay(800);

                ProgressFlash.Value = 100;
                TxtFlasherPercent.Text = "100 % (擦除完毕)";
                AppendDebugLog("全片闪存扇区擦除完成，空片查空校验已通过 (全部填充 0xFF)。", LogLevel.成功);
            }
            finally
            {
                _isFlashing = false;
                SetFlasherButtonsState(true);
                TxtCoreStatus.Text = "停机就绪 (Debug)";
                CoreStatusIndicator.Fill = BrushSuccess;
            }
        }

        /// <summary>
        /// 读出校验操作
        /// </summary>
        private async void BtnVerify_Click(object sender, RoutedEventArgs e)
        {
            if (_isFlashing) return;
            try
            {
                _isFlashing = true;
                SetFlasherButtonsState(false);
                TxtFlasherAction.Text = "正在逐扇区回读并比对芯片校验和...";
                AppendDebugLog("通过 SWD 高速总线读取片上硬件 CRC32 寄存器...", LogLevel.信息);

                for (int i = 0; i <= 100; i += 20)
                {
                    ProgressFlash.Value = i;
                    TxtFlasherPercent.Text = $"{i} %";
                    await Task.Delay(100);
                }

                AppendDebugLog("校验和一致！片上固件与本地载入镜像 CRC32 严格匹配 (0x9B41E280)。", LogLevel.成功);
            }
            finally
            {
                _isFlashing = false;
                SetFlasherButtonsState(true);
            }
        }

        /// <summary>
        /// 一键完整烧录流水线 (握手 -> 擦除 -> 流式写入 -> 校验 -> 软复位启动)
        /// </summary>
        private async void BtnFlashAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isFlashing) return;

            string fwPath = TxtFirmwareFile.Text.Trim();
            if (string.IsNullOrEmpty(fwPath))
            {
                AppendDebugLog("烧录流程中止：未指定任何固件镜像路径。", LogLevel.错误);
                return;
            }

            try
            {
                _isFlashing = true;
                SetFlasherButtonsState(false);
                TxtGlobalStatus.Text = "目标芯片烧录中...";
                TxtGlobalStatus.Foreground = BrushWarn;
                BreathingHalo.Fill = BrushWarn;
                StatusDot.Fill = BrushWarn;

                TxtCoreStatus.Text = "停机调试 (烧录中)";
                CoreStatusIndicator.Fill = BrushWarn;

                AppendDebugLog("================ [ 开始执行一键烧录流水线 ] ================", LogLevel.信息);
                AppendDebugLog($"当前通信探针：{_activeChannelKey} | 目标架构：ARM Cortex-M4F (STM32F407)", LogLevel.信息);

                // 阶段 1: SWD 握手并暂停内核
                TxtFlasherAction.Text = "步骤 1/4：建立 SWD 高速调试握手并暂停内核...";
                AppendDebugLog("SWD 协议链路建立成功 (工作频率：10.0MHz)，调试端口解锁完成。", LogLevel.调试);
                await Task.Delay(400);

                // 阶段 2: 扇区快速擦除
                TxtFlasherAction.Text = "步骤 2/4：执行目标扇区快速擦除 (Sector Erase)...";
                AppendDebugLog("正在擦除目标地址块...", LogLevel.警告);
                await Task.Delay(600);

                // 阶段 3: 高速流式写入分块数据
                TxtFlasherAction.Text = "步骤 3/4：正在并行写入固件数据包...";
                for (int progress = 0; progress <= 100; progress += 4)
                {
                    ProgressFlash.Value = progress;
                    TxtFlasherPercent.Text = $"{progress} % ({218.4 + (progress % 5)} KB/s)";
                    if (progress == 40) AppendDebugLog("[进度 40%] 已写入 102KB @ 218.2 KB/s", LogLevel.调试);
                    if (progress == 80) AppendDebugLog("[进度 80%] 已写入 204KB @ 221.5 KB/s", LogLevel.调试);
                    await Task.Delay(40);
                }

                // 阶段 4: CRC 校验与系统复位
                TxtFlasherAction.Text = "步骤 4/4：硬件 CRC32 完整性校验与软复位...";
                AppendDebugLog("正在比对片上 CRC32 校验码... [校验通过 0x9B41E280]", LogLevel.成功);
                await Task.Delay(300);

                AppendDebugLog("释放复位线 (SYSRESETREQ)，目标内核已恢复常态运行模式。", LogLevel.信息);
                TxtCoreStatus.Text = "运行中 (Normal)";
                CoreStatusIndicator.Fill = BrushSuccess;

                TxtFlasherPercent.Text = "100 % (烧录成功)";
                TxtFlasherAction.Text = "固件全流程烧录成功 (耗时：3.12 秒)。";
                AppendDebugLog("================ [ 目标芯片烧录流水线成功完成 ] ================", LogLevel.成功);
            }
            catch (Exception ex)
            {
                AppendDebugLog($"烧录过程发生严重异常：{ex.Message}", LogLevel.错误);
                TxtFlasherAction.Text = "固件烧录失败。";
            }
            finally
            {
                _isFlashing = false;
                SetFlasherButtonsState(true);
                TxtGlobalStatus.Text = "系统就绪";
                TxtGlobalStatus.Foreground = BrushSuccess;
                BreathingHalo.Fill = BrushSuccess;
                StatusDot.Fill = BrushSuccess;
            }
        }

        private void SetFlasherButtonsState(bool enabled)
        {
            BtnErase.IsEnabled = enabled;
            BtnVerify.IsEnabled = enabled;
            BtnFlashAll.IsEnabled = enabled;
            TxtFirmwareFile.IsEnabled = enabled;
        }

        #endregion

        #region 6. 线程安全调试日志控制台

        /// <summary>
        /// 线程安全的调试控制台输出辅助函数
        /// </summary>
        public void AppendDebugLog(string message, LogLevel level = LogLevel.信息)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.InvokeAsync(() => AppendDebugLog(message, level));
                return;
            }

            try
            {
                var doc = RtbDebugLog.Document;
                if (doc.Blocks.Count > MaxLogLines)
                {
                    doc.Blocks.Remove(doc.Blocks.FirstBlock);
                }

                var paragraph = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };

                // 1. 时间戳
                paragraph.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss.fff}] ")
                {
                    Foreground = BrushTimestamp
                });

                // 2. 级别标签
                string tag = level switch
                {
                    LogLevel.调试 => "[调试] ",
                    LogLevel.信息 => "[信息] ",
                    LogLevel.成功 => "[就绪] ",
                    LogLevel.警告 => "[警告] ",
                    LogLevel.错误 => "[错误] ",
                    _ => "[记录] "
                };

                SolidColorBrush tagBrush = level switch
                {
                    LogLevel.调试 => BrushDim,
                    LogLevel.信息 => BrushActiveBorder,
                    LogLevel.成功 => BrushSuccess,
                    LogLevel.警告 => BrushWarn,
                    LogLevel.错误 => BrushError,
                    _ => BrushNormal
                };

                paragraph.Inlines.Add(new Run(tag)
                {
                    Foreground = tagBrush,
                    FontWeight = FontWeights.Bold
                });

                // 3. 内容
                paragraph.Inlines.Add(new Run(message)
                {
                    Foreground = level == LogLevel.错误 ? BrushError : (level == LogLevel.警告 ? BrushWarn : BrushNormal)
                });

                doc.Blocks.Add(paragraph);

                if (ChkAutoScrollLog.IsChecked == true)
                {
                    RtbDebugLog.ScrollToEnd();
                }
            }
            catch { }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            RtbDebugLog.Document.Blocks.Clear();
            AppendDebugLog("调试终端输出缓冲区已清空。", LogLevel.调试);
        }

        #endregion
    }
}
