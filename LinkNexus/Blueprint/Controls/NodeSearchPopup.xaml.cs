using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using LinkNexus.Blueprint.Core;
using LinkNexus.Blueprint.ViewModels;

namespace LinkNexus.Blueprint.Controls
{
    /// <summary>
    /// Blender / UE5 风格内嵌画布硬件加速搜索与拉线建链浮层 (NodeSearchPopup)
    /// 采用同 HWND 视口边界钳位卡片方案，彻底消除 Popup AllowsTransparency 软件分层渲染与焦点切换延迟
    /// </summary>
    public partial class NodeSearchPopup : UserControl
    {
        private const double CardWidthPx = 380.0;
        private const double CardEstimatedHeightPx = 392.0;

        public WorkspaceInteractionViewModel InteractionVm { get; } = new();

        public event Action<NodeRegistryItem, Point, PinViewModel?>? NodeSelected;

        public bool IsOpen => Visibility == Visibility.Visible;

        public NodeSearchPopup()
        {
            InitializeComponent();
            DataContext = InteractionVm;

            InteractionVm.RequestClose += Hide;
            InteractionVm.RequestSpawnAndConnect += (item, pt, pin) =>
            {
                NodeSelected?.Invoke(item, pt, pin);
            };
        }

        /// <summary>
        /// 在工作区相对坐标处秒开搜索卡片（自动钳位防止超出视口边界）
        /// </summary>
        public void ShowAt(Point viewportPoint, Point canvasPoint, PinViewModel? dragOriginPin = null)
        {
            double hostW = ActualWidth > 10 ? ActualWidth : (Parent is FrameworkElement fe && fe.ActualWidth > 10 ? fe.ActualWidth : 960.0);
            double hostH = ActualHeight > 10 ? ActualHeight : (Parent is FrameworkElement feH && feH.ActualHeight > 10 ? feH.ActualHeight : 640.0);

            double clampedX = Math.Clamp(viewportPoint.X, 8.0, Math.Max(8.0, hostW - CardWidthPx - 12.0));
            double clampedY = Math.Clamp(viewportPoint.Y, 8.0, Math.Max(8.0, hostH - CardEstimatedHeightPx - 12.0));

            PopupCardTransform.X = clampedX;
            PopupCardTransform.Y = clampedY;

            InteractionVm.Open(canvasPoint, dragOriginPin);
            Visibility = Visibility.Visible;

            SearchTextBox.Focus();
            Keyboard.Focus(SearchTextBox);
            SearchTextBox.SelectAll();

            Dispatcher.BeginInvoke(() =>
            {
                if (Visibility == Visibility.Visible)
                {
                    SearchTextBox.Focus();
                    Keyboard.Focus(SearchTextBox);
                }
            }, DispatcherPriority.Input);
        }

        public void Hide()
        {
            if (Visibility != Visibility.Collapsed)
            {
                Visibility = Visibility.Collapsed;
                InteractionVm.Close();
            }
        }

        private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Hide();
            e.Handled = true;
        }

        private void SearchTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down)
            {
                InteractionVm.SelectNext();
                if (InteractionVm.SelectedItem != null)
                {
                    NodesListBox.ScrollIntoView(InteractionVm.SelectedItem);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                InteractionVm.SelectPrevious();
                if (InteractionVm.SelectedItem != null)
                {
                    NodesListBox.ScrollIntoView(InteractionVm.SelectedItem);
                }
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                InteractionVm.ConfirmSelection();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Hide();
                e.Handled = true;
            }
        }

        private void NodesListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            InteractionVm.ConfirmSelection();
            e.Handled = true;
        }

        private void NodesListBox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep &&
                VisualTreeSafeHelper.FindAncestor<ListBoxItem>(dep, includeSelf: true) != null &&
                NodesListBox.SelectedItem != null)
            {
                InteractionVm.ConfirmSelection();
                e.Handled = true;
            }
        }
    }
}
