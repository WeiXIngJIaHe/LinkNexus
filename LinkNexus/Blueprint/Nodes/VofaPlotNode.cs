using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// VOFA+ 风格多通道实时波形示波器卡片 (VofaPlotNode)
    /// 采用零 GC 压力的环形采样队列与冻结 DrawingImage 硬件加速矢量化渲染，支持最多 6 路并发浮点波形自适应量程与透传级联
    /// </summary>
    public sealed class VofaPlotNode : NodeBase
    {
        private const string IconSvg = "M3,17 L7,11 L11,15 L15,6 L19,12 L21,9 M3,20 H21";
        private const int MaxChannels = 6;
        private const int Capacity = 150;

        private static readonly Brush[] ChannelBrushes =
        {
            CreateFrozenBrush("#38BDF8"), // CH0 电光青
            CreateFrozenBrush("#34D399"), // CH1 翡翠绿
            CreateFrozenBrush("#FBBF24"), // CH2 琥珀金
            CreateFrozenBrush("#A78BFA"), // CH3 矢量紫
            CreateFrozenBrush("#FB7185"), // CH4 珊瑚红
            CreateFrozenBrush("#2DD4BF")  // CH5 极光青绿
        };

        private static readonly Pen[] ChannelPens =
        {
            CreateFrozenPen(ChannelBrushes[0], 1.75),
            CreateFrozenPen(ChannelBrushes[1], 1.75),
            CreateFrozenPen(ChannelBrushes[2], 1.65),
            CreateFrozenPen(ChannelBrushes[3], 1.65),
            CreateFrozenPen(ChannelBrushes[4], 1.6),
            CreateFrozenPen(ChannelBrushes[5], 1.6)
        };

        private static readonly Brush ScopeBgBrush = CreateFrozenBrush("#111722");
        private static readonly Pen ScopeBorderPen = CreateFrozenPen(CreateFrozenBrush("#2A384E"), 1.0);
        private static readonly Pen GridPen = CreateFrozenPen(CreateFrozenBrush("#1E293B"), 1.0);
        private static readonly Pen ZeroAxisPen = CreateDashedPen("#334155", 1.0);

        private readonly object _bufLock = new();
        private readonly List<double>[] _ring = new List<double>[MaxChannels];
        private int _activeChannelCount;
        private bool _isPaused;
        private long _lastRenderTickMs;

        private ImageSource? _plotImage;
        private string _yRangeSummary = "Y: 自动量程待命";
        private string _channelReadoutSummary = "等待上游通道信号接入...";

        private readonly NodeParameter _pauseParam;
        private readonly NodeParameter _clearBtnParam;

        public override double CardWidth => 362.0;
        public override bool HasVofaPlotPreview => true;

        public ImageSource? PlotImage
        {
            get => _plotImage;
            private set
            {
                _plotImage = value;
                OnPropertyChanged();
            }
        }

        public string YRangeSummary
        {
            get => _yRangeSummary;
            private set
            {
                if (_yRangeSummary != value)
                {
                    _yRangeSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ChannelReadoutSummary
        {
            get => _channelReadoutSummary;
            private set
            {
                if (_channelReadoutSummary != value)
                {
                    _channelReadoutSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        public VofaPlotNode(double x = 710, double y = 44)
            : base("VOFA+ 实时示波器", "多通道可视化", "#06B6D4", IconSvg, x, y)
        {
            for (int i = 0; i < MaxChannels; i++)
            {
                _ring[i] = new List<double>(Capacity + 2);
            }

            AddInputPin("波形通道 (CH)", PinDataType.ChannelStream);
            AddOutputPin("波形透传 (CH)", PinDataType.ChannelStream);

            _pauseParam = new NodeParameter("pause", "冻结画面", NodeParamKind.Toggle)
            {
                BoolChangedCallback = paused =>
                {
                    _isPaused = paused;
                    UpdateStatusOnUi(paused ? "已冻结" : "实时绘图中", !paused);
                }
            };

            _clearBtnParam = new NodeParameter("clear", "画布控制", NodeParamKind.ActionButton, "清空历史波形曲线")
            {
                ActionCommand = new RelayCommand(ClearBuffers)
            };

            Parameters.Add(_pauseParam);
            Parameters.Add(_clearBtnParam);

            UpdateStatusOnUi("示波待命", true);
            Application.Current?.Dispatcher.BeginInvoke(RenderEmptyGrid);
        }

        private void ClearBuffers()
        {
            lock (_bufLock)
            {
                for (int i = 0; i < MaxChannels; i++)
                {
                    _ring[i].Clear();
                }
                _activeChannelCount = 0;
            }
            YRangeSummary = "Y: 画布已清空";
            ChannelReadoutSummary = "等待新采样帧...";
            RenderEmptyGrid();
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            // 先执行零阻塞级联透传
            EmitPacket(0, packet);

            if (_isPaused || packet.Channels.Length == 0)
            {
                return Task.CompletedTask;
            }

            int chCount = Math.Min(packet.Channels.Length, MaxChannels);
            lock (_bufLock)
            {
                if (chCount > _activeChannelCount)
                {
                    _activeChannelCount = chCount;
                }

                for (int c = 0; c < chCount; c++)
                {
                    var list = _ring[c];
                    list.Add(packet.Channels[c]);
                    if (list.Count > Capacity)
                    {
                        list.RemoveAt(0);
                    }
                }
            }

            long nowMs = Environment.TickCount64;
            if (nowMs - Interlocked.Read(ref _lastRenderTickMs) >= 34)
            {
                Interlocked.Exchange(ref _lastRenderTickMs, nowMs);
                ScheduleRenderFrame();
            }

            return Task.CompletedTask;
        }

        private void ScheduleRenderFrame()
        {
            var app = Application.Current;
            if (app == null) return;

            double[][] snapshot;
            int chCount;
            lock (_bufLock)
            {
                chCount = _activeChannelCount;
                if (chCount <= 0) return;
                snapshot = new double[chCount][];
                for (int c = 0; c < chCount; c++)
                {
                    snapshot[c] = _ring[c].ToArray();
                }
            }

            app.Dispatcher.BeginInvoke(() => RenderSnapshotOnUi(snapshot));
        }

        private void RenderEmptyGrid()
        {
            const double w = 328.0;
            const double h = 152.0;

            var group = new DrawingGroup();
            using (var dc = group.Open())
            {
                DrawOscilloscopeGrid(dc, w, h, -1.0, 1.0);
            }
            group.Freeze();
            var img = new DrawingImage(group);
            img.Freeze();
            PlotImage = img;
        }

        private void RenderSnapshotOnUi(double[][] snapshot)
        {
            const double w = 328.0;
            const double h = 152.0;
            const double padX = 6.0;
            const double padY = 10.0;

            double minVal = double.MaxValue;
            double maxVal = double.MinValue;

            for (int c = 0; c < snapshot.Length; c++)
            {
                var arr = snapshot[c];
                for (int i = 0; i < arr.Length; i++)
                {
                    double v = arr[i];
                    if (v < minVal) minVal = v;
                    if (v > maxVal) maxVal = v;
                }
            }

            if (minVal > maxVal)
            {
                minVal = -1.0;
                maxVal = 1.0;
            }

            double span = maxVal - minVal;
            if (span < 1e-3)
            {
                minVal -= 1.0;
                maxVal += 1.0;
                span = 2.0;
            }
            else
            {
                double margin = span * 0.08;
                minVal -= margin;
                maxVal += margin;
                span = maxVal - minVal;
            }

            double plotW = w - padX * 2;
            double plotH = h - padY * 2;

            var group = new DrawingGroup();
            using (var dc = group.Open())
            {
                DrawOscilloscopeGrid(dc, w, h, minVal, maxVal);

                for (int c = 0; c < snapshot.Length; c++)
                {
                    var samples = snapshot[c];
                    if (samples.Length < 2) continue;

                    var geom = new StreamGeometry();
                    Point lastPt = default;
                    using (var gctx = geom.Open())
                    {
                        for (int i = 0; i < samples.Length; i++)
                        {
                            double xRatio = i / (double)(Capacity - 1);
                            // 靠右对齐推进示波器时间轴
                            int offset = Capacity - samples.Length;
                            xRatio = (i + offset) / (double)(Capacity - 1);

                            double x = padX + xRatio * plotW;
                            double yRatio = (samples[i] - minVal) / span;
                            double y = padY + (1.0 - Math.Clamp(yRatio, 0.0, 1.0)) * plotH;
                            var pt = new Point(x, y);

                            if (i == 0)
                            {
                                gctx.BeginFigure(pt, isFilled: false, isClosed: false);
                            }
                            else
                            {
                                gctx.LineTo(pt, isStroked: true, isSmoothJoin: true);
                            }
                            lastPt = pt;
                        }
                    }
                    geom.Freeze();
                    dc.DrawGeometry(null, ChannelPens[c], geom);
                    dc.DrawEllipse(ChannelBrushes[c], null, lastPt, 2.7, 2.7);
                }
            }

            group.Freeze();
            var drawingImg = new DrawingImage(group);
            drawingImg.Freeze();
            PlotImage = drawingImg;

            YRangeSummary = string.Create(CultureInfo.InvariantCulture, $"Y: [{minVal:F2}, {maxVal:F2}] · {snapshot.Length} CH");

            var sb = new StringBuilder(96);
            int displayCount = Math.Min(snapshot.Length, 4);
            for (int c = 0; c < displayCount; c++)
            {
                var arr = snapshot[c];
                if (arr.Length == 0) continue;
                if (sb.Length > 0) sb.Append("   ");
                sb.Append("CH").Append(c).Append(':').Append(arr[^1].ToString("F2", CultureInfo.InvariantCulture));
            }
            if (snapshot.Length > 4)
            {
                sb.Append("  +").Append(snapshot.Length - 4).Append("CH");
            }

            ChannelReadoutSummary = sb.ToString();
            if (!IsActive && !_isPaused)
            {
                StatusBadge = "实时绘图中";
                IsActive = true;
            }
        }

        private static void DrawOscilloscopeGrid(DrawingContext dc, double w, double h, double minVal, double maxVal)
        {
            dc.DrawRoundedRectangle(ScopeBgBrush, ScopeBorderPen, new Rect(0, 0, w, h), 6, 6);

            for (int i = 1; i <= 3; i++)
            {
                double y = h * (i / 4.0);
                dc.DrawLine(GridPen, new Point(1, y), new Point(w - 1, y));
            }

            for (int i = 1; i <= 5; i++)
            {
                double x = w * (i / 6.0);
                dc.DrawLine(GridPen, new Point(x, 1), new Point(x, h - 1));
            }

            if (minVal < 0 && maxVal > 0)
            {
                double zeroRatio = (0.0 - minVal) / (maxVal - minVal);
                double zeroY = 10.0 + (1.0 - zeroRatio) * (h - 20.0);
                dc.DrawLine(ZeroAxisPen, new Point(2, zeroY), new Point(w - 2, zeroY));
            }
        }

        private static Brush CreateFrozenBrush(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private static Pen CreateFrozenPen(Brush brush, double thickness)
        {
            var p = new Pen(brush, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            p.Freeze();
            return p;
        }

        private static Pen CreateDashedPen(string hex, double thickness)
        {
            var p = new Pen(CreateFrozenBrush(hex), thickness)
            {
                DashStyle = new DashStyle(new double[] { 4, 4 }, 0)
            };
            p.Freeze();
            return p;
        }
    }
}
