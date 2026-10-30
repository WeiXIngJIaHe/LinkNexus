using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// 三维相空间轨迹分析卡片 (PhaseSpace3DPlotNode)
    /// 提取多通道时序信号的三维相空间坐标 (X, Y, Z 或 状态/速度/加速度)，驱动 HelixToolkit 实时构建吸引子与运动相轨迹
    /// </summary>
    public sealed class PhaseSpace3DPlotNode : NodeBase
    {
        private const string IconSvg = "M12,2 L21,7 V17 L12,22 L3,17 V7 Z M12,2 V12 M21,7 L12,12 L3,7";

        private const string ModeChannels = "通道三维投影 (CH0, CH1, CH2)";
        private const string ModeDerivatives = "一阶/二阶差分相空间 (x, dx, d2x)";

        private readonly NodeParameter _modeParam;
        private readonly NodeParameter _scaleParam;

        private double _prevX = 0;
        private double _prevDx = 0;
        private long _lastUiTickMs;

        public override string NodeTypeKey => "phase_space_3d";
        public override double CardWidth => 340.0;

        public Action<double, double, double>? PhasePointCallback { get; set; }

        public PhaseSpace3DPlotNode(double x = 710, double y = 480)
            : base("3D 相空间轨迹", "非线性相平面", "#EC4899", IconSvg, x, y)
        {
            AddInputPin("状态信号流 IN", PinDataType.ChannelStream);
            AddOutputPin("相空间坐标透传", PinDataType.ChannelStream);

            _modeParam = new NodeParameter("mode", "相空间模式", NodeParamKind.Combo, ModeChannels);
            _modeParam.Options.Add(ModeChannels);
            _modeParam.Options.Add(ModeDerivatives);

            _scaleParam = new NodeParameter("scale", "空间缩放比", NodeParamKind.Text, "1.0");

            Parameters.Add(_modeParam);
            Parameters.Add(_scaleParam);

            UpdateStatusOnUi("相空间引擎就绪", true);
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            double[] ch = packet.Channels;
            if (ch.Length == 0) return Task.CompletedTask;

            double x = ch[0];
            double y = ch.Length > 1 ? ch[1] : 0.0;
            double z = ch.Length > 2 ? ch[2] : 0.0;

            if (_modeParam.Value == ModeDerivatives)
            {
                double dx = x - _prevX;
                double d2x = dx - _prevDx;
                _prevX = x;
                _prevDx = dx;

                y = dx * 10.0;
                z = d2x * 50.0;
            }

            if (!double.TryParse(_scaleParam.Value, out double scale) || scale <= 0)
            {
                scale = 1.0;
            }

            double px = x * scale;
            double py = y * scale;
            double pz = z * scale;

            EmitPacket(0, new BlueprintPacket(new[] { px, py, pz }, string.Empty, "PHASE_3D", packet.Timestamp));

            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref _lastUiTickMs) >= 40)
            {
                Interlocked.Exchange(ref _lastUiTickMs, now);
                PhasePointCallback?.Invoke(px, py, pz);
            }

            return Task.CompletedTask;
        }
    }
}

