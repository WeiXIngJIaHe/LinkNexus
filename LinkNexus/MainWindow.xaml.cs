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

        #endregion

        private void MainWindow_Closed(object? sender, EventArgs e)
        {
            if (_hwndSource != null)
            {
                _hwndSource.RemoveHook(WndProc);
                _hwndSource = null;
            }

            _usbMonitorService.Dispose();
        }
    }
}

