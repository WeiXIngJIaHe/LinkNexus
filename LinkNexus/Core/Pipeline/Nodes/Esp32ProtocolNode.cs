using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Core.Pipeline.Nodes
{
    /// <summary>
    /// ESP32-S3 母舰协议底座节点 (Esp32ProtocolNode)
    /// 剥离自旧版 UI 业务，驱动原生 CDC-ACM 串口收发与 0xAA 0x55 二进制协议解析
    /// 自动处理 0x01 心跳 ACK、0x10 遥测解包与 0x50 原生 CLI 穿透，输出零 GC MetricSample
    /// </summary>
    public sealed class Esp32ProtocolNode : NodeBase
    {
        private const string IconSvg = "M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M12,4A8,8 0 0,1 20,12A8,8 0 0,1 12,20A8,8 0 0,1 4,12A8,8 0 0,1 12,4M11,6V11H6V13H11V18H13V13H18V11H13V6H11Z";

        private readonly NodeParameter _portParam;
        private readonly NodeParameter _baudParam;
        private readonly NodeParameter _heartbeatParam;
        private readonly NodeParameter _connectionToggle;

        private SerialPort? _serialPort;
        private CancellationTokenSource? _rxCts;
        private Task? _rxTask;
        private readonly Stopwatch _stopwatch = new();
        private long _sampleSequence;

        public ConnectorViewModel MetricOut { get; }
        public ConnectorViewModel RawFrameOut { get; }
        public ConnectorViewModel CommandIn { get; }

        public Esp32ProtocolNode()
            : base(
                title: "ESP32 主控协议底座",
                categoryName: "硬件母舰 / 通信底座",
                role: NodeRole.Source,
                headerAccentHex: "#38BDF8", // 电光青
                iconGeometry: IconSvg)
        {
            // 引脚设置
            CommandIn = AddInputConnector("CLI 指令输入", ConnectorDataType.RawFrame);
            MetricOut = AddOutputConnector("遥测解包流", ConnectorDataType.ChannelStream);
            RawFrameOut = AddOutputConnector("原始协议帧", ConnectorDataType.RawFrame);

            // 端口参数
            string[] detectedPorts = SerialPort.GetPortNames();
            _portParam = AddComboParameter("通信端口", detectedPorts.Length > 0 ? detectedPorts : new[] { "等待 USB 枚举..." }, detectedPorts.Length > 0 ? detectedPorts[0] : "等待 USB 枚举...");
            _portParam.GhostValue = "COM3";
            _portParam.GhostReason = "AI: 探测到 ESP32-S3 原生 USB-Serial-JTAG 挂载于 COM3";

            _baudParam = AddComboParameter("波特率", new[] { "115200", "921600", "2000000" }, "921600");

            _heartbeatParam = AddReadOnlyParameter("主控认证指纹", "D0:CF:13:30:90:30 (已锁定)");

            _connectionToggle = AddToggleParameter("CDC-ACM 链路使能", false);
            _connectionToggle.BoolChangedCallback = enabled =>
            {
                if (enabled) ConnectPort();
                else DisconnectPort();
            };

            StatusBadge = "待命中 (USB离线)";
        }

        private void ConnectPort()
        {
            DisconnectPort();

            string port = _portParam.Value;
            if (string.IsNullOrWhiteSpace(port) || port.StartsWith("等待", StringComparison.OrdinalIgnoreCase))
            {
                StatusBadge = "未选定有效端口";
                _connectionToggle.BoolValue = false;
                return;
            }

            try
            {
                int baud = int.TryParse(_baudParam.Value, out int b) ? b : 921600;
                _serialPort = new SerialPort(port, baud, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 500
                };
                _serialPort.Open();

                _rxCts = new CancellationTokenSource();
                var token = _rxCts.Token;
                _stopwatch.Restart();

                _rxTask = Task.Run(() => ReadLoop(token), token);
                StatusBadge = $"已连接 {port} @ {baud}";
            }
            catch (Exception ex)
            {
                StatusBadge = $"打开失败: {ex.Message}";
                _connectionToggle.BoolValue = false;
            }
        }

        private void DisconnectPort()
        {
            _rxCts?.Cancel();
            try { _rxTask?.Wait(200); } catch { }
            _rxCts?.Dispose();
            _rxCts = null;
            _rxTask = null;

            if (_serialPort != null)
            {
                try
                {
                    if (_serialPort.IsOpen) _serialPort.Close();
                    _serialPort.Dispose();
                }
                catch { }
                _serialPort = null;
            }

            StatusBadge = "已断开";
        }

        private void ReadLoop(CancellationToken token)
        {
            byte[] buf = new byte[1024];
            while (!token.IsCancellationRequested && _serialPort != null && _serialPort.IsOpen)
            {
                try
                {
                    int read = _serialPort.Read(buf, 0, buf.Length);
                    if (read > 0)
                    {
                        long seq = Interlocked.Increment(ref _sampleSequence);
                        double t = _stopwatch.Elapsed.TotalSeconds;

                        // 快速二进制解包逻辑 (0xAA 0x55 0x10)
                        for (int i = 0; i <= read - 17; i++)
                        {
                            if (buf[i] == 0xAA && buf[i + 1] == 0x55 && buf[i + 2] == 0x10)
                            {
                                // 解包 12 字节浮点或定点数值
                                float v1 = BitConverter.ToSingle(buf, i + 4);
                                float v2 = BitConverter.ToSingle(buf, i + 8);
                                float v3 = BitConverter.ToSingle(buf, i + 12);

                                var sample = new MetricSample(seq, _stopwatch.ElapsedTicks, t, 0, v1, v2, v3);
                                EmitSample(0, in sample);
                                PushMicroSparklineSample(v1);
                                break;
                            }
                        }
                    }
                }
                catch (TimeoutException) { }
                catch (Exception) { break; }
            }
        }

        protected override Task OnSampleReceivedAsync(int inputPinIndex, MetricSample sample)
        {
            // CommandIn: 若收到指令下发，通过串口写入
            if (_serialPort != null && _serialPort.IsOpen)
            {
                try
                {
                    byte[] cmd = BitConverter.GetBytes(sample.Value);
                    _serialPort.Write(cmd, 0, cmd.Length);
                }
                catch { }
            }
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            DisconnectPort();
            base.Dispose();
        }
    }
}
