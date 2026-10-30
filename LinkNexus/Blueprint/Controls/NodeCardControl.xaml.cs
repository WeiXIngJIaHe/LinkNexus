using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Controls
{
    /// <summary>
    /// Blender / UE / ComfyUI 风格节点式流式蓝图卡片控件 (NodeCardControl)
    /// 支持按住 Header 拖拽移动节点、左右 InputPins / OutputPins 端口坐标实时映射与交互连线触发
    /// </summary>
    public partial class NodeCardControl : UserControl
    {
        private readonly Dictionary<PinViewModel, FrameworkElement> _pinSockets = new();
        private BlueprintWorkspaceControl? _parentWorkspace;

        private bool _isDraggingNode;
        private Point _dragStartMouseInWorkspace;
        private double _dragStartNodeX;
        private double _dragStartNodeY;

        public NodeBase? Node => DataContext as NodeBase;

        public NodeCardControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
            SizeChanged += (_, _) => RequestAnchorSync();
        }

        private BlueprintWorkspaceControl? ResolveWorkspace()
        {
            if (_parentWorkspace != null) return _parentWorkspace;
            DependencyObject? curr = VisualTreeHelper.GetParent(this);
            while (curr != null)
            {
                if (curr is BlueprintWorkspaceControl ws)
                {
                    _parentWorkspace = ws;
                    return ws;
                }
                curr = VisualTreeHelper.GetParent(curr);
            }
            return null;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var ws = ResolveWorkspace();
            ws?.RegisterNodeCard(this);
            Dispatcher.BeginInvoke(RequestAnchorSync, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _parentWorkspace?.UnregisterNodeCard(this);
            _parentWorkspace = null;
        }

        private void RequestAnchorSync()
        {
            ResolveWorkspace()?.UpdateAnchorsForCard(this);
        }

        public void SyncPinAnchorsTo(Visual targetCanvas)
        {
            if (!IsLoaded) return;
            foreach (var kv in _pinSockets)
            {
                var pin = kv.Key;
                var el = kv.Value;
                if (!el.IsLoaded || el.ActualWidth <= 0) continue;

                try
                {
                    Point center = new(el.ActualWidth * 0.5, el.ActualHeight * 0.5);
                    Point worldPt = el.TransformToVisual(targetCanvas).Transform(center);
                    pin.CanvasAnchor = worldPt;
                }
                catch
                {
                }
            }
        }

        private void CardRoot_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ResolveWorkspace()?.BringCardToFront(this);
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject src && IsInsideButton(src))
            {
                return;
            }

            var node = Node;
            var ws = ResolveWorkspace();
            if (node == null || ws == null) return;

            ws.BringCardToFront(this);
            _isDraggingNode = true;
            _dragStartMouseInWorkspace = ws.GetPointerInStage(e);
            _dragStartNodeX = node.X;
            _dragStartNodeY = node.Y;
            HeaderDragBorder.CaptureMouse();
            e.Handled = true;
        }

        private void Header_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingNode) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndHeaderDrag();
                return;
            }

            var node = Node;
            var ws = ResolveWorkspace();
            if (node == null || ws == null) return;

            Point current = ws.GetPointerInStage(e);
            double dx = current.X - _dragStartMouseInWorkspace.X;
            double dy = current.Y - _dragStartMouseInWorkspace.Y;

            node.X = _dragStartNodeX + dx;
            node.Y = _dragStartNodeY + dy;

            UpdateLayout();
            ws.UpdateAnchorsForCard(this);
            e.Handled = true;
        }

        private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingNode) return;
            EndHeaderDrag();
            RequestAnchorSync();
            e.Handled = true;
        }

        private void EndHeaderDrag()
        {
            _isDraggingNode = false;
            if (HeaderDragBorder.IsMouseCaptured)
            {
                HeaderDragBorder.ReleaseMouseCapture();
            }
        }

        private void RemoveNodeBtn_Click(object sender, RoutedEventArgs e)
        {
            var node = Node;
            if (node != null)
            {
                ResolveWorkspace()?.RequestRemoveNode(node);
            }
            e.Handled = true;
        }

        private void PinSocket_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is PinViewModel pin)
            {
                _pinSockets[pin] = el;
                Dispatcher.BeginInvoke(RequestAnchorSync, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void PinSocket_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is PinViewModel pin)
            {
                _pinSockets.Remove(pin);
            }
        }

        private void PinSocket_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is PinViewModel pin)
            {
                var ws = ResolveWorkspace();
                if (ws != null)
                {
                    SyncPinAnchorsTo(ws.CoordinateStage);
                    ws.BeginWireDrag(pin, e);
                    e.Handled = true;
                }
            }
        }

        private void PinSocket_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is PinViewModel pin)
            {
                ResolveWorkspace()?.RequestDisconnectPin(pin);
                e.Handled = true;
            }
        }

        private static bool IsInsideButton(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is Button) return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }
    }
}
