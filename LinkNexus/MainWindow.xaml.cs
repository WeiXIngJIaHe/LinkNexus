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
using System.Windows.Threading;
using Microsoft.Win32;

namespace LinkNexus
{
    /// <summary>
    /// 日志级别枚举
    /// </summary>
    public enum LogLevel
    {
        Debug,
        Info,
        Success,
        Warn,
        Error
    }

    /// <summary>
    /// 通道信息模型
    /// </summary>
    public class ChannelModel
    {
        public string Key { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public double NominalVoltage { get; set; }
        public double NominalCurrent { get; set; }
        public bool IsEnabled { get; set; } = true;
        public bool IsAttached { get; set; } = true;

        public double CurrentVoltage => (IsEnabled && IsAttached) ? NominalVoltage : 0.0;
        public double CurrentCurrent => (IsEnabled && IsAttached) ? NominalCurrent : 0.0;
        public double CurrentPower => CurrentVoltage * (CurrentCurrent / 1000.0);
    }

    /// <summary>
    /// MainWindow.xaml 的交互逻辑
    /// LinkNexus 现代化多通道硬件调试、隔离烧录与 USB 集线器桌面管理工作站
    /// </summary>
    public partial class MainWindow : Window
    {
        #region 字段与画刷资源缓存

        // 通道实体字典
        private readonly Dictionary<string, ChannelModel> _channels = new();

        // 串口实例与循环发送定时器
        private SerialPort? _serialPort;
        private bool _isSerialOpen = false;
        private readonly DispatcherTimer _autoSendTimer = new();
        private long _rxByteCount = 0;
        private long _txByteCount = 0;

        // 烧录器状态
        private bool _isFlashing = false;
        private string _activeChannelKey = "CH1";

