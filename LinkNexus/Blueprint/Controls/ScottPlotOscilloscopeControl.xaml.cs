using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LinkNexus.Blueprint.Core;
using ScottPlot;

namespace LinkNexus.Blueprint.Controls
{
    public partial class ScottPlotOscilloscopeControl : UserControl
    {
        private const int MaxChannels = 6;
        private const int BufferCapacity = 600;

        private static readonly ScottPlot.Color[] ChannelColors =
        {
            ScottPlot.Color.FromHex("#38BDF8"), // CH0 电光青
            ScottPlot.Color.FromHex("#34D399"), // CH1 翡翠绿
            ScottPlot.Color.FromHex("#FBBF24"), // CH2 琥珀金
            ScottPlot.Color.FromHex("#A78BFA"), // CH3 矢量紫
            ScottPlot.Color.FromHex("#FB7185"), // CH4 珊瑚红
            ScottPlot.Color.FromHex("#2DD4BF")  // CH5 极光青绿
        };

        private readonly double[][] _channelBuffers = new double[MaxChannels][];
        private readonly ScottPlot.Plottables.Signal[] _signals = new ScottPlot.Plottables.Signal[MaxChannels];
        private readonly int[] _bufferHead = new int[MaxChannels];
        private readonly double[] _latestValues = new double[MaxChannels];
        private int _activeChannels = 0;
        private bool _isPaused = false;
        private readonly DispatcherTimer _renderTimer;

        public ScottPlotOscilloscopeControl()
        {
            InitializeComponent();

            for (int ch = 0; ch < MaxChannels; ch++)
            {
                _channelBuffers[ch] = new double[BufferCapacity];
            }

            InitScottPlotStyle();

            _renderTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33) // ~30 FPS 平滑刷新
            };
            _renderTimer.Tick += OnRenderTimerTick;
            _renderTimer.Start();
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
                var sig = plot.Add.Signal(_channelBuffers[ch]);
                sig.Color = ChannelColors[ch];
                sig.LineWidth = 2.0f;
                sig.LegendText = $"CH{ch}";
                _signals[ch] = sig;
            }

            plot.ShowLegend(Edge.Top);
            plot.Legend.BackgroundColor = ScottPlot.Color.FromHex("#1E293B").WithAlpha(0.85f);
            plot.Legend.FontColor = ScottPlot.Color.FromHex("#F1F5F9");
            plot.Axes.SetLimitsX(0, BufferCapacity);
            plot.Axes.SetLimitsY(-10, 10);
            ScopePlot.Refresh();
        }

        public void PushPacket(BlueprintPacket packet)
        {
            if (_isPaused) return;
            if (packet.Channels.Length == 0) return;

            lock (_channelBuffers)
            {
                int count = Math.Min(packet.Channels.Length, MaxChannels);
                _activeChannels = Math.Max(_activeChannels, count);

                for (int ch = 0; ch < count; ch++)
                {
                    double val = packet.Channels[ch];
                    _latestValues[ch] = val;

                    int head = _bufferHead[ch];
                    if (head < BufferCapacity)
                    {
                        _channelBuffers[ch][head] = val;
                        _bufferHead[ch] = head + 1;
                    }
                    else
                    {
                        // 环形前移
                        Array.Copy(_channelBuffers[ch], 1, _channelBuffers[ch], 0, BufferCapacity - 1);
                        _channelBuffers[ch][BufferCapacity - 1] = val;
                    }
                }
            }
        }

        private void OnRenderTimerTick(object? sender, EventArgs e)
        {
            if (_isPaused) return;

            // 更新状态与读数栏
            var sb = new StringBuilder();
            lock (_channelBuffers)
            {
                for (int ch = 0; ch < _activeChannels; ch++)
                {
                    sb.Append(string.Create(CultureInfo.InvariantCulture, $"CH{ch}: {_latestValues[ch]:+0.00;-0.00;0.00}   "));
                }
            }

            if (sb.Length > 0)
            {
                TxtReadout.Text = sb.ToString();
            }

            ScopePlot.Plot.Axes.AutoScaleY();
            ScopePlot.Refresh();
        }

        private void BtnAutoFit_Click(object sender, RoutedEventArgs e)
        {
            ScopePlot.Plot.Axes.AutoScale();
            ScopePlot.Refresh();
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            _isPaused = !_isPaused;
            BtnPause.Content = _isPaused ? "继续采样" : "冻结波形";
            StatusBadge.Text = _isPaused ? "示波器已暂停" : "硬件加速 Skia 渲染 · 60 FPS";
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            lock (_channelBuffers)
            {
                for (int ch = 0; ch < MaxChannels; ch++)
                {
                    Array.Clear(_channelBuffers[ch], 0, BufferCapacity);
                    _bufferHead[ch] = 0;
                    _latestValues[ch] = 0;
                }
                _activeChannels = 0;
            }
            ScopePlot.Plot.Axes.AutoScale();
            ScopePlot.Refresh();
            TxtReadout.Text = "缓存已清空，等待新数据流...";
        }
    }
}

