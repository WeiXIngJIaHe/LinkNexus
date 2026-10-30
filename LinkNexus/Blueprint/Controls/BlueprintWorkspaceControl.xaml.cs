using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LinkNexus.Blueprint.Core;
using LinkNexus.Blueprint.ViewModels;

namespace LinkNexus.Blueprint.Controls
{
    /// <summary>
    /// 流式卡片蓝图交互主画布控件 (BlueprintWorkspaceControl)
    /// 协调 NodeCardControl 节点拖拽、鼠标滚轮以光标为中心自由缩放 (35% ~ 240%)、三阶贝塞尔曲线磁吸建链与空白区画布平移
    /// </summary>
    public partial class BlueprintWorkspaceControl : UserControl
    {
        private const double MagneticSnapRadius = 26.0;
        private const double MinZoom = 0.35;
        private const double MaxZoom = 2.40;
        private const double ZoomStepFactor = 1.12;

        private readonly HashSet<NodeCardControl> _cards = new();
        private BlueprintWorkspaceViewModel? _boundVm;
        private int _topZIndex = 10;
        private double _currentZoom = 1.0;

        // 交互拉线状态
        private PinViewModel? _dragOriginPin;
        private PinViewModel? _snappedTargetPin;

        // 空白画布拖拽平移状态
        private bool _isPanningStage;
        private Point _panStartViewportPoint;
        private double _panStartHOffset;
        private double _panStartVOffset;

        public Visual CoordinateStage => UnderlayWireCanvas;
        public BlueprintWorkspaceViewModel? ViewModel => DataContext as BlueprintWorkspaceViewModel;

