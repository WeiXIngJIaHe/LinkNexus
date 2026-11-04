using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// 硬件时序级微积分算子卡 (RealtimeCalculusNode)
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

        public override string NodeTypeKey => "realtime_calculus";
        public override double CardWidth => 310.0;

        public RealtimeCalculusNode(double x = 400, double y = 360)
            : base("硬件时序微积分算子", "Math & Calculus", "#D97706", IconSvg, x, y)
        {
            var inPin = AddInputPin("被积/微信号 (In)", PinDataType.FloatStream);
            inPin.HasInlineWidget = true;
            inPin.InlineValue = "0.0";

            var resetPin = AddInputPin("积分复位 (ResetTrigger)", PinDataType.Trigger);

            AddOutputPin("微积分输出 (Out)", PinDataType.FloatStream);

            _calculusModeParam = new NodeParameter("calc_mode", "算子模式", NodeParamKind.Combo, "TrapezoidalIntegral (梯形数值积分)");
            _calculusModeParam.Options.Add("TrapezoidalIntegral (梯形数值积分)");
            _calculusModeParam.Options.Add("Derivative (一阶不完全滤波微分)");
            _calculusModeParam.Options.Add("SecondDerivative (二阶滤波导数)");
            _calculusModeParam.ValueChangedCallback = mode => UpdateParameterVisibility(mode);

            _filterTauParam = new NodeParameter("tau", "滤波阻尼常数 Tau (s)", NodeParamKind.Slider, "0.02")
            {
                MinValue = 0.001, MaxValue = 0.5, Step = 0.005
            };

            _antiWindupMinParam = new NodeParameter("aw_min", "抗饱和下限", NodeParamKind.Slider, "-1000.0")
            {
                MinValue = -10000.0, MaxValue = 10000.0, Step = 100.0
            };
            _antiWindupMaxParam = new NodeParameter("aw_max", "抗饱和上限", NodeParamKind.Slider, "1000.0")
            {
                MinValue = -10000.0, MaxValue = 10000.0, Step = 100.0
            };
            _leakyFactorParam = new NodeParameter("leaky", "漏积分衰减系数", NodeParamKind.Slider, "0.99999")
            {
                MinValue = 0.99000, MaxValue = 1.00000, Step = 0.00001
            };

            _resetActionBtn = new NodeParameter("reset_btn", "立即清零积分器", NodeParamKind.ActionButton)
            {
                ButtonText = "清零积分值",
                ActionCommand = new RelayCommand(_ => ResetIntegral())
            };

            Parameters.Add(_calculusModeParam);
            Parameters.Add(_filterTauParam);
            Parameters.Add(_antiWindupMinParam);
            Parameters.Add(_antiWindupMaxParam);
            Parameters.Add(_leakyFactorParam);
            Parameters.Add(_resetActionBtn);

            UpdateParameterVisibility(_calculusModeParam.Value);
            UpdateStatusOnUi("微积分就绪", true);
        }

        public void ResetIntegral()
        {
            lock (_calcLock)
            {
                _integralSum = 0.0;
                _prevDerivative = 0.0;
                UpdateStatusOnUi("积分器已清零", true);
            }
        }

        private void UpdateParameterVisibility(string mode)
        {
            bool isIntegral = mode.Contains("Integral");
            _antiWindupMinParam.IsVisible = isIntegral;
            _antiWindupMaxParam.IsVisible = isIntegral;
            _leakyFactorParam.IsVisible = isIntegral;
            if (InputPins.Count > 1) InputPins[1].IsVisible = isIntegral;

            bool isDeriv = !isIntegral;
            _filterTauParam.IsVisible = isDeriv;
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            if (inputPinIndex == 1) // ResetTrigger
            {
                ResetIntegral();
                return Task.CompletedTask;
            }

            double val = packet.Channels.Length > 0 ? packet.Channels[0] : 0.0;
            long currTicks = DateTime.UtcNow.Ticks;
            double dt = _prevTicks > 0 ? (currTicks - _prevTicks) * (1.0 / Stopwatch.Frequency) : 0.01;
            if (dt <= 0 || dt > 1.0) dt = 0.01;
            _prevTicks = currTicks;

            double result = 0.0;
            string mode = _calculusModeParam.Value;

            lock (_calcLock)
            {
                if (mode.Contains("Integral"))
                {
                    double leaky = Math.Clamp(_leakyFactorParam.DoubleValue, 0.9, 1.0);
                    _integralSum = _integralSum * leaky + 0.5 * (val + _prevInput) * dt;

                    double min = _antiWindupMinParam.DoubleValue;
                    double max = _antiWindupMaxParam.DoubleValue;
                    _integralSum = Math.Clamp(_integralSum, Math.Min(min, max), Math.Max(min, max));

                    result = _integralSum;
                }
                else if (mode.Contains("SecondDerivative"))
                {
                    double tau = Math.Max(0.001, _filterTauParam.DoubleValue);
                    double denom = 2.0 * tau + dt;
                    double deriv1 = (2.0 * tau - dt) / denom * _prevDerivative + (2.0 / denom) * (val - _prevInput);
                    double deriv2 = (deriv1 - _prevDerivative) / dt;
                    _prevDerivative = deriv1;
                    result = deriv2;
                }
                else // Derivative
                {
                    double tau = Math.Max(0.001, _filterTauParam.DoubleValue);
                    double denom = 2.0 * tau + dt;
                    double deriv = (2.0 * tau - dt) / denom * _prevDerivative + (2.0 / denom) * (val - _prevInput);
                    _prevDerivative = deriv;
                    result = deriv;
                }

                _prevInput = val;
            }

            var outPkt = new BlueprintPacket(new[] { result }, $"CALC:{result:F3}", "CALCULUS_OUT", packet.Timestamp);
            EmitPacket(0, outPkt);
            UpdateStatusOnUi($"CALC: {result:F3}", true);
            return Task.CompletedTask;
        }
    }
}
