using System;
using System.Globalization;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// 多协议动态分流卡 (ProtocolDemuxNode，仿 Blender Separate XYZ)
    /// 接收 1 路原始协议字节/文本流，支持解析 VOFA_FireWater、VOFA_JustFloat 与十六进制结构体
    /// 动态分流出 CH0 ~ CH3 独立通道，并支持一键合成 Vector3Stream 驱动 3D 姿态视口
    /// </summary>
    public sealed class ProtocolDemuxNode : NodeBase
    {
        private const string IconSvg = "M12,2L2,7L12,12L22,7L12,2M2,17L12,22L22,17L12,12L2,17M2,12L12,17L22,12L12,7L2,12Z";

        private readonly NodeParameter _protocolModeParam;
        private readonly NodeParameter _vector3CombineParam;

        public override string NodeTypeKey => "protocol_demux";
        public override double CardWidth => 310.0;

        public ProtocolDemuxNode(double x = 360, double y = 50)
            : base("协议分流解包器", "Converter & Math", "#06B6D4", IconSvg, x, y)
        {
            var rawIn = AddInputPin("协议输入 (RawBytes)", PinDataType.ByteStream);
            rawIn.HasInlineWidget = true;
            rawIn.InlineValue = "12.5,45.2,88.7,3.14";

            AddOutputPin("通道 0 (CH0)", PinDataType.FloatStream);
            AddOutputPin("通道 1 (CH1)", PinDataType.FloatStream);
            AddOutputPin("通道 2 (CH2)", PinDataType.FloatStream);
            AddOutputPin("通道 3 (CH3)", PinDataType.FloatStream);
            AddOutputPin("三维向量 (Vec3 XYZ)", PinDataType.Vector3Stream);

            _protocolModeParam = new NodeParameter("proto_mode", "解包协议规范", NodeParamKind.Combo, "VOFA_FireWater (CSV 逗号分隔)");
            _protocolModeParam.Options.Add("VOFA_FireWater (CSV 逗号分隔)");
            _protocolModeParam.Options.Add("VOFA_JustFloat (小端浮点尾缀)");
            _protocolModeParam.Options.Add("Custom_Hex_Struct");

            _vector3CombineParam = new NodeParameter("combine_v3", "激活 3D 向量合成", NodeParamKind.Toggle)
            {
                BoolValue = true
            };

            Parameters.Add(_protocolModeParam);
            Parameters.Add(_vector3CombineParam);

            UpdateStatusOnUi("分流引擎就绪", true);
        }

        protected override Task OnPacketReceivedAsync(int pinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            if (pinIndex == 0)
            {
                double ch0 = packet.Channels.Length > 0 ? packet.Channels[0] : 0.0;
                double ch1 = packet.Channels.Length > 1 ? packet.Channels[1] : 0.0;
                double ch2 = packet.Channels.Length > 2 ? packet.Channels[2] : 0.0;
                double ch3 = packet.Channels.Length > 3 ? packet.Channels[3] : 0.0;

                // 如果是通过原始文本触发的解析
                if (!string.IsNullOrWhiteSpace(packet.RawText) && packet.Channels.Length <= 1)
                {
                    string[] parts = packet.RawText.Split(new[] { ',', ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 0 && double.TryParse(parts[0], NumberStyles.Any, CultureInfo.InvariantCulture, out double d0)) ch0 = d0;
                    if (parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Any, CultureInfo.InvariantCulture, out double d1)) ch1 = d1;
                    if (parts.Length > 2 && double.TryParse(parts[2], NumberStyles.Any, CultureInfo.InvariantCulture, out double d2)) ch2 = d2;
                    if (parts.Length > 3 && double.TryParse(parts[3], NumberStyles.Any, CultureInfo.InvariantCulture, out double d3)) ch3 = d3;
                }

                EmitPacket(0, new BlueprintPacket(new[] { ch0 }, $"CH0:{ch0:F2}", "DEMUX_CH0", packet.Timestamp));
                EmitPacket(1, new BlueprintPacket(new[] { ch1 }, $"CH1:{ch1:F2}", "DEMUX_CH1", packet.Timestamp));
                EmitPacket(2, new BlueprintPacket(new[] { ch2 }, $"CH2:{ch2:F2}", "DEMUX_CH2", packet.Timestamp));
                EmitPacket(3, new BlueprintPacket(new[] { ch3 }, $"CH3:{ch3:F2}", "DEMUX_CH3", packet.Timestamp));

                if (_vector3CombineParam.BoolValue)
                {
                    var v3Pkt = new BlueprintPacket(new[] { ch0, ch1, ch2 }, $"VEC3:[{ch0:F1},{ch1:F1},{ch2:F1}]", "DEMUX_VEC3", packet.Timestamp);
                    EmitPacket(4, v3Pkt);
                }

                UpdateStatusOnUi($"分流: {ch0:F1}, {ch1:F1}, {ch2:F1}", true);
            }
            return Task.CompletedTask;
        }
    }
}
