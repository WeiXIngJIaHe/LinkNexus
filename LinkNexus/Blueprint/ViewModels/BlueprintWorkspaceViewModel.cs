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
    /// 1. 严格使用真实 USB 枚举端口，零虚拟占位符端口；
    /// 2. 支持动态端口拆分/合并同步、拓扑自动排版 (AutoLayout)；
    /// 3. 将整个蓝图（节点、坐标、连线、MCP 采样窗、外部隔离 Python 白名单与烧录仓）全量共享为 MCP 数据，供 AI Agent 自动化摆放节点与连线。
    /// </summary>
    public sealed class BlueprintWorkspaceViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly List<string> _availableComPorts = new();
        private readonly List<string> _availableBurnerPorts = new();
        private string _workspaceHint = "滚轮可自由缩放画布；右键 COM 输入卡片可按需拆分 CH 通道；整个蓝图已实时共享为 MCP 数据供 Agent 自动编排与调参。";
        private double _canvasZoom = 1.0;
        private int _spawnCascadeIndex;

        public ObservableCollection<NodeBase> Nodes { get; } = new();
        public ObservableCollection<ConnectionViewModel> Connections { get; } = new();

        public IReadOnlyList<string> AvailableComPorts => _availableComPorts;
        public IReadOnlyList<string> AvailableBurnerPorts => _availableBurnerPorts;

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

        public double CanvasZoom
        {
            get => _canvasZoom;
            set
            {
                if (Math.Abs(_canvasZoom - value) > 0.001)
                {
                    _canvasZoom = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(CanvasZoomPercentText));
                }
            }
        }

        public string CanvasZoomPercentText => $"{Math.Round(_canvasZoom * 100)}%";

        public int NodeCount => Nodes.Count;
        public int ConnectionCount => Connections.Count;

        // 顶部节点生成工具栏命令
        public ICommand AddComInputNodeCommand { get; }
        public ICommand AddComOutputNodeCommand { get; }
        public ICommand AddMcpInputNodeCommand { get; }
        public ICommand AddPythonAlgoNodeCommand { get; }
        public ICommand AddVofaPlotNodeCommand { get; }
        public ICommand AddImu3DPlotNodeCommand { get; }
        public ICommand AddCsvSaveNodeCommand { get; }
        public ICommand AddFlashPortNodeCommand { get; }
        public ICommand AddDspFilterNodeCommand { get; }
        public ICommand AddPhaseSpaceNodeCommand { get; }

        // 兼容别名与快速添加绑定
        public ICommand AddVofaScopeNodeCommand => AddVofaPlotNodeCommand;
        public ICommand AddImu3DNodeCommand => AddImu3DPlotNodeCommand;
        public ICommand AddPythonNodeCommand => AddPythonAlgoNodeCommand;
        public ICommand AddCsvLoggerNodeCommand => AddCsvSaveNodeCommand;
        public ICommand AddMathOpNodeCommand => AddDspFilterNodeCommand;
        public ICommand AddEsp32SerialNodeCommand => AddComInputNodeCommand;
        public ICommand AddMcpBridgeNodeCommand => AddMcpInputNodeCommand;

        // 自动排版、预设蓝图与全局流控命令
        public ICommand AutoLayoutCommand { get; }
        public ICommand LoadDualShowcasePresetCommand { get; }
        public ICommand LoadVofaFilterPresetCommand { get; }
        public ICommand LoadImuAttitudePresetCommand { get; }
        public ICommand LoadMcpTelemetryPresetCommand { get; }
        public ICommand StartAllStreamsCommand { get; }
        public ICommand StopAllStreamsCommand { get; }
        public ICommand ClearAllWiresCommand { get; }

        public event Action? GraphRebuilt;
        public event Action<NodeBase, int, BlueprintPacket>? GlobalPacketEmitted;
        public event PropertyChangedEventHandler? PropertyChanged;

        public BlueprintWorkspaceViewModel()
        {
            Nodes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(NodeCount));
            Connections.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ConnectionCount));

            AddComInputNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(40, 52);
                string defaultPort = _availableComPorts.Count > 0 ? _availableComPorts[0] : string.Empty;
                RegisterNode(new ComInputNode(defaultPort, x, y));
            });

            AddComOutputNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(1090, 340);
                string defaultPort = _availableComPorts.Count > 0 ? _availableComPorts[0] : string.Empty;
                RegisterNode(new ComOutputNode(defaultPort, x, y));
            });

            AddMcpInputNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(370, 340);
                RegisterNode(new McpInputNode($"CH_TAP_{Nodes.OfType<McpInputNode>().Count() + 1}", x, y));
            });

            AddPythonAlgoNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(370, 56);
                RegisterNode(new PythonAlgoNode(PythonAlgoNode.AlgoKalman, x, y));
            });

            AddVofaPlotNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(730, 44);
                RegisterNode(new VofaPlotNode(x, y));
            });

            AddImu3DPlotNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(730, 350);
                RegisterNode(new Imu3DPlotNode(x, y));
            });

            AddCsvSaveNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(1130, 56);
                RegisterNode(new CsvSaveNode(x, y));
            });

            AddFlashPortNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(1130, 340);
                string defaultBurner = _availableBurnerPorts.FirstOrDefault() ?? _availableComPorts.FirstOrDefault() ?? string.Empty;
                RegisterNode(new FlashPortNode(defaultBurner, x, y));
            });

            AddDspFilterNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(460, 200);
                RegisterNode(new DspFilterNode(x, y));
            });

            AddPhaseSpaceNodeCommand = new RelayCommand(() =>
            {
                var (x, y) = NextSpawnPosition(740, 460);
                RegisterNode(new PhaseSpace3DPlotNode(x, y));
            });

            AutoLayoutCommand = new RelayCommand(() =>
            {
                AutoLayoutNodes();
                WorkspaceHint = "已按信号流向拓扑层级自动整齐摆放全部蓝图节点。";
            });

            LoadDualShowcasePresetCommand = new RelayCommand(LoadDualShowcasePreset);
            LoadVofaFilterPresetCommand = new RelayCommand(LoadVofaFilterPreset);
            LoadImuAttitudePresetCommand = new RelayCommand(LoadImuAttitudePreset);
            LoadMcpTelemetryPresetCommand = new RelayCommand(LoadMcpTelemetryPreset);

            StartAllStreamsCommand = new RelayCommand(() =>
            {
                foreach (var node in Nodes) node.SetStreamingEnabled(true);
                WorkspaceHint = _availableComPorts.Count > 0
                    ? "已向画布采集源节点下发启动指令。"
                    : "当前未枚举到真实 USB 串口，COM 节点保持待命；MCP 节点正常监听。";
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
            int offsetStep = (_spawnCascadeIndex++ % 8) * 28;
            return (baseX + offsetStep, baseY + offsetStep);
        }

        public void UpdateSystemComPorts(IEnumerable<string> comPorts)
        {
            UpdateEnumeratedUsbPorts(comPorts, _availableBurnerPorts.ToList());
        }

        public void UpdateEnumeratedUsbPorts(IEnumerable<string> comPorts, IEnumerable<string> burnerPorts)
        {
            _availableComPorts.Clear();
            foreach (var p in (comPorts ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s) && s != "--")
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _availableComPorts.Add(p.Trim());
            }

            _availableBurnerPorts.Clear();
            foreach (var b in (burnerPorts ?? Enumerable.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s) && s != "--")
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _availableBurnerPorts.Add(b.Trim());
            }

            foreach (var node in Nodes)
            {
                node.RefreshAvailableComPorts(_availableComPorts);
                node.RefreshAvailableBurnerPorts(_availableBurnerPorts);
            }
        }

        public NodeBase RegisterNode(NodeBase node)
        {
            node.PinRemoving += OnNodePinRemoving;
            node.PinsChanged += OnNodePinsChanged;
            node.PacketEmitted += (src, pin, pkt) => GlobalPacketEmitted?.Invoke(src, pin, pkt);
            node.RefreshAvailableComPorts(_availableComPorts);
            node.RefreshAvailableBurnerPorts(_availableBurnerPorts);
            Nodes.Add(node);
            GraphRebuilt?.Invoke();
            return node;
        }

        private void OnNodePinRemoving(PinViewModel removingPin)
        {
            DisconnectPin(removingPin);
        }

        private void OnNodePinsChanged(NodeBase _)
        {
            GraphRebuilt?.Invoke();
        }

        public void RemoveNode(NodeBase? node)
        {
            if (node == null) return;

            node.PinRemoving -= OnNodePinRemoving;
            node.PinsChanged -= OnNodePinsChanged;

            var related = Connections
                .Where(c => ReferenceEquals(c.SourcePin.OwnerNode, node) || ReferenceEquals(c.TargetPin.OwnerNode, node))
                .ToList();

            foreach (var conn in related)
            {
                RemoveConnection(conn);
            }

            Nodes.Remove(node);
            node.Dispose();
            GraphRebuilt?.Invoke();
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
                return Connections.First(c => ReferenceEquals(c.SourcePin, source) && ReferenceEquals(c.TargetPin, target));
            }

            var conn = new ConnectionViewModel(source, target);
            Connections.Add(conn);
            WorkspaceHint = $"已绑定管道: [{source.OwnerNode.Title}.{source.Name}] -> [{target.OwnerNode.Title}.{target.Name}]";
            GraphRebuilt?.Invoke();
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

        public void ClearConnectionsOnly()
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
            GraphRebuilt?.Invoke();
        }

        public void ClearGraphAll()
        {
            ClearConnectionsOnly();
            foreach (var node in Nodes.ToList())
            {
                node.PinRemoving -= OnNodePinRemoving;
                node.PinsChanged -= OnNodePinsChanged;
                node.Dispose();
            }
            Nodes.Clear();
            _spawnCascadeIndex = 0;
            GraphRebuilt?.Invoke();
        }

        #region MCP 自动化节点工厂、拓扑自动摆放、连线编排与全量蓝图快照共享

        public NodeBase? FindNode(string? nodeIdOrTag)
        {
            if (string.IsNullOrWhiteSpace(nodeIdOrTag)) return null;
            string q = nodeIdOrTag.Trim();

            var byId = Nodes.FirstOrDefault(n => string.Equals(n.NodeId, q, StringComparison.OrdinalIgnoreCase));
            if (byId != null) return byId;

            var byTag = Nodes.OfType<McpInputNode>().FirstOrDefault(m => string.Equals(m.AnalysisTag, q, StringComparison.OrdinalIgnoreCase));
            if (byTag != null) return byTag;

            return Nodes.FirstOrDefault(n => string.Equals(n.Title, q, StringComparison.OrdinalIgnoreCase));
        }

        public NodeBase? CreateAndRegisterNodeByType(
            string nodeType,
            double x,
            double y,
            string? port = null,
            string? algoFile = null,
            string? tag = null)
        {
            string key = (nodeType ?? string.Empty).Trim().ToLowerInvariant();
            string defaultCom = !string.IsNullOrWhiteSpace(port)
                ? port!.Trim()
                : (_availableComPorts.FirstOrDefault() ?? string.Empty);
            string defaultBurner = !string.IsNullOrWhiteSpace(port)
                ? port!.Trim()
                : (_availableBurnerPorts.FirstOrDefault() ?? _availableComPorts.FirstOrDefault() ?? string.Empty);

            NodeBase? created = key switch
            {
                "com_input" or "cominput" or "com_in" => new ComInputNode(defaultCom, x, y),
                "com_output" or "comoutput" or "com_out" => new ComOutputNode(defaultCom, x, y),
                "mcp_node" or "mcp" or "mcp_input" or "mcp_agent" => new McpInputNode(tag ?? $"MCP_TAP_{Nodes.OfType<McpInputNode>().Count() + 1}", x, y),
                "python_algo" or "python" or "py_algo" => new PythonAlgoNode(string.IsNullOrWhiteSpace(algoFile) ? PythonAlgoNode.AlgoKalman : algoFile!.Trim(), x, y),
                "vofa_plot" or "vofa" or "plot" => new VofaPlotNode(x, y),
                "imu_3d" or "imu" or "3d_plot" => new Imu3DPlotNode(x, y),
                "csv_save" or "csv" => new CsvSaveNode(x, y),
                "flash_port" or "flash" or "burner_port" => new FlashPortNode(defaultBurner, x, y),
                "dsp_filter" or "dsp" or "nwaves" => new DspFilterNode(x, y),
                "phase_space_3d" or "phase_space" or "phase3d" => new PhaseSpace3DPlotNode(x, y),
                _ => null
            };

            return created != null ? RegisterNode(created) : null;
        }

        public bool TryConnectByPinIndex(
            string sourceNodeId,
            int sourceOutputPinIndex,
            string targetNodeId,
            int targetInputPinIndex,
            out string resultMessage)
        {
            var srcNode = FindNode(sourceNodeId);
            if (srcNode == null)
            {
                resultMessage = $"未找到源节点 '{sourceNodeId}'。";
                return false;
            }

            var dstNode = FindNode(targetNodeId);
            if (dstNode == null)
            {
                resultMessage = $"未找到目标节点 '{targetNodeId}'。";
                return false;
            }

            if (sourceOutputPinIndex < 0 || sourceOutputPinIndex >= srcNode.OutputPins.Count)
            {
                resultMessage = $"源节点 [{srcNode.Title}] 输出端口索引 {sourceOutputPinIndex} 越界 (共 {srcNode.OutputPins.Count} 个输出端口)。";
                return false;
            }

            if (targetInputPinIndex < 0 || targetInputPinIndex >= dstNode.InputPins.Count)
            {
                resultMessage = $"目标节点 [{dstNode.Title}] 输入端口索引 {targetInputPinIndex} 越界 (共 {dstNode.InputPins.Count} 个输入端口)。";
                return false;
            }

            var conn = ConnectPins(srcNode.OutputPins[sourceOutputPinIndex], dstNode.InputPins[targetInputPinIndex]);
            if (conn == null)
            {
                resultMessage = "端口类型不兼容，连线失败。";
                return false;
            }

            resultMessage = $"已连线 [{srcNode.Title}.{srcNode.OutputPins[sourceOutputPinIndex].Name}] -> [{dstNode.Title}.{dstNode.InputPins[targetInputPinIndex].Name}]";
            return true;
        }

        /// <summary>
        /// 按信号流拓扑层级自动化摆放所有蓝图节点坐标，避免卡片重叠
        /// </summary>
        public void AutoLayoutNodes(double startX = 44.0, double startY = 48.0, double columnSpacing = 368.0, double rowSpacing = 324.0)
        {
            if (Nodes.Count == 0) return;

            var layerMap = new Dictionary<NodeBase, int>();
            foreach (var n in Nodes)
            {
                layerMap[n] = 0;
            }

            // 基于有向边松弛计算最长拓扑层级 (上限 Nodes.Count 次以免疫潜在环路)
            for (int iter = 0; iter < Nodes.Count; iter++)
            {
                bool changed = false;
                foreach (var conn in Connections)
                {
                    var u = conn.SourcePin.OwnerNode;
                    var v = conn.TargetPin.OwnerNode;
                    if (layerMap.TryGetValue(u, out int lu) && layerMap.TryGetValue(v, out int lv))
                    {
                        if (lv < lu + 1)
                        {
                            layerMap[v] = lu + 1;
                            changed = true;
                        }
                    }
                }
                if (!changed) break;
            }

            var columns = layerMap
                .GroupBy(kv => kv.Value)
                .OrderBy(g => g.Key)
                .ToList();

            foreach (var col in columns)
            {
                int colIdx = col.Key;
                double x = startX + colIdx * columnSpacing;
                var orderedNodes = col
                    .Select(kv => kv.Key)
                    .OrderBy(n => n.Y)
                    .ThenBy(n => n.Title, StringComparer.Ordinal)
                    .ToList();

                for (int rowIdx = 0; rowIdx < orderedNodes.Count; rowIdx++)
                {
                    orderedNodes[rowIdx].X = x;
                    orderedNodes[rowIdx].Y = startY + rowIdx * rowSpacing;
                }
            }

            GraphRebuilt?.Invoke();
        }

        /// <summary>
        /// 将当前整个蓝图画布全量序列化为 MCP 共享数据结构
        /// </summary>
        public Dictionary<string, object?> ExportMcpGraphSnapshot(bool includeMcpSamples = true)
        {
            var pyCatalog = PythonAlgoRepoService.Instance.ListWhitelistedAlgorithms()
                .Select(a => new
                {
                    fileName = a.FileName,
                    title = a.DisplayTitle,
                    description = a.Description,
                    defaultScalarParams = a.DefaultParams
                })
                .ToList();

            var fwVault = FirmwareVaultService.Instance.GetStoredImages()
                .Select(f => new
                {
                    fileName = f.FileName,
                    format = f.Format,
                    payloadBytes = f.PayloadBytes,
                    baseAddress = f.BaseAddress,
                    crc32 = f.Crc32Hex
                })
                .ToList();

            var serializedNodes = Nodes.Select(n => new Dictionary<string, object?>
            {
                ["nodeId"] = n.NodeId,
                ["nodeType"] = n.NodeTypeKey,
                ["title"] = n.Title,
                ["category"] = n.CategoryName,
                ["x"] = Math.Round(n.X, 1),
                ["y"] = Math.Round(n.Y, 1),
                ["cardWidth"] = n.CardWidth,
                ["isActive"] = n.IsActive,
                ["statusBadge"] = n.StatusBadge,
                ["packetCounter"] = n.PacketCounter,
                ["inputPins"] = n.InputPins.Select(p => new
                {
                    pinIndex = p.PinIndex,
                    name = p.Name,
                    dataType = p.DataType.ToString(),
                    isConnected = p.IsConnected
                }).ToList(),
                ["outputPins"] = n.OutputPins.Select(p => new
                {
                    pinIndex = p.PinIndex,
                    name = p.Name,
                    dataType = p.DataType.ToString(),
                    isConnected = p.IsConnected
                }).ToList(),
                ["parameters"] = n.Parameters.Select(param => new
                {
                    key = param.Key,
                    label = param.Label,
                    kind = param.Kind.ToString(),
                    value = param.Value,
                    boolValue = param.BoolValue,
                    options = param.Options.ToList()
                }).ToList(),
                ["metadata"] = (n is McpInputNode mcp && !includeMcpSamples)
                    ? mcp.BuildAnalysisReport(maxRecentSamples: 4)
                    : n.ExportMcpMetadata()
            }).ToList();

            var serializedConnections = Connections.Select(c => new
            {
                connectionId = c.ConnectionId,
                sourceNodeId = c.SourcePin.OwnerNode.NodeId,
                sourceNodeTitle = c.SourcePin.OwnerNode.Title,
                sourcePinIndex = c.SourcePin.PinIndex,
                sourcePinName = c.SourcePin.Name,
                targetNodeId = c.TargetPin.OwnerNode.NodeId,
                targetNodeTitle = c.TargetPin.OwnerNode.Title,
                targetPinIndex = c.TargetPin.PinIndex,
                targetPinName = c.TargetPin.Name
            }).ToList();

            return new Dictionary<string, object?>
            {
                ["version"] = VersionInfo.CurrentVersion,
                ["canvas"] = new
                {
                    width = 4200,
                    height = 2800,
                    zoom = Math.Round(_canvasZoom, 3)
                },
                ["enumeratedUsbComPorts"] = _availableComPorts.ToList(),
                ["enumeratedUsbBurnerPorts"] = _availableBurnerPorts.ToList(),
                ["externalPythonAlgoRepo"] = new
                {
                    isolatedFromSoftwareAndGit = true,
                    whitelistedAlgorithms = pyCatalog
                },
                ["firmwareHexBinVault"] = fwVault,
                ["nodeCount"] = Nodes.Count,
                ["connectionCount"] = Connections.Count,
                ["nodes"] = serializedNodes,
                ["connections"] = serializedConnections
            };
        }

        #endregion

        #region 预设拓扑场景 (全部严格绑定真实 USB 枚举端口，零虚拟占位符端口)

        public void LoadDualShowcasePreset()
        {
            ClearGraphAll();

            string port0 = _availableComPorts.Count > 0 ? _availableComPorts[0] : string.Empty;
            string port1 = _availableComPorts.Count > 1 ? _availableComPorts[1] : port0;
            string burner0 = _availableBurnerPorts.FirstOrDefault() ?? port0;

            var comIn = RegisterNode(new ComInputNode(port0, 34, 38));
            var mcpTap = RegisterNode(new McpInputNode("RAW_CH_ANALYZE", 374, 38));
            var pyKalman = RegisterNode(new PythonAlgoNode(PythonAlgoNode.AlgoKalman, 718, 38));
            var vofaScope = RegisterNode(new VofaPlotNode(1072, 28));
            var csvRecorder = RegisterNode(new CsvSaveNode(1476, 38));

            var comImu = RegisterNode(new ComInputNode(port1, 34, 392));
            var pyImuSmooth = RegisterNode(new PythonAlgoNode(PythonAlgoNode.AlgoImuSolve, 374, 392));
            var imu3D = RegisterNode(new Imu3DPlotNode(718, 382));
            var flashCard = RegisterNode(new FlashPortNode(burner0, 1106, 392));

            ConnectPins(comIn.OutputPins[0], mcpTap.InputPins[0]);
            ConnectPins(mcpTap.OutputPins[0], pyKalman.InputPins[0]);
            ConnectPins(pyKalman.OutputPins[0], vofaScope.InputPins[0]);
            ConnectPins(vofaScope.OutputPins[0], csvRecorder.InputPins[0]);

            ConnectPins(comImu.OutputPins[0], pyImuSmooth.InputPins[0]);
            ConnectPins(pyImuSmooth.OutputPins[0], imu3D.InputPins[0]);

            WorkspaceHint = _availableComPorts.Count > 0
                ? $"已载入全景蓝图 (已匹配 USB 枚举端口 {port0})：右键 COM 输入卡片可拆分 CH 通道，鼠标滚轮可自由缩放画布。"
                : "已载入全景蓝图：当前未插入物理 USB 串口，接入硬件后下拉框自动刷新真实端口（零虚拟占位端口）。";
            GraphRebuilt?.Invoke();
        }

        public void LoadVofaFilterPreset()
        {
            ClearGraphAll();

            string port = _availableComPorts.Count > 0 ? _availableComPorts[0] : string.Empty;
            var comIn = RegisterNode(new ComInputNode(port, 38, 84));
            var mcpTap = RegisterNode(new McpInputNode("VOFA_NOISE_TAP", 376, 84));
            var pyAlgo = RegisterNode(new PythonAlgoNode(PythonAlgoNode.AlgoKalman, 716, 84));
            var scopeFiltered = RegisterNode(new VofaPlotNode(1068, 42));
            var csvNode = RegisterNode(new CsvSaveNode(1472, 84));

            ConnectPins(comIn.OutputPins[0], mcpTap.InputPins[0]);
            ConnectPins(mcpTap.OutputPins[0], pyAlgo.InputPins[0]);
            ConnectPins(pyAlgo.OutputPins[0], scopeFiltered.InputPins[0]);
            ConnectPins(scopeFiltered.OutputPins[0], csvNode.InputPins[0]);

            WorkspaceHint = !string.IsNullOrEmpty(port)
                ? $"已载入 VOFA+ 采样分析与外部 Python 滤波链路 (指定端口: {port})。"
                : "已载入 VOFA+ 滤波链路：请插入并指定真实 USB 枚举端口后开启采集。";
            GraphRebuilt?.Invoke();
        }

        public void LoadImuAttitudePreset()
        {
            ClearGraphAll();

            string port = _availableComPorts.Count > 0 ? _availableComPorts[0] : string.Empty;
            var imuSource = RegisterNode(new ComInputNode(port, 40, 100));
            var pyImu = RegisterNode(new PythonAlgoNode(PythonAlgoNode.AlgoImuSolve, 382, 100));
            var view3D = RegisterNode(new Imu3DPlotNode(734, 34));
            var rpyScope = RegisterNode(new VofaPlotNode(734, 380));

            ConnectPins(imuSource.OutputPins[0], pyImu.InputPins[0]);
            ConnectPins(pyImu.OutputPins[0], view3D.InputPins[0]);
            ConnectPins(view3D.OutputPins[0], rpyScope.InputPins[0]);

            WorkspaceHint = !string.IsNullOrEmpty(port)
                ? $"已载入 IMU 三维姿态链路 (指定端口: {port})。"
                : "已载入 IMU 三维姿态链路：请插入并指定真实 USB 枚举端口后开启采集。";
            GraphRebuilt?.Invoke();
        }

        public void LoadMcpTelemetryPreset()
        {
            ClearGraphAll();

            string burner = _availableBurnerPorts.FirstOrDefault() ?? _availableComPorts.FirstOrDefault() ?? string.Empty;
            var mcpNode = RegisterNode(new McpInputNode("ESP32_INA236_TAP", 42, 78));
            var pyKalman = RegisterNode(new PythonAlgoNode(PythonAlgoNode.AlgoKalman, 384, 78));
            var scopeNode = RegisterNode(new VofaPlotNode(736, 42));
            var flashNode = RegisterNode(new FlashPortNode(burner, 384, 396));

            ConnectPins(mcpNode.OutputPins[0], pyKalman.InputPins[0]);
            ConnectPins(pyKalman.OutputPins[0], scopeNode.InputPins[0]);

            WorkspaceHint = "已载入 MCP Agent 数据分析、外部隔离 Python 算子与烧录端口协同蓝图。";
            GraphRebuilt?.Invoke();
        }

        #endregion

        public void Dispose()
        {
            ClearGraphAll();
        }

        private void OnPropertyChanged([CallerMemberName] string? prop = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
    }
}
