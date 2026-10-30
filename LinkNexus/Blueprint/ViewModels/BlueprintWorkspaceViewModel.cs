using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO.Ports;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using LinkNexus.Blueprint.Core;
using LinkNexus.Blueprint.Nodes;

namespace LinkNexus.Blueprint.ViewModels
{
    /// <summary>
    /// 流式卡片蓝图工作台视图模型 (BlueprintWorkspaceViewModel)
    /// 统一编排所有 NodeBase 实例、三阶贝塞尔连线集合 (ConnectionViewModel)、底层 Channel 拓扑构建与一键预设场景切换
    /// </summary>
    public sealed class BlueprintWorkspaceViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly List<string> _availableComPorts = new();
        private string _workspaceHint = "拖拽节点右侧彩色输出圆点至目标左侧输入圆点即可磁吸建链；右键点击连线或端口可快速解绑。";
        private int _spawnCascadeIndex;

        public ObservableCollection<NodeBase> Nodes { get; } = new();
        public ObservableCollection<ConnectionViewModel> Connections { get; } = new();

        public string WorkspaceHint
        {
            get => _workspaceHint;
            set
            {
                if (_workspaceHint != value)
                {
                    _workspaceHint = value;
                    OnPropertyChanged();
                }
            }
        }

        public int NodeCount => Nodes.Count;
        public int ConnectionCount => Connections.Count;

        // 顶部节点生成工具栏命令 (涵盖全部核心流式卡片)
        public ICommand AddComInputNodeCommand { get; }
        public ICommand AddComOutputNodeCommand { get; }
        public ICommand AddMcpInputNodeCommand { get; }
        public ICommand AddPythonAlgoNodeCommand { get; }
        public ICommand AddVofaPlotNodeCommand { get; }
        public ICommand AddImu3DPlotNodeCommand { get; }
        public ICommand AddCsvSaveNodeCommand { get; }

        // 预设蓝图与全局流控命令
        public ICommand LoadDualShowcasePresetCommand { get; }
        public ICommand LoadVofaFilterPresetCommand { get; }
        public ICommand LoadImuAttitudePresetCommand { get; }
        public ICommand LoadMcpTelemetryPresetCommand { get; }
        public ICommand StartAllStreamsCommand { get; }
        public ICommand StopAllStreamsCommand { get; }
        public ICommand ClearAllWiresCommand { get; }

        public event Action? GraphRebuilt;
        public event PropertyChangedEventHandler? PropertyChanged;

