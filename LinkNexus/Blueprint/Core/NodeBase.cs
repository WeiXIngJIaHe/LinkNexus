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

namespace LinkNexus.Blueprint.Core
{
    /// <summary>
    /// 节点卡片右键快捷上下文菜单项模型 (例如 COM 输入节点的按需拆分/合并 CH 通道)
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
    /// 流式蓝图节点底层抽象基类 (NodeBase)
    /// 基于 System.Threading.Channels 构建异步无阻塞响应式数据管道，支持动态端口拆分、右键动作与 MCP 全量图序列化
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

        private readonly Channel<(int InputPin, BlueprintPacket Packet)> _inboundChannel;
        private readonly CancellationTokenSource _cts = new();
        private readonly object _routeLock = new();
        private readonly Dictionary<int, List<DownstreamSubscription>> _routes = new();

        private double _x;
        private double _y;
        private bool _isSelected;
        private bool _isActive;
        private string _statusBadge = "待命";
        private long _packetCounter;

        public string NodeId { get; } = Guid.NewGuid().ToString("N");
        public virtual string NodeTypeKey => GetType().Name;
        public string Title { get; }
        public string CategoryName { get; }
        public string HeaderAccentHex { get; }
        public Brush HeaderAccentBrush { get; }
        public string IconGeometry { get; }
        public virtual double CardWidth => 278.0;

        public virtual bool HasVofaPlotPreview => false;
        public virtual bool HasImu3DPreview => false;

        public ObservableCollection<PinViewModel> InputPins { get; } = new();
        public ObservableCollection<PinViewModel> OutputPins { get; } = new();
        public ObservableCollection<NodeParameter> Parameters { get; } = new();

        public event Action<NodeBase>? PositionChanged;
        public event Action<PinViewModel>? PinRemoving;
        public event Action<NodeBase>? PinsChanged;
        public event PropertyChangedEventHandler? PropertyChanged;

        public double X
        {
            get => _x;
            set
            {
                double clamped = Math.Max(8.0, value);
                if (Math.Abs(_x - clamped) > 0.1)
                {
                    _x = clamped;
                    OnPropertyChanged();
                    PositionChanged?.Invoke(this);
                }
            }
        }

