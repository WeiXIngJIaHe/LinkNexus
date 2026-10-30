using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.ViewModels
{
    /// <summary>
    /// 节点端口连线视图模型 (ConnectionViewModel)
    /// 管理一条三阶贝塞尔曲线连线的两端坐标同步，以及底层 NodeBase 之间的 System.Threading.Channels 管道绑定与解绑
    /// </summary>
    public sealed class ConnectionViewModel : INotifyPropertyChanged, IDisposable
    {
        private bool _isDisposed;
        private bool _isHovered;

        public string ConnectionId { get; } = Guid.NewGuid().ToString("N");
        public PinViewModel SourcePin { get; }
        public PinViewModel TargetPin { get; }
        public Brush WireBrush { get; }
        public Brush GlowBrush { get; }

        public Point StartPoint => SourcePin.CanvasAnchor;
        public Point EndPoint => TargetPin.CanvasAnchor;

        public bool IsHovered
        {
            get => _isHovered;
            set
            {
                if (_isHovered != value)
                {
                    _isHovered = value;
                    OnPropertyChanged();
                    GeometryChanged?.Invoke(this);
                }
            }
        }

        public event Action<ConnectionViewModel>? GeometryChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        public ConnectionViewModel(PinViewModel firstPin, PinViewModel secondPin)
        {
            if (firstPin == null) throw new ArgumentNullException(nameof(firstPin));
            if (secondPin == null) throw new ArgumentNullException(nameof(secondPin));

            if (firstPin.Direction == PinDirection.Output && secondPin.Direction == PinDirection.Input)
            {
                SourcePin = firstPin;
                TargetPin = secondPin;
            }
            else if (firstPin.Direction == PinDirection.Input && secondPin.Direction == PinDirection.Output)
            {
                SourcePin = secondPin;
                TargetPin = firstPin;
            }
            else
            {
                throw new InvalidOperationException("连线两端必须分别为 OutputPin 与 InputPin。");
            }

            var startColor = (Color)ColorConverter.ConvertFromString(SourcePin.ColorHex);
            var endColor = (Color)ColorConverter.ConvertFromString(TargetPin.ColorHex);

            var gradient = new LinearGradientBrush(startColor, endColor, new Point(0, 0.5), new Point(1, 0.5));
            gradient.Freeze();
            WireBrush = gradient;

            var glowColor = Color.FromArgb(68, startColor.R, startColor.G, startColor.B);
            var glow = new SolidColorBrush(glowColor);
            glow.Freeze();
            GlowBrush = glow;

            // 建立底层 System.Threading.Channels 数据路由绑定
            SourcePin.OwnerNode.BindOutputChannel(SourcePin.PinIndex, TargetPin.OwnerNode, TargetPin.PinIndex);
            SourcePin.IsConnected = true;
            TargetPin.IsConnected = true;

            SourcePin.PropertyChanged += OnPinPropertyChanged;
            TargetPin.PropertyChanged += OnPinPropertyChanged;
        }

        private void OnPinPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(PinViewModel.CanvasAnchor))
            {
                OnPropertyChanged(nameof(StartPoint));
                OnPropertyChanged(nameof(EndPoint));
                GeometryChanged?.Invoke(this);
            }
        }

        /// <summary>
        /// 解除底层 Channel 管道绑定并释放事件订阅
        /// </summary>
        public void Disconnect()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            SourcePin.PropertyChanged -= OnPinPropertyChanged;
            TargetPin.PropertyChanged -= OnPinPropertyChanged;

            SourcePin.OwnerNode.UnbindOutputChannel(SourcePin.PinIndex, TargetPin.OwnerNode, TargetPin.PinIndex);
        }

        public void Dispose() => Disconnect();

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