        public BlueprintWorkspaceControl()
        {
            InitializeComponent();

            UnderlayWireCanvas.WireRightClicked += conn =>
            {
                ViewModel?.RemoveConnection(conn);
                UnderlayWireCanvas.InvalidateVisual();
            };

            DataContextChanged += OnDataContextChanged;
            Loaded += (_, _) => ScheduleFullAnchorSync();
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible) ScheduleFullAnchorSync();
            };
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_boundVm != null)
            {
                _boundVm.GraphRebuilt -= ScheduleFullAnchorSync;
            }

            _boundVm = e.NewValue as BlueprintWorkspaceViewModel;
            if (_boundVm != null)
            {
                _boundVm.GraphRebuilt += ScheduleFullAnchorSync;
                _boundVm.CanvasZoom = _currentZoom;
            }

            ScheduleFullAnchorSync();
        }

        private void StageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.OriginalSource is DependencyObject src)
            {
                var openCombo = VisualTreeSafeHelper.FindAncestor<ComboBox>(src);
                if (openCombo != null && openCombo.IsDropDownOpen)
                {
                    return;
                }

                var textBox = VisualTreeSafeHelper.FindAncestor<TextBox>(src);
                if (textBox != null && textBox.AcceptsReturn && textBox.IsKeyboardFocusWithin)
                {
                    return;
                }
            }

            Point mouseInViewport = e.GetPosition(StageScrollViewer);
            Point mouseOnStage = e.GetPosition(BlueprintStageGrid);

            double targetZoom = e.Delta > 0
                ? Math.Min(MaxZoom, _currentZoom * ZoomStepFactor)
                : Math.Max(MinZoom, _currentZoom / ZoomStepFactor);

            ApplyZoomAtAnchor(targetZoom, mouseOnStage, mouseInViewport);
            e.Handled = true;
        }

        private void ZoomInBtn_Click(object sender, RoutedEventArgs e)
        {
            ZoomAroundViewportCenter(Math.Min(MaxZoom, _currentZoom * 1.18));
        }

        private void ZoomOutBtn_Click(object sender, RoutedEventArgs e)
        {
            ZoomAroundViewportCenter(Math.Max(MinZoom, _currentZoom / 1.18));
        }

        private void ZoomResetBtn_Click(object sender, RoutedEventArgs e)
        {
            ZoomAroundViewportCenter(1.0);
        }

        private void ZoomAroundViewportCenter(double targetZoom)
        {
            Point centerInViewport = new(StageScrollViewer.ViewportWidth * 0.5, StageScrollViewer.ViewportHeight * 0.5);
            Point centerOnStage = new(
                (StageScrollViewer.HorizontalOffset + centerInViewport.X) / _currentZoom,
                (StageScrollViewer.VerticalOffset + centerInViewport.Y) / _currentZoom);
            ApplyZoomAtAnchor(targetZoom, centerOnStage, centerInViewport);
        }

        private void ApplyZoomAtAnchor(double newZoom, Point stagePoint, Point viewportPoint)
        {
            double clamped = Math.Clamp(newZoom, MinZoom, MaxZoom);
            if (Math.Abs(clamped - _currentZoom) < 0.001) return;

            _currentZoom = clamped;
            StageScaleTransform.ScaleX = clamped;
            StageScaleTransform.ScaleY = clamped;
            StageScrollViewer.UpdateLayout();

            double newHOffset = Math.Max(0.0, stagePoint.X * clamped - viewportPoint.X);
            double newVOffset = Math.Max(0.0, stagePoint.Y * clamped - viewportPoint.Y);
            StageScrollViewer.ScrollToHorizontalOffset(newHOffset);
            StageScrollViewer.ScrollToVerticalOffset(newVOffset);

            string pctText = $"{Math.Round(clamped * 100)}%";
            if (ZoomResetBtn != null)
            {
                ZoomResetBtn.Content = pctText;
            }
            if (ViewModel != null)
            {
                ViewModel.CanvasZoom = clamped;
            }

            ScheduleFullAnchorSync();
        }

        public void RegisterNodeCard(NodeCardControl card)
        {
            _cards.Add(card);
            ScheduleFullAnchorSync();
        }

        public void UnregisterNodeCard(NodeCardControl card)
        {
            _cards.Remove(card);
        }

        public void ScheduleFullAnchorSync()
        {
            Dispatcher.BeginInvoke(() =>
            {
                UpdateLayout();
                foreach (var card in _cards)
                {
                    card.SyncPinAnchorsTo(UnderlayWireCanvas);
                }
                UnderlayWireCanvas.InvalidateVisual();
            }, DispatcherPriority.Loaded);
        }

        public void UpdateAnchorsForCard(NodeCardControl card)
        {
            card.SyncPinAnchorsTo(UnderlayWireCanvas);
            UnderlayWireCanvas.InvalidateVisual();
        }

        public void BringCardToFront(NodeCardControl card)
        {
            if (card.Node != null && NodesItemsControl != null)
            {
                if (NodesItemsControl.ItemContainerGenerator.ContainerFromItem(card.Node) is UIElement container)
                {
                    Panel.SetZIndex(container, ++_topZIndex);
                }
            }
        }

        public Point GetPointerInStage(MouseEventArgs e)
        {
            return e.GetPosition(BlueprintStageGrid);
        }

        public void RequestRemoveNode(NodeBase node)
        {
            ViewModel?.RemoveNode(node);
            ScheduleFullAnchorSync();
        }

        public void RequestDisconnectPin(PinViewModel pin)
        {
            ViewModel?.DisconnectPin(pin);
            UnderlayWireCanvas.InvalidateVisual();
        }

        public void BeginWireDrag(PinViewModel originPin, MouseButtonEventArgs e)
        {
            SyncAllCardAnchorsImmediate();

            _dragOriginPin = originPin;
            _snappedTargetPin = null;

            Point mousePt = e.GetPosition(UnderlayWireCanvas);
            OverlayPreviewCanvas.PreviewBrush = originPin.PinBrush;
            OverlayPreviewCanvas.IsPreviewSnapped = false;

            if (originPin.Direction == PinDirection.Output)
            {
                OverlayPreviewCanvas.PreviewStart = originPin.CanvasAnchor;
                OverlayPreviewCanvas.PreviewEnd = mousePt;
            }
            else
            {
                OverlayPreviewCanvas.PreviewStart = mousePt;
                OverlayPreviewCanvas.PreviewEnd = originPin.CanvasAnchor;
            }

            OverlayPreviewCanvas.IsDraggingPreview = true;
            BlueprintStageGrid.CaptureMouse();
        }

        private void Stage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, UnderlayWireCanvas) || ReferenceEquals(e.OriginalSource, BlueprintStageGrid))
            {
                _isPanningStage = true;
                _panStartViewportPoint = e.GetPosition(StageScrollViewer);
                _panStartHOffset = StageScrollViewer.HorizontalOffset;
                _panStartVOffset = StageScrollViewer.VerticalOffset;
                BlueprintStageGrid.Cursor = Cursors.ScrollAll;
                BlueprintStageGrid.CaptureMouse();
                e.Handled = true;
            }
        }

        private void Stage_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragOriginPin != null)
            {
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    CancelWireDrag();
                    return;
                }

                Point mousePt = e.GetPosition(UnderlayWireCanvas);
                PinViewModel? nearest = FindNearestCompatiblePin(_dragOriginPin, mousePt, MagneticSnapRadius);

                if (!ReferenceEquals(_snappedTargetPin, nearest))
                {
                    if (_snappedTargetPin != null) _snappedTargetPin.IsSnapHighlighted = false;
                    _snappedTargetPin = nearest;
                    if (_snappedTargetPin != null) _snappedTargetPin.IsSnapHighlighted = true;
                }

                Point activePt = _snappedTargetPin?.CanvasAnchor ?? mousePt;
                OverlayPreviewCanvas.IsPreviewSnapped = _snappedTargetPin != null;

                if (_dragOriginPin.Direction == PinDirection.Output)
                {
                    OverlayPreviewCanvas.PreviewStart = _dragOriginPin.CanvasAnchor;
                    OverlayPreviewCanvas.PreviewEnd = activePt;
                }
                else
                {
                    OverlayPreviewCanvas.PreviewStart = activePt;
                    OverlayPreviewCanvas.PreviewEnd = _dragOriginPin.CanvasAnchor;
                }

                e.Handled = true;
                return;
            }

            if (_isPanningStage)
            {
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    EndStagePan();
                    return;
                }

                Point curr = e.GetPosition(StageScrollViewer);
                double dx = curr.X - _panStartViewportPoint.X;
                double dy = curr.Y - _panStartViewportPoint.Y;

                StageScrollViewer.ScrollToHorizontalOffset(Math.Max(0, _panStartHOffset - dx));
                StageScrollViewer.ScrollToVerticalOffset(Math.Max(0, _panStartVOffset - dy));
                e.Handled = true;
            }
        }

        private void Stage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragOriginPin != null)
            {
                Point mousePt = e.GetPosition(UnderlayWireCanvas);
                PinViewModel? target = _snappedTargetPin ?? FindNearestCompatiblePin(_dragOriginPin, mousePt, MagneticSnapRadius + 4.0);
                PinViewModel origin = _dragOriginPin;

                CancelWireDrag();

                if (target != null)
                {
                    ViewModel?.ConnectPins(origin, target);
                    UnderlayWireCanvas.InvalidateVisual();
                }

                e.Handled = true;
                return;
            }

            if (_isPanningStage)
            {
                EndStagePan();
                e.Handled = true;
            }
        }

        private void CancelWireDrag()
        {
            if (_snappedTargetPin != null)
            {
                _snappedTargetPin.IsSnapHighlighted = false;
                _snappedTargetPin = null;
            }

            _dragOriginPin = null;
            OverlayPreviewCanvas.IsDraggingPreview = false;
            OverlayPreviewCanvas.IsPreviewSnapped = false;

            if (BlueprintStageGrid.IsMouseCaptured)
            {
                BlueprintStageGrid.ReleaseMouseCapture();
            }
        }

        private void EndStagePan()
        {
            _isPanningStage = false;
            BlueprintStageGrid.Cursor = Cursors.Arrow;
            if (BlueprintStageGrid.IsMouseCaptured)
            {
                BlueprintStageGrid.ReleaseMouseCapture();
            }
        }

        private void SyncAllCardAnchorsImmediate()
        {
            foreach (var card in _cards)
            {
                card.SyncPinAnchorsTo(UnderlayWireCanvas);
            }
        }

        private PinViewModel? FindNearestCompatiblePin(PinViewModel origin, Point cursorPt, double maxRadius)
        {
            var vm = ViewModel;
            if (vm == null) return null;

            double bestDistSq = maxRadius * maxRadius;
            PinViewModel? best = null;

            foreach (var node in vm.Nodes)
            {
                if (ReferenceEquals(node, origin.OwnerNode)) continue;

                var candidates = origin.Direction == PinDirection.Output ? node.InputPins : node.OutputPins;
                foreach (var candidate in candidates)
                {
                    if (!origin.IsCompatibleWith(candidate)) continue;

                    double dx = candidate.CanvasAnchor.X - cursorPt.X;
                    double dy = candidate.CanvasAnchor.Y - cursorPt.Y;
                    double dSq = dx * dx + dy * dy;
                    if (dSq <= bestDistSq)
                    {
                        bestDistSq = dSq;
                        best = candidate;
                    }
                }
            }

            return best;
        }
    }
}
