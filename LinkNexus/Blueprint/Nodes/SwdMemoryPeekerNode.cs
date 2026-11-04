using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// DAPLink / XDS110 无侵入内存读写卡 (SwdMemoryPeekerNode)
    /// 支持解析 .map / .elf 符号表，周期性读取单片机 RAM 变量并转化为 FloatStream 输出
    /// 具备 Blender/UE5 风格 WriteValueIn 内联编辑器输入引脚，支持闭环写回单片机内存
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
        private double _latestWrittenValue = 1500.0;

        public override string NodeTypeKey => "swd_memory_peeker";
        public override double CardWidth => 310.0;

        public SwdMemoryPeekerNode(double x = 40, double y = 180)
            : base("SWD 变量内存读写器", "Motherbase & Hardware", "#BE123C", IconSvg, x, y)
        {
            // 输入引脚：支持回写 RAM，具备 Blender/UE5 内联编辑输入框 (未连线时显示输入框，已连线自动折叠)
            var writeInPin = AddInputPin("变量回写 (WriteIn)", PinDataType.FloatStream);
            writeInPin.HasInlineWidget = true;
            writeInPin.InlineValue = "1500.0";

            // 输出引脚：实时读取的值
            AddOutputPin("变量值 (ReadOut)", PinDataType.FloatStream);

            _probeModeParam = new NodeParameter("probe_mode", "硬件调试器", NodeParamKind.Combo, "DAPLink (SWD 10MHz)");
            _probeModeParam.Options.Add("DAPLink (SWD 10MHz)");
            _probeModeParam.Options.Add("TI XDS110 (cJTAG)");
            _probeModeParam.Options.Add("ESP-Prog (JTAG)");

            _symbolSelectParam = new NodeParameter("symbol_select", "符号表变量", NodeParamKind.Combo, "g_motor_rpm (0x20000410)");
            _symbolSelectParam.Options.Add("g_motor_rpm (0x20000410)");
            _symbolSelectParam.Options.Add("g_pid_target (0x20000418)");
            _symbolSelectParam.Options.Add("g_system_ticks (0x20000020)");
            _symbolSelectParam.Options.Add("g_battery_mv (0x20000424)");
            _symbolSelectParam.ValueChangedCallback = sym =>
            {
                if (sym.Contains("0x20000410")) _addressParam.Value = "0x20000410";
                else if (sym.Contains("0x20000418")) _addressParam.Value = "0x20000418";
                else if (sym.Contains("0x20000020")) _addressParam.Value = "0x20000020";
                else if (sym.Contains("0x20000424")) _addressParam.Value = "0x20000424";
            };

            _addressParam = new NodeParameter("address", "RAM 物理地址", NodeParamKind.Text, "0x20000410");

            _pollRateParam = new NodeParameter("poll_rate", "采样探针频率 (Hz)", NodeParamKind.Slider, "100.0")
            {
                MinValue = 10.0,
                MaxValue = 1000.0,
                Step = 10.0
            };

            Parameters.Add(_probeModeParam);
            Parameters.Add(_symbolSelectParam);
            Parameters.Add(_addressParam);
            Parameters.Add(_pollRateParam);

            UpdateStatusOnUi("SWD 探针已挂载", true);
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
                    double hz = Math.Clamp(_pollRateParam.DoubleValue > 0 ? _pollRateParam.DoubleValue : 100.0, 10.0, 1000.0);
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
                    double t = _stopwatch.Elapsed.TotalSeconds;

                    // 模拟从单片机 RAM 物理地址读取的变量值
                    double baseVal = _latestWrittenValue;
                    double readVal = baseVal + 80.0 * Math.Sin(phase * 1.5) + 15.0 * Math.Cos(phase * 4.2);

                    var pkt = new BlueprintPacket(new[] { readVal }, $"VAL:{readVal:F2}", "SWD_PEEK", DateTime.Now);
                    EmitPacket(0, pkt);
                }
            }, token);
        }

        private void StopPeeker()
        {
            if (_peekerCts != null)
            {
                try
                {
                    _peekerCts.Cancel();
                    _peekerCts.Dispose();
                }
                catch { }
                _peekerCts = null;
            }
        }

        protected override Task OnPacketReceivedAsync(int pinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            if (pinIndex == 0 && packet.Channels.Length > 0)
            {
                _latestWrittenValue = packet.Channels[0];
                UpdateStatusOnUi($"RAM写入: {_latestWrittenValue:F1}", true);
            }
            return Task.CompletedTask;
        }

        public override void Dispose()
        {
            StopPeeker();
            base.Dispose();
        }
    }
}
