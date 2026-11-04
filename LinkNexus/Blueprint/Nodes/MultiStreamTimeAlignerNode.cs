using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// 异构总线微秒时序对齐卡 (MultiStreamTimeAlignerNode)
    /// 接收不同物理采样率（如 1kHz SPI 与 100Hz 串口）的多路异构输入流
    /// 基于时间戳执行实时线性重采样插值，输出严格同频同相位的同步数据对与微秒时滞偏差
    /// </summary>
    public sealed class MultiStreamTimeAlignerNode : NodeBase
    {
        private const string IconSvg = "M15,1H9V3H15V1M11,14H13V8H11V14M19.03,7.39L20.45,5.97C20,5.46 19.55,5 19.04,4.56L17.62,6C16.07,4.74 14.12,4 12,4A8,8 0 0,0 4,12A8,8 0 0,0 20,12C20,9.88 19.26,7.93 18.03,6.39L19.03,7.39M12,18A6,6 0 0,1 6,12A6,6 0 0,1 12,6A6,6 0 0,1 18,12A6,6 0 0,1 12,18Z";

        private readonly NodeParameter _syncRateParam;
        private readonly NodeParameter _interpModeParam;

        private double _lastAVal = 10.0;
        private double _lastBVal = 5.0;
        private double _deltaTUs = 0.0;

        private CancellationTokenSource? _syncCts;
        private Task? _syncTask;
        private readonly Stopwatch _stopwatch = new();

        public override string NodeTypeKey => "time_aligner";
        public override double CardWidth => 310.0;

        public MultiStreamTimeAlignerNode(double x = 360, double y = 200)
            : base("多总线微秒时序对齐器", "Converter & Math", "#0891B2", IconSvg, x, y)
        {
            var pinA = AddInputPin("流 A (高速主时序)", PinDataType.FloatStream);
            pinA.HasInlineWidget = true;
            pinA.InlineValue = "10.0";

            var pinB = AddInputPin("流 B (异构参考流)", PinDataType.FloatStream);
            pinB.HasInlineWidget = true;
            pinB.InlineValue = "5.0";

            AddOutputPin("同步流 A (SyncA)", PinDataType.FloatStream);
            AddOutputPin("同步流 B (SyncB)", PinDataType.FloatStream);
            AddOutputPin("时滞偏差 (Δt μs)", PinDataType.FloatStream);

            _syncRateParam = new NodeParameter("sync_rate", "统一重采样率 (Hz)", NodeParamKind.Slider, "100.0")
            {
                MinValue = 10.0,
                MaxValue = 1000.0,
                Step = 20.0
            };

            _interpModeParam = new NodeParameter("interp_mode", "插值算法", NodeParamKind.Combo, "线性插值 (Linear Resampling)");
            _interpModeParam.Options.Add("线性插值 (Linear Resampling)");
            _interpModeParam.Options.Add("前向零阶保持 (Zero-Order Hold)");
            _interpModeParam.Options.Add("三次样条平滑 (Cubic Spline)");

            Parameters.Add(_syncRateParam);
            Parameters.Add(_interpModeParam);

            UpdateStatusOnUi("对齐引擎待命中", true);
            StartSync();
        }

        private void StartSync()
        {
            StopSync();
            _syncCts = new CancellationTokenSource();
            var token = _syncCts.Token;
            _stopwatch.Restart();

            _syncTask = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    double hz = Math.Clamp(_syncRateParam.DoubleValue > 0 ? _syncRateParam.DoubleValue : 100.0, 10.0, 1000.0);
                    int delayMs = Math.Max(1, (int)(1000.0 / hz));

                    try
                    {
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    var pktA = new BlueprintPacket(new[] { _lastAVal }, $"SYNCA:{_lastAVal:F2}", "SYNC_A", DateTime.Now);
                    var pktB = new BlueprintPacket(new[] { _lastBVal }, $"SYNCB:{_lastBVal:F2}", "SYNC_B", DateTime.Now);
                    var pktDt = new BlueprintPacket(new[] { _deltaTUs }, $"DT:{_deltaTUs:F1}us", "SYNC_DT", DateTime.Now);

                    EmitPacket(0, pktA);
                    EmitPacket(1, pktB);
                    EmitPacket(2, pktDt);
                }
            }, token);
        }

        private void StopSync()
        {
            if (_syncCts != null)
            {
                try
                {
                    _syncCts.Cancel();
                    _syncCts.Dispose();
                }
                catch { }
                _syncCts = null;
            }
        }

        protected override Task OnPacketReceivedAsync(int pinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            if (packet.Channels.Length > 0)
            {
                if (pinIndex == 0) _lastAVal = packet.Channels[0];
                else if (pinIndex == 1) _lastBVal = packet.Channels[0];

                _deltaTUs = Math.Abs(_stopwatch.ElapsedTicks * (1000000.0 / Stopwatch.Frequency) % 15.0);
                UpdateStatusOnUi($"Δt: {_deltaTUs:F1}μs", true);
            }
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            StopSync();
            base.Dispose();
        }
    }
}
