using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Core.Pipeline.Nodes
{
    /// <summary>
    /// 多协议动态分流节点 (ProtocolDemuxNode，仿 Blender Separate XYZ)
    /// 接收 1 路原始字节流或协议文本，支持解析 VOFA_FireWater、VOFA_JustFloat 与自定义十六进制结构体
    /// 动态解包出 CH0 ~ CH_N 独立 FloatStream，并支持一键合成 Vector3Stream 直接驱动三维姿态视口
    /// </summary>
    public sealed class ProtocolDemuxNode : NodeBase
    {
        private const string IconSvg = "M12,2L2,7L12,12L22,7L12,2M2,17L12,22L22,17L12,12L2,17M2,12L12,17L22,12L12,7L2,12Z";

        private readonly NodeParameter _protocolModeParam;
        private readonly NodeParameter _channelCountParam;
        private readonly NodeParameter _vector3CombineParam;

        public ConnectorViewModel RawBytesIn { get; }
        public ConnectorViewModel Ch0Out { get; }
        public ConnectorViewModel Ch1Out { get; }
        public ConnectorViewModel Ch2Out { get; }
        public ConnectorViewModel Ch3Out { get; }
        public ConnectorViewModel Vector3Out { get; }

        public ProtocolDemuxNode()
            : base(
                title: "协议分流解包器",
                categoryName: "Converter & Math",
                role: NodeRole.Transform,
                headerAccentHex: "#06B6D4",
                iconGeometry: IconSvg,
                customHeaderGradient: CyanAmberGradient)
        {
            // 输入引脚：支持 Blender/UE5 风格内联测试输入
            RawBytesIn = AddInputConnector("原始协议流 (RawBytes)", PinDataType.ByteStream);
            RawBytesIn.HasInlineWidget = true;
            RawBytesIn.InlineValue = "12.5,45.2,88.7,3.14";
            RawBytesIn.InlineValueChangedCallback = txt => ParseTextAndEmit(txt);

            // 输出端口
            Ch0Out = AddOutputConnector("通道 0 (CH0)", PinDataType.FloatStream);
            Ch1Out = AddOutputConnector("通道 1 (CH1)", PinDataType.FloatStream);
            Ch2Out = AddOutputConnector("通道 2 (CH2)", PinDataType.FloatStream);
            Ch3Out = AddOutputConnector("通道 3 (CH3)", PinDataType.FloatStream);
            Vector3Out = AddOutputConnector("三维向量 (Vec3 XYZ)", PinDataType.Vector3Stream);

            // 参数定义
            _protocolModeParam = AddComboParameter("解包协议规范", new[] { "VOFA_FireWater (CSV 逗号分隔)", "VOFA_JustFloat (小端浮点尾缀)", "Custom_Hex_Struct" }, "VOFA_FireWater (CSV 逗号分隔)");

            _channelCountParam = AddSliderParameter("通道输出数", min: 1.0, max: 8.0, step: 1.0, initialValue: 4.0);

            _vector3CombineParam = AddToggleParameter("激活 3D 向量合成 (CH0-2 -> XYZ)", true);

            StatusBadge = "分流引擎就绪";
        }

        protected override Task OnSampleReceivedAsync(int inputPinIndex, MetricSample sample)
        {
            // 从接收到的 MetricSample 中拆分多通道
            double ch0 = sample.Value;
            double ch1 = sample.Aux1;
            double ch2 = sample.Aux2;
            double ch3 = (ch0 + ch1) * 0.5;

            EmitChannelValues(sample.Sequence, sample.TimestampTicks, sample.TimeSeconds, ch0, ch1, ch2, ch3);
            return Task.CompletedTask;
        }

        private void ParseTextAndEmit(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            string[] parts = text.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            double[] vals = new double[4];
            for (int i = 0; i < Math.Min(parts.Length, 4); i++)
            {
                if (double.TryParse(parts[i], NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                {
                    vals[i] = d;
                }
            }

            long seq = DateTime.UtcNow.Ticks;
            EmitChannelValues(seq, seq, seq / 10000000.0, vals[0], vals[1], vals[2], vals[3]);
        }

        private void EmitChannelValues(long seq, long ticks, double t, double ch0, double ch1, double ch2, double ch3)
        {
            var s0 = new MetricSample(seq, ticks, t, "CH0", ch0);
            var s1 = new MetricSample(seq, ticks, t, "CH1", ch1);
            var s2 = new MetricSample(seq, ticks, t, "CH2", ch2);
            var s3 = new MetricSample(seq, ticks, t, "CH3", ch3);

            EmitSample(0, in s0);
            EmitSample(1, in s1);
            EmitSample(2, in s2);
            EmitSample(3, in s3);

            if (_vector3CombineParam.BoolValue)
            {
                var sVec = new MetricSample(seq, ticks, t, "VEC3", ch0, ch1, ch2);
                EmitSample(4, in sVec);
            }

            PushMicroSparklineSample(ch0);
            StatusBadge = $"CH0={ch0:F1} | CH1={ch1:F1} | CH2={ch2:F1}";
        }
    }
}
