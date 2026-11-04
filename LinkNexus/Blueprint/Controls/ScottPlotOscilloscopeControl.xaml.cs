using System;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LinkNexus.Blueprint.Core;
using LinkNexus.Core.Pipeline;
using ScottPlot;

namespace LinkNexus.Blueprint.Controls
{
    /// <summary>
    /// 基于 ScottPlot 5 官方高帧率流式 DataLogger 的工业级示波器 (ScottPlotOscilloscopeControl)
    /// 严禁每次 Clear 后重绘 Scatter；统一由 60Hz CompositionTarget.Rendering 驱动 Refresh()
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

        private readonly ScottPlot.Plottables.DataLogger[] _loggers = new ScottPlot.Plottables.DataLogger[MaxChannels];
        private readonly double[] _latestValues = new double[MaxChannels];
        private int _activeChannels = 0;
        private bool _isPaused = false;
        private bool _isHookedRendering = false;

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
            _activeChannels = Math.Max(_activeChannels, ch + 1);

            _latestValues[ch] = sample.Value;
            _loggers[ch].Add(sample.Value);
        }

        public void PushPacket(BlueprintPacket packet)
        {
            if (_isPaused) return;
            if (packet.Channels.Length == 0) return;

            int count = Math.Min(packet.Channels.Length, MaxChannels);
            _activeChannels = Math.Max(_activeChannels, count);

            for (int ch = 0; ch < count; ch++)
            {
                double val = packet.Channels[ch];
                _latestValues[ch] = val;
                _loggers[ch].Add(val);
            }
        }

        private void OnCompositionRendering(object? sender, EventArgs e)
        {
            if (_isPaused || !IsVisible) return;

            // 更新状态与读数栏
            var sb = new StringBuilder();
            for (int ch = 0; ch < _activeChannels; ch++)
            {
                sb.Append($"CH{ch}: {_latestValues[ch]:+0.000;-0.000;0.000} | ");
            }

            TxtReadout.Text = sb.Length > 0 ? sb.ToString(0, sb.Length - 3) : "示波通道等待信号...";

            // 60Hz 全局渲染脉冲统一触发 Refresh()
            ScopePlot.Refresh();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            for (int ch = 0; ch < MaxChannels; ch++)
            {
                _loggers[ch].Clear();
                _latestValues[ch] = 0.0;
            }
            ScopePlot.Plot.Axes.AutoScale();
            ScopePlot.Refresh();
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
