using System;
using System.Threading;
using System.Threading.Tasks;
using CodingSeb.ExpressionEvaluator;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// CodingSeb 动态数学表达式计算节点 (ExpressionMathNode)
    /// 运行 C# 动态表达式编译计算 (例如 Math.Sin(x) * 2.0 + y)，输出动态衍生通道
    /// </summary>
    public sealed class ExpressionMathNode : NodeBase
    {
        private const string IconSvg = "M4,4H20V6H4V4M4,9H20V11H4V9M4,14H14V16H4V14M4,19H14V21H4V19M16,14H18V17H21V19H18V22H16V19H13V17H16V14Z";

        private readonly NodeParameter _expressionParam;
        private readonly ExpressionEvaluator _evaluator = new();
        private readonly object _evalLock = new();

        public override string NodeTypeKey => "expression_math";
        public override double CardWidth => 310.0;

        public ExpressionMathNode(double x = 460, double y = 180)
            : base("动态数学公式", "数字信号处理 / 数学算子", "#8B5CF6", IconSvg, x, y)
        {
            AddInputPin("信号 X (自变量)", PinDataType.ChannelStream);
            AddInputPin("信号 Y (辅助量)", PinDataType.ChannelStream);
            AddOutputPin("计算结果 OUT", PinDataType.ChannelStream);

            _expressionParam = new NodeParameter("expr", "表达式 f(x, y, t)", NodeParamKind.Text, "x * 1.5 + Math.Sin(y)");
            Parameters.Add(_expressionParam);

            UpdateStatusOnUi("公式引擎就绪", true);
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            if (packet == null || packet.Channels.Length == 0) return Task.CompletedTask;

            double x = packet.Channels[0];
            double y = packet.Channels.Length > 1 ? packet.Channels[1] : 0.0;
            double t = (DateTime.Now - ProcessStartTime).TotalSeconds;
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
                    UpdateStatusOnUi($"f={result:+0.000;-0.000;0.000}", true);
                }
                catch (Exception ex)
                {
                    UpdateStatusOnUi($"公式错误: {ex.Message}", false);
                }
            }

            var outPkt = new BlueprintPacket(new[] { result }, $"{result:+0.000;-0.000;0.000}", "MATH_EXPR", DateTime.Now);
            EmitPacket(0, outPkt);

            return Task.CompletedTask;
        }

        private static readonly DateTime ProcessStartTime = DateTime.Now;
    }
}
