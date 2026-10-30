using System;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus
{
    /// <summary>
    /// ESP32-S3 下位机全双工通信服务
    /// 负责 USB-CDC 串口通信、流式状态机解包、INA236 遥测分发与 1.9 寸屏幕状态同步
    /// </summary>
    public class Esp32CommService : IDisposable
    {
        private static readonly Lazy<Esp32CommService> _instance = new(() => new Esp32CommService());
        public static Esp32CommService Instance => _instance.Value;

        private SerialPort? _serialPort;
        private System.Net.Sockets.TcpClient? _tcpClient;
        private readonly ProtocolEngine _protocolEngine = new();
        private CancellationTokenSource? _cts;
        private Task? _readTask;
        private readonly object _sendLock = new();

        private readonly System.Timers.Timer _heartbeatTimer = new(1000);
        private readonly System.Timers.Timer _simTelemetryTimer = new(200);

        private DateTime _lastReceiveTime = DateTime.MinValue;
        private byte _heartbeatSeq = 0;
        private bool _isDisposed = false;

        public bool IsConnected { get; private set; } = false;
        public bool IsSimulationMode { get; private set; } = true;
        public string ConnectedPortName { get; private set; } = "仿真模式 (未连接硬件)";
        public int BaudRate { get; private set; } = 115200;

        // 遥测事件与通信状态通知
        public event Action<TelemetryPacketData>? TelemetryReceived;
        public event Action<bool, string>? ConnectionStateChanged;
        public event Action<string>? LogMessage;
        public event Action<byte, bool>? PowerControlAckReceived;
        public event Action<byte>? HeartbeatAckReceived;
        public event Action<byte, string?>? HeartbeatAckReceivedWithMac;
        public event Action<string>? CliResponseReceived;
        public event Action<string>? RawTextReceived;
        public event Action<Esp32WifiStatus>? WifiStatusReceived;

        public Esp32CommService()
        {
            _protocolEngine.FrameReceived += OnFrameReceived;

            _heartbeatTimer.Elapsed += (s, e) => HeartbeatTimerTick();
            _heartbeatTimer.AutoReset = true;

            // 仿真数据定时生成器 (当无物理下位机接入时平滑驱动 UI 展现)
            _simTelemetryTimer.Elapsed += (s, e) => GenerateSimulatedTelemetry();
            _simTelemetryTimer.AutoReset = true;
            _simTelemetryTimer.Start();
        }

        /// <summary>
        /// 启动自动连接或连接指定端口
        /// </summary>
        public void StartAutoConnect(string preferredPort = "COM_AUTO", int baudRate = 115200)
        {
            BaudRate = baudRate;
            string targetPort = preferredPort;

            if (targetPort == "COM_AUTO")
            {
                // 自动嗅探可用端口
                string[] ports = SerialPort.GetPortNames();
                if (ports.Length > 0)
                {
                    targetPort = ports[0];
                }
                else
                {
                    LogMessage?.Invoke("[下位机通信] 未检测到物理 USB-CDC 串口，系统自动启用仿真演示引擎。");
                    SetSimulationMode(true, "未连接物理硬件 (仿真模式)");
                    return;
                }
            }

            Connect(targetPort, baudRate);
        }

        /// <summary>
        /// 连接指定 COM 端口
        /// </summary>
        public bool Connect(string portName, int baudRate = 115200)
        {
            Disconnect();
            if (string.IsNullOrWhiteSpace(portName) || portName == "--")
            {
                SetSimulationMode(true, "未指定有效端口 (仿真模式)");
                return false;
            }

            try
            {
                BaudRate = baudRate;
                _serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = SerialPort.InfiniteTimeout,
                    WriteTimeout = 1500,
                    DtrEnable = false,
                    RtsEnable = false,
                    Handshake = Handshake.None,
                    Encoding = System.Text.Encoding.UTF8
                };

                _serialPort.Open();
                _cts = new CancellationTokenSource();
                _readTask = Task.Run(() => ReadStreamLoopAsync(_serialPort.BaseStream, () => _serialPort != null && _serialPort.IsOpen, _cts.Token));

                IsConnected = true;
                IsSimulationMode = false;
                _simTelemetryTimer.Stop(); // 物理硬件在线，停止仿真
                ConnectedPortName = portName;
                _lastReceiveTime = DateTime.Now;

                _heartbeatTimer.Start();
                ConnectionStateChanged?.Invoke(true, $"已连接 ESP32-S3 ({portName})");
                LogMessage?.Invoke($"[主控通信] 串口 {portName} 已连接 (波特率: {baudRate})");

                // 发送初次握手包
                SendHeartbeat(0x01);
                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[主控通信] 打开串口 {portName} 失败: {ex.Message}，切换至仿真模式");
                SetSimulationMode(true, "打开失败 (仿真模式)");
                return false;
            }
        }

        /// <summary>
        /// 通过 Wi-Fi TCP 无线调试端口 (默认 8266) 直连 ESP32-S3 下位机
        /// </summary>
        public bool ConnectTcp(string host, int port = 8266)
        {
            Disconnect();

            try
            {
                _tcpClient = new System.Net.Sockets.TcpClient();
                var connectTask = _tcpClient.ConnectAsync(host, port);
                if (!connectTask.Wait(2500))
                {
                    _tcpClient.Close();
                    _tcpClient = null;
                    LogMessage?.Invoke($"[无线调试] 连接目标 {host}:{port} 超时 (2500ms)，切换至仿真模式。");
                    SetSimulationMode(true, "无线调试连接超时 (仿真模式)");
                    return false;
                }

                _cts = new CancellationTokenSource();
                _readTask = Task.Run(() => ReadStreamLoopAsync(_tcpClient.GetStream(), () => _tcpClient != null && _tcpClient.Connected, _cts.Token));

                IsConnected = true;
                IsSimulationMode = false;
                _simTelemetryTimer.Stop();
                ConnectedPortName = $"TCP://{host}:{port}";
                _lastReceiveTime = DateTime.Now;

                _heartbeatTimer.Start();
                ConnectionStateChanged?.Invoke(true, $"已通过无线调试连接 ESP32-S3 ({host}:{port})");
                LogMessage?.Invoke($"[无线调试] ESP32-S3 TCP 链路已建立 ({host}:{port})");

                SendHeartbeat(0x01);
                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[无线调试] 连接失败: {ex.Message}，切换至仿真模式");
                SetSimulationMode(true, "连接异常 (仿真模式)");
                return false;
            }
        }

        /// <summary>
        /// 断开当前通信连接并安全复位接收管道
        /// </summary>
        public void Disconnect()
        {
            _heartbeatTimer.Stop();

            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }

            if (_serialPort != null)
            {
                try
                {
                    if (_serialPort.IsOpen)
                    {
                        _serialPort.Close();
                    }
                }
                catch { }
                finally
                {
                    _serialPort.Dispose();
                    _serialPort = null;
                }
            }

            if (_tcpClient != null)
            {
                try
                {
                    _tcpClient.Close();
                }
                catch { }
                finally
                {
                    _tcpClient.Dispose();
                    _tcpClient = null;
                }
            }

            _protocolEngine.Reset();
            IsConnected = false;
        }

        /// <summary>
        /// 切换仿真模式状态
        /// </summary>
        public void SetSimulationMode(bool isSim, string desc)
        {
            IsSimulationMode = isSim;
            ConnectedPortName = desc;

            if (isSim)
            {
                if (!_simTelemetryTimer.Enabled)
                {
                    _simTelemetryTimer.Start();
                }
            }
            ConnectionStateChanged?.Invoke(!isSim, desc);
        }

        /// <summary>
        /// 异步数据流读取循环，完全独立于 UI 主线程，兼容串口与 TCP 网络流
        /// </summary>
        private async Task ReadStreamLoopAsync(System.IO.Stream stream, Func<bool> isAlive, CancellationToken token)
        {
            byte[] readBuffer = new byte[512];

            while (!token.IsCancellationRequested && isAlive())
            {
                try
                {
                    int bytesRead = await stream.ReadAsync(readBuffer, 0, readBuffer.Length, token);
                    if (bytesRead > 0)
                    {
                        _lastReceiveTime = DateTime.Now;
                        _protocolEngine.ParseBytes(readBuffer, 0, bytesRead);
                        ProcessRawStreamText(readBuffer, bytesRead);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!token.IsCancellationRequested)
                    {
                        // 忽略偶发的底层 Win32 信号灯超时或非致命抖动，短暂休眠后继续重试
                        if (ex is System.IO.IOException ioEx && (ioEx.Message.Contains("信号灯超时") || ioEx.Message.Contains("semaphore")))
                        {
                            try
                            {
                                await Task.Delay(80, token);
                                continue;
                            }
                            catch (OperationCanceledException)
                            {
                                break;
                            }
                        }

                        LogMessage?.Invoke($"[通信管道异常] 数据读取失败: {ex.Message}");
                    }
                    break;
                }
            }

            // 若异常退出且未主动取消，则标记离线
            if (!token.IsCancellationRequested && IsConnected)
            {
                SetSimulationMode(true, "连接丢失 (仿真模式)");
            }
        }

        /// <summary>
        /// 捕获并解析底层非二进制协议帧的纯文本字符流 (如 ESP-IDF 控制台文本、Boot Log 或 CLI 回显)
        /// </summary>
        private void ProcessRawStreamText(byte[] buffer, int count)
        {
            if (count <= 0) return;

            // 若整块数据为 0xAA 0x55 引导的二进制协议帧，则由 ProtocolEngine 独占解析
            if (count >= 4 && buffer[0] == ProtocolEngine.Header1 && buffer[1] == ProtocolEngine.Header2)
            {
                return;
            }

            // 统计可打印字符占比，过滤掉随机二进制噪音
            int printableCount = 0;
            for (int i = 0; i < count; i++)
            {
                byte b = buffer[i];
                if (b == '\r' || b == '\n' || b == '\t' || (b >= 32 && b <= 126) || b >= 0x80)
                {
                    printableCount++;
                }
            }

            if (printableCount > count / 2)
            {
                string text = Encoding.UTF8.GetString(buffer, 0, count);
                if (!string.IsNullOrEmpty(text))
                {
                    RawTextReceived?.Invoke(text);
                    CliResponseReceived?.Invoke(text);
                }
            }
        }

        /// <summary>
        /// 协议引擎成功解包回调
        /// </summary>
        private void OnFrameReceived(byte cmd, byte[] payload)
        {
            switch (cmd)
            {
                case ProtocolCommand.Heartbeat:
                    byte ack = payload.Length > 0 ? payload[0] : (byte)0x06;
                    string? mac = ProtocolEngine.ParseHeartbeatMacPayload(payload);
                    HeartbeatAckReceived?.Invoke(ack);
                    HeartbeatAckReceivedWithMac?.Invoke(ack, mac);
                    break;

                case ProtocolCommand.TelemetryReport:
                    var telemetry = ProtocolEngine.ParseTelemetryPayload(payload);
                    if (telemetry != null)
                    {
                        TelemetryReceived?.Invoke(telemetry);
                    }
                    break;

                case ProtocolCommand.PowerControl:
                    if (payload.Length >= 2)
                    {
                        byte chIndex = payload[0];
                        bool state = payload[1] != 0x00;
                        PowerControlAckReceived?.Invoke(chIndex, state);
                    }
                    break;

                case ProtocolCommand.WifiControl:
                    var wifiStatus = ProtocolEngine.ParseWifiStatusPayload(payload);
                    if (wifiStatus != null)
                    {
                        WifiStatusReceived?.Invoke(wifiStatus);
                    }
                    break;

                case ProtocolCommand.CliTransfer:
                    string cliText = Encoding.UTF8.GetString(payload);
                    CliResponseReceived?.Invoke(cliText);
                    break;
            }
        }

        /// <summary>
        /// 发送通用二进制帧 (支持 TCP 与串口双通道)
        /// </summary>
        public bool SendPacket(byte[] packet)
        {
            if (!IsConnected)
            {
                return false;
            }

            lock (_sendLock)
            {
                try
                {
                    if (_tcpClient != null && _tcpClient.Connected)
                    {
                        var stream = _tcpClient.GetStream();
                        stream.Write(packet, 0, packet.Length);
                        return true;
                    }

                    if (_serialPort != null && _serialPort.IsOpen)
                    {
                        _serialPort.Write(packet, 0, packet.Length);
                        return true;
                    }

                    return false;
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"[下位机发送错误] {ex.Message}");
                    return false;
                }
            }
        }

        /// <summary>
        /// 发送 0x01 心跳包
        /// </summary>
        public void SendHeartbeat(byte sequence = 0)
        {
            byte[] packet = ProtocolEngine.BuildHeartbeatPacket(sequence == 0 ? ++_heartbeatSeq : sequence);
            SendPacket(packet);
        }

        /// <summary>
        /// 发送 0x30 通道供电控制命令
        /// </summary>
        public void SendPowerControl(byte channelIndex, bool enable)
        {
            byte[] packet = ProtocolEngine.BuildPowerControlPacket(channelIndex, enable);
            bool sent = SendPacket(packet);

            LogMessage?.Invoke($"[供电控制] 通道 {channelIndex} -> {(enable ? "通电开启" : "断电关断")} {(sent ? "[已下发]" : "[仿真触发]")}");

            // 仿真模式下模拟下位机 ACK 反馈
            if (IsSimulationMode)
            {
                PowerControlAckReceived?.Invoke(channelIndex, enable);
            }
        }

        /// <summary>
        /// 发送 0x20 屏幕状态同步命令（推送到 1.9 寸 ST7789V3 液晶屏）
        /// </summary>
        public void SendScreenSync(byte activeChannelIndex, byte flashProgress, string deviceName, bool isFlashing = false)
        {
            byte[] packet = ProtocolEngine.BuildScreenSyncPacket(activeChannelIndex, flashProgress, deviceName, isFlashing);
            bool sent = SendPacket(packet);

            if (sent)
            {
                LogMessage?.Invoke($"[屏幕同步] 已将卡片通道 {activeChannelIndex} 与设备 [{deviceName}] 状态推送至 ESP32 屏幕");
            }
        }

        /// <summary>
        /// 发送 0x50 ESP32 CLI 文本命令并调度应答 (兼顾二进制帧与裸机串口字符终端)
        /// </summary>
        public bool SendCliCommand(string command)
        {
            // 1. 发送 0x50 标准二进制协议帧
            byte[] packet = ProtocolEngine.BuildCliPacket(command);
            bool sent = SendPacket(packet);

            // 2. 如果存在物理串口，同时追加发送纯文本换行指令 (兼顾原生 USB-Serial-JTAG 控制台与裸机终端)
            if (_serialPort != null && _serialPort.IsOpen)
            {
                try
                {
                    byte[] rawBytes = Encoding.UTF8.GetBytes(command + "\r\n");
                    _serialPort.Write(rawBytes, 0, rawBytes.Length);
                    sent = true;
                }
                catch (Exception ex)
                {
                    LogMessage?.Invoke($"[CLI串口发送异常] {ex.Message}");
                }
            }

            if (sent)
            {
                LogMessage?.Invoke($"[ESP32 CLI] 已向下位机发送命令: {command}");
            }
            return sent;
        }

        /// <summary>
        /// 异步执行 ESP32-S3 原生交互式命令行 (CLI) 并等待回显文本 (支持 0x50 二进制帧、物理串口与脱机仿真环境)
        /// </summary>
        /// <param name="command">CLI 命令行文本 (例如 "wifi status", "wifi connect JiaHe 12345678", "power status", "help")</param>
        /// <param name="timeoutMs">等待下位机回显响应的超时时间 (毫秒)，默认 5000ms</param>
        /// <returns>下位机返回的 CLI 输出文本</returns>
        public async Task<string> ExecuteCliCommandAsync(string command, int timeoutMs = 5000)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return "[Error] CLI 命令不能为空";
            }

            command = command.Trim();

            // 若尚未连接硬件且非纯仿真，尝试自动连接首个可用串口
            if (!IsConnected)
            {
                try
                {
                    string[] ports = SerialPort.GetPortNames();
                    if (ports.Length > 0)
                    {
                        string targetPort = ports.FirstOrDefault(p => p.Equals("COM9", StringComparison.OrdinalIgnoreCase)) ?? ports[0];
                        Connect(targetPort, BaudRate);
                        await Task.Delay(250);
                    }
                }
                catch
                {
                    // ignore
                }
            }

            // 若依然处于纯仿真模式 (无真实硬件接入)
            if (!IsConnected || IsSimulationMode)
            {
                return SimulateCliExecution(command);
            }

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sb = new StringBuilder();
            var lockObj = new object();
            CancellationTokenSource? debounceCts = null;

            void ResetDebounce()
            {
                lock (lockObj)
                {
                    debounceCts?.Cancel();
                    debounceCts?.Dispose();
                    debounceCts = new CancellationTokenSource();
                    var token = debounceCts.Token;

                    _ = Task.Delay(300, token).ContinueWith(t =>
                    {
                        if (!t.IsCanceled)
                        {
                            lock (lockObj)
                            {
                                tcs.TrySetResult(sb.ToString().Trim());
                            }
                        }
                    }, TaskScheduler.Default);
                }
            }

            Action<string> onChunk = chunk =>
            {
                lock (lockObj)
                {
                    sb.Append(chunk);
                    ResetDebounce();
                }
            };

            CliResponseReceived += onChunk;

            try
            {
                bool sent = SendCliCommand(command);
                if (!sent)
                {
                    return "[Error] 发送 CLI 指令失败 (底层传输通道未就绪)";
                }

                using var globalCts = new CancellationTokenSource(timeoutMs);
                using (globalCts.Token.Register(() =>
                {
                    lock (lockObj)
                    {
                        if (sb.Length > 0)
                        {
                            tcs.TrySetResult(sb.ToString().Trim());
                        }
                        else
                        {
                            tcs.TrySetResult($"[Timeout] 等待 ESP32-S3 CLI 响应超时 ({timeoutMs}ms)");
                        }
                    }
                }))
                {
                    return await tcs.Task;
                }
            }
            finally
            {
                CliResponseReceived -= onChunk;
                lock (lockObj)
                {
                    debounceCts?.Cancel();
                    debounceCts?.Dispose();
                    debounceCts = null;
                }
            }
        }

        /// <summary>
        /// 脱机仿真模式下的 CLI 原生响应模拟器
        /// </summary>
        private string SimulateCliExecution(string command)
        {
            string[] parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "[Error] 空指令";

            string root = parts[0].ToLowerInvariant();
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : string.Empty;

            if (root == "help")
            {
                return "================ LinkNexus ESP32-S3 CLI 指令手册 (esp_console) ================\r\n" +
                       "  help                               - 显示 CLI 指令帮助列表\r\n" +
                       "  wifi ap <ssid> [pwd] [ch] [save]   - 启动 SoftAP 热点 (可选 [save] 固化至 NVS)\r\n" +
                       "  wifi connect <ssid> [pwd] [save]   - 连接局域网 STA (可选 [save] 固化至 NVS)\r\n" +
                       "  wifi status                        - 查询当前 Wi-Fi 工作模式、射频功率与连接状态\r\n" +
                       "  wifi txpower [dbm] [save]          - 查询/设置发射功率 (8~20 dBm, 可选 [save])\r\n" +
                       "  wifi protocol [b|bg|bgn] [save]    - 设置调制协议 (b/bg/bgn, 可选 [save])\r\n" +
                       "  wifi channel <1-13> [save]         - 设置射频工作信道 (1~13, 可选 [save])\r\n" +
                       "  wifi scan [force]                  - 扫描周围可用 Wi-Fi 热点 (复用扫描缓存)\r\n" +
                       "  wifi disconnect                    - 断开 Wi-Fi 连接并关闭无线调试端口\r\n" +
                       "  wifi save                          - 将当前所有 Wi-Fi 运行时参数固化至 NVS\r\n" +
                       "  wifi reset                         - 清除 NVS 自定义配置并恢复出厂默认值\r\n" +
                       "  wifi config                        - 查看当前 NVS 持久化参数配置表\r\n" +
                       "  power <ch 0-5|all> <true|false>    - 控制指定通道/所有通道 MOS 开通/隔离\r\n" +
                       "  power status                       - 显示 6 通道当前供电状态与 NVS 默认 Mask\r\n" +
                       "  status                             - 查询 ESP32-S3 双核系统资源与运行状态\r\n" +
                       "  telemetry [true|false] [save]      - 查询遥测数据或控制二进制广播流\r\n" +
                       "  whoami                             - 查询设备身份标识与主控角色确认\r\n" +
                       "  reboot                             - 软复位重启 ESP32-S3\r\n" +
                       "  clear                              - 清空当前终端屏幕\r\n" +
                       "=================================================================================";
            }

            if (root == "wifi")
            {
                if (sub == "connect" || sub == "sta")
                {
                    string ssid = parts.Length > 2 ? parts[2] : "JiaHe";
                    return $"[SUCCESS] ESP32-S3 从 AP (Station) 正在连接目标网络 (仿真环境):\r\n" +
                           $"  目标 SSID : {ssid}\r\n" +
                           $"  发射功率  : 20.00 dBm (raw: 80)\r\n" +
                           $"  连接状态  : 握手成功 (已分配 IP: 10.95.213.162, 掩码: 255.255.255.0)\r\n" +
                           $"  无线调试服务 : 已启动 [LISTENING] (TCP :8266)";
                }
                if (sub == "status")
                {
                    return "ESP32-S3 Wi-Fi & 射频天线状态 (仿真模式):\r\n" +
                           "  硬件芯片     : ESP32-S3-WROOM-1-N16R8\r\n" +
                           "  天线射频电路 : 陶瓷贴片天线 + Π 型 0Ω 阻抗匹配网络 (50Ω 射频走线)\r\n" +
                           "  工作模式     : Station (从 AP / 已接入局域网)\r\n" +
                           "  当前 SSID    : JiaHe\r\n" +
                           "  IP 地址      : 10.95.213.162\r\n" +
                           "  信道频率     : CH6 (2437 MHz)\r\n" +
                           "  发射功率     : 13.00 dBm (raw: 52)\r\n" +
                           "  调制协议     : 802.11b/g/n (HT20)\r\n" +
                           "  RSSI 信号    : -5 dBm\r\n" +
                           "  已连客户端   : 1\r\n" +
                           "  无线调试服务 : 已启动 [LISTENING] (TCP :8266)";
                }
                if (sub == "ap")
                {
                    string ssid = parts.Length > 2 ? parts[2] : "LinkNexus-ESP32-AP";
                    return $"[SUCCESS] ESP32-S3 SoftAP 独立热点已成功启动 (仿真环境):\r\n" +
                           $"  热点 SSID : {ssid}\r\n" +
                           $"  网关 IP   : 192.168.4.1\r\n" +
                           $"  信道频率  : CH1 (2412 MHz)\r\n" +
                           $"  无线调试  : TCP 192.168.4.1:8266 [监听中]";
                }
                if (sub == "disconnect")
                {
                    return "[SUCCESS] Wi-Fi 已断开连接，射频处于空闲待机模式。";
                }
                if (sub == "scan")
                {
                    return "周边 Wi-Fi 射频热点扫描结果 (仿真模式, 共 5 个热点):\r\n" +
                           "  [01] SSID: 'JiaHe'             CH: 6   RSSI: -4 dBm    AUTH: WPA2-PSK\r\n" +
                           "  [02] SSID: 'yeteng-424D'       CH: 6   RSSI: -47 dBm   AUTH: WPA/WPA2\r\n" +
                           "  [03] SSID: 'GDPI'              CH: 11  RSSI: -48 dBm   AUTH: OPEN\r\n" +
                           "  [04] SSID: 'OUGD'              CH: 11  RSSI: -49 dBm   AUTH: WPA2-PSK\r\n" +
                           "  [05] SSID: 'vivo S30 Pro mini' CH: 11  RSSI: -51 dBm   AUTH: WPA2-PSK";
                }
            }

            if (root == "power")
            {
                if (sub == "status")
                {
                    return "ESP32-S3 6 通道限流开关供电状态 (仿真模式):\r\n" +
                           "  Channel 0 (总路 VBUS) : [ON]  (开机默认: ON)\r\n" +
                           "  Channel 1 (CH1-XDS110): [ON]  (开机默认: ON)\r\n" +
                           "  Channel 2 (CH2-DAP)   : [ON]  (开机默认: ON)\r\n" +
                           "  Channel 3 (CH3-UART)  : [ON]  (开机默认: ON)\r\n" +
                           "  Channel 4 (CH4-EXT1)  : [ON]  (开机默认: ON)\r\n" +
                           "  Channel 5 (CH5-EXT2)  : [ON]  (开机默认: ON)";
                }
                return $"[POWER] 供电通道控制指令 \"{command}\" 已生效 (仿真触发)。";
            }

            if (root == "status")
            {
                return "ESP32-S3 双核系统资源与运行状态 (仿真模式):\r\n" +
                       "  固件版本     : 0.30-preview-2641\r\n" +
                       "  芯片型号     : ESP32-S3 (双核 Xtensa LX7 @ 240MHz)\r\n" +
                       "  系统剩余堆栈 : 232808 Bytes (SRAM)\r\n" +
                       "  全局唯一 MAC : d0:cf:13:30:90:30\r\n" +
                       "  任务调度     : Core 0 [Telemetry/UI], Core 1 [CommRx/CLI]";
            }

            if (root == "whoami")
            {
                return "LinkNexus Dual-Core Firmware [ESP32-S3 Core]\r\n" +
                       "Hardware Model : LinkNexus-Pro-ESP32S3\r\n" +
                       "Baseline Version: 0.30-preview-2641\r\n" +
                       "MAC Address     : d0:cf:13:30:90:30";
            }

            return $"[CLI-SIM] 执行命令: \"{command}\"\r\nOK";
        }

        /// <summary>
        /// 发送 0x40 Wi-Fi 模式与网络控制命令
        /// </summary>
        public bool SendWifiControl(byte action, string ssid = "", string password = "", byte channel = 1)
        {
            byte[] packet = ProtocolEngine.BuildWifiControlPacket(action, ssid, password, channel);
            bool sent = SendPacket(packet);
            if (sent)
            {
                LogMessage?.Invoke($"[Wi-Fi控制] 下发网络动作 0x{action:X2}, SSID='{ssid}'");
            }
            return sent;
        }

        /// <summary>
        /// 心跳超时检查
        /// </summary>
        private void HeartbeatTimerTick()
        {
            if (!IsConnected) return;

            if ((DateTime.Now - _lastReceiveTime).TotalSeconds > 3.5)
            {
                LogMessage?.Invoke("[下位机通信] 心跳检测超时，ESP32-S3 下位机失去响应，尝试重连...");
                SetSimulationMode(true, "通信超时 (仿真模式)");
            }
            else
            {
                SendHeartbeat();
            }
        }

        /// <summary>
        /// 仿真模式下生成高度逼真的 INA236 工业电气遥测信号
        /// </summary>
        private double _simAngle = 0;
        private readonly Random _rand = new();

        private void GenerateSimulatedTelemetry()
        {
            if (!IsSimulationMode) return;

            _simAngle += 0.05;
            double noise = (_rand.NextDouble() - 0.5) * 0.02;

            // 总线 12V 扰动, 电流 420mA 波动
            double busVolt = 12.00 + Math.Sin(_simAngle) * 0.04 + noise;
            double busCurr = 425 + Math.Sin(_simAngle * 1.5) * 8 + (_rand.NextDouble() - 0.5) * 4;

            // XDS110 3.3V, 电流 165mA 波动
            double xdsVolt = 3.30 + Math.Sin(_simAngle * 0.8) * 0.01;
            double xdsCurr = 165 + Math.Sin(_simAngle * 2.0) * 5 + (_rand.NextDouble() - 0.5) * 3;

            // DAPLink 3.32V, 电流 142mA 波动
            double dapVolt = 3.32 + Math.Cos(_simAngle * 0.9) * 0.01;
            double dapCurr = 142 + Math.Cos(_simAngle * 1.8) * 4 + (_rand.NextDouble() - 0.5) * 3;

            var simData = new TelemetryPacketData
            {
                BusVoltage = Math.Round(busVolt, 2),
                BusCurrent = Math.Round(busCurr, 0),
                XdsVoltage = Math.Round(xdsVolt, 2),
                XdsCurrent = Math.Round(xdsCurr, 0),
                DapVoltage = Math.Round(dapVolt, 2),
                DapCurrent = Math.Round(dapCurr, 0),
                Timestamp = DateTime.Now
            };

            TelemetryReceived?.Invoke(simData);
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Disconnect();
            _simTelemetryTimer.Stop();
            _simTelemetryTimer.Dispose();
            _heartbeatTimer.Dispose();
        }
    }
}
