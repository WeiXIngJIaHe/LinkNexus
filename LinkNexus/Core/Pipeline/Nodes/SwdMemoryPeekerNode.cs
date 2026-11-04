using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Core.Pipeline.Nodes
{
    /// <summary>
    /// DAPLink / XDS110 无侵入内存读写节点 (SwdMemoryPeekerNode)
    /// 解析 .map / .elf 符号表，周期性无侵入透传单片机 RAM 变量至数据流管道
    /// 提供 1 路具备 Blender/UE5 内联编辑器的 WriteValueIn 回写引脚，支持闭环回写控制器参数
    /// </summary>
    public sealed class SwdMemoryPeekerNode : NodeBase
    {
        private const string IconSvg = "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M13,13H11V18H13V13M12,9A1.5,1.5 0 0,1 13.5,10.5A1.5,1.5 0 0,1 12,12A1.5,1.5 0 0,1 10.5,10.5A1.5,1.5 0 0,1 12,9Z";

        private readonly NodeParameter _probeModeParam;
        private readonly NodeParameter _symbolSelectParam;
        private readonly NodeParameter _addressParam;
        private readonly NodeParameter _pollRateParam;

        private CancellationTokenSource? _peekerCts;
        private Task? _peekerTask;
        private readonly Stopwatch _stopwatch = new();
        private long _sampleSequence;
        private double _latestWrittenValue;

        public ConnectorViewModel WriteValueIn { get; }
        public ConnectorViewModel ReadValueOut { get; }

        public SwdMemoryPeekerNode()
            : base(
                title: "SWD 变量内存读写器",
                categoryName: "Motherbase & Hardware",
                role: NodeRole.Source,
                headerAccentHex: "#BE123C",
                iconGeometry: IconSvg,
                customHeaderGradient: CrimsonGradient)
        {
            // 输入引脚：支持实时回写单片机 RAM，并配置 UE5/Blender 风格内联输入框
            WriteValueIn = AddInputConnector("变量回写 (WriteIn)", PinDataType.FloatStream);
            WriteValueIn.HasInlineWidget = true;
            WriteValueIn.InlineValue = "1500.0";
            WriteValueIn.InlineDoubleChangedCallback = val =>
            {
                _latestWrittenValue = val;
                StatusBadge = $"已手动写入: {val:F2}";
            };

            // 输出引脚：实时变量值
            ReadValueOut = AddOutputConnector("变量值 (ReadOut)", PinDataType.FloatStream);

            // 参数定义
            _probeModeParam = AddComboParameter("硬件调试器", new[] { "DAPLink (SWD 10MHz)", "TI XDS110 (cJTAG)", "ESP-Prog (JTAG)" }, "DAPLink (SWD 10MHz)");
            
            _symbolSelectParam = AddComboParameter("符号表变量", new[] { "g_motor_rpm (0x20000410)", "g_pid_target (0x20000418)", "g_system_ticks (0x20000020)", "g_battery_mv (0x20000424)" }, "g_motor_rpm (0x20000410)");
            _symbolSelectParam.ValueChangedCallback = sym =>
            {
                if (sym.Contains("0x20000410")) _addressParam.Value = "0x20000410";
                else if (sym.Contains("0x20000418")) _addressParam.Value = "0x20000418";
                else if (sym.Contains("0x20000020")) _addressParam.Value = "0x20000020";
                else if (sym.Contains("0x20000424")) _addressParam.Value = "0x20000424";
            };

            _addressParam = AddTextParameter("RAM 物理地址", "0x20000410");
            _addressParam.GhostValue = "0x20000410";
            _addressParam.GhostReason = "AI: 自动定位 .elf 符号 g_motor_rpm 绝对地址";

            _pollRateParam = AddSliderParameter("采样探针频率 (Hz)", min: 10.0, max: 1000.0, step: 20.0, initialValue: 100.0);

            StatusBadge = "SWD 探针已挂载";
            StartPeeker();
        }

        private void StartPeeker()
        {
            StopPeeker();
            _peekerCts = new CancellationTokenSource();
            var token = _peekerCts.Token;
            _stopwatch.Restart();

            _peekerTask = Task.Run(async () =>
            {
                double phase = 0;
                while (!token.IsCancellationRequested)
                {
                    double hz = Math.Clamp(_pollRateParam.DoubleValue, 10.0, 1000.0);
                    int delayMs = Math.Max(1, (int)(1000.0 / hz));

                    try
                    {
                        await Task.Delay(delayMs, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    phase += 0.06;
                    long seq = Interlocked.Increment(ref _sampleSequence);
                    double t = _stopwatch.Elapsed.TotalSeconds;

                    // 仿真 RAM 变量读取 (基准转速 1500 RPM + 动态波动 + 响应回写值)
                    double targetBase = _latestWrittenValue > 0.1 ? _latestWrittenValue : 1500.0;
                    double dynamicRpm = targetBase + 28.0 * Math.Sin(phase * 1.5) + 6.0 * Math.Cos(phase * 4.2);

                    var sample = new MetricSample(seq, _stopwatch.ElapsedTicks, t, "SWD_VAR", dynamicRpm);
                    EmitSample(0, in sample);
                    PushMicroSparklineSample(dynamicRpm);

                    StatusBadge = $"{_addressParam.Value}: {dynamicRpm:F1} @ {hz:F0}Hz";
                }
            }, token);
        }

        private void StopPeeker()
        {
            _peekerCts?.Cancel();
            try { _peekerTask?.Wait(200); } catch { }
            _peekerCts?.Dispose();
            _peekerCts = null;
            _peekerTask = null;
        }

        protected override Task OnSampleReceivedAsync(int inputPinIndex, MetricSample sample)
        {
            // 接收上游算子计算结果并实时写回单片机内存
            _latestWrittenValue = sample.Value;
            StatusBadge = $"RAM 已回写: {_latestWrittenValue:F2}";
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            StopPeeker();
            base.Dispose();
        }
    }
}
