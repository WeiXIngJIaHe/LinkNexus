using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Media;

namespace LinkNexus.Core.Pipeline
{
    public enum PinDirection
    {
        Input,
        Output
    }

    public enum ConnectorDataType
    {
        ChannelStream,
        FloatStream,
        Vector3Stream,
        ImuAttitude,
        RawFrame,
        ByteStream,
        Trigger,
        Any
    }

    public enum PinDataType
    {
        ChannelStream, // 多通道浮点数据流 (VOFA+ / 传感器 / 算法) - 电光青 #38BDF8
        FloatStream,   // 单路浮点连续控制量 - 电光青 #38BDF8
        Vector3Stream, // 三维向量坐标/姿态 (X, Y, Z) - 矢量紫 #A78BFA
        ImuAttitude,   // 三维姿态欧拉角/四元数流 - 矢量紫 #A78BFA
        RawFrame,      // 原始文本/字节流 - 翡翠绿 #34D399
        ByteStream,    // 高速二进制字节流 (CH343P / FT2232) - 翡翠绿 #10B981
        Trigger,       // 微秒级软硬联锁触发脉冲 (ExecTrigger) - 珍珠白 #F8FAFC
        Any            // 通用流端口 - 琥珀金 #FBBF24
    }

    /// <summary>
    /// 对齐 Nodify MVVM 规范的端口引脚视图模型 (ConnectorViewModel)
    /// 内置基于 System.Threading.Channels 的无阻塞响应式高速数据管道
    /// 遵循 Blender/UE5 Pin 内联布局规范：未连线显示输入框，已连线自动隐藏
    /// </summary>
    public class ConnectorViewModel : INotifyPropertyChanged
    {
        private Point _anchor;
        private bool _isConnected;
        private bool _isSnapHighlighted;
        private int _pinIndex;
        private string _title;
        private bool _hasInlineWidget;
        private string _inlineValue = "0.0";
        private double _inlineDoubleValue = 0.0;
        private readonly Channel<MetricSample> _channel;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public NodeBase? OwnerNode { get; internal set; }

        public int PinIndex
        {
            get => _pinIndex;
            internal set
            {
                if (_pinIndex != value)
                {
                    _pinIndex = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Title
        {
            get => _title;
            set
            {
                if (_title != value)
                {
                    _title = value;
                    OnPropertyChanged();
                }
            }
        }

        public PinDirection Direction { get; }
        public PinDataType DataType { get; }

        public string ColorHex => DataType switch
        {
            PinDataType.Trigger => "#F8FAFC",
            PinDataType.Vector3Stream or PinDataType.ImuAttitude => "#A78BFA",
            PinDataType.ByteStream or PinDataType.RawFrame => "#10B981",
            PinDataType.ChannelStream or PinDataType.FloatStream => "#38BDF8",
            _ => "#FBBF24"
        };

        public Brush PinBrush { get; }

        /// <summary>
        /// Blender/UE5 风格：是否具备内联输入控件 (TextBox / NumberBox)
        /// </summary>
        public bool HasInlineWidget
        {
            get => _hasInlineWidget;
            set
            {
                if (_hasInlineWidget != value)
                {
                    _hasInlineWidget = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowInlineWidget));
                }
            }
        }

        /// <summary>
        /// 遵循 Blender / UE5 规范：未连线且支持内联控件时可见，连线后自动折叠隐藏
        /// </summary>
        public bool ShowInlineWidget => _hasInlineWidget && !_isConnected;

        public string InlineValue
        {
            get => _inlineValue;
            set
            {
                if (_inlineValue != value)
                {
                    _inlineValue = value ?? string.Empty;
                    if (double.TryParse(_inlineValue, out double d))
                    {
                        _inlineDoubleValue = d;
                        InlineDoubleChangedCallback?.Invoke(d);
                    }
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(InlineDoubleValue));
                    InlineValueChangedCallback?.Invoke(_inlineValue);
                }
            }
        }

        public double InlineDoubleValue
        {
            get => _inlineDoubleValue;
            set
            {
                if (!_inlineDoubleValue.Equals(value))
                {
                    _inlineDoubleValue = value;
                    _inlineValue = value.ToString("G");
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(InlineValue));
                    InlineDoubleChangedCallback?.Invoke(_inlineDoubleValue);
                }
            }
        }

        public Action<double>? InlineDoubleChangedCallback { get; set; }
        public Action<string>? InlineValueChangedCallback { get; set; }

        /// <summary>
        /// Nodify 自动回写计算出的引脚屏幕绝对坐标 (Mode=OneWayToSource)
        /// </summary>
        public Point Anchor
        {
            get => _anchor;
            set
            {
                if (_anchor != value)
                {
                    _anchor = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                if (_isConnected != value)
                {
                    _isConnected = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowInlineWidget));
                }
            }
        }

        public bool IsSnapHighlighted
        {
            get => _isSnapHighlighted;
            set
            {
                if (_isSnapHighlighted != value)
                {
                    _isSnapHighlighted = value;
                    OnPropertyChanged();
                }
            }
        }

        public ChannelReader<MetricSample> Reader => _channel.Reader;
        public ChannelWriter<MetricSample> Writer => _channel.Writer;

        public event Action<MetricSample>? SamplePushed;
        public event PropertyChangedEventHandler? PropertyChanged;

        public ConnectorViewModel(string title, PinDirection direction, PinDataType dataType = PinDataType.ChannelStream, NodeBase? ownerNode = null, int pinIndex = 0)
        {
            _title = title;
            Direction = direction;
            DataType = dataType;
            OwnerNode = ownerNode;
            _pinIndex = pinIndex;

            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorHex));
            brush.Freeze();
            PinBrush = brush;

            var channelOptions = new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = false,
                SingleWriter = false
            };
            _channel = Channel.CreateBounded<MetricSample>(channelOptions);
        }

        public bool PushSample(in MetricSample sample)
        {
            bool written = _channel.Writer.TryWrite(sample);
            SamplePushed?.Invoke(sample);
            return written;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
