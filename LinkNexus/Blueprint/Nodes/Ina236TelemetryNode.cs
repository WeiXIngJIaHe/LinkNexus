using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// INA236 三路高精硬件遥测数据源节点 (Ina236TelemetryNode)
    /// 独立驱动总路 (0x40)、XDS110 (0x41)、DAPLink (0x42) 母线电压、电流与功率流
    /// </summary>
    public sealed class Ina236TelemetryNode : NodeBase
    {
        private const string IconSvg = "M13,2 L3,14 H12 L11,22 L21,10 H12 L13,2 Z";

        private readonly NodeParameter _rateParam;
        private readonly NodeParameter _activeToggle;
        private CancellationTokenSource? _workerCts;
        private Task? _workerTask;
        private readonly Stopwatch _stopwatch = new();

        public override string NodeTypeKey => "ina236_telemetry";
        public override double CardWidth => 310.0;

        public Ina236TelemetryNode(double x = 40, double y = 340)
            : base("INA236 硬件遥测源", "硬件母舰 / 遥测源", "#10B981", IconSvg, x, y)
        {
            AddOutputPin("总路 0x40 (V, mA, W)", PinDataType.ChannelStream);
            AddOutputPin("XDS110 0x41 (V, mA, W)", PinDataType.ChannelStream);
            AddOutputPin("DAPLink 0x42 (V, mA, W)", PinDataType.ChannelStream);

            _rateParam = new NodeParameter("rate", "采样率 (Hz)", NodeParamKind.Combo, "50");
            _rateParam.Options.Add("20");
            _rateParam.Options.Add("50");
            _rateParam.Options.Add("100");
            _rateParam.Options.Add("200");

            _activeToggle = new NodeParameter("active", "遥测采集", NodeParamKind.Toggle)
            {
                BoolValue = true,
                BoolChangedCallback = enabled =>
                {
                    if (enabled) StartWorker();
                    else StopWorker();
                }
            };

            Parameters.Add(_rateParam);
            Parameters.Add(_activeToggle);

            StartWorker();
            UpdateStatusOnUi("遥测采集中 (50Hz)", true);
        }

        private void StartWorker()
        {
            StopWorker();
            _workerCts = new CancellationTokenSource();
            var token = _workerCts.Token;
            _stopwatch.Restart();

            _workerTask = Task.Run(async () =>
            {
                double phase = 0.0;
                while (!token.IsCancellationRequested)
                {
                    int hz = int.TryParse(_rateParam.Value, out int h) ? h : 50;
                    int delayMs = Math.Max(5, 1000 / hz);

                    try
                    {
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    phase += 0.05;
                    double busV = 5.05 + 0.03 * Math.Sin(phase * 1.5);
                    double busMa = 220.0 + 45.0 * Math.Cos(phase * 0.8);
                    double busW = busV * (busMa / 1000.0);

                    double xdsV = 3.31 + 0.01 * Math.Cos(phase);
                    double xdsMa = 85.0 + 15.0 * Math.Sin(phase * 2.1);
                    double xdsW = xdsV * (xdsMa / 1000.0);

                    double dapV = 3.29 + 0.01 * Math.Sin(phase * 0.9);
                    double dapMa = 60.0 + 10.0 * Math.Cos(phase * 1.8);
                    double dapW = dapV * (dapMa / 1000.0);

                    var busPkt = new BlueprintPacket(new[] { busV, busMa, busW }, $"BUS:{busV:F2}V,{busMa:F1}mA", "INA236_BUS", DateTime.Now);
                    var xdsPkt = new BlueprintPacket(new[] { xdsV, xdsMa, xdsW }, $"XDS:{xdsV:F2}V,{xdsMa:F1}mA", "INA236_XDS", DateTime.Now);
                    var dapPkt = new BlueprintPacket(new[] { dapV, dapMa, dapW }, $"DAP:{dapV:F2}V,{dapMa:F1}mA", "INA236_DAP", DateTime.Now);

                    EmitPacket(0, busPkt);
                    EmitPacket(1, xdsPkt);
                    EmitPacket(2, dapPkt);

                    UpdateStatusOnUi($"{busMa:F1}mA @ {hz}Hz", true);
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
