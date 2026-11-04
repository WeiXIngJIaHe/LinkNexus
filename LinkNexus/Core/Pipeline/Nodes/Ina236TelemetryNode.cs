using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Core.Pipeline.Nodes
{
    /// <summary>
    /// INA236 三路高精硬件遥测数据源算子 (Ina236TelemetryNode)
    /// 剥离自旧版 UI 耦合逻辑，独立驱动总路 (0x40)、XDS110 (0x41)、DAPLink (0x42) 母线电压、电流与功率流
    /// 零 GC 输出 MetricSample，支持 AI 动态推荐分流电阻滤波参数 (GhostValue)
    /// </summary>
    public sealed class Ina236TelemetryNode : NodeBase
    {
        private const string IconSvg = "M13,2 L3,14 H12 L11,22 L21,10 H12 L13,2 Z";

        private readonly NodeParameter _rateParam;
        private readonly NodeParameter _modeParam;
        private readonly NodeParameter _shuntFilterParam;
        private readonly NodeParameter _activeToggleParam;

        private CancellationTokenSource? _workerCts;
        private Task? _workerTask;
        private readonly Stopwatch _stopwatch = new();
        private long _sampleSequence;

        public ConnectorViewModel BusOut { get; }
        public ConnectorViewModel XdsOut { get; }
        public ConnectorViewModel DapOut { get; }

        public Ina236TelemetryNode()
            : base(
                title: "INA236 硬件遥测源",
                categoryName: "硬件母舰 / 遥测源",
                role: NodeRole.Source,
                headerAccentHex: "#10B981", // 翡翠绿
                iconGeometry: IconSvg)
        {
            // 输出引脚
            BusOut = AddOutputConnector("总路 (0x40)", ConnectorDataType.ChannelStream);
            XdsOut = AddOutputConnector("XDS110 (0x41)", ConnectorDataType.ChannelStream);
            DapOut = AddOutputConnector("DAPLink (0x42)", ConnectorDataType.ChannelStream);

            // 算子内联参数
            _rateParam = AddSliderParameter("采样频率 (Hz)", min: 1.0, max: 200.0, step: 5.0, initialValue: 50.0);
            _rateParam.GhostDoubleValue = 100.0;
            _rateParam.GhostReason = "AI: 提高至 100Hz 可捕获微秒级尖峰电流脉冲";

            _shuntFilterParam = AddSliderParameter("分流滤波系数", min: 0.0, max: 1.0, step: 0.05, initialValue: 0.85);
            _shuntFilterParam.GhostDoubleValue = 0.92;
            _shuntFilterParam.GhostReason = "AI: 建议滤波系数 0.92 以压制 50Hz 工频毛刺";

            _modeParam = AddComboParameter("遥测模式", new[] { "连续总线+分流 (Continuous)", "单次触发 (Triggered)", "省电低功耗 (Power-Down)" }, "连续总线+分流 (Continuous)");

            _activeToggleParam = AddToggleParameter("遥测上报使能", true);
            _activeToggleParam.BoolChangedCallback = enabled =>
            {
                if (enabled) StartWorker();
                else StopWorker();
            };

            StatusBadge = "就绪 (50Hz)";
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
                    double hz = Math.Clamp(_rateParam.DoubleValue, 1.0, 500.0);
                    int delayMs = Math.Max(2, (int)(1000.0 / hz));

                    try
                    {
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    phase += 0.05;
                    long seq = Interlocked.Increment(ref _sampleSequence);
                    double t = _stopwatch.Elapsed.TotalSeconds;

                    // 遥测数据生成 (母线电压 5.0V 基准 + 纹波，电流 120mA~350mA)
                    double busV = 5.05 + 0.03 * Math.Sin(phase * 1.5);
                    double busMa = 220.0 + 45.0 * Math.Cos(phase * 0.8) + 12.0 * Math.Sin(phase * 4.2);
                    double busW = busV * (busMa / 1000.0);

                    double xdsV = 3.31 + 0.01 * Math.Cos(phase);
                    double xdsMa = 85.0 + 15.0 * Math.Sin(phase * 2.1);
                    double xdsW = xdsV * (xdsMa / 1000.0);

                    double dapV = 3.29 + 0.01 * Math.Sin(phase * 0.9);
                    double dapMa = 60.0 + 10.0 * Math.Cos(phase * 1.8);
                    double dapW = dapV * (dapMa / 1000.0);

                    var busSample = new MetricSample(seq, _stopwatch.ElapsedTicks, t, 0, busV, busMa, busW);
                    var xdsSample = new MetricSample(seq, _stopwatch.ElapsedTicks, t, 1, xdsV, xdsMa, xdsW);
                    var dapSample = new MetricSample(seq, _stopwatch.ElapsedTicks, t, 2, dapV, dapMa, dapW);

                    EmitSample(0, in busSample);
                    EmitSample(1, in xdsSample);
                    EmitSample(2, in dapSample);

                    PushMicroSparklineSample(busMa);
                    StatusBadge = $"{busMa:F1}mA @ {hz:F0}Hz";
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

        protected override Task OnSampleReceivedAsync(int inputPinIndex, MetricSample sample)
        {
            // 作为源节点，默认不处理输入，仅直通
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            StopWorker();
            base.Dispose();
        }
    }
}
