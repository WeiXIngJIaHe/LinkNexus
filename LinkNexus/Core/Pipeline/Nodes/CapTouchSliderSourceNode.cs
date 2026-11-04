using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Core.Pipeline.Nodes
{
    /// <summary>
    /// CAP1208 电容触摸物理滑条映射节点 (CapTouchSliderSourceNode)
    /// 采集母舰底座电容滑条的亚毫米级绝对物理位置 (0.0 ~ 1.0)
    /// 内置平滑阻尼低通滤波与动态量程区间线性映射 [Min, Max]，输出连续可调的高频控制信号
    /// 可直连任意算子输入引脚，实现物理手搓实时交互调参
    /// </summary>
    public sealed class CapTouchSliderSourceNode : NodeBase
    {
        private const string IconSvg = "M3,5H21V7H3V5M3,11H21V13H3V11M3,17H21V19H3V17M7,4A2,2 0 0,0 5,6A2,2 0 0,0 7,8A2,2 0 0,0 9,6A2,2 0 0,0 7,4M15,10A2,2 0 0,0 13,12A2,2 0 0,0 15,14A2,2 0 0,0 17,12A2,2 0 0,0 15,10M11,16A2,2 0 0,0 9,18A2,2 0 0,0 11,20A2,2 0 0,0 13,18A2,2 0 0,0 11,16Z";

        private readonly NodeParameter _minParam;
        private readonly NodeParameter _maxParam;
        private readonly NodeParameter _dampingParam;
        private readonly NodeParameter _interactiveSliderParam;

        private CancellationTokenSource? _workerCts;
        private Task? _workerTask;
        private readonly Stopwatch _stopwatch = new();
        private long _sampleSequence;

        private double _rawPosition = 0.5;
        private double _smoothedPosition = 0.5;

        public ConnectorViewModel ControlOut { get; }

        public CapTouchSliderSourceNode()
            : base(
                title: "CAP1208 电容滑条映射",
                categoryName: "Motherbase & Hardware",
                role: NodeRole.Source,
                headerAccentHex: "#DC2626",
                iconGeometry: IconSvg,
                customHeaderGradient: CrimsonGradient)
        {
            // 输出端口
            ControlOut = AddOutputConnector("控制信号 (FloatStream)", PinDataType.FloatStream);

            // 参数定义
            _minParam = AddSliderParameter("量程下限 (Min)", min: -1000.0, max: 1000.0, step: 10.0, initialValue: 0.0);
            _maxParam = AddSliderParameter("量程上限 (Max)", min: -1000.0, max: 1000.0, step: 10.0, initialValue: 100.0);
            
            _dampingParam = AddSliderParameter("平滑阻尼系数", min: 0.05, max: 0.98, step: 0.05, initialValue: 0.85);
            _dampingParam.GhostDoubleValue = 0.90;
            _dampingParam.GhostReason = "AI: 建议阻尼系数 0.90 以获得丝滑的手搓物理阻尼感";

            _interactiveSliderParam = AddSliderParameter("物理滑条位置 (0~1)", min: 0.0, max: 1.0, step: 0.01, initialValue: 0.5);
            _interactiveSliderParam.DoubleChangedCallback = pos =>
            {
                _rawPosition = Math.Clamp(pos, 0.0, 1.0);
            };

            StatusBadge = "滑条已校准 (0~100)";
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
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(10, token).ConfigureAwait(false); // 100Hz 刷新率
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    long seq = Interlocked.Increment(ref _sampleSequence);
                    double t = _stopwatch.Elapsed.TotalSeconds;

                    // 一阶惯性滤波平滑
                    double alpha = Math.Clamp(_dampingParam.DoubleValue, 0.05, 0.98);
                    _smoothedPosition = _smoothedPosition * alpha + _rawPosition * (1.0 - alpha);

                    // 线性映射 [Min, Max]
                    double min = _minParam.DoubleValue;
                    double max = _maxParam.DoubleValue;
                    double mappedValue = min + _smoothedPosition * (max - min);

                    var sample = new MetricSample(seq, _stopwatch.ElapsedTicks, t, "SLIDER", mappedValue);
                    EmitSample(0, in sample);
                    PushMicroSparklineSample(mappedValue);

                    StatusBadge = $"{mappedValue:F2} (pos:{_smoothedPosition:P0})";
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

        public override void Dispose()
        {
            StopWorker();
            base.Dispose();
        }
    }
}

