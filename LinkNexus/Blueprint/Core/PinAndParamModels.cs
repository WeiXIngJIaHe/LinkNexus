using System;
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
        ImuAttitude,   // 三维姿态欧拉角/四元数流 - 矢量紫 #A78BFA
        RawFrame,      // 原始文本/字节流 - 翡翠绿 #34D399
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

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public NodeBase OwnerNode { get; }
        public int PinIndex { get; }
        public string Name { get; }
        public PinDirection Direction { get; }
        public PinDataType DataType { get; }

        public string ColorHex => DataType switch
        {
            PinDataType.ChannelStream => "#38BDF8",
            PinDataType.ImuAttitude => "#A78BFA",
            PinDataType.RawFrame => "#34D399",
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

        public bool IsConnected
        {
            get => _isConnected;
            set
            {
                if (_isConnected != value)
                {
                    _isConnected = value;
                    OnPropertyChanged();
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
            PinIndex = pinIndex;
            Name = name;
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

            // ChannelStream 与 ImuAttitude 均为浮点向量通道流，允许直接互联解算
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

        public string Key { get; }
        public string Label { get; }
        public NodeParamKind Kind { get; }
        public ObservableCollection<string> Options { get; } = new();

        public Action<string>? ValueChangedCallback { get; set; }
        public Action<bool>? BoolChangedCallback { get; set; }
        public ICommand? ActionCommand { get; set; }

        public bool IsCombo => Kind == NodeParamKind.Combo;
        public bool IsText => Kind == NodeParamKind.Text;
        public bool IsToggle => Kind == NodeParamKind.Toggle;
        public bool IsActionButton => Kind == NodeParamKind.ActionButton;
        public bool IsCodeScript => Kind == NodeParamKind.CodeScript;
        public bool IsReadOnlyBadge => Kind == NodeParamKind.ReadOnlyBadge;

        public string Value
        {
            get => _value;
            set
            {
                if (_value != value)
                {
                    _value = value;
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

        public NodeParameter(string key, string label, NodeParamKind kind, string initialValue = "")
        {
            Key = key;
            Label = label;
            Kind = kind;
            _value = initialValue;
            _buttonText = initialValue;
        }

        public void SetValueSilently(string newValue)
        {
            if (_value != newValue)
            {
                _value = newValue;
                OnPropertyChanged(nameof(Value));
            }
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
