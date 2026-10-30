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
    /// 严格仅列出真实 USB 枚举出来的物理 COM 端口，零虚拟回环占位符端口；必须指定已枚举端口后方可启用输出
    /// </summary>
    public sealed class ComOutputNode : NodeBase
    {
        private const string IconSvg = "M3,12 H15 M11,8 L15,12 L11,16 M18,6 V18";

        private readonly NodeParameter _portParam;
        private readonly NodeParameter _baudParam;
        private readonly NodeParameter _formatParam;
        private readonly NodeParameter _enableParam;
        private readonly NodeParameter _lastTxBadge;

        private readonly object _portLock = new();
        private readonly List<string> _enumeratedPorts = new();
        private SerialPort? _serialPort;

        public override string NodeTypeKey => "com_output";

        public ComOutputNode(string initialPort = "", double x = 760, double y = 320)
            : base("COM 串口输出", "外设下发端", "#10B981", IconSvg, x, y)
        {
            AddInputPin("下发数据流", PinDataType.Any);

            _portParam = new NodeParameter("port", "指定端口", NodeParamKind.Combo, initialPort, "未枚举到 USB 串口");
            PopulatePortOptions(SerialPort.GetPortNames(), initialPort);
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

            _lastTxBadge = new NodeParameter(
                "tx",
                "最近下发",
                NodeParamKind.ReadOnlyBadge,
                _enumeratedPorts.Count > 0 ? "待命 · 选择端口后开启使能" : "等待 USB 枚举物理串口");

            Parameters.Add(_portParam);
            Parameters.Add(_baudParam);
            Parameters.Add(_formatParam);
            Parameters.Add(_enableParam);
            Parameters.Add(_lastTxBadge);
        }

        public override void RefreshAvailableComPorts(IReadOnlyList<string> comPorts)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                PopulatePortOptions(comPorts, _portParam.Value);
            });
        }

        private void PopulatePortOptions(IEnumerable<string>? usbPorts, string? preferred)
        {
            _enumeratedPorts.Clear();
            if (usbPorts != null)
            {
                foreach (var p in usbPorts.Where(s => !string.IsNullOrWhiteSpace(s) && s != "--").Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    _enumeratedPorts.Add(p.Trim());
                }
            }

            _portParam.ReplaceOptions(_enumeratedPorts, preferred);

            if (_enumeratedPorts.Count == 0 && _enableParam != null && _enableParam.BoolValue)
            {
                _enableParam.SetBoolSilently(false);
                ClosePort();
                UpdateStatusOnUi("无USB端口", false);
                UpdateTxBadge("USB 串口已拔出 · 输出已关闭");
            }
        }

        private void ReopenPort()
        {
            lock (_portLock)
            {
                ClosePortCore();
                string portName = _portParam?.Value?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(portName) ||
                    !_enumeratedPorts.Any(p => string.Equals(p, portName, StringComparison.OrdinalIgnoreCase)))
                {
                    UpdateStatusOnUi("未指定端口", false);
                    UpdateTxBadge("请先指定一个 USB 枚举物理串口");
                    Application.Current?.Dispatcher.BeginInvoke(() => _enableParam?.SetBoolSilently(false));
                    return;
                }

                try
                {
                    int baud = int.TryParse(_baudParam.Value, out int b) ? b : 115200;
                    _serialPort = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
                    {
                        Encoding = Encoding.UTF8,
                        WriteTimeout = 300,
                        DtrEnable = false,
                        RtsEnable = false
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
                if (_serialPort == null || !_serialPort.IsOpen)
                {
                    return Task.CompletedTask;
                }

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

            if (PacketCounter == 1 || PacketCounter % 12 == 0)
            {
                string preview = payloadLine.Length > 28 ? payloadLine[..28] + "…" : payloadLine;
                UpdateTxBadge($"TX: {preview}");
            }

            return Task.CompletedTask;
        }

        public override Dictionary<string, object?> ExportMcpMetadata()
        {
            return new Dictionary<string, object?>
            {
                ["designatedPort"] = _portParam.Value,
                ["baudRate"] = _baudParam.Value,
                ["format"] = _formatParam.Value,
                ["isEnabled"] = _enableParam.BoolValue
            };
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
