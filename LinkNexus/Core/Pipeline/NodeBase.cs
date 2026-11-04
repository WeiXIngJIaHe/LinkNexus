using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace LinkNexus.Core.Pipeline
{
    public enum NodeRole
    {
        Source,    // 数据采集源 (COM 串口、网络流、硬件探针)
        Transform, // 算子变换 (DSP 滤波、数学公式计算、卡尔曼、FFT、Python 算法)
        Sink       // 终端呈现与落盘 (ScottPlot 示波器、Helix 3D 动捕、CSV 落盘、烧录下发)
    }

    /// <summary>
    /// 节点卡片右键快捷上下文菜单项模型
    /// </summary>
    public sealed class NodeContextAction
    {
        public string Header { get; init; } = string.Empty;
        public Action? Execute { get; init; }
        public bool IsSeparator { get; init; }
        public bool IsEnabled { get; init; } = true;
        public bool IsChecked { get; init; }
        public List<NodeContextAction> Children { get; init; } = new();

        public static NodeContextAction Separator() => new() { IsSeparator = true };
    }

    /// <summary>
    /// 对齐 Nodify MVVM 规范的 DAG 节点抽象核心基类 (NodeBase)
    /// 集成 Bonsai 拓扑解耦模型、System.Threading.Channels 异步微秒级响应式管道与 60px 内嵌微波形总线
    /// </summary>
    public abstract class NodeBase : INotifyPropertyChanged, IDisposable
    {
        private readonly struct DownstreamSubscription : IEquatable<DownstreamSubscription>
        {
            public NodeBase TargetNode { get; }
            public int TargetInputPinIndex { get; }

            public DownstreamSubscription(NodeBase targetNode, int targetInputPinIndex)
            {
                TargetNode = targetNode;
                TargetInputPinIndex = targetInputPinIndex;
            }

            public bool Equals(DownstreamSubscription other)
                => ReferenceEquals(TargetNode, other.TargetNode) && TargetInputPinIndex == other.TargetInputPinIndex;

            public override bool Equals(object? obj) => obj is DownstreamSubscription s && Equals(s);
            public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(TargetNode), TargetInputPinIndex);
        }

        private readonly Channel<(int InputPin, MetricSample Sample)> _inboundChannel;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _routeLock = new();
        private readonly Dictionary<int, List<DownstreamSubscription>> _routes = new();

        private Point _location;
        private bool _isSelected;
        private bool _isActive;
        private string _statusBadge = "待命";
        private long _sampleCounter;

        // 60px 内嵌微波形数据环形缓冲区 (Micro Sparkline Visual)
        private const int SparklineCapacity = 48;
        private readonly double[] _sparklineRing = new double[SparklineCapacity];
        private int _sparklineHead;
        private PointCollection _sparklinePoints = new();

        public string NodeId { get; } = Guid.NewGuid().ToString("N");
        public virtual string NodeTypeKey => GetType().Name;
        public string Title { get; }
        public string CategoryName { get; }
        public NodeRole Role { get; }
        public string HeaderAccentHex { get; }
        public Brush HeaderAccentBrush { get; }
        public Brush HeaderGradientBrush { get; }

        public static LinearGradientBrush CrimsonGradient { get; }
        public static LinearGradientBrush CyanAmberGradient { get; }
        public static LinearGradientBrush PurpleGradient { get; }

        static NodeBase()
        {
            var crimson = new LinearGradientBrush(
                Color.FromArgb(0x88, 0xE1, 0x1D, 0x48),
                Color.FromArgb(0x33, 0x1E, 0x15, 0x20),
                new Point(0, 0), new Point(1, 0));
            crimson.Freeze();
            CrimsonGradient = crimson;

            var cyanAmber = new LinearGradientBrush(
                Color.FromArgb(0x77, 0x06, 0xB6, 0xD4),
                Color.FromArgb(0x44, 0xD9, 0x77, 0x06),
                new Point(0, 0), new Point(1, 0));
            cyanAmber.Freeze();
            CyanAmberGradient = cyanAmber;

            var purple = new LinearGradientBrush(
                Color.FromArgb(0x88, 0x93, 0x33, 0xEA),
                Color.FromArgb(0x33, 0x22, 0x10, 0x38),
                new Point(0, 0), new Point(1, 0));
            purple.Freeze();
            PurpleGradient = purple;
        }
        public string IconGeometry { get; }
        public virtual double CardWidth => 284.0;

        public virtual bool HasVofaPlotPreview => false;
        public virtual bool HasImu3DPreview => false;
        public virtual bool HasEmbeddedMicroScope => true;

        public ObservableCollection<ConnectorViewModel> InputConnectors { get; } = new();
        public ObservableCollection<ConnectorViewModel> OutputConnectors { get; } = new();
        public ObservableCollection<NodeParameter> Parameters { get; } = new();

        public PointCollection SparklinePoints
        {
            get => _sparklinePoints;
            private set
            {
                _sparklinePoints = value;
                OnPropertyChanged();
            }
        }

        public Point Location
        {
            get => _location;
            set
            {
                if (!_location.Equals(value))
                {
                    _location = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(X));
                    OnPropertyChanged(nameof(Y));
                    PositionChanged?.Invoke(this);
                }
            }
        }

        public double X
        {
            get => _location.X;
            set
            {
                if (!_location.X.Equals(value))
                {
                    _location = new Point(value, _location.Y);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Location));
                    PositionChanged?.Invoke(this);
                }
            }
        }

        public double Y
        {
            get => _location.Y;
            set
            {
                if (!_location.Y.Equals(value))
                {
                    _location = new Point(_location.X, value);
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(Location));
                    PositionChanged?.Invoke(this);
                }
            }
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsActive
        {
            get => _isActive;
            set
            {
                if (_isActive != value)
                {
                    _isActive = value;
                    OnPropertyChanged();
                }
            }
        }

        public string StatusBadge
        {
            get => _statusBadge;
            set
            {
                if (_statusBadge != value)
                {
                    _statusBadge = value;
                    OnPropertyChanged();
                }
            }
        }

        public long SampleCounter
        {
            get => _sampleCounter;
            private set
            {
                if (_sampleCounter != value)
                {
                    _sampleCounter = value;
                    OnPropertyChanged();
                }
            }
        }

        public event Action<NodeBase>? PositionChanged;
        public event Action<ConnectorViewModel>? ConnectorRemoving;
        public event Action<NodeBase>? ConnectorsChanged;
        public event Action<NodeBase, int, MetricSample>? SampleEmitted;
        public event PropertyChangedEventHandler? PropertyChanged;

        protected NodeBase(string title, string categoryName, NodeRole role, string headerAccentHex, string iconGeometry, Brush? customHeaderGradient = null)
        {
            Title = title;
            CategoryName = categoryName;
            Role = role;
            HeaderAccentHex = headerAccentHex;
            IconGeometry = iconGeometry;

            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(headerAccentHex));
            brush.Freeze();
            HeaderAccentBrush = brush;

            if (customHeaderGradient != null)
            {
                HeaderGradientBrush = customHeaderGradient;
            }
            else
            {
                var c1 = (Color)ColorConverter.ConvertFromString(headerAccentHex);
                var c2 = Color.FromArgb(0x55, c1.R, c1.G, c1.B);
                var grad = new LinearGradientBrush(c2, Color.FromRgb(0x1E, 0x24, 0x30), new Point(0, 0), new Point(1, 0));
                grad.Freeze();
                HeaderGradientBrush = grad;
            }

            var channelOptions = new BoundedChannelOptions(2048)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            };
            _inboundChannel = Channel.CreateBounded<(int InputPin, MetricSample Sample)>(channelOptions);

            _ = Task.Run(ProcessInboundLoopAsync);
        }

        public ConnectorViewModel AddInputConnector(string name, ConnectorDataType dataType)
            => AddInputConnector(name, (PinDataType)dataType);

        public ConnectorViewModel AddOutputConnector(string name, ConnectorDataType dataType)
            => AddOutputConnector(name, (PinDataType)dataType);

        public NodeParameter AddSliderParameter(string key, double min, double max, double step, double initialValue)
        {
            var p = NodeParameter.Slider(key, key, initialValue, min, max, step);
            Parameters.Add(p);
            return p;
        }

        public NodeParameter AddSliderParameter(string label, double min, double max, double step, double initialValue, string? key = null)
        {
            var p = NodeParameter.Slider(key ?? label, label, initialValue, min, max, step);
            Parameters.Add(p);
            return p;
        }

        public NodeParameter AddToggleParameter(string label, bool initial = false, string? key = null)
        {
            var p = NodeParameter.Toggle(key ?? label, label, initial);
            Parameters.Add(p);
            return p;
        }

        public NodeParameter AddComboParameter(string label, System.Collections.Generic.IEnumerable<string> options, string initial = "", string? key = null)
        {
            var p = NodeParameter.Combo(key ?? label, label);
            foreach (var opt in options) p.Options.Add(opt);
            p.Value = initial;
            Parameters.Add(p);
            return p;
        }

        public NodeParameter AddTextParameter(string label, string initial = "", string? key = null)
        {
            var p = new NodeParameter(key ?? label, label, NodeParamKind.Text, initial);
            Parameters.Add(p);
            return p;
        }

        public NodeParameter AddExpressionParameter(string label, string initial = "", string? key = null)
        {
            var p = new NodeParameter(key ?? label, label, NodeParamKind.Expression, initial);
            Parameters.Add(p);
            return p;
        }

        public NodeParameter AddReadOnlyParameter(string label, string text = "", string? key = null)
        {
            var p = new NodeParameter(key ?? label, label, NodeParamKind.ReadOnlyBadge, text);
            Parameters.Add(p);
            return p;
        }

        public NodeParameter AddActionParameter(string buttonText, Action onClick, string? label = null, string? key = null)
        {
            var p = NodeParameter.Action(key ?? buttonText, label ?? buttonText, buttonText, onClick);
            Parameters.Add(p);
            return p;
        }

        public ConnectorViewModel AddInputConnector(string name, PinDataType dataType = PinDataType.ChannelStream)
        {
            int index = InputConnectors.Count;
            var connector = new ConnectorViewModel(name, PinDirection.Input, dataType, this, index);
            InputConnectors.Add(connector);
            ConnectorsChanged?.Invoke(this);
            return connector;
        }

        public ConnectorViewModel AddOutputConnector(string name, PinDataType dataType = PinDataType.ChannelStream)
        {
            int index = OutputConnectors.Count;
            var connector = new ConnectorViewModel(name, PinDirection.Output, dataType, this, index);
            OutputConnectors.Add(connector);
            ConnectorsChanged?.Invoke(this);
            return connector;
        }

        public void RemoveInputConnector(ConnectorViewModel connector)
        {
            if (InputConnectors.Remove(connector))
            {
                ConnectorRemoving?.Invoke(connector);
                for (int i = 0; i < InputConnectors.Count; i++)
                {
                    InputConnectors[i].PinIndex = i;
                }
                ConnectorsChanged?.Invoke(this);
            }
        }

        public void RemoveOutputConnector(ConnectorViewModel connector)
        {
            if (OutputConnectors.Remove(connector))
            {
                ConnectorRemoving?.Invoke(connector);
                for (int i = 0; i < OutputConnectors.Count; i++)
                {
                    OutputConnectors[i].PinIndex = i;
                }
                ConnectorsChanged?.Invoke(this);
            }
        }

        public void ConnectTo(int outputPinIndex, NodeBase downstreamNode, int targetInputPinIndex)
        {
            lock (_routeLock)
            {
                if (!_routes.TryGetValue(outputPinIndex, out var list))
                {
                    list = new List<DownstreamSubscription>();
                    _routes[outputPinIndex] = list;
                }

                var sub = new DownstreamSubscription(downstreamNode, targetInputPinIndex);
                if (!list.Contains(sub))
                {
                    list.Add(sub);
                }
            }

            if (outputPinIndex < OutputConnectors.Count) OutputConnectors[outputPinIndex].IsConnected = true;
            if (targetInputPinIndex < downstreamNode.InputConnectors.Count) downstreamNode.InputConnectors[targetInputPinIndex].IsConnected = true;
        }

        public void DisconnectFrom(int outputPinIndex, NodeBase downstreamNode, int targetInputPinIndex)
        {
            lock (_routeLock)
            {
                if (_routes.TryGetValue(outputPinIndex, out var list))
                {
                    list.Remove(new DownstreamSubscription(downstreamNode, targetInputPinIndex));
                    if (list.Count == 0 && outputPinIndex < OutputConnectors.Count)
                    {
                        OutputConnectors[outputPinIndex].IsConnected = false;
                    }
                }
            }
        }

        public void DisconnectAllRoutes()
        {
            lock (_routeLock)
            {
                _routes.Clear();
                foreach (var outPin in OutputConnectors) outPin.IsConnected = false;
            }
        }

        public virtual void ReceiveSample(int inputPinIndex, in MetricSample sample)
        {
            _inboundChannel.Writer.TryWrite((inputPinIndex, sample));
        }

        protected void EmitSample(int outputPinIndex, in MetricSample sample)
        {
            Interlocked.Increment(ref _sampleCounter);
            OnPropertyChanged(nameof(SampleCounter));

            // 更新内嵌微波形
            PushMicroSparklineSample(sample.Value);

            if (outputPinIndex < OutputConnectors.Count)
            {
                OutputConnectors[outputPinIndex].PushSample(sample);
            }

            List<DownstreamSubscription>? targets = null;
            lock (_routeLock)
            {
                if (_routes.TryGetValue(outputPinIndex, out var list) && list.Count > 0)
                {
                    targets = new List<DownstreamSubscription>(list);
                }
            }

            if (targets != null)
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    targets[i].TargetNode.ReceiveSample(targets[i].TargetInputPinIndex, sample);
                }
            }

            SampleEmitted?.Invoke(this, outputPinIndex, sample);
        }

        private async Task ProcessInboundLoopAsync()
        {
            var reader = _inboundChannel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var item))
                    {
                        Interlocked.Increment(ref _sampleCounter);
                        OnPropertyChanged(nameof(SampleCounter));
                        PushMicroSparklineSample(item.Sample.Value);

                        try
                        {
                            await OnSampleReceivedAsync(item.InputPin, item.Sample).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            StatusBadge = "异常";
                            Console.Error.WriteLine($"[NodeBase] {Title} Process Error: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        protected virtual Task OnSampleReceivedAsync(int inputPinIndex, MetricSample sample)
        {
            OnSampleReceived(inputPinIndex, sample);
            return Task.CompletedTask;
        }

        protected virtual void OnSampleReceived(int inputPinIndex, in MetricSample sample) { }

        public virtual void Start() => IsActive = true;
        public virtual void Stop() => IsActive = false;
        public virtual void Reset() => SampleCounter = 0;

        public virtual IEnumerable<NodeContextAction> GetContextMenuActions()
        {
            yield return new NodeContextAction
            {
                Header = "重置计数",
                Execute = Reset
            };
        }

        protected void PushMicroSparklineSample(double val)
        {
            _sparklineRing[_sparklineHead] = val;
            _sparklineHead = (_sparklineHead + 1) % SparklineCapacity;

            // 每 8 次更新触发一次轻量 UI 折线投影 (60px 高度, 140px 宽度)
            if (_sampleCounter % 8 == 0)
            {
                Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    double min = double.MaxValue;
                    double max = double.MinValue;
                    for (int i = 0; i < SparklineCapacity; i++)
                    {
                        double v = _sparklineRing[i];
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }

                    double range = max - min;
                    if (range < 0.001) range = 1.0;

                    var pts = new PointCollection(SparklineCapacity);
                    double dx = 140.0 / (SparklineCapacity - 1);
                    for (int i = 0; i < SparklineCapacity; i++)
                    {
                        int idx = (_sparklineHead + i) % SparklineCapacity;
                        double normalized = (_sparklineRing[idx] - min) / range;
                        double y = 48.0 - (normalized * 40.0);
                        pts.Add(new Point(i * dx, y));
                    }
                    SparklinePoints = pts;
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        public virtual void Dispose()
        {
            _cts.Cancel();
            _cts.Dispose();
            DisconnectAllRoutes();
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