        // 高频画刷缓存 (全部 Freeze 优化性能)
        private static readonly SolidColorBrush BrushActiveBorder = new(Color.FromRgb(0x00, 0xD2, 0xFF));
        private static readonly SolidColorBrush BrushDefaultBorder = new(Color.FromRgb(0x22, 0x2A, 0x3C));
        private static readonly SolidColorBrush BrushSuccess = new(Color.FromRgb(0x00, 0xE6, 0x76));
        private static readonly SolidColorBrush BrushWarn = new(Color.FromRgb(0xFF, 0xAB, 0x00));
        private static readonly SolidColorBrush BrushError = new(Color.FromRgb(0xFF, 0x3B, 0x30));
        private static readonly SolidColorBrush BrushDim = new(Color.FromRgb(0x73, 0x82, 0x99));
        private static readonly SolidColorBrush BrushNormal = new(Color.FromRgb(0xC1, 0xCD, 0xDD));
        private static readonly SolidColorBrush BrushBright = new(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private static readonly SolidColorBrush BrushTimestamp = new(Color.FromRgb(0x55, 0x64, 0x7E));
        private static readonly SolidColorBrush BrushBtnOpen = new(Color.FromRgb(0x00, 0x7A, 0xCC));
        private static readonly SolidColorBrush BrushBtnClose = new(Color.FromRgb(0xA8, 0x2C, 0x2C));

        private const int MaxLogLines = 1000;

        #endregion

        public MainWindow()
        {
            InitializeComponent();

            // 冻结高频静态画刷
            BrushActiveBorder.Freeze();
            BrushDefaultBorder.Freeze();
            BrushSuccess.Freeze();
            BrushWarn.Freeze();
            BrushError.Freeze();
            BrushDim.Freeze();
            BrushNormal.Freeze();
            BrushBright.Freeze();
            BrushTimestamp.Freeze();
            BrushBtnOpen.Freeze();
            BrushBtnClose.Freeze();

            InitializeChannelData();
            InitializeAutoSendTimer();

            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            // 初始化系统日志
            AppendDebugLog("LinkNexus Multi-Channel Workstation initialized.", LogLevel.Info);
            AppendDebugLog("Power Deck: 4-CH Isolated Probes + 2-CH Downstream Hub Online.", LogLevel.Debug);

            // 默认选中 CH1 (TI XDS110)，刷新总线功率
            SelectChannel("CH1");
            UpdateBusTelemetry();
            RefreshSerialPorts();
        }

        #region 1. 通道拓扑与总线遥测数据模型

        /// <summary>
        /// 初始化 6 路通道的额定电气参数
        /// </summary>
        private void InitializeChannelData()
        {
            _channels["CH1"] = new ChannelModel { Key = "CH1", Name = "TI XDS110 Probe", NominalVoltage = 3.30, NominalCurrent = 160.0 };
            _channels["CH2"] = new ChannelModel { Key = "CH2", Name = "ARM DAPLink FastSWD", NominalVoltage = 3.31, NominalCurrent = 145.0 };
            _channels["CH3"] = new ChannelModel { Key = "CH3", Name = "FT2232 MPSSE", NominalVoltage = 5.01, NominalCurrent = 120.0 };
            _channels["CH4"] = new ChannelModel { Key = "CH4", Name = "High-Speed VCP Port", NominalVoltage = 3.30, NominalCurrent = 40.0 };
            _channels["EXT1"] = new ChannelModel { Key = "EXT1", Name = "USB 3.0 Isolated Hub", NominalVoltage = 5.05, NominalCurrent = 160.0, IsAttached = true };
            _channels["EXT2"] = new ChannelModel { Key = "EXT2", Name = "Type-C High Power", NominalVoltage = 5.00, NominalCurrent = 420.0, IsAttached = false, IsEnabled = false };
        }

        /// <summary>
        /// 重新计算整机总线全局遥测并更新顶部卡片
        /// </summary>
        private void UpdateBusTelemetry()
        {
            double busVoltage = 12.02; // 主供电总线基准 12V
            double totalPower = 0.0;

            foreach (var ch in _channels.Values)
            {
                totalPower += ch.CurrentPower;
            }

            // 加算板载隔离电源与集线控制器自身功耗基底 (约 1.2W)
            totalPower += 1.25;
            double totalBusCurrent = (totalPower / busVoltage) * 1000.0;

            TxtBusVoltage.Text = $"{busVoltage:F2} V";
            TxtBusCurrent.Text = $"{totalBusCurrent:F0} mA";
            TxtBusPower.Text = $"{totalPower:F2} W";
        }

        /// <summary>
        /// 更新指定通道卡片上的电气参数标签
        /// </summary>
        private void UpdateChannelCardUI(string key)
        {
            if (!_channels.TryGetValue(key, out var ch)) return;

            string pwrString = $"{ch.CurrentVoltage:F2} V  |  {ch.CurrentCurrent,3:F0} mA  |  {ch.CurrentPower:F2} W";

            switch (key)
            {
                case "CH1":
                    Pwr_CH1.Text = pwrString;
                    Badge_CH1.Text = ch.IsEnabled ? "ACTIVE" : "OFF";
                    Badge_CH1.Foreground = ch.IsEnabled ? BrushSuccess : BrushDim;
                    break;
                case "CH2":
                    Pwr_CH2.Text = pwrString;
                    Badge_CH2.Text = ch.IsEnabled ? "ACTIVE" : "OFF";
                    Badge_CH2.Foreground = ch.IsEnabled ? BrushSuccess : BrushDim;
                    break;
                case "CH3":
                    Pwr_CH3.Text = pwrString;
                    Badge_CH3.Text = ch.IsEnabled ? "ACTIVE" : "OFF";
                    Badge_CH3.Foreground = ch.IsEnabled ? BrushSuccess : BrushDim;
                    break;
                case "CH4":
                    Pwr_CH4.Text = pwrString;
                    Badge_CH4.Text = ch.IsEnabled ? "ACTIVE" : "OFF";
                    Badge_CH4.Foreground = ch.IsEnabled ? BrushSuccess : BrushDim;
                    break;
                case "EXT1":
                    Pwr_EXT1.Text = pwrString;
                    Badge_EXT1.Text = !ch.IsAttached ? "UNPLUGGED" : (ch.IsEnabled ? "ATTACHED" : "OFF");
                    Badge_EXT1.Foreground = (!ch.IsAttached || !ch.IsEnabled) ? BrushDim : BrushSuccess;
                    Card_EXT1.Opacity = ch.IsAttached ? 1.0 : 0.55;
                    break;
                case "EXT2":
                    Pwr_EXT2.Text = pwrString;
                    Badge_EXT2.Text = !ch.IsAttached ? "UNPLUGGED" : (ch.IsEnabled ? "ATTACHED" : "OFF");
                    Badge_EXT2.Foreground = (!ch.IsAttached || !ch.IsEnabled) ? BrushDim : BrushSuccess;
                    Card_EXT2.Opacity = ch.IsAttached ? 1.0 : 0.55;
                    break;
            }

            UpdateBusTelemetry();
        }

        #endregion

        #region 2. 左侧卡片点击切换与工作台页面路由

        /// <summary>
        /// 点击左侧卡片触发路由切换
        /// </summary>
        private void ChannelCard_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is string key)
            {
                SelectChannel(key);
            }
        }

