using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Controls
{
    /// <summary>
    /// Blender / UE / ComfyUI 风格节点式流式蓝图卡片控件 (NodeCardControl)
    /// 支持按住 Header 拖拽移动节点（兼容 Run 等 FrameworkContentElement 安全回溯）、左右端口坐标实时映射与右键动态拆分菜单
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
            DataContextChanged += OnDataContextChanged;
            SizeChanged += (_, _) => RequestAnchorSync();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is NodeBase oldNode)
            {
                oldNode.PinsChanged -= OnNodePinsChanged;
            }
            if (e.NewValue is NodeBase newNode)
            {
                newNode.PinsChanged += OnNodePinsChanged;
            }
        }

        private void OnNodePinsChanged(NodeBase _)
        {
            Dispatcher.BeginInvoke(() =>
            {
                UpdateLayout();
                RequestAnchorSync();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private BlueprintWorkspaceControl? ResolveWorkspace()
        {
            if (_parentWorkspace != null) return _parentWorkspace;
            _parentWorkspace = VisualTreeSafeHelper.FindAncestor<BlueprintWorkspaceControl>(this, includeSelf: false);
            return _parentWorkspace;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (Node != null)
            {
                Node.PinsChanged -= OnNodePinsChanged;
                Node.PinsChanged += OnNodePinsChanged;
            }
            var ws = ResolveWorkspace();
            ws?.RegisterNodeCard(this);
            Dispatcher.BeginInvoke(RequestAnchorSync, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (Node != null)
            {
                Node.PinsChanged -= OnNodePinsChanged;
            }
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

        private void CardRoot_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject src && VisualTreeSafeHelper.FindAncestor<TextBox>(src) != null)
            {
                return;
            }

            var node = Node;
            if (node == null) return;

            var actions = node.GetContextMenuActions();
            if (actions == null || actions.Count == 0) return;

            var menu = BuildStyledContextMenu(actions);
            menu.PlacementTarget = CardRootBorder;
            menu.Placement = PlacementMode.MousePoint;
            menu.IsOpen = true;
            e.Handled = true;
        }

        private ContextMenu BuildStyledContextMenu(IReadOnlyList<NodeContextAction> actions)
        {
            var menu = new ContextMenu
            {
                Background = TryFindResource("AppSurfaceBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(34, 41, 56)),
                Foreground = TryFindResource("AppTextPrimaryBrush") as Brush ?? Brushes.White,
                BorderBrush = TryFindResource("AccentCyanBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(56, 189, 248)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(3)
            };

            foreach (var act in actions)
            {
                menu.Items.Add(CreateMenuObject(act));
            }

            return menu;
        }

        private Control CreateMenuObject(NodeContextAction action)
        {
            if (action.IsSeparator)
            {
                return new Separator
                {
                    Background = TryFindResource("AppBorderBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(62, 77, 104)),
                    Margin = new Thickness(4, 3, 4, 3)
                };
            }

            var item = new MenuItem
            {
                Header = action.Header,
                IsEnabled = action.IsEnabled,
                IsChecked = action.IsChecked,
                IsCheckable = false,
                Foreground = TryFindResource("AppTextPrimaryBrush") as Brush ?? Brushes.White,
                Background = TryFindResource("AppSurfaceBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(34, 41, 56)),
                FontSize = 11.5,
                Padding = new Thickness(8, 5, 10, 5),
                Cursor = Cursors.Hand
            };

            if (action.Execute != null)
            {
                var callback = action.Execute;
                item.Click += (_, _) =>
                {
                    callback();
                    Dispatcher.BeginInvoke(RequestAnchorSync, System.Windows.Threading.DispatcherPriority.Loaded);
                };
            }

            if (action.Children != null && action.Children.Count > 0)
            {
                foreach (var child in action.Children)
                {
                    item.Items.Add(CreateMenuObject(child));
                }
            }

            return item;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject src &&
                VisualTreeSafeHelper.FindAncestor<ButtonBase>(src, includeSelf: true) != null)
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
    }
}
