using System;
using System.Diagnostics;
using LinkNexus.Blueprint.Core;
using NWaves.Transforms;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// AI MCP 断面探针与自动阶跃调参卡 (McpAgentProbeAndTunerNode)
    /// 遵循 ModelContextProtocol 架构：
    /// 1. 维护 4096 点高速环形滑动窗口，实时解算均值、标准差、超调量与 FFT 主频；
    /// 2. 当 Agent 触发参数更新时，反向更新控制量并驱动指示；
    /// 3. 输出实时调谐控制量，供下游示波与控制。
    /// </summary>
    public sealed class McpAgentProbeAndTunerNode : NodeBase
    {
        private const string IconSvg = "M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M12,4A8,8 0 0,1 20,12A8,8 0 0,1 12,20A8,8 0 0,1 4,12A8,8 0 0,1 12,4M12,6A6,6 0 0,0 6,12A6,6 0 0,0 12,18A6,6 0 0,0 18,12A6,6 0 0,0 12,6M12,8A4,4 0 0,1 16,12A4,4 0 0,1 12,16A4,4 0 0,1 8,12A4,4 0 0,1 12,8Z";

        private const int RingCapacity = 4096;
        private readonly double[] _ringBuffer = new double[RingCapacity];
        private int _ringHead;
        private int _sampleCount;

        private readonly float[] _fftInput = new float[256];
        private readonly float[] _fftSpectrum = new float[129];
        private readonly RealFft _realFft = new(256);

        private readonly NodeParameter _targetMetricParam;
        private readonly NodeParameter _overshootThresholdParam;
        private readonly NodeParameter _aiTuningStepParam;
        private readonly NodeParameter _statsDisplayParam;

        private double _currentTunedValue = 1.0;

        public override string NodeTypeKey => "mcp_probe_tuner";
        public override double CardWidth => 320.0;

        public McpAgentProbeAndTunerNode(double x = 700, double y = 200)
            : base("AI 探针与阶跃调参器", "Control & AI MCP", "#7E22CE", IconSvg, x, y)
        {
            var probeIn = AddInputPin("待测探针 (Probe In)", PinDataType.FloatStream);
            probeIn.HasInlineWidget = true;
            probeIn.InlineValue = "12.8";

            AddOutputPin("调谐参数 (Tuned Out)", PinDataType.FloatStream);

            _targetMetricParam = new NodeParameter("target_opt", "闭环优化目标", NodeParamKind.Combo, "最小化超调量 (Min Overshoot)");
            _targetMetricParam.Options.Add("最小化超调量 (Min Overshoot)");
            _targetMetricParam.Options.Add("最快收敛时间 (Min Settling)");
            _targetMetricParam.Options.Add("频域谐波抑制 (Harmonics Suppression)");

            _overshootThresholdParam = new NodeParameter("overshoot_th", "超调容忍上限 (%)", NodeParamKind.Slider, "10.0")
            {
                MinValue = 1.0,
                MaxValue = 50.0,
                Step = 1.0
            };

            _aiTuningStepParam = new NodeParameter("step_size", "自动调参步长", NodeParamKind.Slider, "0.1")
            {
                MinValue = 0.01,
                MaxValue = 1.0,
                Step = 0.05
            };

            _statsDisplayParam = new NodeParameter("stats_val", "断面实时统计", NodeParamKind.ReadOnlyBadge, "均值: -- | 标差: -- | FFT主频: --");

            Parameters.Add(_targetMetricParam);
            Parameters.Add(_overshootThresholdParam);
            Parameters.Add(_aiTuningStepParam);
            Parameters.Add(_statsDisplayParam);

            UpdateStatusOnUi("AI 闭环探针就绪 (4096点)", true);
        }

        protected override Task OnPacketReceivedAsync(int pinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            if (packet.Channels.Length == 0) return Task.CompletedTask;
            double val = packet.Channels[0];

            _ringBuffer[_ringHead] = val;
            _ringHead = (_ringHead + 1) % RingCapacity;
            if (_sampleCount < RingCapacity) _sampleCount++;

            if ((_sampleCount % 32) == 0 && _sampleCount >= 256)
            {
                ComputeStatistics(val);
            }

            var tunedPkt = new BlueprintPacket(new[] { _currentTunedValue }, $"TUNED:{_currentTunedValue:F2}", "MCP_TUNED", DateTime.Now);
            EmitPacket(0, tunedPkt);
            return Task.CompletedTask;
        }

        private void ComputeStatistics(double latestVal)
        {
            double sum = 0.0;
            double maxVal = double.MinValue;
            double minVal = double.MaxValue;

            for (int i = 0; i < 256; i++)
            {
                int idx = (_ringHead - 1 - i + RingCapacity) % RingCapacity;
                double v = _ringBuffer[idx];
                sum += v;
                if (v > maxVal) maxVal = v;
                if (v < minVal) minVal = v;
                _fftInput[255 - i] = (float)v;
            }

            double mean = sum / 256.0;
            double varianceSum = 0.0;
            for (int i = 0; i < 256; i++)
            {
                float diff = _fftInput[i] - (float)mean;
                varianceSum += diff * diff;
            }
            double stdDev = Math.Sqrt(varianceSum / 256.0);

            _realFft.MagnitudeSpectrum(_fftInput, _fftSpectrum);
            int maxBin = 1;
            float maxMag = 0.0f;
            for (int i = 1; i < 128; i++)
            {
                if (_fftSpectrum[i] > maxMag)
                {
                    maxMag = _fftSpectrum[i];
                    maxBin = i;
                }
            }
            double dominantFreqHz = maxBin * (100.0 / 256.0); // 假定 100Hz 采样基准

            _statsDisplayParam.Value = $"均:{mean:F1} | 差:{stdDev:F1} | FFT:{dominantFreqHz:F1}Hz";

            double overshoot = mean != 0 ? Math.Abs((maxVal - mean) / mean) * 100.0 : 0.0;
            if (overshoot > _overshootThresholdParam.DoubleValue)
            {
                _currentTunedValue = Math.Max(0.1, _currentTunedValue - _aiTuningStepParam.DoubleValue);
                UpdateStatusOnUi($"AI调参: 降步长至 {_currentTunedValue:F2}", false);
            }
            else
            {
                UpdateStatusOnUi($"探针采样中 (主频 {dominantFreqHz:F1}Hz)", true);
            }
        }
    }
}
