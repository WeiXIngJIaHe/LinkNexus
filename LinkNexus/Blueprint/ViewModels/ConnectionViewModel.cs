using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using LinkNexus.Blueprint.Controls;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.ViewModels
{
    /// <summary>
    /// 节点端口连线视图模型 (ConnectionViewModel)
    /// 管理一条三阶贝塞尔曲线连线的两端坐标同步、冻结画笔与几何缓存，以及底层 NodeBase 之间的 System.Threading.Channels 管道绑定与解绑
    /// </summary>
    public sealed class ConnectionViewModel : INotifyPropertyChanged, IDisposable
    {
        private static readonly Brush HoverDeleteHighlightBrush = CreateFrozenHighlightBrush();
        private static readonly Pen StaticHoveredCorePen = CreateFrozenPen(HoverDeleteHighlightBrush, 3.2);

        private bool _isDisposed;
        private bool _isHovered;

        // 几何缓存：仅在两端锚点坐标变化时重算，避免静态连线每帧重复构建 StreamGeometry
        private Point _cachedP0 = new(double.NaN, double.NaN);
        private Point _cachedP3 = new(double.NaN, double.NaN);
        private StreamGeometry? _cachedGeometry;
        private Point _cachedMidPoint;

        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public PinViewModel SourcePin { get; }
        public PinViewModel TargetPin { get; }
        public Brush WireBrush { get; }
        public Brush GlowBrush { get; }

        public Pen NormalHaloPen { get; }
        public Pen HoveredHaloPen { get; }
        public Pen NormalCorePen { get; }
        public Pen HoveredCorePen => StaticHoveredCorePen;
        public Brush HoverCoreBrush => HoverDeleteHighlightBrush;

        public Point StartPoint => SourcePin.CanvasAnchor;
        public Point EndPoint => TargetPin.CanvasAnchor;

        public bool IsHovered
        {
            get => _isHovered;
            set
            {
                if (_isHovered != value)
                {
                    _isHovered = value;
                    OnPropertyChanged();
                    GeometryChanged?.Invoke(this);
                }
            }
        }

        public event Action<ConnectionViewModel>? GeometryChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        public ConnectionViewModel(PinViewModel firstPin, PinViewModel secondPin)
        {
            if (firstPin == null) throw new ArgumentNullException(nameof(firstPin));
            if (secondPin == null) throw new ArgumentNullException(nameof(secondPin));

            if (firstPin.Direction == PinDirection.Output && secondPin.Direction == PinDirection.Input)
            {
                SourcePin = firstPin;
                TargetPin = secondPin;
            }
            else if (firstPin.Direction == PinDirection.Input && secondPin.Direction == PinDirection.Output)
            {
                SourcePin = secondPin;
                TargetPin = firstPin;
            }
            else
            {
                throw new InvalidOperationException("连线两端必须分别为 OutputPin 与 InputPin。");
            }

            var startColor = (Color)ColorConverter.ConvertFromString(SourcePin.ColorHex);
            var endColor = (Color)ColorConverter.ConvertFromString(TargetPin.ColorHex);

            var gradient = new LinearGradientBrush(startColor, endColor, new Point(0, 0.5), new Point(1, 0.5));
            gradient.Freeze();
            WireBrush = gradient;

            var glowColor = Color.FromArgb(68, startColor.R, startColor.G, startColor.B);
            var glow = new SolidColorBrush(glowColor);
            glow.Freeze();
            GlowBrush = glow;

            NormalHaloPen = CreateFrozenPen(GlowBrush, 5.5);
            HoveredHaloPen = CreateFrozenPen(GlowBrush, 8.0);
            NormalCorePen = CreateFrozenPen(WireBrush, 2.3);

            // 建立底层 System.Threading.Channels 数据路由绑定
            SourcePin.OwnerNode.BindOutputChannel(SourcePin.PinIndex, TargetPin.OwnerNode, TargetPin.PinIndex);
            SourcePin.IsConnected = true;
            TargetPin.IsConnected = true;

            SourcePin.PropertyChanged += OnPinPropertyChanged;
            TargetPin.PropertyChanged += OnPinPropertyChanged;
        }

        /// <summary>
        /// 获取或按需构建冻结的三阶贝塞尔曲线几何与中点坐标
        /// </summary>
        public StreamGeometry GetOrBuildGeometry(out Point midPoint)
        {
            Point p0 = StartPoint;
            Point p3 = EndPoint;

            if (_cachedGeometry != null && p0 == _cachedP0 && p3 == _cachedP3)
            {
                midPoint = _cachedMidPoint;
                return _cachedGeometry;
            }

            _cachedP0 = p0;
            _cachedP3 = p3;

            BezierWireCanvas.GetControlPoints(p0, p3, out Point c1, out Point c2);

            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(p0, isFilled: false, isClosed: false);
                ctx.BezierTo(c1, c2, p3, isStroked: true, isSmoothJoin: true);
            }
            geom.Freeze();

            _cachedGeometry = geom;
            _cachedMidPoint = BezierWireCanvas.EvaluateCubicBezierPoint(p0, c1, c2, p3, 0.5);
            midPoint = _cachedMidPoint;
            return geom;
        }

        private void OnPinPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PinViewModel.CanvasAnchor))
            {
                _cachedGeometry = null;
                OnPropertyChanged(nameof(StartPoint));
                OnPropertyChanged(nameof(EndPoint));
                GeometryChanged?.Invoke(this);
            }
        }

        /// <summary>
        /// 解除底层 Channel 管道绑定并释放事件订阅
        /// </summary>
        public void Disconnect()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            SourcePin.PropertyChanged -= OnPinPropertyChanged;
            TargetPin.PropertyChanged -= OnPinPropertyChanged;

            SourcePin.OwnerNode.UnbindOutputChannel(SourcePin.PinIndex, TargetPin.OwnerNode, TargetPin.PinIndex);
        }

        public void Dispose() => Disconnect();

        private static Brush CreateFrozenHighlightBrush()
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F87171"));
            b.Freeze();
            return b;
        }

        private static Pen CreateFrozenPen(Brush brush, double thickness)
        {
            var pen = new Pen(brush, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
            return pen;
        }

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
