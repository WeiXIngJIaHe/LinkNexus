using System;
using System.Threading;
using System.Threading.Tasks;
using NWaves.Filters;
using NWaves.Filters.Base;
using NWaves.Filters.Butterworth;
using NWaves.Transforms;

namespace LinkNexus.Core.Pipeline.Nodes
{
    /// <summary>
    /// NWaves 工业级信号处理算子 (DspFilterNode)
    /// 继承自 NodeBase，基于零 GC MetricSample 管道提供 Butterworth 低通/高通/带通、滑动均值与 FFT 频谱特征提取
    /// 支持 AI GhostValue 动态推荐最佳截止频率与采样率
    /// </summary>
    public sealed class DspFilterNode : NodeBase
    {
        private const string IconSvg = "M3,12 C6,3 10,21 14,12 C17,5 19,16 21,12";

        private const string AlgoButterLow = "Butterworth 低通滤波";
        private const string AlgoButterHigh = "Butterworth 高通滤波";
        private const string AlgoButterBand = "Butterworth 带通滤波";
        private const string AlgoMovingAvg = "滑动均值滤波 (Moving Average N=16)";
        private const string AlgoFft = "FFT 频谱分析 (峰值主频提取)";

        private readonly NodeParameter _algoParam;
        private readonly NodeParameter _cutoffParam;
        private readonly NodeParameter _fsParam;

        private IOnlineFilter? _activeFilter;
        private readonly float[] _fftBuffer = new float[256];
        private int _fftIndex;
        private readonly RealFft _realFft = new(256);
        private readonly float[] _spectrum = new float[129];

        public ConnectorViewModel InputSignal { get; }
        public ConnectorViewModel FilteredOutput { get; }
        public ConnectorViewModel PeakFreqOutput { get; }

        public DspFilterNode()
            : base(
                title: "NWaves 信号处理",
                categoryName: "数字信号处理 / 滤波器",
                role: NodeRole.Transform,
                headerAccentHex: "#06B6D4", // 电光青蓝
                iconGeometry: IconSvg)
        {
            InputSignal = AddInputConnector("原始信号 IN", ConnectorDataType.ChannelStream);
            FilteredOutput = AddOutputConnector("滤波输出 OUT", ConnectorDataType.ChannelStream);
            PeakFreqOutput = AddOutputConnector("频谱主频 (Hz)", ConnectorDataType.ChannelStream);

            _algoParam = AddComboParameter("滤波算法", new[] { AlgoButterLow, AlgoButterHigh, AlgoButterBand, AlgoMovingAvg, AlgoFft }, AlgoButterLow);
            _algoParam.ValueChangedCallback = _ => RebuildFilter();

            _cutoffParam = AddSliderParameter("截止频率 (Hz)", min: 1.0, max: 200.0, step: 1.0, initialValue: 10.0);
            _cutoffParam.GhostDoubleValue = 15.0;
            _cutoffParam.GhostReason = "AI: 截止频率推荐 15Hz 消除母线电源 50Hz 工频偶次谐波";
            _cutoffParam.ValueChangedCallback = _ => RebuildFilter();

            _fsParam = AddSliderParameter("基准采样率 (Hz)", min: 10.0, max: 1000.0, step: 10.0, initialValue: 100.0);
            _fsParam.ValueChangedCallback = _ => RebuildFilter();

            RebuildFilter();
            StatusBadge = "DSP 引擎就绪";
        }

        private void RebuildFilter()
        {
            double fs = Math.Clamp(_fsParam.DoubleValue, 10.0, 10000.0);
            double fc = Math.Clamp(_cutoffParam.DoubleValue, 0.5, fs * 0.45);
            string algo = _algoParam.Value;

            try
            {
                switch (algo)
                {
                    case AlgoButterLow:
                        _activeFilter = new LowPassFilter(fc / fs, 4);
                        break;
                    case AlgoButterHigh:
                        _activeFilter = new HighPassFilter(fc / fs, 4);
                        break;
                    case AlgoButterBand:
                        double f1 = Math.Max(0.5, fc * 0.5);
                        double f2 = Math.Min(fs * 0.45, fc * 1.5);
                        _activeFilter = new BandPassFilter(f1 / fs, f2 / fs, 3);
                        break;
                    case AlgoMovingAvg:
                        _activeFilter = new MovingAverageFilter(16);
                        break;
                    default:
                        _activeFilter = null;
                        break;
                }
                StatusBadge = $"{algo} @ {fc:F0}Hz";
            }
            catch (Exception ex)
            {
                StatusBadge = $"配置异常: {ex.Message}";
            }
        }

        protected override Task OnSampleReceivedAsync(int inputPinIndex, MetricSample sample)
        {
            double rawVal = sample.Value;
            double filteredVal = rawVal;

            if (_activeFilter != null)
            {
                filteredVal = _activeFilter.Process((float)rawVal);
            }

            // 输出滤波后的样本
            var outSample = new MetricSample(
                sample.Sequence,
                sample.TimestampTicks,
                sample.TimeSeconds,
                sample.Channel,
                filteredVal,
                sample.Aux1,
                sample.Aux2);

            EmitSample(0, in outSample);
            PushMicroSparklineSample(filteredVal);

            // FFT 频谱主频提取
            _fftBuffer[_fftIndex++] = (float)rawVal;
            if (_fftIndex >= _fftBuffer.Length)
            {
                _fftIndex = 0;
                _realFft.MagnitudeSpectrum(_fftBuffer, _spectrum);

                int maxBin = 1;
                float maxMag = _spectrum[1];
                for (int b = 2; b < _spectrum.Length; b++)
                {
                    if (_spectrum[b] > maxMag)
                    {
                        maxMag = _spectrum[b];
                        maxBin = b;
                    }
                }

                double fs = _fsParam.DoubleValue;
                double peakFreq = maxBin * (fs / 256.0);

                var freqSample = new MetricSample(
                    sample.Sequence,
                    sample.TimestampTicks,
                    sample.TimeSeconds,
                    0,
                    peakFreq);

                EmitSample(1, in freqSample);
            }

            return Task.CompletedTask;
        }
    }
}