        public BlueprintWorkspaceViewModel()
        {
            foreach (var p in SerialPort.GetPortNames())
            {
                _availableComPorts.Add(p);
            }

            Nodes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NodeCount));
            Connections.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ConnectionCount));

            AddComInputNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(36, 48);
                string defaultPort = _availableComPorts.Count > 0 ? _availableComPorts[0] : ComInputNode.SimVofaOption;
                RegisterNode(new ComInputNode(defaultPort, x, y));
            });

            AddComOutputNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(760, 320);
                RegisterNode(new ComOutputNode(x, y));
            });

            AddMcpInputNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(36, 330);
                RegisterNode(new McpInputNode(x, y));
            });

            AddPythonAlgoNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(360, 64);
                RegisterNode(new PythonAlgoNode(PythonAlgoNode.AlgoKalman, x, y));
            });

            AddVofaPlotNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(710, 40);
                RegisterNode(new VofaPlotNode(x, y));
            });

            AddImu3DPlotNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(710, 340);
                RegisterNode(new Imu3DPlotNode(x, y));
            });

            AddCsvSaveNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(1105, 64);
                RegisterNode(new CsvSaveNode(x, y));
            });

            LoadDualShowcasePresetCommand = new RelayCommand(LoadDualShowcasePreset);
            LoadVofaFilterPresetCommand = new RelayCommand(LoadVofaFilterPreset);
            LoadImuAttitudePresetCommand = new RelayCommand(LoadImuAttitudePreset);
            LoadMcpTelemetryPresetCommand = new RelayCommand(LoadMcpTelemetryPreset);

            StartAllStreamsCommand = new RelayCommand(() =>
            {
                foreach (var node in Nodes) node.SetStreamingEnabled(true);
                WorkspaceHint = "已启动当前画布全部采集源节点，Channel 异步管道高速推流中。";
            });

            StopAllStreamsCommand = new RelayCommand(() =>
            {
                foreach (var node in Nodes) node.SetStreamingEnabled(false);
                WorkspaceHint = "已停止全部采集源节点推流。";
            });

            ClearAllWiresCommand = new RelayCommand(() =>
            {
                ClearConnectionsOnly();
                WorkspaceHint = "已清空全部贝塞尔连线并解除底层 Channel 绑定。";
            });

            LoadDualShowcasePreset();
        }

        private (double X, double Y) NextSpawnPosition(double baseX, double baseY)
        {
            int offsetStep = (_spawnCascadeIndex++ % 6) * 26;
            return (baseX + offsetStep, baseY + offsetStep);
        }

        public void UpdateSystemComPorts(IEnumerable<string> comPorts)
        {
            _availableComPorts.Clear();
            foreach (var p in comPorts.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _availableComPorts.Add(p.Trim());
            }

            foreach (var node in Nodes)
            {
                node.RefreshAvailableComPorts(_availableComPorts);
            }
        }

        public NodeBase RegisterNode(NodeBase node)
        {
            node.RefreshAvailableComPorts(_availableComPorts);
            Nodes.Add(node);
            return node;
        }

        public void RemoveNode(NodeBase? node)
        {
            if (node == null) return;

            var related = Connections
                .Where(c => ReferenceEquals(c.SourcePin.OwnerNode, node) || ReferenceEquals(c.TargetPin.OwnerNode, node))
                .ToList();

            foreach (var conn in related)
            {
                RemoveConnection(conn);
            }

            Nodes.Remove(node);
            node.Dispose();
        }

        public ConnectionViewModel? ConnectPins(PinViewModel? first, PinViewModel? second)
        {
            if (first == null || second == null) return null;
            if (!first.IsCompatibleWith(second))
            {
                WorkspaceHint = "端口方向或信号类型不兼容，无法建立 Channel 绑定。";
                return null;
            }

            PinViewModel source = first.Direction == PinDirection.Output ? first : second;
            PinViewModel target = first.Direction == PinDirection.Input ? first : second;

            bool exists = Connections.Any(c => ReferenceEquals(c.SourcePin, source) && ReferenceEquals(c.TargetPin, target));
            if (exists)
            {
                return null;
            }

            var conn = new ConnectionViewModel(source, target);
            Connections.Add(conn);
            WorkspaceHint = $"已绑定管道: [{source.OwnerNode.Title}.{source.Name}] -> [{target.OwnerNode.Title}.{target.Name}]";
            return conn;
        }

        public void RemoveConnection(ConnectionViewModel? conn)
        {
            if (conn == null) return;
            conn.Disconnect();
            Connections.Remove(conn);
            RefreshPinConnectedFlags(conn.SourcePin);
            RefreshPinConnectedFlags(conn.TargetPin);
        }

        public void DisconnectPin(PinViewModel? pin)
        {
            if (pin == null) return;
            var matches = Connections
                .Where(c => ReferenceEquals(c.SourcePin, pin) || ReferenceEquals(c.TargetPin, pin))
                .ToList();

            if (matches.Count == 0) return;

            foreach (var conn in matches)
            {
                RemoveConnection(conn);
            }
            WorkspaceHint = $"已解绑端口 [{pin.OwnerNode.Title}.{pin.Name}] 的 {matches.Count} 条连线。";
        }

        private void RefreshPinConnectedFlags(PinViewModel pin)
        {
            pin.IsConnected = Connections.Any(c => ReferenceEquals(c.SourcePin, pin) || ReferenceEquals(c.TargetPin, pin));
        }

        private void ClearConnectionsOnly()
        {
            foreach (var conn in Connections.ToList())
            {
                conn.Disconnect();
            }
            Connections.Clear();

            foreach (var node in Nodes)
            {
                foreach (var p in node.InputPins) p.IsConnected = false;
                foreach (var p in node.OutputPins) p.IsConnected = false;
            }
        }

        private void ClearGraphAll()
        {
            ClearConnectionsOnly();
            foreach (var node in Nodes.ToList())
            {
                node.Dispose();
            }
            Nodes.Clear();
            _spawnCascadeIndex = 0;
        }

        /// <summary>
        /// 预设 1: 全景双链路蓝图 (VOFA+ 卡尔曼滤波示波 + IMU 三维姿态解算 + CSV 落盘)
        /// </summary>
        public void LoadDualShowcasePreset()
        {
            ClearGraphAll();

            var comVofa = RegisterNode(new ComInputNode(ComInputNode.SimVofaOption, 28, 30));
            var pyKalman = RegisterNode(new PythonAlgoNode(PythonAlgoNode.AlgoKalman, 342, 30));
            var vofaScope = RegisterNode(new VofaPlotNode(682, 24));
            var csvRecorder = RegisterNode(new CsvSaveNode(1078, 30));

            var comImu = RegisterNode(new ComInputNode(ComInputNode.SimImuOption, 28, 344));
            var pyImuSmooth = RegisterNode(new PythonAlgoNode(PythonAlgoNode.AlgoImuSolve, 342, 356));
            var imu3D = RegisterNode(new Imu3DPlotNode(682, 344));
            var comTx = RegisterNode(new ComOutputNode(1078, 344));

            ConnectPins(comVofa.OutputPins[0], pyKalman.InputPins[0]);
            ConnectPins(pyKalman.OutputPins[0], vofaScope.InputPins[0]);
            ConnectPins(vofaScope.OutputPins[0], csvRecorder.InputPins[0]);

            ConnectPins(comImu.OutputPins[0], pyImuSmooth.InputPins[0]);
            ConnectPins(pyImuSmooth.OutputPins[0], imu3D.InputPins[0]);
            ConnectPins(imu3D.OutputPins[0], comTx.InputPins[0]);

            WorkspaceHint = "已载入双链路全景蓝图：点击顶部 [启动全部源] 或单卡上的 [流式采集] 开关即可实时查看 VOFA+ 波形与 IMU 三维姿态。";
            GraphRebuilt?.Invoke();
        }

        /// <summary>
        /// 预设 2: VOFA+ 多通道卡尔曼滤波与落盘对比蓝图
        /// </summary>
        public void LoadVofaFilterPreset()
        {
            ClearGraphAll();

            string port = _availableComPorts.Count > 0 ? _availableComPorts[0] : ComInputNode.SimVofaOption;
            var comIn = RegisterNode(new ComInputNode(port, 36, 90));
            var pyAlgo = RegisterNode(new PythonAlgoNode(PythonAlgoNode.AlgoKalman, 358, 90));
            var scopeFiltered = RegisterNode(new VofaPlotNode(700, 40));
            var csvNode = RegisterNode(new CsvSaveNode(1096, 90));

            ConnectPins(comIn.OutputPins[0], pyAlgo.InputPins[0]);
            ConnectPins(pyAlgo.OutputPins[0], scopeFiltered.InputPins[0]);
            ConnectPins(scopeFiltered.OutputPins[0], csvNode.InputPins[0]);

            comIn.SetStreamingEnabled(true);
            WorkspaceHint = $"已启动 VOFA+ 实时滤波链路 (源端口: {port})。";
            GraphRebuilt?.Invoke();
        }

        /// <summary>
        /// 预设 3: IMU 三维空间姿态解算与三轴波形同步监视蓝图
        /// </summary>
        public void LoadImuAttitudePreset()
        {
            ClearGraphAll();

            var imuSource = RegisterNode(new ComInputNode(ComInputNode.SimImuOption, 36, 110));
            var pyImu = RegisterNode(new PythonAlgoNode(PythonAlgoNode.AlgoImuSolve, 356, 110));
            var view3D = RegisterNode(new Imu3DPlotNode(698, 28));
            var rpyScope = RegisterNode(new VofaPlotNode(698, 360));

            ConnectPins(imuSource.OutputPins[0], pyImu.InputPins[0]);
            ConnectPins(pyImu.OutputPins[0], view3D.InputPins[0]);
            ConnectPins(view3D.OutputPins[0], rpyScope.InputPins[0]);

            imuSource.SetStreamingEnabled(true);
            WorkspaceHint = "已启动 IMU 三维姿态与 R/P/Y 三轴欧拉角同步示波链路。";
            GraphRebuilt?.Invoke();
        }

        /// <summary>
        /// 预设 4: MCP Agent & ESP32 INA236 实时母线遥测流蓝图
        /// </summary>
        public void LoadMcpTelemetryPreset()
        {
            ClearGraphAll();

            var mcpNode = RegisterNode(new McpInputNode(40, 96));
            var scopeNode = RegisterNode(new VofaPlotNode(376, 48));
            var csvNode = RegisterNode(new CsvSaveNode(772, 96));

            ConnectPins(mcpNode.OutputPins[0], scopeNode.InputPins[0]);
            ConnectPins(scopeNode.OutputPins[0], csvNode.InputPins[0]);

            WorkspaceHint = "已绑定 MCP Agent / ESP32 INA236 6 通道电气遥测总线。";
            GraphRebuilt?.Invoke();
        }

        public void Dispose()
        {
            ClearGraphAll();
        }

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
