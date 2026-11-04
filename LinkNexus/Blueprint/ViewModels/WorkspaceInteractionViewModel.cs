using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.ViewModels
{
    /// <summary>
    /// Blender (Shift+A / 右键) 与 UE5 (拉线松手弹窗 / 拖拽切线) 画布交互视图模型 (WorkspaceInteractionViewModel)
    /// 集中管理模糊搜索、引脚类型兼容性过滤、节点实例化连线与连线切入逻辑
    /// </summary>
    public sealed class WorkspaceInteractionViewModel : INotifyPropertyChanged
    {
        private string _searchText = string.Empty;
        private NodeRegistryItem? _selectedItem;
        private PinViewModel? _activePinFilter;
        private Point _canvasSpawnLocation;
        private bool _isOpen;
        private string _pinFilterDescription = string.Empty;

        public ObservableCollection<NodeRegistryItem> FilteredItems { get; } = new();

        public event Action<NodeRegistryItem, Point, PinViewModel?>? RequestSpawnAndConnect;
        public event Action? RequestClose;

        public string SearchText
        {
            get => _searchText;
            set
            {
                if (_searchText != value)
                {
                    _searchText = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(HasSearchText));
                    ApplyFilter();
                }
            }
        }

        public bool HasSearchText => !string.IsNullOrWhiteSpace(_searchText);

        public NodeRegistryItem? SelectedItem
        {
            get => _selectedItem;
            set
            {
                if (!ReferenceEquals(_selectedItem, value))
                {
                    _selectedItem = value;
                    OnPropertyChanged();
                }
            }
        }

        public PinViewModel? ActivePinFilter
        {
            get => _activePinFilter;
            set
            {
                if (!ReferenceEquals(_activePinFilter, value))
                {
                    _activePinFilter = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsPinFilterActive));
                }
            }
        }

        public bool IsPinFilterActive => _activePinFilter != null;

        public string PinFilterDescription
        {
            get => _pinFilterDescription;
            set
            {
                if (_pinFilterDescription != value)
                {
                    _pinFilterDescription = value;
                    OnPropertyChanged();
                }
            }
        }

        public Point CanvasSpawnLocation
        {
            get => _canvasSpawnLocation;
            set
            {
                if (_canvasSpawnLocation != value)
                {
                    _canvasSpawnLocation = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsOpen
        {
            get => _isOpen;
            set
            {
                if (_isOpen != value)
                {
                    _isOpen = value;
                    OnPropertyChanged();
                }
            }
        }

        public string FilteredCountText => $"共 {FilteredItems.Count} 个可用节点";

        public WorkspaceInteractionViewModel()
        {
            ApplyFilter();
        }

        public void Open(Point canvasSpawnLocation, PinViewModel? dragOriginPin = null)
        {
            CanvasSpawnLocation = canvasSpawnLocation;
            ActivePinFilter = dragOriginPin;

            if (dragOriginPin != null)
            {
                string dirText = dragOriginPin.Direction == PinDirection.Output ? "输出" : "输入";
                PinFilterDescription = $"从 [{dragOriginPin.Name} ({dirText})] 拉线 · 自动筛选兼容输入/输出算子";
            }
            else
            {
                PinFilterDescription = string.Empty;
            }

            _searchText = string.Empty;
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(HasSearchText));

            ApplyFilter();
            IsOpen = true;
        }

        public void Close()
        {
            IsOpen = false;
            ActivePinFilter = null;
            RequestClose?.Invoke();
        }

        public void SelectNext()
        {
            if (FilteredItems.Count == 0) return;
            int idx = SelectedItem != null ? FilteredItems.IndexOf(SelectedItem) : -1;
            idx = (idx + 1) % FilteredItems.Count;
            SelectedItem = FilteredItems[idx];
        }

        public void SelectPrevious()
        {
            if (FilteredItems.Count == 0) return;
            int idx = SelectedItem != null ? FilteredItems.IndexOf(SelectedItem) : 0;
            idx = (idx - 1 + FilteredItems.Count) % FilteredItems.Count;
            SelectedItem = FilteredItems[idx];
        }

        public void ConfirmSelection()
        {
            if (SelectedItem == null && FilteredItems.Count > 0)
            {
                SelectedItem = FilteredItems[0];
            }

            if (SelectedItem != null)
            {
                var item = SelectedItem;
                var pt = CanvasSpawnLocation;
                var originPin = ActivePinFilter;
                Close();
                RequestSpawnAndConnect?.Invoke(item, pt, originPin);
            }
        }

        public void ApplyFilter()
        {
            var all = NodeRegistry.AllItems;
            var list = new List<NodeRegistryItem>();

            foreach (var item in all)
            {
                // 1. 引脚兼容性过滤 (UE5 Drop-Wire to Spawn 机制)
                if (ActivePinFilter != null)
                {
                    if (ActivePinFilter.Direction == PinDirection.Output)
                    {
                        // 从 Output 引脚拉出，寻找具备兼容 Input 引脚的卡片
                        bool compatible = item.InputPinTypes.Any(t => IsTypeCompatible(ActivePinFilter.DataType, t));
                        if (!compatible) continue;
                    }
                    else
                    {
                        // 从 Input 引脚反向拉出，寻找具备兼容 Output 引脚的卡片
                        bool compatible = item.OutputPinTypes.Any(t => IsTypeCompatible(t, ActivePinFilter.DataType));
                        if (!compatible) continue;
                    }
                }

                // 2. 模糊搜索匹配 (英文、中文、缩写拼音如 jf/km/+)
                if (item.Matches(_searchText))
                {
                    list.Add(item);
                }
            }

            FilteredItems.Clear();
            foreach (var item in list)
            {
                FilteredItems.Add(item);
            }

            SelectedItem = FilteredItems.FirstOrDefault();
            OnPropertyChanged(nameof(FilteredCountText));
        }

        private static bool IsTypeCompatible(PinDataType source, PinDataType target)
        {
            if (source == PinDataType.Any || target == PinDataType.Any) return true;
            if (source == target) return true;
            if (source == PinDataType.FloatStream && target == PinDataType.FloatStream) return true;
            if (source == PinDataType.Vector3Stream && target == PinDataType.Vector3Stream) return true;
            return false;
        }

        #region Blender 式拖拽卡片切入现有连线 (Auto-Insert Node on Wire)

        /// <summary>
        /// 判定节点是否满足 Blender “拖拽切线” 条件并自动重构连线: A -> B 变为 A -> NewNode -> B
        /// </summary>
        public static bool TryAutoInsertNodeOnWire(
            NodeBase node,
            IReadOnlyList<ConnectionViewModel> connections,
            Action<ConnectionViewModel> removeConnection,
            Action<PinViewModel, PinViewModel> addConnection,
            double tolerance = 36.0)
        {
            if (node == null || connections == null || connections.Count == 0) return false;

            // 必须拥有至少 1 个输入引脚和 1 个输出引脚
            if (node.InputPins.Count == 0 || node.OutputPins.Count == 0) return false;

            // 如果节点的主输入或主输出引脚已处于连线状态，则跳过避免意图冲突
            if (node.InputPins[0].IsConnected && node.OutputPins[0].IsConnected) return false;

            // 计算节点在画布上的中心测试点与包围盒
            double nodeWidth = node.CardWidth > 0 ? node.CardWidth : 260.0;
            double nodeHeight = 150.0;
            Point nodeCenter = new(node.X + nodeWidth * 0.5, node.Y + nodeHeight * 0.5);

            double bestDistSq = tolerance * tolerance;
            ConnectionViewModel? hitConn = null;

            foreach (var conn in connections)
            {
                // 不能是自连卡片的连线
                if (ReferenceEquals(conn.SourcePin?.OwnerNode, node) ||
                    ReferenceEquals(conn.TargetPin?.OwnerNode, node))
                {
                    continue;
                }

                if (conn.SourcePin == null || conn.TargetPin == null) continue;

                // 采样测试贝塞尔连线距离
                Point p0 = conn.StartPoint;
                Point p3 = conn.EndPoint;
                double dx = p3.X - p0.X;
                double tangent = dx >= 0
                    ? Math.Clamp(dx * 0.52, 58.0, 260.0)
                    : Math.Clamp(Math.Abs(dx) * 0.45 + 75.0, 85.0, 280.0);
                Point c1 = new(p0.X + tangent, p0.Y);
                Point c2 = new(p3.X - tangent, p3.Y);

                const int samples = 24;
                Point prev = p0;
                for (int s = 1; s <= samples; s++)
                {
                    double t = s / (double)samples;
                    Point curr = EvaluateCubicBezier(p0, c1, c2, p3, t);
                    double distSq = DistanceToSegmentSquared(nodeCenter, prev, curr);
                    if (distSq <= bestDistSq)
                    {
                        bestDistSq = distSq;
                        hitConn = conn;
                    }
                    prev = curr;
                }
            }

            if (hitConn != null && hitConn.SourcePin != null && hitConn.TargetPin != null)
            {
                var src = hitConn.SourcePin;
                var tgt = hitConn.TargetPin;

                // 寻找新节点上第一个与 src 兼容的输入引脚
                var inPin = node.InputPins.FirstOrDefault(p => !p.IsConnected && src.IsCompatibleWith(p))
                            ?? node.InputPins.FirstOrDefault(p => src.IsCompatibleWith(p));

                // 寻找新节点上第一个与 tgt 兼容的输出引脚
                var outPin = node.OutputPins.FirstOrDefault(p => !p.IsConnected && p.IsCompatibleWith(tgt))
                             ?? node.OutputPins.FirstOrDefault(p => p.IsCompatibleWith(tgt));

                if (inPin != null && outPin != null)
                {
                    // 断开原连线
                    removeConnection(hitConn);

                    // 接入两端构建 A -> Node -> B
                    addConnection(src, inPin);
                    addConnection(outPin, tgt);
                    return true;
                }
            }

            return false;
        }

        private static Point EvaluateCubicBezier(Point p0, Point c1, Point c2, Point p3, double t)
        {
            double u = 1.0 - t;
            double tt = t * t;
            double uu = u * u;
            double uuu = uu * u;
            double ttt = tt * t;

            double x = uuu * p0.X + 3 * uu * t * c1.X + 3 * u * tt * c2.X + ttt * p3.X;
            double y = uuu * p0.Y + 3 * uu * t * c1.Y + 3 * u * tt * c2.Y + ttt * p3.Y;
            return new Point(x, y);
        }

        private static double DistanceToSegmentSquared(Point p, Point a, Point b)
        {
            double vx = b.X - a.X;
            double vy = b.Y - a.Y;
            double wx = p.X - a.X;
            double wy = p.Y - a.Y;

            double lenSq = vx * vx + vy * vy;
            if (lenSq <= 1e-6) return wx * wx + wy * wy;

            double t = Math.Clamp((wx * vx + wy * vy) / lenSq, 0.0, 1.0);
            double projX = a.X + t * vx;
            double projY = a.Y + t * vy;
            double dx = p.X - projX;
            double dy = p.Y - projY;
            return dx * dx + dy * dy;
        }

        #endregion

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
