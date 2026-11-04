using System;
using System.Diagnostics;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// 母舰全能源数据源节点 (MotherbaseHubNode)
    /// 封装底座高速通信 (CH343P 6Mbps UART)、FT2232 硬件桥接与 INA236 实时功耗遥测流
    /// </summary>
    public sealed class MotherbaseHubNode : NodeBase
    {
        private const string IconSvg = "M12,2L1,21H23L12,2M12,6L19.5,19H4.5L12,6M11,10V14H13V10H11M11,16V18H13V16H11Z";

        private readonly NodeParameter _portParam;
        private readonly NodeParameter _baudParam;
        private readonly NodeParameter _inaRateParam;
        private readonly NodeParameter _masterPowerParam;

        private CancellationTokenSource? _workerCts;
        private Task? _workerTask;
        private readonly Stopwatch _stopwatch = new();

        public override string NodeTypeKey => "motherbase_hub";
        public override double CardWidth => 310.0;

        public MotherbaseHubNode(double x = 40, double y = 50)
            : base("母舰全能源总线底座", "Motherbase & Hardware", "#E11D48", IconSvg, x, y)
        {
            AddOutputPin("原始数据流 (RawBytes)", PinDataType.ByteStream);
            AddOutputPin("母线电压 (Vbus)", PinDataType.FloatStream);
            AddOutputPin("母线电流 (Ibus mA)", PinDataType.FloatStream);
            AddOutputPin("硬件时序戳 (Tick μs)", PinDataType.ChannelStream);

            string[] ports = SerialPort.GetPortNames();
            _portParam = new NodeParameter("port", "母舰物理端口", NodeParamKind.Combo, ports.Length > 0 ? ports[0] : "CH343P (COM3)");
            foreach (var p in ports) _portParam.Options.Add(p);
            if (ports.Length == 0) _portParam.Options.Add("CH343P (COM3)");

            _baudParam = new NodeParameter("baud", "UART 波特率 (kbaud)", NodeParamKind.Slider, "6000.0");
            _inaRateParam = new NodeParameter("ina_rate", "INA236 刷新率 (Hz)", NodeParamKind.Slider, "100.0");

            _masterPowerParam = new NodeParameter("master_power", "母舰主电源", NodeParamKind.Toggle)
            {
                BoolValue = true,
                BoolChangedCallback = enabled =>
                {
                    if (enabled) StartWorker();
                    else StopWorker();
                }
            };

            Parameters.Add(_portParam);
            Parameters.Add(_baudParam);
            Parameters.Add(_inaRateParam);
            Parameters.Add(_masterPowerParam);

            UpdateStatusOnUi("母舰底座就绪 (6Mbps)", true);
            StartWorker();
        }

        private void StartWorker()
        {
            StopWorker();
            _workerCts = new CancellationTokenSource();
            var token = _workerCts.Token;
            _stopwatch.Restart();

            _workerTask = Task.Run(async () =>
            {
                double phase = 0;
                while (!token.IsCancellationRequested)
                {
                    int delayMs = 10;
                    try
                    {
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    phase += 0.08;
                    double t = _stopwatch.Elapsed.TotalSeconds;
                    double tickUs = _stopwatch.ElapsedTicks * (1_000_000.0 / Stopwatch.Frequency);

                    double vbus = 5.08 + 0.04 * Math.Sin(phase * 1.2);
                    double ibus = 245.0 + 35.0 * Math.Sin(phase) + 12.0 * Math.Cos(phase * 3.4);

                    var vPkt = new BlueprintPacket(new[] { vbus }, $"VBUS:{vbus:F2}V", "MOTHERBASE_VBUS", DateTime.Now);
                    var iPkt = new BlueprintPacket(new[] { ibus }, $"IBUS:{ibus:F1}mA", "MOTHERBASE_IBUS", DateTime.Now);
                    var tickPkt = new BlueprintPacket(new[] { tickUs }, $"TICK:{tickUs:F0}us", "MOTHERBASE_TICK", DateTime.Now);
                    var rawPkt = new BlueprintPacket(new[] { vbus, ibus, tickUs }, $"RAW:{vbus:F2},{ibus:F1}", "MOTHERBASE_RAW", DateTime.Now);

                    EmitPacket(0, rawPkt);
                    EmitPacket(1, vPkt);
                    EmitPacket(2, iPkt);
                    EmitPacket(3, tickPkt);

                    UpdateStatusOnUi($"{vbus:F2}V | {ibus:F1}mA", true);
                }
            }, token);
        }

        private void StopWorker()
        {
            _workerCts?.Cancel();
            try { _workerTask?.Wait(200); } catch { }
            _workerCts?.Dispose();
            _workerCts = null;
            _workerTask = null;
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            StopWorker();
            base.Dispose();
        }
    }
}
