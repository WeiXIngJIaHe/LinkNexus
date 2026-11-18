using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LinkNexus.Blueprint.ViewModels;

namespace LinkNexus.Blueprint.Controls
{
    /// <summary>
    /// 三阶贝塞尔曲线交互连线画布 (BezierWireCanvas)
    /// 1. 4200×2800 工程点阵底图采用静态冻结平铺 DrawingBrush (96×96 Tile)，单次 GPU 矩形绘制完成，彻底消除每帧数百次 DrawLine 开销；
    /// 2. 已建立连线复用 ConnectionViewModel 冻结 Pen 与缓存 StreamGeometry，静态连线零 GC 零重算；
    /// 3. 支持实时拖拽预览曲线、磁吸光环及右键点击曲线快速解绑。
    /// </summary>
    public sealed class BezierWireCanvas : FrameworkElement
    {
        public static readonly DependencyProperty ConnectionsProperty =
            DependencyProperty.Register(
                nameof(Connections),
                typeof(ObservableCollection<ConnectionViewModel>),
                typeof(BezierWireCanvas),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnConnectionsChanged));

        public static readonly DependencyProperty ShowBackgroundGridProperty =
            DependencyProperty.Register(
                nameof(ShowBackgroundGrid),
                typeof(bool),
                typeof(BezierWireCanvas),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty RenderEstablishedConnectionsProperty =
            DependencyProperty.Register(
                nameof(RenderEstablishedConnections),
                typeof(bool),
                typeof(BezierWireCanvas),
                new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IsDraggingPreviewProperty =
            DependencyProperty.Register(
                nameof(IsDraggingPreview),
                typeof(bool),
                typeof(BezierWireCanvas),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PreviewStartProperty =
            DependencyProperty.Register(
                nameof(PreviewStart),
                typeof(Point),
                typeof(BezierWireCanvas),
                new FrameworkPropertyMetadata(default(Point), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PreviewEndProperty =
            DependencyProperty.Register(
                nameof(PreviewEnd),
                typeof(Point),
                typeof(BezierWireCanvas),
                new FrameworkPropertyMetadata(default(Point), FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty PreviewBrushProperty =
            DependencyProperty.Register(
                nameof(PreviewBrush),
                typeof(Brush),
                typeof(BezierWireCanvas),
                new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty IsPreviewSnappedProperty =
            DependencyProperty.Register(
                nameof(IsPreviewSnapped),
                typeof(bool),
                typeof(BezierWireCanvas),
                new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

        private static readonly Brush DarkTiledGridBrush = CreateFrozenGridTileBrush(
            bgHex: "#161C27",
            minorHex: "#1F2A3C",
            majorHex: "#29374E");

        private static readonly Brush LightTiledGridBrush = CreateFrozenGridTileBrush(
            bgHex: "#F1F5F9",
            minorHex: "#E2E8F0",
            majorHex: "#CBD5E1");

        private static readonly DashStyle PreviewDashStyle = CreateFrozenDashStyle();

        // 预览画笔复用缓存
        private Brush? _cachedPreviewBrushSource;
        private Pen? _cachedPreviewDashedPen;
        private Pen? _cachedPreviewSnappedPen;
        private Pen? _cachedSnapRingPen;

        public ObservableCollection<ConnectionViewModel>? Connections
        {
            get => (ObservableCollection<ConnectionViewModel>?)GetValue(ConnectionsProperty);
            set => SetValue(ConnectionsProperty, value);
        }

        public bool ShowBackgroundGrid
        {
            get => (bool)GetValue(ShowBackgroundGridProperty);
            set => SetValue(ShowBackgroundGridProperty, value);
        }

        public bool RenderEstablishedConnections
        {
            get => (bool)GetValue(RenderEstablishedConnectionsProperty);
            set => SetValue(RenderEstablishedConnectionsProperty, value);
        }

        public bool IsDraggingPreview
        {
            get => (bool)GetValue(IsDraggingPreviewProperty);
            set => SetValue(IsDraggingPreviewProperty, value);
        }

        public Point PreviewStart
        {
            get => (Point)GetValue(PreviewStartProperty);
            set => SetValue(PreviewStartProperty, value);
        }

        public Point PreviewEnd
        {
            get => (Point)GetValue(PreviewEndProperty);
            set => SetValue(PreviewEndProperty, value);
        }

        public Brush PreviewBrush
        {
            get => (Brush)GetValue(PreviewBrushProperty);
            set => SetValue(PreviewBrushProperty, value);
        }

        public bool IsPreviewSnapped
        {
            get => (bool)GetValue(IsPreviewSnappedProperty);
            set => SetValue(IsPreviewSnappedProperty, value);
        }

        public event Action<ConnectionViewModel>? WireRightClicked;

        public BezierWireCanvas()
        {
            ClipToBounds = true;
            ThemeManager.ThemeChanged += OnThemeChanged;
            Unloaded += (_, _) => ThemeManager.ThemeChanged -= OnThemeChanged;
        }

        private void OnThemeChanged(bool _)
        {
            Dispatcher.BeginInvoke(InvalidateVisual);
        }

        private static void OnConnectionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not BezierWireCanvas canvas) return;

            if (e.OldValue is ObservableCollection<ConnectionViewModel> oldCol)
            {
                oldCol.CollectionChanged -= canvas.OnConnectionsCollectionChanged;
                foreach (var conn in oldCol)
                {
                    conn.GeometryChanged -= canvas.OnConnectionGeometryChanged;
                }
            }

            if (e.NewValue is ObservableCollection<ConnectionViewModel> newCol)
            {
                newCol.CollectionChanged += canvas.OnConnectionsCollectionChanged;
                foreach (var conn in newCol)
                {
                    conn.GeometryChanged += canvas.OnConnectionGeometryChanged;
                }
            }

            canvas.InvalidateVisual();
        }

        private void OnConnectionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.OldItems != null)
            {
                foreach (ConnectionViewModel oldItem in e.OldItems)
                {
                    oldItem.GeometryChanged -= OnConnectionGeometryChanged;
                }
            }

            if (e.NewItems != null)
            {
                foreach (ConnectionViewModel newItem in e.NewItems)
                {
                    newItem.GeometryChanged += OnConnectionGeometryChanged;
                }
            }

            InvalidateVisual();
        }

        private void OnConnectionGeometryChanged(ConnectionViewModel _)
        {
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);

            double w = ActualWidth;
            double h = ActualHeight;
            if (w <= 1 || h <= 1) return;

            // 1. 工程点阵底图：使用预冻结的 96×96 Tile DrawingBrush 单次硬件填充
            if (ShowBackgroundGrid)
            {
                bool dark = ThemeManager.IsDarkMode;
                dc.DrawRectangle(dark ? DarkTiledGridBrush : LightTiledGridBrush, null, new Rect(0, 0, w, h));
            }

            // 2. 已建立的三阶贝塞尔数据流连线：直接复用冻结画笔与缓存 StreamGeometry
            if (RenderEstablishedConnections && Connections != null)
            {
                int count = Connections.Count;
                for (int i = 0; i < count; i++)
                {
                    var conn = Connections[i];
                    Point p0 = conn.StartPoint;
                    Point p3 = conn.EndPoint;
                    if (p0.X <= 0.1 && p0.Y <= 0.1 && p3.X <= 0.1 && p3.Y <= 0.1) continue;

                    var geom = conn.GetOrBuildGeometry(out Point mid);
                    bool hovered = conn.IsHovered;

                    Pen haloPen = hovered ? conn.HoveredHaloPen : conn.NormalHaloPen;
                    Pen corePen = hovered ? conn.HoveredCorePen : conn.NormalCorePen;
                    Brush coreBrush = hovered ? conn.HoverCoreBrush : conn.WireBrush;

                    dc.DrawGeometry(null, haloPen, geom);
                    dc.DrawGeometry(null, corePen, geom);

                    double radius = hovered ? 4.2 : 3.0;
                    dc.DrawEllipse(coreBrush, null, mid, radius, radius);
                }
            }

            // 3. 实时拖拽预览曲线
            if (IsDraggingPreview)
            {
                Point p0 = PreviewStart;
                Point p3 = PreviewEnd;
                GetControlPoints(p0, p3, out Point c1, out Point c2);
                var geom = BuildBezierGeometry(p0, c1, c2, p3);

                Brush brush = PreviewBrush ?? Brushes.DeepSkyBlue;
                EnsurePreviewPens(brush);

                Pen previewPen = IsPreviewSnapped ? _cachedPreviewSnappedPen! : _cachedPreviewDashedPen!;
                dc.DrawGeometry(null, previewPen, geom);

                if (IsPreviewSnapped)
                {
                    dc.DrawEllipse(null, _cachedSnapRingPen, p3, 9.5, 9.5);
                    dc.DrawEllipse(brush, null, p3, 4.0, 4.0);
                }
                else
                {
                    dc.DrawEllipse(brush, null, p3, 3.5, 3.5);
                }
            }
        }

        private void EnsurePreviewPens(Brush brush)
        {
            if (ReferenceEquals(_cachedPreviewBrushSource, brush) &&
                _cachedPreviewDashedPen != null &&
                _cachedPreviewSnappedPen != null &&
                _cachedSnapRingPen != null)
            {
                return;
            }

            _cachedPreviewBrushSource = brush;

            var dashed = new Pen(brush, 2.2)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                DashStyle = PreviewDashStyle
            };
            dashed.Freeze();
            _cachedPreviewDashedPen = dashed;

            var snapped = new Pen(brush, 2.8)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            snapped.Freeze();
            _cachedPreviewSnappedPen = snapped;

            var ring = new Pen(brush, 2.0);
            ring.Freeze();
            _cachedSnapRingPen = ring;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!RenderEstablishedConnections || Connections == null || Connections.Count == 0) return;
            if (e.LeftButton == MouseButtonState.Pressed) return;

            Point pos = e.GetPosition(this);
            ConnectionViewModel? hit = HitTestConnection(pos, 10.0);

            for (int i = 0; i < Connections.Count; i++)
            {
                var conn = Connections[i];
                conn.IsHovered = ReferenceEquals(conn, hit);
            }

            Cursor = hit != null ? Cursors.Hand : Cursors.Arrow;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (Connections == null) return;
            for (int i = 0; i < Connections.Count; i++)
            {
                Connections[i].IsHovered = false;
            }
            Cursor = Cursors.Arrow;
        }

        protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseRightButtonDown(e);
            if (!RenderEstablishedConnections || Connections == null || Connections.Count == 0) return;

            Point pos = e.GetPosition(this);
            ConnectionViewModel? hit = HitTestConnection(pos, 12.0);
            if (hit != null)
            {
                WireRightClicked?.Invoke(hit);
                e.Handled = true;
            }
        }

        private ConnectionViewModel? HitTestConnection(Point pt, double tolerance)
        {
            if (Connections == null) return null;
            double bestDistSq = tolerance * tolerance;
            ConnectionViewModel? best = null;

            for (int i = Connections.Count - 1; i >= 0; i--)
            {
                var conn = Connections[i];
                Point p0 = conn.StartPoint;
                Point p3 = conn.EndPoint;
                GetControlPoints(p0, p3, out Point c1, out Point c2);

                const int samples = 24;
                Point prev = p0;
                for (int s = 1; s <= samples; s++)
                {
                    double t = s / (double)samples;
                    Point curr = EvaluateCubicBezierPoint(p0, c1, c2, p3, t);
                    double distSq = DistanceToSegmentSquared(pt, prev, curr);
                    if (distSq <= bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = conn;
                    }
                    prev = curr;
                }
            }

            return best;
        }

        public static void GetControlPoints(Point p0, Point p3, out Point c1, out Point c2)
        {
            double dx = p3.X - p0.X;
            double tangent = dx >= 0
                ? Math.Clamp(dx * 0.52, 58.0, 260.0)
                : Math.Clamp(Math.Abs(dx) * 0.45 + 75.0, 85.0, 280.0);

            c1 = new Point(p0.X + tangent, p0.Y);
            c2 = new Point(p3.X - tangent, p3.Y);
        }

        private static StreamGeometry BuildBezierGeometry(Point p0, Point c1, Point c2, Point p3)
        {
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(p0, isFilled: false, isClosed: false);
                ctx.BezierTo(c1, c2, p3, isStroked: true, isSmoothJoin: true);
            }
            geom.Freeze();
            return geom;
        }

