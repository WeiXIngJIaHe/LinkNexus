using System;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Core.Pipeline.Nodes
{
    /// <summary>
    /// ScottPlot 5 示波器接收终端算子 (OscilloscopeSinkNode)
    /// 继承自 NodeBase，提供多通道实时数据汇聚并驱动卡片内嵌微波形和主示波器数据管道
    /// </summary>
    public sealed class OscilloscopeSinkNode : NodeBase
    {
        private const string IconSvg = "M3,17 L7,11 L11,15 L15,6 L19,12 L21,9 M3,20 H21";

        private readonly NodeParameter _windowCapacityParam;
        private readonly NodeParameter _autoscaleToggle;

        public ConnectorViewModel Ch0In { get; }
        public ConnectorViewModel Ch1In { get; }
        public ConnectorViewModel Ch2In { get; }

        public OscilloscopeSinkNode()
            : base(
                title: "ScottPlot 示波终端",
                categoryName: "数据呈现 / 示波器",
                role: NodeRole.Sink,
                headerAccentHex: "#38BDF8", // 电光青
                iconGeometry: IconSvg)
        {
            Ch0In = AddInputConnector("通道 0 (CH0)", ConnectorDataType.ChannelStream);
            Ch1In = AddInputConnector("通道 1 (CH1)", ConnectorDataType.ChannelStream);
            Ch2In = AddInputConnector("通道 2 (CH2)", ConnectorDataType.ChannelStream);

            _windowCapacityParam = AddSliderParameter("缓冲容量 (点数)", min: 500.0, max: 20000.0, step: 500.0, initialValue: 5000.0);
            _windowCapacityParam.GhostDoubleValue = 10000.0;
            _windowCapacityParam.GhostReason = "AI: 增大窗口容量至 10,000 点以记录完整瞬态波形";

            _autoscaleToggle = AddToggleParameter("实时自适应量程", true);

            StatusBadge = "监听就绪";
        }

        protected override Task OnSampleReceivedAsync(int inputPinIndex, MetricSample sample)
        {
            double val = sample.Value;
            PushMicroSparklineSample(val);
            StatusBadge = $"CH{inputPinIndex}: {val:+0.000;-0.000;0.000}";
            return Task.CompletedTask;
        }
    }
}
