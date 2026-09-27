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

            try
            {
                BaudRate = baudRate;
                _serialPort = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 2000,
                    WriteTimeout = 1000,
                    DtrEnable = true,
                    RtsEnable = true
                };

                _serialPort.Open();
                _cts = new CancellationTokenSource();
                _readTask = Task.Run(() => ReadLoopAsync(_serialPort, _cts.Token));

                IsConnected = true;
                IsSimulationMode = false;
                _simTelemetryTimer.Stop(); // 物理硬件在线，停止仿真
                ConnectedPortName = portName;
                _lastReceiveTime = DateTime.Now;

                _heartbeatTimer.Start();
                ConnectionStateChanged?.Invoke(true, $"已连接 ESP32-S3 ({portName})");
                LogMessage?.Invoke($"[下位机通信] 成功打开通信端口 {portName}，波特率 {baudRate}，全双工解包管道已就绪。");

                // 发送初次握手包
                SendHeartbeat(0x01);
                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[下位机通信] 打开串口 {portName} 失败: {ex.Message}，切换至仿真模式。");
                SetSimulationMode(true, "打开失败 (仿真模式)");
                return false;
            }
        }

        /// <summary>
        /// 断开连接并释放串口
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
                    _serialPort.Dispose();
                }
                catch { }
                _serialPort = null;
            }

            IsConnected = false;
            SetSimulationMode(true, "已断开 (仿真模式)");
        }

        private void SetSimulationMode(bool isSim, string desc)
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
        /// 异步数据流读取循环，完全独立于 UI 主线程
        /// </summary>
        private async Task ReadLoopAsync(SerialPort port, CancellationToken token)
        {
            byte[] readBuffer = new byte[512];

            while (!token.IsCancellationRequested && port.IsOpen)
            {
                try
                {
                    int bytesRead = await port.BaseStream.ReadAsync(readBuffer, 0, readBuffer.Length, token);
                    if (bytesRead > 0)
                    {
                        _lastReceiveTime = DateTime.Now;
                        _protocolEngine.ParseBytes(readBuffer, 0, bytesRead);
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
        /// 协议引擎成功解包回调
        /// </summary>
        private void OnFrameReceived(byte cmd, byte[] payload)
        {
            switch (cmd)
            {
                case ProtocolCommand.Heartbeat:
                    byte ack = payload.Length > 0 ? payload[0] : (byte)0x06;
                    HeartbeatAckReceived?.Invoke(ack);
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
            }
        }

        /// <summary>
        /// 发送通用二进制帧
        /// </summary>
        public bool SendPacket(byte[] packet)
        {
            if (!IsConnected || _serialPort == null || !_serialPort.IsOpen)
            {
                return false;
            }

            lock (_sendLock)
            {
                try
                {
                    _serialPort.Write(packet, 0, packet.Length);
                    return true;
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