        public static Point EvaluateCubicBezierPoint(Point p0, Point c1, Point c2, Point p3, double t)
        {
            double u = 1.0 - t;
            double tt = t * t;
            double uu = u * u;
            double uuu = uu * u;
            double ttt = tt * t;

            double x = uuu * p0.X + 3 * uu * t * c1.X + 3 * u * tt * c2.X + ttt * p3.X;
            double y = uuu * p0.Y + 3 * uu * t * c1.Y + 3 * u * tt * c2.Y + ttt * p3.Y;
            return new Point(x, y);
        }

        private static double DistanceToSegmentSquared(Point p, Point a, Point b)
        {
            double vx = b.X - a.X;
            double vy = b.Y - a.Y;
            double wx = p.X - a.X;
            double wy = p.Y - a.Y;

            double lenSq = vx * vx + vy * vy;
            if (lenSq <= 1e-6)
            {
                return wx * wx + wy * wy;
            }

            double t = Math.Clamp((wx * vx + wy * vy) / lenSq, 0.0, 1.0);
            double projX = a.X + t * vx;
            double projY = a.Y + t * vy;
            double dx = p.X - projX;
            double dy = p.Y - projY;
            return dx * dx + dy * dy;
        }

        /// <summary>
        /// 构建静态冻结的 96×96 (4×24px) 工程点阵平铺画刷，由 GPU 纹理单元直接硬件平铺
        /// </summary>
        private static Brush CreateFrozenGridTileBrush(string bgHex, string minorHex, string majorHex)
        {
            const double step = 24.0;
            const double tileSize = step * 4.0; // 96.0

            var bgBrush = CreateFrozenBrush(bgHex);
            var minorPen = CreateFrozenPen(minorHex, 1.0);
            var majorPen = CreateFrozenPen(majorHex, 1.0);

            var group = new DrawingGroup();
            using (DrawingContext dc = group.Open())
            {
                dc.DrawRectangle(bgBrush, null, new Rect(0, 0, tileSize, tileSize));

                // 次网格线 (24, 48, 72)
                for (int i = 1; i < 4; i++)
                {
                    double pos = i * step;
                    dc.DrawLine(minorPen, new Point(pos, 0), new Point(pos, tileSize));
                    dc.DrawLine(minorPen, new Point(0, pos), new Point(tileSize, pos));
                }

                // 主网格线 (0)
                dc.DrawLine(majorPen, new Point(0, 0), new Point(0, tileSize));
                dc.DrawLine(majorPen, new Point(0, 0), new Point(tileSize, 0));
            }
            group.Freeze();

            var drawingBrush = new DrawingBrush(group)
            {
                TileMode = TileMode.Tile,
                Viewport = new Rect(0, 0, tileSize, tileSize),
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.None
            };
            drawingBrush.Freeze();
            return drawingBrush;
        }

        private static DashStyle CreateFrozenDashStyle()
        {
            var ds = new DashStyle(new double[] { 4, 3 }, 0);
            ds.Freeze();
            return ds;
        }

        private static Brush CreateFrozenBrush(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        private static Pen CreateFrozenPen(string hex, double thickness)
        {
            var p = new Pen(CreateFrozenBrush(hex), thickness);
            p.Freeze();
            return p;
        }
    }
}