        public double Y
        {
            get => _y;
            set
            {
                double clamped = Math.Max(8.0, value);
                if (Math.Abs(_y - clamped) > 0.1)
                {
                    _y = clamped;
                    OnPropertyChanged();
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
            protected set
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
            protected set
            {
                if (_statusBadge != value)
                {
                    _statusBadge = value;
                    OnPropertyChanged();
                }
            }
        }

        public long PacketCounter
        {
            get => Interlocked.Read(ref _packetCounter);
        }

        protected NodeBase(string title, string categoryName, string headerAccentHex, string iconGeometry, double x = 40, double y = 40)
        {
            Title = title;
            CategoryName = categoryName;
            HeaderAccentHex = headerAccentHex;
            IconGeometry = iconGeometry;
            _x = x;
            _y = y;

            var accent = new SolidColorBrush((Color)ColorConverter.ConvertFromString(headerAccentHex));
            accent.Freeze();
            HeaderAccentBrush = accent;

            _inboundChannel = Channel.CreateBounded<(int, BlueprintPacket)>(new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            _ = Task.Run(() => ConsumeInboundLoopAsync(_cts.Token));
        }

        protected PinViewModel AddInputPin(string name, PinDataType type)
        {
            var pin = new PinViewModel(this, InputPins.Count, name, PinDirection.Input, type);
            InputPins.Add(pin);
            PinsChanged?.Invoke(this);
            return pin;
        }

        protected PinViewModel AddOutputPin(string name, PinDataType type)
        {
            var pin = new PinViewModel(this, OutputPins.Count, name, PinDirection.Output, type);
            OutputPins.Add(pin);
            PinsChanged?.Invoke(this);
            return pin;
        }

        /// <summary>
        /// 动态移除指定输出端口 (先通知外部断开关联连线，再同步平移后续端口索引与底层路由表)
        /// </summary>
        protected void RemoveOutputPin(PinViewModel pin)
        {
            int removedIdx = OutputPins.IndexOf(pin);
            if (removedIdx < 0) return;

            PinRemoving?.Invoke(pin);
            OutputPins.RemoveAt(removedIdx);

            lock (_routeLock)
            {
                _routes.Remove(removedIdx);
                var shifted = new Dictionary<int, List<DownstreamSubscription>>();
                foreach (var kv in _routes)
                {
                    int newKey = kv.Key > removedIdx ? kv.Key - 1 : kv.Key;
                    shifted[newKey] = kv.Value;
                }
                _routes.Clear();
                foreach (var kv in shifted)
                {
                    _routes[kv.Key] = kv.Value;
                }
            }

            for (int i = 0; i < OutputPins.Count; i++)
            {
                OutputPins[i].PinIndex = i;
            }

            PinsChanged?.Invoke(this);
        }

        /// <summary>
        /// 将当前节点的指定 Output Pin 绑定至下游节点的指定 Input Pin (建立底层 Channel 路由)
        /// </summary>
        public void BindOutputChannel(int outputPinIndex, NodeBase targetNode, int targetInputPinIndex)
        {
            if (targetNode == null || ReferenceEquals(this, targetNode)) return;
            lock (_routeLock)
            {
                if (!_routes.TryGetValue(outputPinIndex, out var list))
                {
                    list = new List<DownstreamSubscription>();
                    _routes[outputPinIndex] = list;
                }
                var sub = new DownstreamSubscription(targetNode, targetInputPinIndex);
                if (!list.Contains(sub))
                {
                    list.Add(sub);
                }
            }
        }

        /// <summary>
        /// 解除当前节点 Output Pin 到下游节点 Input Pin 的 Channel 绑定
        /// </summary>
        public void UnbindOutputChannel(int outputPinIndex, NodeBase targetNode, int targetInputPinIndex)
        {
            if (targetNode == null) return;
            lock (_routeLock)
            {
                if (_routes.TryGetValue(outputPinIndex, out var list))
                {
                    list.Remove(new DownstreamSubscription(targetNode, targetInputPinIndex));
                }
            }
        }

        /// <summary>
        /// 向下游已绑定的所有节点 Channel 投递数据包 (零阻塞 TryWrite)
        /// </summary>
        protected void EmitPacket(int outputPinIndex, BlueprintPacket packet)
        {
            DownstreamSubscription[] targets;
            lock (_routeLock)
            {
                if (!_routes.TryGetValue(outputPinIndex, out var list) || list.Count == 0)
                {
                    return;
                }
                targets = list.ToArray();
            }

            foreach (var sub in targets)
            {
                sub.TargetNode.EnqueueInbound(sub.TargetInputPinIndex, packet);
            }
        }

        internal void EnqueueInbound(int inputPinIndex, BlueprintPacket packet)
        {
            _inboundChannel.Writer.TryWrite((inputPinIndex, packet));
        }

        private async Task ConsumeInboundLoopAsync(CancellationToken ct)
        {
            var reader = _inboundChannel.Reader;
            try
            {
                while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                {
                    while (reader.TryRead(out var item))
                    {
                        IncrementCounter();
                        await OnPacketReceivedAsync(item.InputPin, item.Packet, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                UpdateStatusOnUi($"异常: {ex.Message}", false);
            }
        }

        protected void IncrementCounter()
        {
            long val = Interlocked.Increment(ref _packetCounter);
            if (val == 1 || val % 10 == 0)
            {
                Application.Current?.Dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(PacketCounter)));
            }
        }

        protected void UpdateStatusOnUi(string badgeText, bool active)
        {
            var app = Application.Current;
            if (app == null)
            {
                StatusBadge = badgeText;
                IsActive = active;
                return;
            }
            app.Dispatcher.BeginInvoke(() =>
            {
                StatusBadge = badgeText;
                IsActive = active;
                OnPropertyChanged(nameof(PacketCounter));
            });
        }

        /// <summary>
        /// 当 USB 枚举出的真实 COM 端口列表变更时通知节点刷新下拉选项
        /// </summary>
        public virtual void RefreshAvailableComPorts(IReadOnlyList<string> comPorts)
        {
        }

        /// <summary>
        /// 当 USB 枚举出的真实烧录探针/端口列表变更时通知节点刷新下拉选项
        /// </summary>
        public virtual void RefreshAvailableBurnerPorts(IReadOnlyList<string> burnerPorts)
        {
        }

        /// <summary>
        /// 支持一键全局启动/停止源节点
        /// </summary>
        public virtual void SetStreamingEnabled(bool enabled)
        {
        }

        /// <summary>
        /// 提供当前节点的右键上下文快捷菜单 (如 COM 输入节点按需拆分 CH 通道)
        /// </summary>
        public virtual IReadOnlyList<NodeContextAction> GetContextMenuActions() => Array.Empty<NodeContextAction>();

        /// <summary>
        /// 导出该节点的专有 MCP 扩展元数据，用于将整个蓝图全量共享给 AI Agent
        /// </summary>
        public virtual Dictionary<string, object?> ExportMcpMetadata() => new();

        protected abstract Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct);

        protected void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));

        public virtual void Dispose()
        {
            _cts.Cancel();
            _inboundChannel.Writer.TryComplete();
            _cts.Dispose();
        }
    }
}
