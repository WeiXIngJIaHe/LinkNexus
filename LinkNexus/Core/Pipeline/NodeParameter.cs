using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace LinkNexus.Core.Pipeline
{
    public enum NodeParamKind
    {
        Text,
        Number,
        Slider,
        Toggle,
        Combo,
        ActionButton,
        CodeScript,
        ReadOnlyBadge,
        Expression
    }

    /// <summary>
    /// 工业级节点参数项定义模型 (NodeParameter)
    /// 集成双模响应式绑定、AI GhostValue 幽灵值预审机制与 Clamp 安全范围钳位
    /// </summary>
    public class NodeParameter : INotifyPropertyChanged
    {
        private string _value = string.Empty;
        private double _doubleValue;
        private bool _boolValue;
        private string _buttonText = string.Empty;
        private string _emptyWatermark = "未枚举到端口";
        private bool _isUpdatingOptions;

        // Clamp 安全钳位范围
        private double _min = double.MinValue;
        private double _max = double.MaxValue;
        private double _step = 1.0;

        // AI Agent GhostValue 幽灵值机制 (AI 建议但尚未提交确认的参数)
        private string? _ghostValue;
        private double? _ghostDoubleValue;
        private string? _ghostReason;

        public string Key { get; }
        public string Label { get; }
        public NodeParamKind Kind { get; }
        public ObservableCollection<string> Options { get; } = new();

        public Action<string>? ValueChangedCallback { get; set; }
        public Action<double>? DoubleChangedCallback { get; set; }
        public Action<bool>? BoolChangedCallback { get; set; }
        public ICommand? ActionCommand { get; set; }
        public ICommand AcceptGhostCommand { get; }
        public ICommand RejectGhostCommand { get; }

        public bool IsCombo => Kind == NodeParamKind.Combo;
        public bool IsText => Kind == NodeParamKind.Text;
        public bool IsNumber => Kind == NodeParamKind.Number;
        public bool IsSlider => Kind == NodeParamKind.Slider;
        public bool IsToggle => Kind == NodeParamKind.Toggle;
        public bool IsActionButton => Kind == NodeParamKind.ActionButton;
        public bool IsCodeScript => Kind == NodeParamKind.CodeScript;
        public bool IsReadOnlyBadge => Kind == NodeParamKind.ReadOnlyBadge;
        public bool IsExpression => Kind == NodeParamKind.Expression;

        public bool HasNoComboOptions => Kind == NodeParamKind.Combo && Options.Count == 0;

        public double Min
        {
            get => _min;
            set
            {
                if (!_min.Equals(value))
                {
                    _min = value;
                    OnPropertyChanged();
                    // 动态钳位当前值
                    DoubleValue = ClampValue(_doubleValue);
                }
            }
        }

        public double Max
        {
            get => _max;
            set
            {
                if (!_max.Equals(value))
                {
                    _max = value;
                    OnPropertyChanged();
                    DoubleValue = ClampValue(_doubleValue);
                }
            }
        }

        public double Step
        {
            get => _step;
            set
            {
                if (!_step.Equals(value))
                {
                    _step = value;
                    OnPropertyChanged();
                }
            }
        }

        public string? GhostValue
        {
            get => _ghostValue;
            set
            {
                if (_ghostValue != value)
                {
                    _ghostValue = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasGhostValue));
                }
            }
        }

        public double? GhostDoubleValue
        {
            get => _ghostDoubleValue;
            set
            {
                if (_ghostDoubleValue != value)
                {
                    _ghostDoubleValue = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasGhostValue));
                }
            }
        }

        public string? GhostReason
        {
            get => _ghostReason;
            set
            {
                if (_ghostReason != value)
                {
                    _ghostReason = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasGhostValue => !string.IsNullOrEmpty(_ghostValue) || _ghostDoubleValue.HasValue;

        public string Value
        {
            get => _value;
            set
            {
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

        public double DoubleValue
        {
            get => _doubleValue;
            set
            {
                double clamped = ClampValue(value);
                if (!_doubleValue.Equals(clamped))
                {
                    _doubleValue = clamped;
                    _value = clamped.ToString("G");
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Value));
                    DoubleChangedCallback?.Invoke(_doubleValue);
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

        public string ToggleStateLabel => _boolValue ? "已启用 (ON)" : "已旁路 (OFF)";

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

        public event PropertyChangedEventHandler? PropertyChanged;

        public NodeParameter(string key, string label, NodeParamKind kind = NodeParamKind.Text, string initialValue = "")
        {
            Key = key;
            Label = label;
            Kind = kind;
            _value = initialValue;
            if (double.TryParse(initialValue, out double d))
            {
                _doubleValue = d;
            }

            AcceptGhostCommand = new RelayParamCommand(AcceptGhostValue);
            RejectGhostCommand = new RelayParamCommand(RejectGhostValue);
        }

        public static NodeParameter Slider(string key, string label, double initial, double min, double max, double step = 1.0)
        {
            var p = new NodeParameter(key, label, NodeParamKind.Slider, initial.ToString("G"))
            {
                Min = min,
                Max = max,
                Step = step,
                DoubleValue = initial
            };
            return p;
        }

        public static NodeParameter Toggle(string key, string label, bool initial = false)
        {
            var p = new NodeParameter(key, label, NodeParamKind.Toggle, initial ? "true" : "false")
            {
                BoolValue = initial
            };
            return p;
        }

        public static NodeParameter Combo(string key, string label, string watermark = "无可选端口")
        {
            var p = new NodeParameter(key, label, NodeParamKind.Combo, string.Empty)
            {
                EmptyWatermark = watermark
            };
            return p;
        }

        public static NodeParameter Action(string key, string label, string buttonText, Action onClick)
        {
            var p = new NodeParameter(key, label, NodeParamKind.ActionButton, string.Empty)
            {
                ButtonText = buttonText,
                ActionCommand = new RelayParamCommand(onClick)
            };
            return p;
        }

        public void AcceptGhostValue()
        {
            if (_ghostDoubleValue.HasValue)
            {
                DoubleValue = _ghostDoubleValue.Value;
            }
            else if (!string.IsNullOrEmpty(_ghostValue))
            {
                Value = _ghostValue;
            }
            GhostValue = null;
            GhostDoubleValue = null;
            GhostReason = null;
        }

        public void RejectGhostValue()
        {
            GhostValue = null;
            GhostDoubleValue = null;
            GhostReason = null;
        }

        public void ReplaceOptions(System.Collections.Generic.IEnumerable<string> newOptions, string? preferredSelection = null)
        {
            _isUpdatingOptions = true;
            try
            {
                Options.Clear();
                string? matched = null;
                foreach (var opt in newOptions)
                {
                    Options.Add(opt);
                    if (matched == null && preferredSelection != null && opt.StartsWith(preferredSelection, StringComparison.OrdinalIgnoreCase))
                    {
                        matched = opt;
                    }
                }

                if (matched != null)
                {
                    Value = matched;
                }
                else if (Options.Count > 0 && (string.IsNullOrEmpty(Value) || !Options.Contains(Value)))
                {
                    Value = Options[0];
                }
                else if (Options.Count == 0)
                {
                    Value = string.Empty;
                }
            }
            finally
            {
                _isUpdatingOptions = false;
                OnPropertyChanged(nameof(HasNoComboOptions));
            }
        }

        private double ClampValue(double val)
        {
            if (val < _min) return _min;
            if (val > _max) return _max;
            return val;
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private sealed class RelayParamCommand : ICommand
        {
            private readonly Action _act;
            public RelayParamCommand(Action act) => _act = act;
            public bool CanExecute(object? parameter) => true;
            public void Execute(object? parameter) => _act();
            public event EventHandler? CanExecuteChanged { add { } remove { } }
        }
    }
}
