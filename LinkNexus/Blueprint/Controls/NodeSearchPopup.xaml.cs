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
    /// Blender / UE5 风格无边框毛玻璃暗黑搜索与拉线建链弹窗 (NodeSearchPopup)
    /// </summary>
    public partial class NodeSearchPopup : UserControl
    {
        public WorkspaceInteractionViewModel InteractionVm { get; } = new();

        public event Action<NodeRegistryItem, Point, PinViewModel?>? NodeSelected;

        public bool IsOpen => HostPopup.IsOpen;

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

        public void ShowAt(Point screenPoint, Point canvasPoint, PinViewModel? dragOriginPin = null)
        {
            HostPopup.HorizontalOffset = screenPoint.X;
            HostPopup.VerticalOffset = screenPoint.Y;

            InteractionVm.Open(canvasPoint, dragOriginPin);
            HostPopup.IsOpen = true;
        }

        public void Hide()
        {
            HostPopup.IsOpen = false;
        }

        private void HostPopup_Opened(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(() =>
            {
                SearchTextBox.Focus();
                SearchTextBox.SelectAll();
            }, DispatcherPriority.Input);
        }

        private void HostPopup_Closed(object sender, EventArgs e)
        {
            InteractionVm.Close();
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
            if (NodesListBox.SelectedItem != null)
            {
                InteractionVm.ConfirmSelection();
                e.Handled = true;
            }
        }
    }
}
