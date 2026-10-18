using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace LinkNexus
{
    /// <summary>
    /// MainWindow.xaml 的交互与系统底层控制接入
    /// 接管 Windows Win32 消息泵 WndProc，监听 USB PnP 热插拔并挂载 MVVM 架构
    /// 包含文件拖拽载入与 Linux CLI 键盘回车交互
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly UsbMonitorService _usbMonitorService;
        private readonly MainViewModel _viewModel;
        private HwndSource? _hwndSource;

        public MainWindow()
        {
            InitializeComponent();

            // 1. 初始化底层 USB 监听服务与主 ViewModel
            _usbMonitorService = new UsbMonitorService();
            _viewModel = new MainViewModel(_usbMonitorService);
            DataContext = _viewModel;

            // 2. 窗口生命周期事件
            StateChanged += MainWindow_StateChanged;
            Closed += MainWindow_Closed;

            // 3. 监听 CLI 终端行输出自动滚动到底部
            ((System.Collections.Specialized.INotifyCollectionChanged)_viewModel.Esp32CliTerminalLines).CollectionChanged += (s, e) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    Esp32CliScrollViewer?.ScrollToBottom();
                });
            };
        }

        /// <summary>
        /// 当 WPF 窗体底层 Win32 HWND 句柄生成就绪后触发
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
            if (_hwndSource != null)
            {
                // 挂接 WndProc 钩子捕获 WM_DEVICECHANGE 广播
                _hwndSource.AddHook(WndProc);

                // 注册 USB 与 COM 端口设备广播监听
                _usbMonitorService.Register(_hwndSource.Handle);
            }
        }

        /// <summary>
        /// 原生 Windows 消息泵过滤器
        /// </summary>
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            return _usbMonitorService.HwndHandler(hwnd, msg, wParam, lParam, ref handled);
        }

        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                PathMaximizeIcon.Data = Geometry.Parse("M 2 0 H 10 V 8 H 8 V 10 H 0 V 2 H 2 Z M 2 2 V 8 H 8 V 2 Z");
            }
            else
            {
                PathMaximizeIcon.Data = Geometry.Parse("M 0 0 H 10 V 10 H 0 Z");
            }
        }

        #region 自定义标题栏窗口控制按钮

        private void BtnWindowMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnWindowMaximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = (WindowState == WindowState.Maximized)
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void BtnWindowClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        #endregion

        #region 固件文件拖拽放入处理

        private void Window_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void Window_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            string[]? files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0 && File.Exists(files[0]))
            {
                _viewModel.LoadFile(files[0]);
                e.Handled = true;
            }
        }

        private void FileDropZone_DragOver(object sender, DragEventArgs e)
        {
            Window_DragOver(sender, e);
        }

        private void FileDropZone_Drop(object sender, DragEventArgs e)
        {
            Window_Drop(sender, e);
        }

        #endregion

        #region 串口与智能输入按键交互

        private void SmartInputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (_viewModel.SendSmartInputCommand.CanExecute(null))
                {
                    _viewModel.SendSmartInputCommand.Execute(null);
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// 双击“正常就绪”唤醒 ESP32-S3 无线调试与网络控制 CLI 终端
        /// </summary>
        private void OnStatusBadgeMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                if (_viewModel.OpenEsp32CliCommand.CanExecute(null))
                {
                    _viewModel.OpenEsp32CliCommand.Execute(null);
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// ESP32-S3 CLI 输入框键盘交互：Enter执行、Tab补全、↑/↓历史导航 (采用 PreviewKeyDown 彻底拦截 Tab 制表符)
        /// </summary>
        private void Esp32CliInputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                _viewModel.ExecuteCliTabCompletion();
                if (sender is System.Windows.Controls.TextBox tbTab)
                {
                    tbTab.CaretIndex = tbTab.Text.Length;
                }
                return;
            }
            if (e.Key == Key.Up)
            {
                e.Handled = true;
                _viewModel.NavigateCliHistory(up: true);
                if (sender is System.Windows.Controls.TextBox tbUp)
                {
                    tbUp.CaretIndex = tbUp.Text.Length;
                }
                return;
            }
            if (e.Key == Key.Down)
            {
                e.Handled = true;
                _viewModel.NavigateCliHistory(up: false);
                if (sender is System.Windows.Controls.TextBox tbDown)
                {
                    tbDown.CaretIndex = tbDown.Text.Length;
                }
                return;
            }
            if (e.Key == Key.Enter)
            {
                if (_viewModel.SendEsp32CliCommand.CanExecute(null))
                {
                    _viewModel.SendEsp32CliCommand.Execute(null);
                }
                e.Handled = true;
            }
        }

        /// <summary>
        /// 全局 Esc 快捷键关闭 ESP32-S3 CLI 悬浮终端
        /// </summary>
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _viewModel.IsEsp32CliOpen)
            {
                if (_viewModel.CloseEsp32CliCommand.CanExecute(null))
                {
                    _viewModel.CloseEsp32CliCommand.Execute(null);
                }
                e.Handled = true;
            }
        }

        #endregion

        #region ESP32 CLI 终端自由拖拽与边框磁吸贴合

        private bool _isCliDragging = false;
        private Point _cliDragStartPoint;
        private double _cliStartTransformX;
        private double _cliStartTransformY;
        private const double CliSnapThreshold = 25.0; // 靠近边框 25px 内触发磁吸贴合

        private void CliHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 排除点击右上角关闭按钮等子控件
            if (e.OriginalSource is DependencyObject dep && FindVisualParent<System.Windows.Controls.Button>(dep) != null)
            {
                return;
            }

            if (sender is FrameworkElement element)
            {
                _isCliDragging = true;
                _cliDragStartPoint = e.GetPosition(this);
                _cliStartTransformX = CliWindowTransform.X;
                _cliStartTransformY = CliWindowTransform.Y;
                element.CaptureMouse();
                e.Handled = true;
            }
        }

        private void CliHeader_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isCliDragging) return;

            Point currentPoint = e.GetPosition(this);
            double deltaX = currentPoint.X - _cliDragStartPoint.X;
            double deltaY = currentPoint.Y - _cliDragStartPoint.Y;

            double newX = _cliStartTransformX + deltaX;
            double newY = _cliStartTransformY + deltaY;

            double containerWidth = Esp32CliOverlayGrid.ActualWidth;
            double containerHeight = Esp32CliOverlayGrid.ActualHeight;
            double modalWidth = Esp32CliWindowBorder.ActualWidth > 0 ? Esp32CliWindowBorder.ActualWidth : 860.0;
            double modalHeight = Esp32CliWindowBorder.ActualHeight > 0 ? Esp32CliWindowBorder.ActualHeight : 580.0;

            if (containerWidth > modalWidth && containerHeight > modalHeight)
            {
                // 居中基准偏移量
                double baseX = (containerWidth - modalWidth) / 2.0;
                double baseY = (containerHeight - modalHeight) / 2.0;

                // 当前距离四边边界的视觉绝对距离 (px)
                double currentLeft = baseX + newX;
                double currentRight = containerWidth - (currentLeft + modalWidth);
                double currentTop = baseY + newY;
                double currentBottom = containerHeight - (currentTop + modalHeight);

                // 左/右边框磁吸贴合 (Snap to Left / Right Edge)
                if (currentLeft >= -CliSnapThreshold && currentLeft <= CliSnapThreshold)
                {
                    newX = -baseX; // 吸附贴紧左边框
                }
                else if (currentRight >= -CliSnapThreshold && currentRight <= CliSnapThreshold)
                {
                    newX = containerWidth - modalWidth - baseX; // 吸附贴紧右边框
                }

                // 上/下边框磁吸贴合 (Snap to Top / Bottom Edge)
                if (currentTop >= -CliSnapThreshold && currentTop <= CliSnapThreshold)
                {
                    newY = -baseY; // 吸附贴紧上边框
                }
                else if (currentBottom >= -CliSnapThreshold && currentBottom <= CliSnapThreshold)
                {
                    newY = containerHeight - modalHeight - baseY; // 吸附贴紧下边框
                }

                // 边界物理钳位 (Clamp: 确保无论如何拖动，面板绝不被拖拽到可视区域外)
                double minX = -baseX;
                double maxX = containerWidth - modalWidth - baseX;
                double minY = -baseY;
                double maxY = containerHeight - modalHeight - baseY;

                newX = Math.Clamp(newX, minX, maxX);
                newY = Math.Clamp(newY, minY, maxY);
            }

            CliWindowTransform.X = newX;
            CliWindowTransform.Y = newY;
            e.Handled = true;
        }

        private void CliHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isCliDragging)
            {
                _isCliDragging = false;
                if (sender is FrameworkElement element)
                {
                    element.ReleaseMouseCapture();
                }
                e.Handled = true;
            }
        }

        private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject? parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindVisualParent<T>(parentObject);
        }

        #endregion

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(WndProc);
                _hwndSource = null;
            }

            _viewModel.CloseSerialPortInternal();
            _usbMonitorService.Dispose();
        }
    }
}

