using System;
using System.Threading;
using System.Threading.Tasks;
using CodingSeb.ExpressionEvaluator;

namespace LinkNexus.Core.Pipeline.Nodes
{
    /// <summary>
    /// CodingSeb 动态数学表达式计算算子 (ExpressionMathNode)
    /// 继承自 NodeBase，可在运行时编译执行任意 C# 数学公式 (如 Math.Sin(x) * Math.Exp(-0.1*t) + y)
    /// 并支持 AI GhostValue 动态生成最优补偿公式
    /// </summary>
    public sealed class ExpressionMathNode : NodeBase
    {
        private const string IconSvg = "M4,4H20V6H4V4M4,9H20V11H4V9M4,14H14V16H4V14M4,19H14V21H4V19M16,14H18V17H21V19H18V22H16V19H13V17H16V14Z";

        private readonly NodeParameter _expressionParam;
        private readonly ExpressionEvaluator _evaluator = new();
        private readonly object _evalLock = new();

        public ConnectorViewModel InputX { get; }
        public ConnectorViewModel InputY { get; }
        public ConnectorViewModel OutputResult { get; }

        public ExpressionMathNode()
            : base(
                title: "动态数学公式",
                categoryName: "数字信号处理 / 数学算子",
                role: NodeRole.Transform,
                headerAccentHex: "#8B5CF6", // 矢量紫
                iconGeometry: IconSvg)
        {
            InputX = AddInputConnector("自变量 X", ConnectorDataType.ChannelStream);
            InputY = AddInputConnector("辅助量 Y", ConnectorDataType.ChannelStream);
            OutputResult = AddOutputConnector("计算结果 OUT", ConnectorDataType.ChannelStream);

            _expressionParam = AddExpressionParameter("表达式 f(x, y, t)", "x * 1.5 + Math.Sin(y)");
            _expressionParam.GhostValue = "Math.Sqrt(Math.Pow(x, 2) + Math.Pow(y, 2))";
            _expressionParam.GhostReason = "AI: 计算欧氏空间合成模长 (L2范数)";

            StatusBadge = "公式引擎就绪";
        }

        protected override Task OnSampleReceivedAsync(int inputPinIndex, MetricSample sample)
        {
            double x = sample.Value;
            double y = sample.Aux1;
            double t = sample.TimeSeconds;
            double result = x;

            lock (_evalLock)
            {
                try
                {
                    _evaluator.Variables["x"] = x;
                    _evaluator.Variables["y"] = y;
                    _evaluator.Variables["t"] = t;

                    object res = _evaluator.Evaluate(_expressionParam.Value);
                    result = Convert.ToDouble(res);
                    StatusBadge = $"f={result:+0.000;-0.000;0.000}";
                }
                catch (Exception ex)
                {
                    StatusBadge = $"公式错误: {ex.Message}";
                }
            }

            var outSample = new MetricSample(
                sample.Sequence,
                sample.TimestampTicks,
                sample.TimeSeconds,
                0,
                result,
                sample.Aux1,
                sample.Aux2);

            EmitSample(0, in outSample);
            PushMicroSparklineSample(result);

            return Task.CompletedTask;
        }
    }
}
