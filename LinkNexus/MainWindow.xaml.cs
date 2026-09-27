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
    /// 负责接管 Windows Win32 消息泵 WndProc，监听 USB PnP 热插拔并挂载 MVVM 架构
    /// 包含文件拖拽载入、开发者功能预览选项卡切换与快捷键管理
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

            // 监听 ViewModel 属性变化 (例如调试模式开关时自动同步界面卡片与控制台选项)
            _viewModel.PropertyChanged += ViewModel_PropertyChanged;

            // 2. 窗口生命周期事件与快捷键监听
            StateChanged += MainWindow_StateChanged;
            PreviewKeyDown += MainWindow_PreviewKeyDown;
            Closed += MainWindow_Closed;
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsDebugMode))
            {
                if (_viewModel.IsDebugMode)
                {
                    // 进入调试模式：自动展开功能预览工作台
                    RdoDebugPreview.IsChecked = true;
                    PnlPnpLogs.Visibility = Visibility.Collapsed;
                    PnlDebugPreviewWorkbench.Visibility = Visibility.Visible;
                }
                else
                {
                    // 退出调试模式：回退至纯净 PnP 诊断事件流
                    PnlPnpLogs.Visibility = Visibility.Visible;
                    PnlDebugPreviewWorkbench.Visibility = Visibility.Collapsed;
                }
            }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            // F12 快捷键全局切换开发者调试模式
            if (e.Key == Key.F12)
            {
                _viewModel.ToggleDebugModeCommand.Execute(null);
                e.Handled = true;
            }
        }

        /// <summary>
        /// 当 WPF 窗体底层 Win32 HWND 句柄生成就绪后触发
        /// </summary>
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // 获取原生 Win32 窗口句柄
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

        /// <summary>
        /// 窗体最大化 / 还原状态变更时更新图标
        /// </summary>
        private void MainWindow_StateChanged(object? sender, EventArgs e)
        {
            if (WindowState == WindowState.Maximized)
            {
                // 还原状态双层框图标
                PathMaximizeIcon.Data = Geometry.Parse("M 2 0 H 10 V 8 H 8 V 10 H 0 V 2 H 2 Z M 2 2 V 8 H 8 V 2 Z");
            }
            else
            {
                // 最大化单层正方形图标
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

        #region 文件拖拽放入处理 (支持全窗口拖拽与专用放入区)

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
                // 自动激活开发者调试模式以便直观审查文件
                if (!_viewModel.IsDebugMode)
                {
                    _viewModel.IsDebugMode = true;
                }

                _viewModel.LoadFile(files[0]);

                RdoDebugPreview.IsChecked = true;
                PnlPnpLogs.Visibility = Visibility.Collapsed;
                PnlDebugPreviewWorkbench.Visibility = Visibility.Visible;
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

        #region 控制台视图与功能预览选项卡切换

        private void RdoPnpLogs_Click(object sender, RoutedEventArgs e)
        {
            PnlPnpLogs.Visibility = Visibility.Visible;
            PnlDebugPreviewWorkbench.Visibility = Visibility.Collapsed;
        }

        private void RdoDebugPreview_Click(object sender, RoutedEventArgs e)
        {
            PnlPnpLogs.Visibility = Visibility.Collapsed;
            PnlDebugPreviewWorkbench.Visibility = Visibility.Visible;
        }

        private void TabPreview_Click(object sender, RoutedEventArgs e)
        {
            ViewFlashPreview.Visibility = (TabFlash.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            ViewSerialPreview.Visibility = (TabSerial.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            ViewLoadPreview.Visibility = (TabLoad.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
            ViewProtocolPreview.Visibility = (TabProtocol.IsChecked == true) ? Visibility.Visible : Visibility.Collapsed;
        }

        #endregion

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            // 卸载钩子与释放 PnP 资源
            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(WndProc);
                _hwndSource = null;
            }

            _usbMonitorService.Dispose();
        }
    }
}

