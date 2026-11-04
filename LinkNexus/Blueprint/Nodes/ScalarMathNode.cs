using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
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

        public override string NodeTypeKey => "scalar_math";
        public override double CardWidth => 300.0;

        public ScalarMathNode(double x = 400, double y = 200)
            : base("标量数学算子", "Math & Calculus", "#D97706", IconSvg, x, y)
        {
            var pinA = AddInputPin("自变量 A", PinDataType.FloatStream);
            pinA.HasInlineWidget = true;
            pinA.InlineValue = "0.0";
            pinA.InlineDoubleChangedCallback = v => { _valA = v; TriggerEval(); };

            var pinB = AddInputPin("自变量 B", PinDataType.FloatStream);
            pinB.HasInlineWidget = true;
            pinB.InlineValue = "1.0";
            pinB.InlineDoubleChangedCallback = v => { _valB = v; TriggerEval(); };

            var pinC = AddInputPin("偏置/参数 C", PinDataType.FloatStream);
            pinC.HasInlineWidget = true;
            pinC.InlineValue = "0.0";
            pinC.InlineDoubleChangedCallback = v => { _valC = v; TriggerEval(); };

            AddOutputPin("计算结果 (Result)", PinDataType.FloatStream);

            _operationModeParam = new NodeParameter("op_mode", "运算模式", NodeParamKind.Combo, "Add (加法 A+B)");
            _operationModeParam.Options.Add("Add (加法 A+B)");
            _operationModeParam.Options.Add("Subtract (减法 A-B)");
            _operationModeParam.Options.Add("Multiply (乘法 A*B)");
            _operationModeParam.Options.Add("Divide (除法 A/B 防除零)");
            _operationModeParam.Options.Add("MultiplyAdd (复合乘加 A*B+C)");
            _operationModeParam.Options.Add("MapRange (线性映射)");
            _operationModeParam.Options.Add("Deadzone (死区抑制 |A|<B)");
            _operationModeParam.Options.Add("Atan2 (方位角 atan2(A,B))");

            _operationModeParam.ValueChangedCallback = mode => OnModeChanged(mode);

            _mapInMinParam = new NodeParameter("in_min", "源下限 InMin", NodeParamKind.Slider, "0.0")
            {
                MinValue = -100.0, MaxValue = 100.0, Step = 1.0
            };
            _mapInMaxParam = new NodeParameter("in_max", "源上限 InMax", NodeParamKind.Slider, "1.0")
            {
                MinValue = -100.0, MaxValue = 100.0, Step = 1.0
            };
            _mapOutMinParam = new NodeParameter("out_min", "目标下限 OutMin", NodeParamKind.Slider, "0.0")
            {
                MinValue = -1000.0, MaxValue = 1000.0, Step = 10.0
            };
            _mapOutMaxParam = new NodeParameter("out_max", "目标上限 OutMax", NodeParamKind.Slider, "100.0")
            {
                MinValue = -1000.0, MaxValue = 1000.0, Step = 10.0
            };

            Parameters.Add(_operationModeParam);
            Parameters.Add(_mapInMinParam);
            Parameters.Add(_mapInMaxParam);
            Parameters.Add(_mapOutMinParam);
            Parameters.Add(_mapOutMaxParam);

            UpdatePinVisibility(_operationModeParam.Value);
            UpdateStatusOnUi("数学算子待命", true);
        }

        private void OnModeChanged(string mode)
        {
            UpdatePinVisibility(mode);
            TriggerEval();
        }

        private void UpdatePinVisibility(string mode)
        {
            bool needsC = mode.Contains("MultiplyAdd") || mode.Contains("MapRange");
            if (InputPins.Count > 2)
            {
                InputPins[2].IsVisible = needsC;
            }

            bool isMap = mode.Contains("MapRange");
            _mapInMinParam.IsVisible = isMap;
            _mapInMaxParam.IsVisible = isMap;
            _mapOutMinParam.IsVisible = isMap;
            _mapOutMaxParam.IsVisible = isMap;
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            long currTicks = DateTime.UtcNow.Ticks;
            double dt = _prevTicks > 0 ? (currTicks - _prevTicks) * (1.0 / Stopwatch.Frequency) : 0.01;
            if (dt <= 0 || dt > 1.0) dt = 0.01;
            _prevTicks = currTicks;

            if (packet.Channels.Length > 0)
            {
                if (inputPinIndex == 0) _valA = packet.Channels[0];
                else if (inputPinIndex == 1) _valB = packet.Channels[0];
                else if (inputPinIndex == 2) _valC = packet.Channels[0];
            }

            ExecuteMath(packet.Timestamp, dt);
            return Task.CompletedTask;
        }

        private void TriggerEval()
        {
            ExecuteMath(DateTime.Now, 0.01);
        }

        private void ExecuteMath(DateTime ts, double dt)
        {
            double a = InputPins.Count > 0 && InputPins[0].IsConnected ? _valA : (InputPins.Count > 0 ? InputPins[0].InlineDoubleValue : 0.0);
            double b = InputPins.Count > 1 && InputPins[1].IsConnected ? _valB : (InputPins.Count > 1 ? InputPins[1].InlineDoubleValue : 1.0);
            double c = InputPins.Count > 2 && InputPins[2].IsConnected ? _valC : (InputPins.Count > 2 ? InputPins[2].InlineDoubleValue : 0.0);

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

            var outPkt = new BlueprintPacket(new[] { res }, $"RES:{res:F3}", "SCALAR_OUT", ts);
            EmitPacket(0, outPkt);
            UpdateStatusOnUi($"RES: {res:F3}", true);
        }
    }
}
