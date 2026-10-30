using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// COM 串口流输出卡片 (ComOutputNode)
    /// 将上游解算后的通道向量或协议帧按 VOFA+ FireWater、ASCII 行或 HEX 帧实时下发至目标物理串口或虚拟回环监视器
    /// </summary>
    public sealed class ComOutputNode : NodeBase
    {
        public const string LoopbackOption = "LOOPBACK (虚拟回环监视)";

        private const string IconSvg = "M3,12 H15 M11,8 L15,12 L11,16 M18,6 V18";

        private readonly NodeParameter _portParam;
        private readonly NodeParameter _baudParam;
        private readonly NodeParameter _formatParam;
        private readonly NodeParameter _enableParam;
        private readonly NodeParameter _lastTxBadge;

        private readonly object _portLock = new();
        private SerialPort? _serialPort;

        public ComOutputNode(double x = 760, double y = 320)
            : base("COM 串口输出", "外设下发端", "#10B981", IconSvg, x, y)
        {
            AddInputPin("下发数据流", PinDataType.Any);

            _portParam = new NodeParameter("port", "目标端口", NodeParamKind.Combo, LoopbackOption);
            PopulatePortOptions(SerialPort.GetPortNames(), LoopbackOption);
            _portParam.ValueChangedCallback = _ =>
            {
                if (_enableParam != null && _enableParam.BoolValue) ReopenPort();
            };

            _baudParam = new NodeParameter("baud", "波特率", NodeParamKind.Combo, "115200");
            foreach (var b in new[] { "115200", "921600", "460800", "230400", "9600" })
            {
                _baudParam.Options.Add(b);
            }

            _formatParam = new NodeParameter("fmt", "封装格式", NodeParamKind.Combo, "VOFA+ FireWater (CSV)");
            _formatParam.Options.Add("VOFA+ FireWater (CSV)");
            _formatParam.Options.Add("ASCII 原始行透传");
            _formatParam.Options.Add("HEX 十六进制字节");

            _enableParam = new NodeParameter("enable", "输出使能", NodeParamKind.Toggle)
            {
                BoolChangedCallback = enabled =>
                {
                    if (enabled) ReopenPort();
                    else ClosePort();
                }
            };

            _lastTxBadge = new NodeParameter("tx", "最近下发", NodeParamKind.ReadOnlyBadge, "待命 · 尚未发送");

            Parameters.Add(_portParam);
            Parameters.Add(_baudParam);
            Parameters.Add(_formatParam);
            Parameters.Add(_enableParam);
            Parameters.Add(_lastTxBadge);

            // 默认开启虚拟回环输出使能，连线即见实时下发载荷
            _enableParam.SetBoolSilently(true);
            UpdateStatusOnUi("回环就绪", true);
        }

        public override void RefreshAvailableComPorts(IReadOnlyList<string> comPorts)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                PopulatePortOptions(comPorts, _portParam.Value);
            });
        }

        private void PopulatePortOptions(IEnumerable<string>? systemPorts, string? preferred)
        {
            var merged = new List<string> { LoopbackOption };
            if (systemPorts != null)
            {
                foreach (var p in systemPorts.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    merged.Add(p.Trim());
                }
            }

            string fallback = string.IsNullOrWhiteSpace(preferred) ? LoopbackOption : preferred!;
            _portParam.ReplaceOptions(merged, fallback);
        }

        private void ReopenPort()
        {
            lock (_portLock)
            {
                ClosePortCore();
                string portName = _portParam?.Value ?? LoopbackOption;
                if (string.IsNullOrWhiteSpace(portName) || portName.StartsWith("LOOPBACK", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateStatusOnUi("回环监听", true);
                    UpdateTxBadge("虚拟回环已激活");
                    return;
                }

                try
                {
                    int baud = int.TryParse(_baudParam.Value, out int b) ? b : 115200;
                    _serialPort = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
                    {
                        Encoding = Encoding.UTF8,
                        WriteTimeout = 300
                    };
                    _serialPort.Open();
                    UpdateStatusOnUi($"{portName} 发送中", true);
                    UpdateTxBadge($"已打开 {portName} @ {baud}");
                }
                catch (Exception ex)
                {
                    UpdateStatusOnUi("端口异常", false);
                    UpdateTxBadge($"打开失败: {ex.Message}");
                    Application.Current?.Dispatcher.BeginInvoke(() => _enableParam.SetBoolSilently(false));
                }
            }
        }

        private void ClosePort()
        {
            lock (_portLock)
            {
                ClosePortCore();
            }
            UpdateStatusOnUi("已关断", false);
            UpdateTxBadge("输出通道已关闭");
        }

        private void ClosePortCore()
        {
            if (_serialPort != null)
            {
                try { if (_serialPort.IsOpen) _serialPort.Close(); } catch { }
                _serialPort.Dispose();
                _serialPort = null;
            }
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            if (!_enableParam.BoolValue) return Task.CompletedTask;

            string fmt = _formatParam?.Value ?? string.Empty;
            string payloadLine;
            if (fmt.StartsWith("VOFA+", StringComparison.OrdinalIgnoreCase))
            {
                payloadLine = packet.ToFireWaterLine();
            }
            else if (fmt.StartsWith("HEX", StringComparison.OrdinalIgnoreCase))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(packet.ToFireWaterLine());
                payloadLine = BitConverter.ToString(bytes).Replace('-', ' ');
            }
            else
            {
                payloadLine = !string.IsNullOrEmpty(packet.RawText) ? packet.RawText : packet.ToFireWaterLine();
            }

            lock (_portLock)
            {
                if (_serialPort != null && _serialPort.IsOpen)
                {
                    try
                    {
                        _serialPort.WriteLine(payloadLine);
                    }
                    catch (Exception ex)
                    {
                        UpdateTxBadge($"写入错误: {ex.Message}");
                        return Task.CompletedTask;
                    }
                }
            }

            if (PacketCounter == 1 || PacketCounter % 12 == 0)
            {
                string preview = payloadLine.Length > 28 ? payloadLine[..28] + "…" : payloadLine;
                UpdateTxBadge($"TX: {preview}");
            }

            return Task.CompletedTask;
        }

        private void UpdateTxBadge(string text)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => _lastTxBadge.SetValueSilently(text));
        }

        public override void Dispose()
        {
            ClosePort();
            base.Dispose();
        }
    }
}

