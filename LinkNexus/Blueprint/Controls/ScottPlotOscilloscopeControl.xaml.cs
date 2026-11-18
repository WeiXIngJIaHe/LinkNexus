using System;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkNexus.Blueprint.Core;
using ScottPlot;

namespace LinkNexus.Blueprint.Controls
{
    /// <summary>
    /// 基于 ScottPlot 5 官方高帧率流式 DataLogger 的工业级示波器 (ScottPlotOscilloscopeControl)
    /// 采用脏数据标记 (_hasDirtyData) + 显示器刷新率自适应节流 (60Hz ~ 240Hz+) + 单帧绘制耗时预算退避：
    /// 静止无数据时 0 重绘零 CPU 占用；高频推流时满帧顺滑且绝不阻塞主界面交互。
    /// </summary>
    public partial class ScottPlotOscilloscopeControl : UserControl
    {
        private const int MaxChannels = 6;
        private const int BufferCapacity = 800;

        private static readonly ScottPlot.Color[] ChannelColors =
        {
            ScottPlot.Color.FromHex("#38BDF8"), // CH0 电光青
            ScottPlot.Color.FromHex("#34D399"), // CH1 翡翠绿
            ScottPlot.Color.FromHex("#FBBF24"), // CH2 琥珀金
            ScottPlot.Color.FromHex("#A78BFA"), // CH3 矢量紫
            ScottPlot.Color.FromHex("#FB7185"), // CH4 珊瑚红
            ScottPlot.Color.FromHex("#2DD4BF")  // CH5 极光青绿
        };

        private readonly object _dataLock = new();
        private readonly ScottPlot.Plottables.DataLogger[] _loggers = new ScottPlot.Plottables.DataLogger[MaxChannels];
        private readonly double[] _latestValues = new double[MaxChannels];
        private readonly StringBuilder _readoutBuilder = new(128);

        private int _activeChannels = 0;
        private bool _isPaused = false;
        private bool _isHookedRendering = false;

        // 脏数据标记与高刷屏自适应预算状态
        private volatile bool _hasDirtyData = false;
        private long _lastRenderTick = 0;
        private double _smoothedRenderDurationMs = 1.5;

        public ScottPlotOscilloscopeControl()
        {
            InitializeComponent();
            InitScottPlotStyle();

            Loaded += ScottPlotOscilloscopeControl_Loaded;
            Unloaded += ScottPlotOscilloscopeControl_Unloaded;
        }

        private void ScottPlotOscilloscopeControl_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_isHookedRendering)
            {
                CompositionTarget.Rendering += OnCompositionRendering;
                _isHookedRendering = true;
            }
        }

        private void ScottPlotOscilloscopeControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_isHookedRendering)
            {
                CompositionTarget.Rendering -= OnCompositionRendering;
                _isHookedRendering = false;
            }
        }

        private void InitScottPlotStyle()
        {
            var plot = ScopePlot.Plot;
            plot.FigureBackground.Color = ScottPlot.Color.FromHex("#0B0F17");
            plot.DataBackground.Color = ScottPlot.Color.FromHex("#030712");
            plot.Axes.Color(ScottPlot.Color.FromHex("#64748B"));
            plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#1E293B");
            plot.Grid.MinorLineColor = ScottPlot.Color.FromHex("#0F172A");

            for (int ch = 0; ch < MaxChannels; ch++)
            {
                var logger = plot.Add.DataLogger();
                logger.Color = ChannelColors[ch];
                logger.LineWidth = 2.0f;
                logger.LegendText = $"CH{ch}";
                logger.ViewSlide(BufferCapacity);
                _loggers[ch] = logger;
            }

            plot.ShowLegend(Edge.Top);
            plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#1E293B").WithAlpha(0.85f);
            plot.Legend.FontColor = ScottPlot.Color.FromHex("#F1F5F9");
            plot.Axes.SetLimitsY(-10, 10);
            ScopePlot.Refresh();
        }

        public void PushSample(in MetricSample sample, int channelIndex = 0)
        {
            if (_isPaused) return;
            int ch = Math.Clamp(channelIndex, 0, MaxChannels - 1);

            lock (_dataLock)
            {
                _activeChannels = Math.Max(_activeChannels, ch + 1);
                _latestValues[ch] = sample.Value;
                _loggers[ch].Add(sample.Value);
                _hasDirtyData = true;
            }
        }

        public void PushPacket(BlueprintPacket packet)
        {
            if (_isPaused) return;
            if (packet.Channels.Length == 0) return;

            int count = Math.Min(packet.Channels.Length, MaxChannels);
            lock (_dataLock)
            {
                _activeChannels = Math.Max(_activeChannels, count);
                for (int ch = 0; ch < count; ch++)
                {
                    double val = packet.Channels[ch];
                    _latestValues[ch] = val;
                    _loggers[ch].Add(val);
                }
                _hasDirtyData = true;
            }
        }

        private void OnCompositionRendering(object? sender, EventArgs e)
        {
            // 静止无数据、冻结或不可见时 0 重绘，彻底消除 60~240Hz 空转开销
            if (_isPaused || !IsVisible || !_hasDirtyData) return;

            long nowTick = Stopwatch.GetTimestamp();
            double elapsedMs = (nowTick - _lastRenderTick) * (1000.0 / Stopwatch.Frequency);

            // 按当前显示器周期 (1000 / TargetHz) 与单帧绘制耗时预算动态节流，防止高刷屏过载掉帧
            double targetIntervalMs = DisplayRefreshService.TargetFrameIntervalMs * 0.90;
            double budgetBackoffMs = _smoothedRenderDurationMs * 1.65;
            double requiredIntervalMs = Math.Max(targetIntervalMs, budgetBackoffMs);

            if (elapsedMs < requiredIntervalMs)
            {
                return;
            }

            _lastRenderTick = nowTick;

            long t0 = Stopwatch.GetTimestamp();
            lock (_dataLock)
            {
                _hasDirtyData = false;
                _readoutBuilder.Clear();
                for (int ch = 0; ch < _activeChannels; ch++)
                {
                    _readoutBuilder.Append($"CH{ch}: {_latestValues[ch]:+0.000;-0.000;0.000} | ");
                }

                TxtReadout.Text = _readoutBuilder.Length > 0
                    ? _readoutBuilder.ToString(0, _readoutBuilder.Length - 3)
                    : "示波通道等待信号...";

                ScopePlot.Refresh();
            }
            long t1 = Stopwatch.GetTimestamp();

            double renderMs = (t1 - t0) * (1000.0 / Stopwatch.Frequency);
            _smoothedRenderDurationMs = _smoothedRenderDurationMs * 0.80 + renderMs * 0.20;
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            lock (_dataLock)
            {
                for (int ch = 0; ch < MaxChannels; ch++)
                {
                    _loggers[ch].Clear();
                    _latestValues[ch] = 0.0;
                }
                _hasDirtyData = false;
                ScopePlot.Plot.Axes.AutoScale();
                ScopePlot.Refresh();
            }
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            _isPaused = !_isPaused;
            BtnPause.Content = _isPaused ? "继续" : "冻结";
            BtnPause.Foreground = _isPaused
                ? (System.Windows.Media.Brush)FindResource("WarningAmberBrush")
                : (System.Windows.Media.Brush)FindResource("AppTextPrimaryBrush");
        }

        private void BtnAutoFit_Click(object sender, RoutedEventArgs e)
        {
            ScopePlot.Plot.Axes.AutoScale();
            ScopePlot.Refresh();
        }
    }
}
