using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace LinkNexus.Core.Pipeline.Nodes
{
    /// <summary>
    /// 硬件时序级微积分算子卡 (RealtimeCalculusNode)
    /// 严格基于硬件时间戳 dt = (current.TimestampTicks - prev.TimestampTicks) * 1e-6 积分/微分
    /// 模式 A: 带一阶低通滤波 FilterTau 阻尼的不完全微分 (Dirty Derivative)
    /// 模式 B: 双精度梯形积分，带 AntiWindup 抗饱和、LeakyFactor 漏积分防温漂与白色 ResetTrigger 清零脉冲
    /// </summary>
    public sealed class RealtimeCalculusNode : NodeBase
    {
        private const string IconSvg = "M19,3H5C3.89,3 3,3.89 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5C21,3.89 20.1,3 19,3M9,17H7V7H9V17M13,17H11V11H13V17M17,17H15V13H17V17Z";

        private readonly NodeParameter _calculusModeParam;
        private readonly NodeParameter _filterTauParam;
        private readonly NodeParameter _antiWindupMinParam;
        private readonly NodeParameter _antiWindupMaxParam;
        private readonly NodeParameter _leakyFactorParam;
        private readonly NodeParameter _resetActionBtn;

        private double _prevInput;
        private double _prevDerivative;
        private double _integralSum;
        private long _prevTicks;
        private readonly object _calcLock = new();

        public ConnectorViewModel SignalIn { get; }
        public ConnectorViewModel ResetTriggerIn { get; }
        public ConnectorViewModel CalculusOut { get; }

        public RealtimeCalculusNode()
            : base(
                title: "硬件时序微积分算子",
                categoryName: "Math & Calculus",
                role: NodeRole.Transform,
                headerAccentHex: "#D97706",
                iconGeometry: IconSvg,
                customHeaderGradient: AmberGoldGradient)
        {
            SignalIn = AddInputConnector("被积/微信号 (In)", PinDataType.FloatStream);
            SignalIn.HasInlineWidget = true;
            SignalIn.InlineValue = "0.0";

            // 白色 Unreal 风格清零脉冲引脚
            ResetTriggerIn = AddInputConnector("积分复位 (ResetTrigger)", PinDataType.Trigger);

            CalculusOut = AddOutputConnector("微积分输出 (Out)", PinDataType.FloatStream);

            _calculusModeParam = AddComboParameter("算子模式", new[]
            {
                "TrapezoidalIntegral (梯形数值积分)",
                "Derivative (一阶不完全滤波微分)",
                "SecondDerivative (二阶滤波导数)"
            }, "TrapezoidalIntegral (梯形数值积分)");

            _calculusModeParam.ValueChangedCallback = mode => UpdateParameterVisibility(mode);

            _filterTauParam = AddSliderParameter("滤波阻尼时间常数 FilterTau (s)", 0.001, 0.5, 0.005, 0.02);

            _antiWindupMinParam = AddSliderParameter("抗饱和下限 AntiWindupMin", -10000.0, 10000.0, 100.0, -1000.0);
            _antiWindupMaxParam = AddSliderParameter("抗饱和上限 AntiWindupMax", -10000.0, 10000.0, 100.0, 1000.0);
            _leakyFactorParam = AddSliderParameter("防漂移漏积分衰减 LeakyFactor", 0.99000, 1.00000, 0.00001, 0.99999);

            _resetActionBtn = AddActionParameter("立即清零积分器", () => ResetIntegral());

            UpdateParameterVisibility(_calculusModeParam.Value);
            StatusBadge = "微积分算子就绪";
        }

        public void ResetIntegral()
        {
            lock (_calcLock)
            {
                _integralSum = 0.0;
                _prevDerivative = 0.0;
                StatusBadge = "积分器已清零";
            }
        }

        private void UpdateParameterVisibility(string mode)
        {
            bool isIntegral = mode.Contains("Integral");
            _antiWindupMinParam.IsVisible = isIntegral;
            _antiWindupMaxParam.IsVisible = isIntegral;
            _leakyFactorParam.IsVisible = isIntegral;
            ResetTriggerIn.IsVisible = isIntegral;

            bool isDeriv = !isIntegral;
            _filterTauParam.IsVisible = isDeriv;
        }

        protected override Task OnSampleReceivedAsync(int inputPinIndex, MetricSample sample)
        {
            if (inputPinIndex == 1 && sample.Value > 0.5) // ResetTrigger
            {
                ResetIntegral();
                return Task.CompletedTask;
            }

            double val = sample.Value;
            long currTicks = sample.TimestampTicks > 0 ? sample.TimestampTicks : DateTime.UtcNow.Ticks;
            double dt = _prevTicks > 0 ? (currTicks - _prevTicks) * (1.0 / Stopwatch.Frequency) : 0.01;
            if (dt <= 0 || dt > 1.0) dt = 0.01;
            _prevTicks = currTicks;

            double result = 0.0;
            string mode = _calculusModeParam.Value;

            lock (_calcLock)
            {
                if (mode.Contains("Integral"))
                {
                    // 双精度梯形积分公式：Sum = Sum * LeakyFactor + 0.5 * (curr + prev) * dt
                    double leaky = Math.Clamp(_leakyFactorParam.DoubleValue, 0.9, 1.0);
                    _integralSum = _integralSum * leaky + 0.5 * (val + _prevInput) * dt;

                    // 抗饱和限幅 AntiWindup
                    double min = _antiWindupMinParam.DoubleValue;
                    double max = _antiWindupMaxParam.DoubleValue;
                    _integralSum = Math.Clamp(_integralSum, Math.Min(min, max), Math.Max(min, max));

                    result = _integralSum;
                }
                else if (mode.Contains("SecondDerivative"))
                {
                    // 二阶不完全微分
                    double tau = Math.Max(0.001, _filterTauParam.DoubleValue);
                    double denom = 2.0 * tau + dt;
                    double deriv1 = (2.0 * tau - dt) / denom * _prevDerivative + (2.0 / denom) * (val - _prevInput);
                    double deriv2 = (deriv1 - _prevDerivative) / dt;
                    _prevDerivative = deriv1;
                    result = deriv2;
                }
                else // Derivative
                {
                    // 一阶不完全微分 (Dirty Derivative): y[k] = (2*tau - dt)/(2*tau + dt)*y[k-1] + 2/(2*tau + dt)*(x[k] - x[k-1])
                    double tau = Math.Max(0.001, _filterTauParam.DoubleValue);
                    double denom = 2.0 * tau + dt;
                    double deriv = (2.0 * tau - dt) / denom * _prevDerivative + (2.0 / denom) * (val - _prevInput);
                    _prevDerivative = deriv;
                    result = deriv;
                }

                _prevInput = val;
            }

            PushMicroSparklineSample(result);
            StatusBadge = $"CALC: {result:F3}";

            var outSample = new MetricSample(sample.Sequence, currTicks, sample.TimeSeconds, "CALCULUS", result);
            EmitSample(0, in outSample);
            return Task.CompletedTask;
        }
    }
}