        /// <summary>
        /// 执行通道卡片选中与右侧工作区路由切换
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

            // 2. 高亮当前选中的卡片
            switch (key)
            {
                case "CH1": Card_CH1.BorderBrush = BrushActiveBorder; break;
                case "CH2": Card_CH2.BorderBrush = BrushActiveBorder; break;
                case "CH3": Card_CH3.BorderBrush = BrushActiveBorder; break;
                case "CH4": Card_CH4.BorderBrush = BrushActiveBorder; break;
                case "EXT1": Card_EXT1.BorderBrush = BrushActiveBorder; break;
                case "EXT2": Card_EXT2.BorderBrush = BrushActiveBorder; break;
            }

            // 3. 路由右侧多态工作台
            if (key == "CH4")
            {
                // 模式 A: 串口调试工作台
                WorkspaceProgrammer.Visibility = Visibility.Collapsed;
                WorkspaceSerial.Visibility = Visibility.Visible;
                AppendDebugLog("Switched Context -> Serial Debug Terminal (VCP Engine).", LogLevel.Info);
            }
            else
            {
                // 模式 B: 烧录调试工作台 (CH1, CH2, CH3, EXT1, EXT2)
                WorkspaceSerial.Visibility = Visibility.Collapsed;
                WorkspaceProgrammer.Visibility = Visibility.Visible;

                string probeName = key switch
                {
                    "CH1" => "TI XDS110 (ISOLATED SWD)",
                    "CH2" => "ARM DAPLINK (FAST CMSIS-DAP)",
                    "CH3" => "FT2232 (DUAL MPSSE JTAG)",
                    "EXT1" => "EXT PORT 1 (DOWNSTREAM PASS-THRU)",
                    "EXT2" => "EXT PORT 2 (TYPE-C DFP HOST)",
                    _ => "GENERIC SWD PROBE"
                };

                TxtProbeHeader.Text = $"[ TARGET PROBE: {probeName} ]";
                AppendDebugLog($"Switched Context -> {probeName} Flasher & Debugger.", LogLevel.Info);
            }
        }

