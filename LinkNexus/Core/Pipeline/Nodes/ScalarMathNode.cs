using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace LinkNexus.Core.Pipeline.Nodes
{
    /// <summary>
    /// 标量数学多模算子卡 (ScalarMathNode，仿 Blender 一卡多模标量运算卡)
    /// 内置 ComboBox 切换运算模式：Add, Subtract, Multiply, Divide (防除零), MultiplyAdd, MapRange, Deadzone, Atan2
    /// 动态显示/隐藏对应引脚 (InA, InB, InC)，严格遵循 Blender/UE5 Pin 内联控件折叠规范
    /// </summary>
    public sealed class ScalarMathNode : NodeBase
    {
        private const string IconSvg = "M19,13H13V19H11V13H5V11H11V5H13V11H19V13Z";

        private readonly NodeParameter _operationModeParam;
        private readonly NodeParameter _mapInMinParam;
        private readonly NodeParameter _mapInMaxParam;
        private readonly NodeParameter _mapOutMinParam;
        private readonly NodeParameter _mapOutMaxParam;

        private double _valA;
        private double _valB = 1.0;
        private double _valC;
        private long _prevTicks;

        public ConnectorViewModel InA { get; }
        public ConnectorViewModel InB { get; }
        public ConnectorViewModel InC { get; }
        public ConnectorViewModel ResultOut { get; }

        public ScalarMathNode()
            : base(
                title: "标量数学算子",
                categoryName: "Math & Calculus",
                role: NodeRole.Transform,
                headerAccentHex: "#D97706",
                iconGeometry: IconSvg,
                customHeaderGradient: AmberGoldGradient)
        {
            InA = AddInputConnector("自变量 A", PinDataType.FloatStream);
            InA.HasInlineWidget = true;
            InA.InlineValue = "0.0";
            InA.InlineDoubleChangedCallback = v => { _valA = v; TriggerEval(); };

            InB = AddInputConnector("自变量 B", PinDataType.FloatStream);
            InB.HasInlineWidget = true;
            InB.InlineValue = "1.0";
            InB.InlineDoubleChangedCallback = v => { _valB = v; TriggerEval(); };

            InC = AddInputConnector("偏置/参数 C", PinDataType.FloatStream);
            InC.HasInlineWidget = true;
            InC.InlineValue = "0.0";
            InC.InlineDoubleChangedCallback = v => { _valC = v; TriggerEval(); };

            ResultOut = AddOutputConnector("计算结果 (Result)", PinDataType.FloatStream);

            _operationModeParam = AddComboParameter("运算模式", new[]
            {
                "Add (加法 A+B)",
                "Subtract (减法 A-B)",
                "Multiply (乘法 A*B)",
                "Divide (除法 A/B 防除零)",
                "MultiplyAdd (复合乘加 A*B+C)",
                "MapRange (线性映射)",
                "Deadzone (死区抑制 |A|<B)",
                "Atan2 (方位角 atan2(A,B))"
            }, "Add (加法 A+B)");

            _operationModeParam.ValueChangedCallback = mode => OnModeChanged(mode);

            _mapInMinParam = AddSliderParameter("映射源下限 InMin", -100.0, 100.0, 1.0, 0.0);
            _mapInMaxParam = AddSliderParameter("映射源上限 InMax", -100.0, 100.0, 1.0, 1.0);
            _mapOutMinParam = AddSliderParameter("映射目标下限 OutMin", -1000.0, 1000.0, 10.0, 0.0);
            _mapOutMaxParam = AddSliderParameter("映射目标上限 OutMax", -1000.0, 1000.0, 10.0, 100.0);

            UpdatePinVisibility(_operationModeParam.Value);
            StatusBadge = "数学算子待命";
        }

        private void OnModeChanged(string mode)
        {
            UpdatePinVisibility(mode);
            TriggerEval();
        }

        private void UpdatePinVisibility(string mode)
        {
            bool needsC = mode.Contains("MultiplyAdd") || mode.Contains("MapRange");
            InC.IsVisible = needsC;

            bool isMap = mode.Contains("MapRange");
            _mapInMinParam.IsVisible = isMap;
            _mapInMaxParam.IsVisible = isMap;
            _mapOutMinParam.IsVisible = isMap;
            _mapOutMaxParam.IsVisible = isMap;
        }

        protected override Task OnSampleReceivedAsync(int inputPinIndex, MetricSample sample)
        {
            long currTicks = sample.TimestampTicks > 0 ? sample.TimestampTicks : DateTime.UtcNow.Ticks;
            double dt = _prevTicks > 0 ? (currTicks - _prevTicks) * (1.0 / Stopwatch.Frequency) : 0.01;
            if (dt <= 0 || dt > 1.0) dt = 0.01;
            _prevTicks = currTicks;

            if (inputPinIndex == 0) _valA = sample.Value;
            else if (inputPinIndex == 1) _valB = sample.Value;
            else if (inputPinIndex == 2) _valC = sample.Value;

            ExecuteMath(sample.Sequence, currTicks, sample.TimeSeconds, dt);
            return Task.CompletedTask;
        }

        private void TriggerEval()
        {
            long ticks = DateTime.UtcNow.Ticks;
            ExecuteMath(ticks, ticks, ticks / 10000000.0, 0.01);
        }

        private void ExecuteMath(long seq, long ticks, double t, double dt)
        {
            double a = InA.IsConnected ? _valA : InA.InlineDoubleValue;
            double b = InB.IsConnected ? _valB : InB.InlineDoubleValue;
            double c = InC.IsConnected ? _valC : InC.InlineDoubleValue;

            string mode = _operationModeParam.Value;
            double res;

            if (mode.Contains("Subtract"))
            {
                res = a - b;
            }
            else if (mode.Contains("MultiplyAdd"))
            {
                res = a * b + c;
            }
            else if (mode.Contains("Multiply"))
            {
                res = a * b;
            }
            else if (mode.Contains("Divide"))
            {
                res = Math.Abs(b) > 1e-12 ? a / b : 0.0;
            }
            else if (mode.Contains("MapRange"))
            {
                double inMin = _mapInMinParam.DoubleValue;
                double inMax = _mapInMaxParam.DoubleValue;
                double outMin = _mapOutMinParam.DoubleValue;
                double outMax = _mapOutMaxParam.DoubleValue;
                double span = inMax - inMin;
                double norm = Math.Abs(span) > 1e-12 ? (a - inMin) / span : 0.0;
                res = outMin + norm * (outMax - outMin);
            }
            else if (mode.Contains("Deadzone"))
            {
                double threshold = Math.Abs(b);
                if (Math.Abs(a) <= threshold) res = 0.0;
                else res = Math.Sign(a) * (Math.Abs(a) - threshold);
            }
            else if (mode.Contains("Atan2"))
            {
                res = Math.Atan2(a, b);
            }
            else // Add
            {
                res = a + b;
            }

            PushMicroSparklineSample(res);
            StatusBadge = $"RES: {res:F3}";

            var outSample = new MetricSample(seq, ticks, t, "RESULT", res);
            EmitSample(0, in outSample);
        }
    }
}
