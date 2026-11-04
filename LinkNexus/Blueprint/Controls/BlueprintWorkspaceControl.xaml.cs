using System;
using System.Collections.Generic;
using System.Linq;
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
    /// 协调 NodeCardControl 节点拖拽、鼠标滚轮自由缩放 (35% ~ 240%)、三阶贝塞尔曲线磁吸建链、
    /// Blender (Shift+A / 右键) 模糊搜索弹窗、UE5 (拉线松手弹窗自适应连线) 与 Blender (拖拽切线)
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

            SearchPopup.NodeSelected += OnSearchPopupNodeSelected;

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

        #region 画布缩放与平移

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

        #endregion

        #region 卡片与连线生命周期

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

        #endregion

        #region Blender (Shift+A / 右键) 与 UE5 (拉线松手弹窗 / 拖拽切线) 交互核心

        private void Workspace_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            {
                Point mouseInControl = Mouse.GetPosition(this);
                Point mouseOnStage = Mouse.GetPosition(BlueprintStageGrid);
                Point screenPt = PointToScreenSafe(mouseInControl);
                OpenSearchPopup(mouseOnStage, screenPt, null);
                e.Handled = true;
            }
        }

        private void Stage_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, UnderlayWireCanvas) || ReferenceEquals(e.OriginalSource, BlueprintStageGrid))
            {
                Point mouseOnStage = e.GetPosition(BlueprintStageGrid);
                Point screenPt = PointToScreenSafe(e.GetPosition(this));
                OpenSearchPopup(mouseOnStage, screenPt, null);
                e.Handled = true;
            }
        }

        public void OpenSearchPopup(Point stagePt, Point screenPt, PinViewModel? dragOriginPin = null)
        {
            SearchPopup.ShowAt(screenPt, stagePt, dragOriginPin);
        }

        private void OnSearchPopupNodeSelected(NodeRegistryItem item, Point canvasPt, PinViewModel? dragOriginPin)
        {
            if (ViewModel == null) return;

            var newNode = item.Factory(canvasPt.X, canvasPt.Y);
            ViewModel.RegisterNode(newNode);

            // UE5 蓝图式自动连线
            if (dragOriginPin != null)
            {
                if (dragOriginPin.Direction == PinDirection.Output)
                {
                    var inPin = newNode.InputPins.FirstOrDefault(p => dragOriginPin.IsCompatibleWith(p));
                    if (inPin != null)
                    {
                        ViewModel.ConnectPins(dragOriginPin, inPin);
                        ViewModel.WorkspaceHint = $"已生成 [{newNode.Title}] 并自动与 [{dragOriginPin.Name}] 建立连线。";
                    }
                }
                else
                {
                    var outPin = newNode.OutputPins.FirstOrDefault(p => p.IsCompatibleWith(dragOriginPin));
                    if (outPin != null)
                    {
                        ViewModel.ConnectPins(outPin, dragOriginPin);
                        ViewModel.WorkspaceHint = $"已生成 [{newNode.Title}] 并自动反向连入 [{dragOriginPin.Name}]。";
                    }
                }
            }
            else
            {
                ViewModel.WorkspaceHint = $"已实例化 [{newNode.Title}] 算子卡片 (X: {Math.Round(canvasPt.X)}, Y: {Math.Round(canvasPt.Y)})。";
            }

            ScheduleFullAnchorSync();
        }

        /// <summary>
        /// Blender 式拖拽卡片切入现有连线 (Auto-Insert Node on Wire)
        /// </summary>
        public bool CheckAndPerformAutoInsert(NodeBase node)
        {
            if (ViewModel == null) return false;

            bool inserted = WorkspaceInteractionViewModel.TryAutoInsertNodeOnWire(
                node,
                ViewModel.Connections,
                conn => ViewModel.RemoveConnection(conn),
                (src, tgt) => ViewModel.ConnectPins(src, tgt),
                tolerance: 36.0);

            if (inserted)
            {
                ScheduleFullAnchorSync();
                ViewModel.WorkspaceHint = $"已自动切入连线: 节点 [{node.Title}] 已插入现有数据流管道。";
            }
            return inserted;
        }

        private Point PointToScreenSafe(Point relativePt)
        {
            try
            {
                if (IsLoaded && PresentationSource.FromVisual(this) != null)
                {
                    return PointToScreen(relativePt);
                }
            }
            catch
            {
            }
            return relativePt;
        }

        #endregion

        #region 拉线建链与画布交互

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
                else
                {
                    // UE5 蓝图式: 拉线至空白处松手自动弹窗并连线 (Drop-Wire to Spawn)
                    Point screenPt = PointToScreenSafe(e.GetPosition(this));
                    OpenSearchPopup(mousePt, screenPt, origin);
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

        #endregion
    }
}