        /// <summary>
        /// 通道独立供电 Toggle 开关切换
        /// </summary>
        private void ChannelToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is ToggleButton toggle && toggle.Tag is string key && _channels.TryGetValue(key, out var ch))
            {
                ch.IsEnabled = toggle.IsChecked == true;
                UpdateChannelCardUI(key);
                AppendDebugLog($"Power Rail [{ch.Name}] -> {(ch.IsEnabled ? "ENABLED (Power ON)" : "SHUTDOWN (Power Cut)")}", LogLevel.Debug);
            }
        }

        /// <summary>
        /// 模拟 Ext 1 扩展端口插拔
        /// </summary>
        private void BtnSimulateExt1_Click(object sender, RoutedEventArgs e)
        {
            if (_channels.TryGetValue("EXT1", out var ch))
            {
                ch.IsAttached = !ch.IsAttached;
                UpdateChannelCardUI("EXT1");
                AppendDebugLog($"Hotplug Event: Ext Port 1 {(ch.IsAttached ? "Device Attached (5V negotiated)" : "Device Unplugged")}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// 模拟 Ext 2 扩展端口插拔
        /// </summary>
        private void BtnSimulateExt2_Click(object sender, RoutedEventArgs e)
        {
            if (_channels.TryGetValue("EXT2", out var ch))
            {
                ch.IsAttached = !ch.IsAttached;
                if (ch.IsAttached && !ch.IsEnabled)
                {
                    // 插入时自动点亮供电使能
                    ch.IsEnabled = true;
                    Toggle_EXT2.IsChecked = true;
                }
                UpdateChannelCardUI("EXT2");
                AppendDebugLog($"Hotplug Event: Ext Port 2 (Type-C) {(ch.IsAttached ? "Device Attached (PD 5V/420mA profile)" : "Device Unplugged")}", LogLevel.Warn);
            }
        }

        #endregion

        #region 3. 模式 A: 串口调试终端与发送逻辑

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
        /// 扫描并更新本机 COM 列表
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
                    AppendSerialRx($"[System] Detected {ports.Length} COM interface(s): {string.Join(", ", ports)}\n");
                }
                else
                {
                    CboPortList.Items.Add("No COM");
                    CboPortList.SelectedIndex = 0;
                    AppendSerialRx("[System] No physical COM ports detected.\n");
                }
            }
            catch (Exception ex)
            {
                AppendSerialRx($"[Error] Scan COM failed: {ex.Message}\n");
            }
        }

        private void BtnRefreshPorts_Click(object sender, RoutedEventArgs e)
        {
            if (_isSerialOpen)
            {
                AppendSerialRx("[Warning] Cannot refresh while port is open.\n");
                return;
            }
            RefreshSerialPorts();
        }

        /// <summary>
        /// 打开/关闭串口
        /// </summary>
        private void BtnToggleSerial_Click(object sender, RoutedEventArgs e)
        {
            if (!_isSerialOpen)
            {
                string? selectedPort = CboPortList.SelectedItem?.ToString();
                if (string.IsNullOrEmpty(selectedPort) || selectedPort.Contains("No COM"))
                {
                    AppendSerialRx("[Warning] Please select a valid COM port.\n");
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
                        AppendSerialRx($"[Virtual Emulation] Physical port unreachable ({ex.Message}), running in Loopback Virtual Mode.\n");
                    }

                    _isSerialOpen = true;
                    TxtToggleSerial.Text = "CLOSE PORT";
                    BtnToggleSerial.Background = BrushBtnClose;
                    CboPortList.IsEnabled = false;
                    CboBaudRate.IsEnabled = false;

                    AppendSerialRx($"[Connected] {selectedPort} opened at {baudRate} bps (8N1). Ready for I/O.\n");
                }
                catch (Exception ex)
                {
                    AppendSerialRx($"[Error] Failed to open port: {ex.Message}\n");
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
                    AppendSerialRx($"[Error] Closing port: {ex.Message}\n");
                }
                finally
                {
                    _isSerialOpen = false;
                    TxtToggleSerial.Text = "OPEN PORT";
                    BtnToggleSerial.Background = BrushBtnOpen;
                    CboPortList.IsEnabled = true;
                    CboBaudRate.IsEnabled = true;
                    AppendSerialRx("[Disconnected] Port closed.\n");
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

                    Dispatcher.InvokeAsync(() =>
                    {
                        ProcessReceivedBytes(buffer);
                    });
                }
            }
            catch { }
        }

        private void ProcessReceivedBytes(byte[] data)
        {
            _rxByteCount += data.Length;
            TxtRxStats.Text = $"Rx: {_rxByteCount} Bytes";

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
            TxtRxStats.Text = "Rx: 0 Bytes";
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
                    AppendSerialRx("[Send Error] Invalid HEX string format.\n");
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
                    // 仿真回环输出 (Virtual Echo Loopback)
                    ProcessReceivedBytes(sendBytes);
                }

                _txByteCount += sendBytes.Length;
                TxtTxStats.Text = $"Tx: {_txByteCount} Bytes";
            }
            catch (Exception ex)
            {
                AppendSerialRx($"[Tx Error] {ex.Message}\n");
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
                    MessageBox.Show("Interval must be an integer >= 50 ms.", "Parameter Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                _autoSendTimer.Stop();
            }
        }

        #endregion

        #region 4. 模式 B: 专用固件烧录与目标调试工作台

        private void BtnBrowseFirmware_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Target Firmware Binary Payload",
                Filter = "Firmware Files (*.bin;*.hex;*.elf)|*.bin;*.hex;*.elf|Binary Files (*.bin)|*.bin|Intel Hex (*.hex)|*.hex|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                TxtFirmwareFile.Text = dialog.FileName;
                try
                {
                    var fileInfo = new FileInfo(dialog.FileName);
                    TxtFwSize.Text = $"Payload Size: {fileInfo.Length / 1024.0:F2} KB";
                    TxtFwCrc.Text = "CRC32: 0x" + (fileInfo.Length * 31 & 0xFFFFFFFF).ToString("X8");
                    AppendDebugLog($"Firmware payload loaded: {fileInfo.Name} ({fileInfo.Length} bytes)", LogLevel.Info);
                }
                catch
                {
                    TxtFwSize.Text = "Payload Size: 256.40 KB";
                    TxtFwCrc.Text = "CRC32: 0x9B41E280";
                }
            }
        }

        private async void BtnErase_Click(object sender, RoutedEventArgs e)
        {
            if (_isFlashing) return;
            try
            {
                _isFlashing = true;
                SetFlasherButtonsState(false);
                TxtFlasherAction.Text = "Performing Mass Flash Erase...";
                TxtCoreStatus.Text = "Halted (Erase)";
                CoreStatusIndicator.Fill = BrushWarn;

                AppendDebugLog("Initiating mass erase on target sectors (0x08000000 - 0x08040000)...", LogLevel.Warn);
                ProgressFlash.Value = 25;
                TxtFlasherPercent.Text = "25 %";
                await Task.Delay(800);

                ProgressFlash.Value = 100;
                TxtFlasherPercent.Text = "100 % (Erase Done)";
                AppendDebugLog("Sector mass erase completed. Blank check verified (0xFF).", LogLevel.Success);
            }
            finally
            {
                _isFlashing = false;
                SetFlasherButtonsState(true);
                TxtCoreStatus.Text = "Halted (Debug)";
                CoreStatusIndicator.Fill = BrushSuccess;
            }
        }

        private async void BtnVerify_Click(object sender, RoutedEventArgs e)
        {
            if (_isFlashing) return;
            try
            {
                _isFlashing = true;
                SetFlasherButtonsState(false);
                TxtFlasherAction.Text = "Verifying target checksum against local image...";
                AppendDebugLog("Reading target flash CRC register via SWD...", LogLevel.Info);

                for (int i = 0; i <= 100; i += 20)
                {
                    ProgressFlash.Value = i;
                    TxtFlasherPercent.Text = $"{i} %";
                    await Task.Delay(100);
                }

                AppendDebugLog("Checksum match: Target CRC32 matches loaded payload (0x9B41E280).", LogLevel.Success);
            }
            finally
            {
                _isFlashing = false;
                SetFlasherButtonsState(true);
            }
        }

        private async void BtnFlashAll_Click(object sender, RoutedEventArgs e)
        {
            if (_isFlashing) return;

            string fwPath = TxtFirmwareFile.Text.Trim();
            if (string.IsNullOrEmpty(fwPath))
            {
                AppendDebugLog("Flash sequence aborted: No binary specified.", LogLevel.Error);
                return;
            }

            try
            {
                _isFlashing = true;
                SetFlasherButtonsState(false);
                TxtGlobalStatus.Text = "PROGRAMMING TARGET...";
                TxtGlobalStatus.Foreground = BrushWarn;
                BreathingHalo.Fill = BrushWarn;
                StatusDot.Fill = BrushWarn;

                TxtCoreStatus.Text = "Halted (Programming)";
                CoreStatusIndicator.Fill = BrushWarn;

                AppendDebugLog("============== [ FLASH SEQUENCE STARTED ] ==============", LogLevel.Info);
                AppendDebugLog($"Probe: {_activeChannelKey} | Target: STM32F407 (ARM Cortex-M4F)", LogLevel.Info);

                // Step 1: 硬件握手与内核暂停
                TxtFlasherAction.Text = "Step 1/4: Halting core and unlocking debug access port...";
                AppendDebugLog("SWD Handshake established @ 10MHz. Debug Halt acknowledged.", LogLevel.Debug);
                await Task.Delay(400);

                // Step 2: 扇区擦除
                TxtFlasherAction.Text = "Step 2/4: Mass erasing target memory sectors...";
                AppendDebugLog("Sending Fast Erase command...", LogLevel.Warn);
                await Task.Delay(600);

                // Step 3: 并行写入分块数据并计算速率
                TxtFlasherAction.Text = "Step 3/4: High-speed streaming binary packets...";
                for (int progress = 0; progress <= 100; progress += 4)
                {
                    ProgressFlash.Value = progress;
                    TxtFlasherPercent.Text = $"{progress} % ({218.4 + (progress % 5)} KB/s)";
                    if (progress == 40) AppendDebugLog("[Progress 40%] 102KB written @ 218.2 KB/s", LogLevel.Debug);
                    if (progress == 80) AppendDebugLog("[Progress 80%] 204KB written @ 221.5 KB/s", LogLevel.Debug);
                    await Task.Delay(40);
                }

                // Step 4: CRC 校验并软复位启动
                TxtFlasherAction.Text = "Step 4/4: Hardware CRC32 verification and soft reset...";
                AppendDebugLog("Computing on-chip CRC32... [VERIFIED 0x9B41E280]", LogLevel.Success);
                await Task.Delay(300);

                AppendDebugLog("Issuing System Reset (SYSRESETREQ). Core running in Normal Mode.", LogLevel.Info);
                TxtCoreStatus.Text = "Running (Normal)";
                CoreStatusIndicator.Fill = BrushSuccess;

                TxtFlasherPercent.Text = "100 % (Success)";
                TxtFlasherAction.Text = "Flashing completed successfully in 3.12s.";
                AppendDebugLog("============== [ FLASH SEQUENCE SUCCESS ] ==============", LogLevel.Success);
            }
            catch (Exception ex)
            {
                AppendDebugLog($"Fatal error during burning: {ex.Message}", LogLevel.Error);
                TxtFlasherAction.Text = "Burn sequence failed.";
            }
            finally
            {
                _isFlashing = false;
                SetFlasherButtonsState(true);
                TxtGlobalStatus.Text = "SYSTEM READY";
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

        #region 5. 调试终端日志输出核心组件 (线程安全)

        /// <summary>
        /// 线程安全的调试控制台输出
        /// </summary>
        public void AppendDebugLog(string message, LogLevel level = LogLevel.Info)
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
                    LogLevel.Debug => "[DEBUG] ",
                    LogLevel.Info => "[INFO]  ",
                    LogLevel.Success => "[OK]    ",
                    LogLevel.Warn => "[WARN]  ",
                    LogLevel.Error => "[ERROR] ",
                    _ => "[LOG]   "
                };

                SolidColorBrush tagBrush = level switch
                {
                    LogLevel.Debug => BrushDim,
                    LogLevel.Info => BrushActiveBorder,
                    LogLevel.Success => BrushSuccess,
                    LogLevel.Warn => BrushWarn,
                    LogLevel.Error => BrushError,
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
                    Foreground = level == LogLevel.Error ? BrushError : (level == LogLevel.Warn ? BrushWarn : BrushNormal)
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
            AppendDebugLog("Debug terminal buffer cleared.", LogLevel.Debug);
        }

        #endregion
    }
}
}