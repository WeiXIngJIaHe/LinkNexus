using System;
using System.Collections.Generic;
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
    /// 负责渲染工业工程点阵底图、所有已绑定的 Cubic Bezier 数据流连线、实时拖拽预览曲线及磁吸光环，并支持右键点击曲线快速解绑
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

        private static readonly Pen DarkMinorGridPen = CreateFrozenPen("#1F2A3C", 1.0);
        private static readonly Pen DarkMajorGridPen = CreateFrozenPen("#29374E", 1.0);
        private static readonly Brush DarkBgBrush = CreateFrozenBrush("#161C27");

        private static readonly Pen LightMinorGridPen = CreateFrozenPen("#E2E8F0", 1.0);
        private static readonly Pen LightMajorGridPen = CreateFrozenPen("#CBD5E1", 1.0);
        private static readonly Brush LightBgBrush = CreateFrozenBrush("#F1F5F9");

        private static readonly Brush HoverDeleteHighlightBrush = CreateFrozenBrush("#F87171");

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

            if (ShowBackgroundGrid)
            {
                bool dark = ThemeManager.IsDarkMode;
                dc.DrawRectangle(dark ? DarkBgBrush : LightBgBrush, null, new Rect(0, 0, w, h));

                var minorPen = dark ? DarkMinorGridPen : LightMinorGridPen;
                var majorPen = dark ? DarkMajorGridPen : LightMajorGridPen;

                const double step = 24.0;
                int indexX = 0;
                for (double x = 0; x < w; x += step, indexX++)
                {
                    if (indexX % 4 != 0)
                    {
                        dc.DrawLine(minorPen, new Point(x, 0), new Point(x, h));
                    }
                }

                int indexY = 0;
                for (double y = 0; y < h; y += step, indexY++)
                {
                    if (indexY % 4 != 0)
                    {
                        dc.DrawLine(minorPen, new Point(0, y), new Point(w, y));
                    }
                }

                for (double x = 0; x < w; x += step * 4)
                {
                    dc.DrawLine(majorPen, new Point(x, 0), new Point(x, h));
                }

                for (double y = 0; y < h; y += step * 4)
                {
                    dc.DrawLine(majorPen, new Point(0, y), new Point(w, y));
                }
            }

            if (RenderEstablishedConnections && Connections != null)
            {
                foreach (var conn in Connections)
                {
                    Point p0 = conn.StartPoint;
                    Point p3 = conn.EndPoint;
                    if (p0.X <= 0.1 && p0.Y <= 0.1 && p3.X <= 0.1 && p3.Y <= 0.1) continue;

                    GetControlPoints(p0, p3, out Point c1, out Point c2);
                    var geom = BuildBezierGeometry(p0, c1, c2, p3);

                    double haloThickness = conn.IsHovered ? 8.0 : 5.5;
                    double coreThickness = conn.IsHovered ? 3.2 : 2.3;
                    Brush coreBrush = conn.IsHovered ? HoverDeleteHighlightBrush : conn.WireBrush;

                    var haloPen = new Pen(conn.GlowBrush, haloThickness)
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round
                    };
                    var corePen = new Pen(coreBrush, coreThickness)
                    {
                        StartLineCap = PenLineCap.Round,
                        EndLineCap = PenLineCap.Round
                    };

                    dc.DrawGeometry(null, haloPen, geom);
                    dc.DrawGeometry(null, corePen, geom);

                    // 中点流向节点微标
                    Point mid = EvaluateCubicBezier(p0, c1, c2, p3, 0.5);
                    dc.DrawEllipse(coreBrush, null, mid, conn.IsHovered ? 4.2 : 3.0, conn.IsHovered ? 4.2 : 3.0);
                }
            }

            if (IsDraggingPreview)
            {
                Point p0 = PreviewStart;
                Point p3 = PreviewEnd;
                GetControlPoints(p0, p3, out Point c1, out Point c2);
                var geom = BuildBezierGeometry(p0, c1, c2, p3);

                Brush brush = PreviewBrush ?? Brushes.DeepSkyBlue;
                var previewPen = new Pen(brush, IsPreviewSnapped ? 2.8 : 2.2)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };

                if (!IsPreviewSnapped)
                {
                    previewPen.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
                }

                dc.DrawGeometry(null, previewPen, geom);

                if (IsPreviewSnapped)
                {
                    var snapRingPen = new Pen(brush, 2.0);
                    dc.DrawEllipse(null, snapRingPen, p3, 9.5, 9.5);
                    dc.DrawEllipse(brush, null, p3, 4.0, 4.0);
                }
                else
                {
                    dc.DrawEllipse(brush, null, p3, 3.5, 3.5);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!RenderEstablishedConnections || Connections == null || Connections.Count == 0) return;

            Point pos = e.GetPosition(this);
            ConnectionViewModel? hit = HitTestConnection(pos, 10.0);

            foreach (var conn in Connections)
            {
                conn.IsHovered = ReferenceEquals(conn, hit);
            }

            Cursor = hit != null ? Cursors.Hand : Cursors.Arrow;
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            if (Connections == null) return;
            foreach (var conn in Connections)
            {
                conn.IsHovered = false;
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

                const int samples = 28;
                Point prev = p0;
                for (int s = 1; s <= samples; s++)
                {
                    double t = s / (double)samples;
                    Point curr = EvaluateCubicBezier(p0, c1, c2, p3, t);
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

        private static Point EvaluateCubicBezier(Point p0, Point c1, Point c2, Point p3, double t)
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
