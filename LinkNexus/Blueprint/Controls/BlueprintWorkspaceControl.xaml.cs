using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Blender (Shift+A / 右键) 零延迟搜索浮层、UE5 (拉线松手弹窗自适应连线) 与 Blender (拖拽切线)
    /// 支持高刷屏 VSync 合帧调度 (60Hz ~ 240Hz+)
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

        // 高刷屏 VSync 合帧节流状态 (适配 1000Hz 鼠标回报率 + 60~240Hz 显示器)
        private bool _wireRepaintPending;
        private long _lastWirePreviewTick;

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

        #region 卡片与连线生命周期 & VSync 合帧刷新

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
            RequestCoalescedWireRepaint();
        }

        /// <summary>
        /// 按屏幕物理刷新节拍合并连线重绘请求 (VSync Coalescing)，消除 1000Hz 鼠标高频事件造成的无效重绘
        /// </summary>
        public void RequestCoalescedWireRepaint()
        {
            if (_wireRepaintPending) return;
            _wireRepaintPending = true;

            Dispatcher.BeginInvoke(() =>
            {
                _wireRepaintPending = false;
                UnderlayWireCanvas.InvalidateVisual();
            }, DispatcherPriority.Render);
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
                if (TryHandleShiftAShortcut(e.OriginalSource as DependencyObject))
                {
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape && SearchPopup.IsOpen)
            {
                SearchPopup.Hide();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 响应 Shift+A 快捷键：若当前焦点位于节点内部 TextBox 则放行正常输入，否则秒开搜索浮层
        /// </summary>
        public bool TryHandleShiftAShortcut(DependencyObject? focusSource = null)
        {
            if (!IsVisible) return false;

            DependencyObject? target = focusSource ?? Keyboard.FocusedElement as DependencyObject;
            if (target != null)
            {
                var activeTextBox = VisualTreeSafeHelper.FindAncestor<TextBox>(target, includeSelf: true);
                if (activeTextBox != null && !SearchPopup.IsOpen)
                {
                    // 保护节点内嵌参数输入框正常输入大写字母 'A'
                    return false;
                }
            }

            Point viewportPt = Mouse.GetPosition(this);
            if (viewportPt.X < 0 || viewportPt.Y < 0 || viewportPt.X > ActualWidth || viewportPt.Y > ActualHeight)
            {
                viewportPt = new Point(Math.Max(60, ActualWidth * 0.38), Math.Max(60, ActualHeight * 0.28));
            }

            Point stagePt = Mouse.GetPosition(BlueprintStageGrid);
            if (stagePt.X < 0 || stagePt.Y < 0)
            {
                stagePt = new Point(
                    (StageScrollViewer.HorizontalOffset + viewportPt.X) / _currentZoom,
                    (StageScrollViewer.VerticalOffset + viewportPt.Y) / _currentZoom);
            }

            OpenSearchPopup(stagePt, viewportPt, null);
            return true;
        }

        private void Stage_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (ReferenceEquals(e.OriginalSource, UnderlayWireCanvas) || ReferenceEquals(e.OriginalSource, BlueprintStageGrid))
            {
                Point mouseOnStage = e.GetPosition(BlueprintStageGrid);
                Point viewportPt = e.GetPosition(this);
                OpenSearchPopup(mouseOnStage, viewportPt, null);
                e.Handled = true;
            }
        }

        public void OpenSearchPopup(Point stagePt, Point viewportPt, PinViewModel? dragOriginPin = null)
        {
            SearchPopup.ShowAt(viewportPt, stagePt, dragOriginPin);
        }

        private void OnSearchPopupNodeSelected(NodeRegistryItem item, Point canvasPt, PinViewModel? dragOriginPin)
        {
            if (ViewModel == null) return;

            var newNode = ViewModel.CreateAndRegisterNodeByType(item.Key, canvasPt.X, canvasPt.Y)
                          ?? ViewModel.RegisterNode(item.Factory(canvasPt.X, canvasPt.Y));

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

        #endregion

        #region 拉线建链与画布交互

        public void BeginWireDrag(PinViewModel originPin, MouseButtonEventArgs e)
        {
            if (SearchPopup.IsOpen) SearchPopup.Hide();
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
            if (SearchPopup.IsOpen)
            {
                SearchPopup.Hide();
            }

            if (ReferenceEquals(e.OriginalSource, UnderlayWireCanvas) || ReferenceEquals(e.OriginalSource, BlueprintStageGrid))
            {
                Focus();
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

                // 按当前显示器物理刷新率周期合帧节流，防止 1000Hz 鼠标过度计算
                long nowTick = Stopwatch.GetTimestamp();
                double elapsedMs = (nowTick - _lastWirePreviewTick) * (1000.0 / Stopwatch.Frequency);
                double minIntervalMs = DisplayRefreshService.TargetFrameIntervalMs * 0.45;
                if (elapsedMs < minIntervalMs)
                {
                    e.Handled = true;
                    return;
                }
                _lastWirePreviewTick = nowTick;

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
                    Point viewportPt = e.GetPosition(this);
                    OpenSearchPopup(mousePt, viewportPt, origin);
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
