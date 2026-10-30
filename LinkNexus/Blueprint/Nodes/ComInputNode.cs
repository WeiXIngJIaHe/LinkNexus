using System;
using System.Collections.Generic;
using System.Globalization;
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
    /// COM 串口流输入卡片 (ComInputNode)
    /// 支持真实物理 COM 端口异步高速行解析 (兼容 VOFA+ FireWater 协议)，同时内置多通道电气谐波与 IMU 欧拉角仿真流源
    /// </summary>
    public sealed class ComInputNode : NodeBase
    {
        public const string SimVofaOption = "SIM-VOFA (6通道谐波流)";
        public const string SimImuOption = "SIM-IMU (三维姿态流)";

        // 工业串口连接器几何 SVG 图标
        private const string IconSvg = "M4,7 H16 L19,12 L16,17 H4 Z M7,10.5 A1,1 0 1,1 7,10.51 M11,10.5 A1,1 0 1,1 11,10.51 M15,10.5 A1,1 0 1,1 15,10.51 M9,13.8 A1,1 0 1,1 9,13.81 M13,13.8 A1,1 0 1,1 13,13.81";

        private readonly NodeParameter _portParam;
        private readonly NodeParameter _baudParam;
        private readonly NodeParameter _enableParam;
        private readonly NodeParameter _rateBadgeParam;

        private CancellationTokenSource? _streamCts;
        private SerialPort? _serialPort;
        private readonly object _streamLock = new();

        public ComInputNode(string initialPort = SimVofaOption, double x = 36, double y = 48)
            : base("COM 串口输入", "硬件采集源", "#0EA5E9", IconSvg, x, y)
        {
            AddOutputPin("通道流 (CH)", PinDataType.ChannelStream);
            AddOutputPin("原始文本帧", PinDataType.RawFrame);

            _portParam = new NodeParameter("port", "采集端口", NodeParamKind.Combo, initialPort);
            PopulatePortOptions(SerialPort.GetPortNames(), initialPort);
            _portParam.ValueChangedCallback = _ =>
            {
                if (_enableParam != null && _enableParam.BoolValue)
                {
                    RestartStream();
                }
            };

            _baudParam = new NodeParameter("baud", "波特率", NodeParamKind.Combo, "115200");
            foreach (var b in new[] { "115200", "921600", "460800", "230400", "57600", "9600" })
            {
                _baudParam.Options.Add(b);
            }
            _baudParam.ValueChangedCallback = _ =>
            {
                if (_enableParam != null && _enableParam.BoolValue)
                {
                    RestartStream();
                }
            };

            _enableParam = new NodeParameter("enable", "流式采集", NodeParamKind.Toggle)
            {
                BoolChangedCallback = enabled =>
                {
                    if (enabled) StartStream();
                    else StopStream();
                }
            };

            _rateBadgeParam = new NodeParameter("rate", "链路遥测", NodeParamKind.ReadOnlyBadge, "未启动 · 等待触发");

            Parameters.Add(_portParam);
            Parameters.Add(_baudParam);
            Parameters.Add(_enableParam);
            Parameters.Add(_rateBadgeParam);
        }

        public override void RefreshAvailableComPorts(IReadOnlyList<string> comPorts)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                string current = _portParam.Value;
                PopulatePortOptions(comPorts, current);
            });
        }

        public override void SetStreamingEnabled(bool enabled)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _enableParam.BoolValue = enabled;
            });
        }

        private void PopulatePortOptions(IEnumerable<string>? systemPorts, string? preferred)
        {
            var merged = new List<string>();
            if (systemPorts != null)
            {
                foreach (var p in systemPorts.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    merged.Add(p.Trim());
                }
            }
            merged.Add(SimVofaOption);
            merged.Add(SimImuOption);

            string fallback = string.IsNullOrWhiteSpace(preferred) ? SimVofaOption : preferred!;
            _portParam.ReplaceOptions(merged, fallback);
        }

        private void RestartStream()
        {
            StopStream();
            StartStream();
        }

        private void StartStream()
        {
            lock (_streamLock)
            {
                StopStreamCore();
                _streamCts = new CancellationTokenSource();
                var token = _streamCts.Token;
                string selectedPort = _portParam?.Value ?? SimVofaOption;
                int baud = int.TryParse(_baudParam?.Value, out int parsedBaud) ? parsedBaud : 115200;

                if (string.IsNullOrWhiteSpace(selectedPort) || selectedPort.StartsWith("SIM-VOFA", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateStatusOnUi("仿真采集中", true);
                    _ = Task.Run(() => RunSimVofaLoopAsync(token));
                }
                else if (selectedPort.StartsWith("SIM-IMU", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateStatusOnUi("姿态仿真中", true);
                    _ = Task.Run(() => RunSimImuLoopAsync(token));
                }
                else
                {
                    _ = Task.Run(() => RunPhysicalSerialLoopAsync(selectedPort, baud, token));
                }
            }
        }

        private void StopStream()
        {
            lock (_streamLock)
            {
                StopStreamCore();
            }
            UpdateStatusOnUi("已停止", false);
            UpdateRateBadge("已停止 · 端口空闲");
        }

        private void StopStreamCore()
        {
            if (_streamCts != null)
            {
                try { _streamCts.Cancel(); } catch { }
                _streamCts.Dispose();
                _streamCts = null;
            }

            if (_serialPort != null)
            {
                try
                {
                    if (_serialPort.IsOpen) _serialPort.Close();
                }
                catch { }
                _serialPort.Dispose();
                _serialPort = null;
            }
        }

        private async Task RunSimVofaLoopAsync(CancellationToken ct)
        {
            double phase = 0.0;
            var rand = new Random();
            int ticks = 0;

            UpdateRateBadge("30 Hz · 6 通道 VOFA+ 流");

            while (!ct.IsCancellationRequested)
            {
                phase += 0.09;
                double ch0 = Math.Sin(phase) * 5.0 + Math.Sin(phase * 3.0) * 1.1;
                double ch1 = Math.Cos(phase * 0.85) * 4.2 + (rand.NextDouble() - 0.5) * 0.45;
                double ch2 = Math.Sin(phase * 1.6 + 1.1) * 3.0;
                double ch3 = 12.0 + Math.Sin(phase * 0.3) * 0.18;
                double ch4 = 3.30 + Math.Cos(phase * 0.5) * 0.04;
                double ch5 = (ch0 + ch1) * 0.5;

                var channels = new[]
                {
                    Math.Round(ch0, 3),
                    Math.Round(ch1, 3),
                    Math.Round(ch2, 3),
                    Math.Round(ch3, 3),
                    Math.Round(ch4, 3),
                    Math.Round(ch5, 3)
                };

                var pkt = new BlueprintPacket(channels, string.Join(",", channels.Select(v => v.ToString("F2", CultureInfo.InvariantCulture))), "SIM-VOFA");
                IncrementCounter();
                EmitPacket(0, pkt);
                EmitPacket(1, pkt);

                ticks++;
                if (ticks % 30 == 0)
                {
                    UpdateRateBadge($"30 Hz · CH0={ch0:F2} CH1={ch1:F2}");
                }

                try
                {
                    await Task.Delay(33, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task RunSimImuLoopAsync(CancellationToken ct)
        {
            double t = 0.0;
            int ticks = 0;
            UpdateRateBadge("33 Hz · R/P/Y 欧拉角流");

            while (!ct.IsCancellationRequested)
            {
                t += 0.045;
                double roll = Math.Sin(t * 1.25) * 32.0;
                double pitch = Math.Cos(t * 0.9) * 22.0;
                double yaw = (t * 28.0) % 360.0 - 180.0;

                var channels = new[]
                {
                    Math.Round(roll, 2),
                    Math.Round(pitch, 2),
                    Math.Round(yaw, 2)
                };

                string raw = string.Create(CultureInfo.InvariantCulture, $"imu:{roll:F2},{pitch:F2},{yaw:F2}");
                var pkt = new BlueprintPacket(channels, raw, "SIM-IMU");
                IncrementCounter();
                EmitPacket(0, pkt);
                EmitPacket(1, pkt);

                ticks++;
                if (ticks % 25 == 0)
                {
                    UpdateRateBadge($"R:{roll:F1}° P:{pitch:F1}° Y:{yaw:F0}°");
                }

                try
                {
                    await Task.Delay(30, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private async Task RunPhysicalSerialLoopAsync(string portName, int baudRate, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                UpdateStatusOnUi("端口为空", false);
                UpdateRateBadge("未选择有效物理串口");
                return;
            }

            SerialPort? port = null;
            try
            {
                port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                {
                    Encoding = Encoding.UTF8,
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    DtrEnable = true,
                    RtsEnable = true
                };
                port.Open();

                lock (_streamLock)
                {
                    _serialPort = port;
                }

                UpdateStatusOnUi($"{portName} 在线", true);
                UpdateRateBadge($"{portName} @ {baudRate} bps");

                var buffer = new byte[1024];
                var lineBuilder = new StringBuilder(256);

                while (!ct.IsCancellationRequested && port.IsOpen)
                {
                    int readCount = await port.BaseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (readCount <= 0) continue;

                    string chunk = Encoding.UTF8.GetString(buffer, 0, readCount);
                    foreach (char c in chunk)
                    {
                        if (c == '\n' || c == '\r')
                        {
                            if (lineBuilder.Length > 0)
                            {
                                string line = lineBuilder.ToString();
                                lineBuilder.Clear();
                                DispatchSerialLine(portName, line);
                            }
                        }
                        else if (lineBuilder.Length < 2048)
                        {
                            lineBuilder.Append(c);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                UpdateStatusOnUi("打开失败", false);
                UpdateRateBadge($"串口异常: {ex.Message}");
                Application.Current?.Dispatcher.BeginInvoke(() => _enableParam.SetBoolSilently(false));
            }
        }

        private void DispatchSerialLine(string portName, string line)
        {
            IncrementCounter();
            if (BlueprintPacket.TryParseLine(line, portName, out var parsed))
            {
                EmitPacket(0, parsed);
                EmitPacket(1, parsed);
                if (PacketCounter % 15 == 1)
                {
                    UpdateRateBadge($"{portName} · {parsed.Channels.Length} 通道流");
                }
            }
            else
            {
                var rawPkt = new BlueprintPacket(Array.Empty<double>(), line, portName);
                EmitPacket(1, rawPkt);
            }
        }

        private void UpdateRateBadge(string text)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => _rateBadgeParam.SetValueSilently(text));
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
            => Task.CompletedTask;

        public override void Dispose()
        {
            StopStream();
            base.Dispose();
        }
    }
}

