using System;
using System.Threading;
using System.Threading.Tasks;
using LinkNexus.Blueprint.Core;
using NWaves.Filters.Butterworth;
using NWaves.Filters.Base;
using NWaves.Filters;
using NWaves.Transforms;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// NWaves 工业级数字信号处理节点 (DspFilterNode)
    /// 基于开源 NWaves 算法库，提供巴特沃斯低通/高通/带通、滑动均值、中值滤波与实时 FFT 频谱特征提取
    /// </summary>
    public sealed class DspFilterNode : NodeBase
    {
        private const string IconSvg = "M3,12 C6,3 10,21 14,12 C17,5 19,16 21,12";

        private const string AlgoButterLow = "Butterworth 低通滤波 (10 Hz)";
        private const string AlgoButterHigh = "Butterworth 高通滤波 (1 Hz)";
        private const string AlgoButterBand = "Butterworth 带通滤波 (5~40 Hz)";
        private const string AlgoMovingAvg = "滑动均值滤波 (Moving Average N=16)";
        private const string AlgoMedian = "中值滤波 (Median Filter N=9)";
        private const string AlgoFft = "FFT 频谱分析 (峰值主频提取)";

        private readonly NodeParameter _algoParam;
        private readonly NodeParameter _samplingRateParam;

        private IOnlineFilter? _activeFilter;
        private string _lastAlgo = string.Empty;
        private double _lastSamplingRate = 100.0;

        // FFT 分析缓存
        private readonly float[] _fftBuffer = new float[256];
        private int _fftIndex = 0;
        private readonly RealFft _realFft = new RealFft(256);
        private readonly float[] _spectrum = new float[129];

        public override string NodeTypeKey => "dsp_filter";
        public override double CardWidth => 330.0;

        public DspFilterNode(double x = 460, double y = 240)
            : base("NWaves 信号处理", "数字信号处理", "#06B6D4", IconSvg, x, y)
        {
            AddInputPin("原始信号流 IN", PinDataType.ChannelStream);
            AddOutputPin("滤波输出 OUT", PinDataType.ChannelStream);
            AddOutputPin("频谱主频 (Hz)", PinDataType.ChannelStream);

            _algoParam = new NodeParameter("algo", "DSP 算法", NodeParamKind.Combo, AlgoButterLow)
            {
                ValueChangedCallback = _ => RebuildFilter()
            };
            _algoParam.Options.Add(AlgoButterLow);
            _algoParam.Options.Add(AlgoButterHigh);
            _algoParam.Options.Add(AlgoButterBand);
            _algoParam.Options.Add(AlgoMovingAvg);
            _algoParam.Options.Add(AlgoMedian);
            _algoParam.Options.Add(AlgoFft);

            _samplingRateParam = new NodeParameter("fs", "采样率 (Hz)", NodeParamKind.Text, "100.0")
            {
                ValueChangedCallback = _ => RebuildFilter()
            };

            Parameters.Add(_algoParam);
            Parameters.Add(_samplingRateParam);

            RebuildFilter();
            UpdateStatusOnUi("DSP 引擎就绪", true);
        }

        private void RebuildFilter()
        {
            string algo = _algoParam.Value;
            if (!double.TryParse(_samplingRateParam.Value, out double fs) || fs <= 0)
            {
                fs = 100.0;
            }

            _lastAlgo = algo;
            _lastSamplingRate = fs;

            try
            {
                switch (algo)
                {
                    case AlgoButterLow:
                        double freqLow = Math.Min(10.0, fs * 0.45);
                        _activeFilter = new LowPassFilter(freqLow / fs, 4);
                        break;
                    case AlgoButterHigh:
                        double freqHigh = Math.Min(1.0, fs * 0.45);
                        _activeFilter = new HighPassFilter(freqHigh / fs, 4);
                        break;
                    case AlgoButterBand:
                        double f1 = Math.Min(5.0, fs * 0.4);
                        double f2 = Math.Min(40.0, fs * 0.45);
                        if (f2 <= f1) f2 = f1 + 5.0;
                        _activeFilter = new BandPassFilter(f1 / fs, f2 / fs, 3);
                        break;
                    case AlgoMovingAvg:
                        _activeFilter = new MovingAverageFilter(16);
                        break;
                    case AlgoMedian:
                        _activeFilter = new MedianFilter(9);
                        break;
                    case AlgoFft:
                        _activeFilter = null;
                        break;
                    default:
                        _activeFilter = new MovingAverageFilter(8);
                        break;
                }
                UpdateStatusOnUi($"已载入: {algo}", true);
            }
            catch (Exception ex)
            {
                UpdateStatusOnUi($"滤波器构建失败: {ex.Message}", false);
            }
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            if (packet.Channels.Length == 0) return Task.CompletedTask;

            double[] inputChannels = packet.Channels;
            double[] filteredChannels = new double[inputChannels.Length];

            lock (this)
            {
                var filter = _activeFilter;
                if (filter != null)
                {
                    for (int i = 0; i < inputChannels.Length; i++)
                    {
                        filteredChannels[i] = filter.Process((float)inputChannels[i]);
                    }
                }
                else
                {
                    Array.Copy(inputChannels, filteredChannels, inputChannels.Length);
                }

                // 若启用 FFT 频谱分析模式，累积首通道并做实时频谱变换
                if (_algoParam.Value == AlgoFft)
                {
                    float sample = (float)inputChannels[0];
                    _fftBuffer[_fftIndex] = sample;
                    _fftIndex = (_fftIndex + 1) % _fftBuffer.Length;

                    if (_fftIndex == 0)
                    {
                        _realFft.MagnitudeSpectrum(_fftBuffer, _spectrum);
                        // 寻找直流分量之后的最高能量谱峰
                        float maxMag = 0f;
                        int maxBin = 0;
                        for (int k = 1; k < _spectrum.Length; k++)
                        {
                            if (_spectrum[k] > maxMag)
                            {
                                maxMag = _spectrum[k];
                                maxBin = k;
                            }
                        }
                        double binFreqHz = (maxBin * _lastSamplingRate) / _fftBuffer.Length;
                        EmitPacket(1, new BlueprintPacket(new[] { binFreqHz }, binFreqHz.ToString("F1"), "DSP_FFT_PEAK", packet.Timestamp));
                    }
                }
            }

            // 发射滤波后数据流
            EmitPacket(0, new BlueprintPacket(filteredChannels, packet.RawText, packet.StreamPrefix, packet.Timestamp));
            return Task.CompletedTask;
        }
    }
}

