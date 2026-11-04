using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LinkNexus.Blueprint.Core
{
    public enum PinDirection
    {
        Input,
        Output
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
    /// 节点输入/输出端口视图模型 (对应 NodeCardControl 左右两侧带颜色的圆点 Pin)
    /// </summary>
    public sealed class PinViewModel : INotifyPropertyChanged
    {
        private Point _canvasAnchor;
        private bool _isConnected;
        private bool _isSnapHighlighted;
        private int _pinIndex;
        private string _name;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public NodeBase OwnerNode { get; }

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

        public string Name
        {
            get => _name;
            internal set
            {
                if (_name != value)
                {
                    _name = value;
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

        public Point CanvasAnchor
        {
            get => _canvasAnchor;
            set
            {
                if (_canvasAnchor != value)
                {
                    _canvasAnchor = value;
                    OnPropertyChanged();
                }
            }
        }

        private bool _hasInlineWidget;
        private string _inlineValue = "0.0";
        private double _inlineDoubleValue = 0.0;

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

        public PinViewModel(NodeBase ownerNode, int pinIndex, string name, PinDirection direction, PinDataType dataType)
        {
            OwnerNode = ownerNode ?? throw new ArgumentNullException(nameof(ownerNode));
            _pinIndex = pinIndex;
            _name = name;
            Direction = direction;
            DataType = dataType;

            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ColorHex));
            brush.Freeze();
            PinBrush = brush;
        }

        /// <summary>
        /// 校验两个 Pin 是否兼容可连线 (方向相反、非同一节点、数据类型兼容)
        /// </summary>
        public bool IsCompatibleWith(PinViewModel? other)
        {
            if (other == null || ReferenceEquals(OwnerNode, other.OwnerNode)) return false;
            if (Direction == other.Direction) return false;

            if (DataType == PinDataType.Any || other.DataType == PinDataType.Any) return true;
            if (DataType == other.DataType) return true;

            bool thisNumeric = DataType is PinDataType.ChannelStream or PinDataType.ImuAttitude;
            bool otherNumeric = other.DataType is PinDataType.ChannelStream or PinDataType.ImuAttitude;
            return thisNumeric && otherNumeric;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }

    public enum NodeParamKind
    {
        Combo,
        Text,
        Number,
        Slider,
        Toggle,
        ActionButton,
        CodeScript,
        ReadOnlyBadge
    }

    /// <summary>
    /// 节点卡片中部动态渲染的参数项模型 (NodeParameter)
    /// </summary>
    public sealed class NodeParameter : INotifyPropertyChanged
    {
        private string _value = string.Empty;
        private bool _boolValue;
        private string _buttonText = string.Empty;
        private string _emptyWatermark = "未枚举到 USB 端口";
        private bool _isUpdatingOptions;

        public string Key { get; }
        public string Label { get; }
        public NodeParamKind Kind { get; }
        public ObservableCollection<string> Options { get; } = new();

        public Action<string>? ValueChangedCallback { get; set; }
        public Action<bool>? BoolChangedCallback { get; set; }
        public ICommand? ActionCommand { get; set; }

        public bool IsCombo => Kind == NodeParamKind.Combo;
        public bool IsText => Kind == NodeParamKind.Text;
        public bool IsNumber => Kind == NodeParamKind.Number;
        public bool IsSlider => Kind == NodeParamKind.Slider;
        public bool IsToggle => Kind == NodeParamKind.Toggle;
        public bool IsActionButton => Kind == NodeParamKind.ActionButton;
        public bool IsCodeScript => Kind == NodeParamKind.CodeScript;
        public bool IsReadOnlyBadge => Kind == NodeParamKind.ReadOnlyBadge;

        public double MinValue { get; set; } = 0.0;
        public double MaxValue { get; set; } = 100.0;
        public double Step { get; set; } = 1.0;

        public double DoubleValue
        {
            get => double.TryParse(_value, out double v) ? v : 0.0;
            set
            {
                Value = value.ToString("G");
                OnPropertyChanged();
            }
        }

        public bool HasNoComboOptions => Kind == NodeParamKind.Combo && Options.Count == 0;

        public string EmptyWatermark
        {
            get => _emptyWatermark;
            set
            {
                if (_emptyWatermark != value)
                {
                    _emptyWatermark = value;
                    OnPropertyChanged();
                }
            }
        }

        public string Value
        {
            get => _value;
            set
            {
                // WPF ComboBox 在 ItemsSource (Options) 刷新或清空瞬间会通过 TwoWay 绑定回写 null，直接拦截
                if (_isUpdatingOptions) return;
                if (Kind == NodeParamKind.Combo && value == null) return;

                string safeValue = value ?? string.Empty;
                if (_value != safeValue)
                {
                    _value = safeValue;
                    OnPropertyChanged();
                    ValueChangedCallback?.Invoke(_value);
                }
            }
        }

        public bool BoolValue
        {
            get => _boolValue;
            set
            {
                if (_boolValue != value)
                {
                    _boolValue = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ToggleStateLabel));
                    BoolChangedCallback?.Invoke(_boolValue);
                }
            }
        }

        public string ButtonText
        {
            get => _buttonText;
            set
            {
                if (_buttonText != value)
                {
                    _buttonText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ToggleStateLabel => _boolValue ? "运行中 (点击停止)" : "已停止 (点击启动)";

        public NodeParameter(string key, string label, NodeParamKind kind, string initialValue = "", string emptyWatermark = "未枚举到 USB 端口")
        {
            Key = key;
            Label = label;
            Kind = kind;
            _value = initialValue ?? string.Empty;
            _buttonText = initialValue ?? string.Empty;
            _emptyWatermark = emptyWatermark;
            Options.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoComboOptions));
        }

        public void SetValueSilently(string? newValue)
        {
            string safeValue = newValue ?? string.Empty;
            _value = safeValue;
            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(HasNoComboOptions));
        }

        /// <summary>
        /// 原子刷新下拉列表选项（严格使用真实枚举项，无选项时 Value 置空字符串，绝不填充占位符端口）
        /// </summary>
        public void ReplaceOptions(IEnumerable<string>? newOptions, string? preferredValue = null)
        {
            var targetList = new List<string>();
            if (newOptions != null)
            {
                foreach (var item in newOptions)
                {
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        targetList.Add(item.Trim());
                    }
                }
            }

            string candidate = !string.IsNullOrWhiteSpace(preferredValue)
                ? preferredValue!.Trim()
                : (!string.IsNullOrWhiteSpace(_value) ? _value : (targetList.Count > 0 ? targetList[0] : string.Empty));

            string resolved = targetList.Contains(candidate)
                ? candidate
                : (targetList.Count > 0 ? targetList[0] : string.Empty);

            bool identical = Options.Count == targetList.Count;
            if (identical)
            {
                for (int i = 0; i < targetList.Count; i++)
                {
                    if (!string.Equals(Options[i], targetList[i], StringComparison.Ordinal))
                    {
                        identical = false;
                        break;
                    }
                }
            }

            if (identical)
            {
                SetValueSilently(resolved);
                return;
            }

            _isUpdatingOptions = true;
            try
            {
                Options.Clear();
                foreach (var opt in targetList)
                {
                    Options.Add(opt);
                }
                _value = resolved;
            }
            finally
            {
                _isUpdatingOptions = false;
            }

            OnPropertyChanged(nameof(Value));
            OnPropertyChanged(nameof(HasNoComboOptions));
        }

        public void SetBoolSilently(bool newValue)
        {
            if (_boolValue != newValue)
            {
                _boolValue = newValue;
                OnPropertyChanged(nameof(BoolValue));
                OnPropertyChanged(nameof(ToggleStateLabel));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
