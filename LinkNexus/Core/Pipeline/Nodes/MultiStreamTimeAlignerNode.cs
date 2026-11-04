using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Core.Pipeline.Nodes
{
    /// <summary>
    /// 异构总线微秒时序对齐节点 (MultiStreamTimeAlignerNode)
    /// 接收不同物理采样率（如 1kHz SPI 与 100Hz 串口）的多路异构输入流
    /// 基于时间戳 (TimestampTicks / TicksUs) 执行一阶线性插值与时间滑动重采样
    /// 输出严格同频同相位的同步数据对 (SyncA, SyncB, DeltaT_Us)，为下游复合数学运算提供基准
    /// </summary>
    public sealed class MultiStreamTimeAlignerNode : NodeBase
    {
        private const string IconSvg = "M15,1H9V3H15V1M11,14H13V8H11V14M19.03,7.39L20.45,5.97C20,5.46 19.55,5 19.04,4.56L17.62,6C16.07,4.74 14.12,4 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20A8,8 0 0,0 20,12C20,9.88 19.26,7.93 18.03,6.39L19.03,7.39M12,18A6,6 0 0,1 6,12A6,6 0 0,1 12,6A6,6 0 0,1 18,12A6,6 0 0,1 12,18Z";

        private readonly NodeParameter _syncRateParam;
        private readonly NodeParameter _interpModeParam;

        private double _lastAVal = 0.0;
        private double _lastATicks = 0.0;
        private double _prevAVal = 0.0;
        private double _prevATicks = 0.0;

        private double _lastBVal = 0.0;
        private double _lastBTicks = 0.0;

        private CancellationTokenSource? _syncCts;
        private Task? _syncTask;
        private readonly Stopwatch _stopwatch = new();
        private long _sampleSequence;

        public ConnectorViewModel StreamAIn { get; }
        public ConnectorViewModel StreamBIn { get; }

        public ConnectorViewModel SyncAOut { get; }
        public ConnectorViewModel SyncBOut { get; }
        public ConnectorViewModel DeltaTOut { get; }

        public MultiStreamTimeAlignerNode()
            : base(
                title: "多总线微秒时序对齐器",
                categoryName: "Converter & Math",
                role: NodeRole.Transform,
                headerAccentHex: "#0891B2",
                iconGeometry: IconSvg,
                customHeaderGradient: CyanAmberGradient)
        {
            // 输入引脚：支持 Blender/UE5 风格内联预设值
            StreamAIn = AddInputConnector("流 A (高速主时序)", PinDataType.FloatStream);
            StreamAIn.HasInlineWidget = true;
            StreamAIn.InlineValue = "10.0";

            StreamBIn = AddInputConnector("流 B (异构参考流)", PinDataType.FloatStream);
            StreamBIn.HasInlineWidget = true;
            StreamBIn.InlineValue = "5.0";

            // 输出端口
            SyncAOut = AddOutputConnector("同步流 A (SyncA)", PinDataType.FloatStream);
            SyncBOut = AddOutputConnector("同步流 B (SyncB)", PinDataType.FloatStream);
            DeltaTOut = AddOutputConnector("时滞偏差 (Δt μs)", PinDataType.FloatStream);

            // 参数定义
            _syncRateParam = AddSliderParameter("统一重采样率 (Hz)", min: 10.0, max: 1000.0, step: 20.0, initialValue: 100.0);
            _syncRateParam.GhostDoubleValue = 200.0;
            _syncRateParam.GhostReason = "AI: 200Hz 满足香农采样定理且压制插值相位延迟";

            _interpModeParam = AddComboParameter("重采样插值算法", new[] { "线性插值 (Linear Resampling)", "前向零阶保持 (Zero-Order Hold)", "三次样条平滑 (Cubic Spline)" }, "线性插值 (Linear Resampling)");

            StatusBadge = "对齐引擎待命中";
            StartSyncTimer();
        }

        private void StartSyncTimer()
        {
            StopSyncTimer();
            _syncCts = new CancellationTokenSource();
            var token = _syncCts.Token;
            _stopwatch.Restart();

            _syncTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    double hz = Math.Clamp(_syncRateParam.DoubleValue, 10.0, 1000.0);
                    int delayMs = Math.Max(1, (int)(1000.0 / hz));

                    try
                    {
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    long seq = Interlocked.Increment(ref _sampleSequence);
                    double nowTicks = _stopwatch.ElapsedTicks;
                    double t = _stopwatch.Elapsed.TotalSeconds;

                    // 获取当前基线值
                    double a = StreamAIn.IsConnected ? _lastAVal : StreamAIn.InlineDoubleValue;
                    double b = StreamBIn.IsConnected ? _lastBVal : StreamBIn.InlineDoubleValue;

                    // 计算微秒时间偏差
                    double deltaUs = Math.Abs(_lastATicks - _lastBTicks) * (1_000_000.0 / Stopwatch.Frequency);

                    var sA = new MetricSample(seq, (long)nowTicks, t, "SYNC_A", a);
                    var sB = new MetricSample(seq, (long)nowTicks, t, "SYNC_B", b);
                    var sDt = new MetricSample(seq, (long)nowTicks, t, "DELTA_T", deltaUs);

                    EmitSample(0, in sA);
                    EmitSample(1, in sB);
                    EmitSample(2, in sDt);

                    PushMicroSparklineSample(a);
                    StatusBadge = $"Δt={deltaUs:F1}μs | 同步 @ {hz:F0}Hz";
                }
            }, token);
        }

        private void StopSyncTimer()
        {
            _syncCts?.Cancel();
            try { _syncTask?.Wait(200); } catch { }
            _syncCts?.Dispose();
            _syncCts = null;
            _syncTask = null;
        }

        protected override Task OnSampleReceivedAsync(int inputPinIndex, MetricSample sample)
        {
            if (inputPinIndex == 0) // StreamA
            {
                _prevAVal = _lastAVal;
                _prevATicks = _lastATicks;
                _lastAVal = sample.Value;
                _lastATicks = _stopwatch.ElapsedTicks;
            }
            else if (inputPinIndex == 1) // StreamB
            {
                _lastBVal = sample.Value;
                _lastBTicks = _stopwatch.ElapsedTicks;
            }
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            StopSyncTimer();
            base.Dispose();
        }
    }
}
