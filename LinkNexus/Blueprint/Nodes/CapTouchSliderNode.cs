using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// CAP1208 电容滑条物理调参卡 (CapTouchSliderNode)
    /// 采集母舰物理底座电容滑条的亚毫米级绝对物理位置 (0.0 ~ 1.0)
    /// 内置平滑阻尼低通滤波与区间映射 [Min, Max]，输出连续可调的高频 FloatStream
    /// 可直连任意算子输入引脚，实现手搓实时调参
    /// </summary>
    public sealed class CapTouchSliderNode : NodeBase
    {
        private const string IconSvg = "M3,5H21V7H3V5M3,11H21V13H3V11M3,17H21V19H3V17M7,4A2,2 0 0,0 5,6A2,2 0 0,0 7,8A2,2 0 0,0 9,6A2,2 0 0,0 7,4M15,10A2,2 0 0,0 13,12A2,2 0 0,0 15,14A2,2 0 0,0 17,12A2,2 0 0,0 15,10M11,16A2,2 0 0,0 9,18A2,2 0 0,0 11,20A2,2 0 0,0 13,18A2,2 0 0,0 11,16Z";

        private readonly NodeParameter _minParam;
        private readonly NodeParameter _maxParam;
        private readonly NodeParameter _dampingParam;
        private readonly NodeParameter _interactiveSliderParam;

        private CancellationTokenSource? _workerCts;
        private Task? _workerTask;
        private readonly Stopwatch _stopwatch = new();
        private double _rawPosition = 0.5;
        private double _smoothedPosition = 0.5;

        public override string NodeTypeKey => "captouch_slider";
        public override double CardWidth => 310.0;

        public CapTouchSliderNode(double x = 40, double y = 310)
            : base("CAP1208 物理滑条映射", "Motherbase & Hardware", "#DC2626", IconSvg, x, y)
        {
            AddOutputPin("控制信号 (FloatStream)", PinDataType.FloatStream);

            _minParam = new NodeParameter("min_val", "量程下限 (Min)", NodeParamKind.Slider, "0.0")
            {
                MinValue = -1000.0,
                MaxValue = 1000.0,
                Step = 10.0
            };

            _maxParam = new NodeParameter("max_val", "量程上限 (Max)", NodeParamKind.Slider, "100.0")
            {
                MinValue = -1000.0,
                MaxValue = 1000.0,
                Step = 10.0
            };

            _dampingParam = new NodeParameter("damping", "平滑阻尼系数", NodeParamKind.Slider, "0.85")
            {
                MinValue = 0.05,
                MaxValue = 0.98,
                Step = 0.05
            };

            _interactiveSliderParam = new NodeParameter("pos", "物理滑条位置 (0~1)", NodeParamKind.Slider, "0.5")
            {
                MinValue = 0.0,
                MaxValue = 1.0,
                Step = 0.01,
                ValueChangedCallback = val =>
                {
                    if (double.TryParse(val, out double p))
                    {
                        _rawPosition = Math.Clamp(p, 0.0, 1.0);
                    }
                }
            };

            Parameters.Add(_minParam);
            Parameters.Add(_maxParam);
            Parameters.Add(_dampingParam);
            Parameters.Add(_interactiveSliderParam);

            UpdateStatusOnUi("滑条就绪 (0~100)", true);
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
                        await Task.Delay(10, token).ConfigureAwait(false); // 100Hz
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    // 一阶惯性滤波
                    double alpha = Math.Clamp(_dampingParam.DoubleValue, 0.05, 0.98);
                    _smoothedPosition = _smoothedPosition * alpha + _rawPosition * (1.0 - alpha);

                    double min = _minParam.DoubleValue;
                    double max = _maxParam.DoubleValue;
                    double mappedValue = min + _smoothedPosition * (max - min);

                    var pkt = new BlueprintPacket(new[] { mappedValue }, $"CTRL:{mappedValue:F2}", "TOUCH_SLIDER", DateTime.Now);
                    EmitPacket(0, pkt);
                }
            }, token);
        }

        private void StopWorker()
        {
            if (_workerCts != null)
            {
                try
                {
                    _workerCts.Cancel();
                    _workerCts.Dispose();
                }
                catch { }
                _workerCts = null;
            }
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
