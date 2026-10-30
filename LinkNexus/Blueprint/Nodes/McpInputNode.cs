using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// MCP 数据分析与双向共享节点 (McpInputNode / MCP Node)
    /// 1. 左侧提供 [待分析流 IN] 输入端口：可将任意 COM 拆分通道或 Python 算子输出连入此节点，指定该路数据交由 AI Agent 采样分析；
    /// 2. 内置 256 帧环形采样缓存与实时信号统计器 (均值/标准差/峰峰值/RMS/高频抖动度/采样率)，供 Agent 调用 blueprint_analyze_mcp_node 读取并自动调优 Python 算法参数；
    /// 3. 右侧提供 [MCP 通道流] 与 [MCP 原始帧] 输出端口：支持零损耗透传上游信号，同时接收 Agent push_blueprint_data 注入流与 ESP32 INA236 遥测流。
    /// </summary>
    public sealed class McpInputNode : NodeBase
    {
        private const string IconSvg = "M12,3 L20,7.5 V16.5 L12,21 L4,16.5 V7.5 Z M12,8 A4,4 0 1,0 12,16 A4,4 0 1,0 12,8";
        private const int MaxBufferedSamples = 256;

        private const string ModeTapAndBus = "指定输入流分析 + 总线透传";
        private const string ModeTapOnly = "仅采样输入流供 Agent 分析";
        private const string ModeBusOnly = "MCP Agent 推流 + ESP32 遥测";

        private readonly NodeParameter _tagParam;
        private readonly NodeParameter _sourceModeParam;
        private readonly NodeParameter _listenToggleParam;
        private readonly NodeParameter _clearBtnParam;
        private readonly NodeParameter _analysisBadgeParam;

        private readonly object _bufferLock = new();
        private readonly Queue<BlueprintPacket> _analysisBuffer = new(MaxBufferedSamples + 4);
        private string _lastAgentNote = string.Empty;

        public override string NodeTypeKey => "mcp_node";
        public override double CardWidth => 292.0;

        public string AnalysisTag
        {
            get => _tagParam.Value;
            set => _tagParam.Value = value;
        }

        public McpInputNode(string initialTag = "CH_ANALYZE_1", double x = 36, double y = 340)
            : base("MCP 数据与分析节点", "Agent 采样分析 · 总线共享", "#14B8A6", IconSvg, x, y)
        {
            AddInputPin("待分析流 IN", PinDataType.Any);
            AddOutputPin("MCP 通道流", PinDataType.ChannelStream);
            AddOutputPin("MCP 原始帧", PinDataType.RawFrame);

            _tagParam = new NodeParameter("tag", "分析标识", NodeParamKind.Text, string.IsNullOrWhiteSpace(initialTag) ? "CH_ANALYZE_1" : initialTag);

            _sourceModeParam = new NodeParameter("mode", "节点模式", NodeParamKind.Combo, ModeTapAndBus);
            _sourceModeParam.Options.Add(ModeTapAndBus);
            _sourceModeParam.Options.Add(ModeTapOnly);
            _sourceModeParam.Options.Add(ModeBusOnly);

            _listenToggleParam = new NodeParameter("listen", "采样监听", NodeParamKind.Toggle)
            {
                BoolChangedCallback = active =>
                {
                    UpdateStatusOnUi(active ? "采样监听中" : "已挂起", active);
                }
            };
            _listenToggleParam.SetBoolSilently(true);
            UpdateStatusOnUi("采样监听中", true);

            _clearBtnParam = new NodeParameter("clear", "缓存复位", NodeParamKind.ActionButton, "清空 Agent 分析采样窗")
            {
                ActionCommand = new RelayCommand(ClearAnalysisBuffer)
            };

            _analysisBadgeParam = new NodeParameter("verdict", "Agent 诊断", NodeParamKind.ReadOnlyBadge, "等待连入指定数据流...");

            Parameters.Add(_tagParam);
            Parameters.Add(_sourceModeParam);
            Parameters.Add(_listenToggleParam);
            Parameters.Add(_clearBtnParam);
            Parameters.Add(_analysisBadgeParam);

            McpBlueprintBus.PacketPublished += OnMcpPacketPublished;
            Esp32CommService.Instance.TelemetryReceived += OnEsp32TelemetryReceived;
        }

        public override void SetStreamingEnabled(bool enabled)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => _listenToggleParam.BoolValue = enabled);
        }

        public void ClearAnalysisBuffer()
        {
            lock (_bufferLock)
            {
                _analysisBuffer.Clear();
                _lastAgentNote = string.Empty;
            }
            UpdateBadge("采样窗已清空 · 等待新帧");
        }

        public void ApplyAgentDiagnosticNote(string note)
        {
            if (string.IsNullOrWhiteSpace(note)) return;
            lock (_bufferLock)
            {
                _lastAgentNote = note.Trim();
            }
            UpdateStatusOnUi("Agent 已分析", true);
            UpdateBadge($"Agent: {note.Trim()}");
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            if (!_listenToggleParam.BoolValue) return Task.CompletedTask;

            int bufferedCount;
            lock (_bufferLock)
            {
                _analysisBuffer.Enqueue(packet);
                while (_analysisBuffer.Count > MaxBufferedSamples)
                {
                    _analysisBuffer.Dequeue();
                }
                bufferedCount = _analysisBuffer.Count;
            }

            // 向下游透传被监测的数据流
            EmitPacket(0, packet);
            EmitPacket(1, packet);

            if (bufferedCount == 1 || bufferedCount % 12 == 0)
            {
                string chText = packet.Channels.Length > 0
                    ? $"CH0={packet.Channels[0]:F3}"
                    : packet.RawText;
                UpdateBadge($"已缓存 {bufferedCount} 帧 · {chText}");
            }

            return Task.CompletedTask;
        }

        private void OnMcpPacketPublished(BlueprintPacket packet)
        {
            if (!_listenToggleParam.BoolValue) return;
            if (_sourceModeParam.Value == ModeTapOnly) return;

            lock (_bufferLock)
            {
                _analysisBuffer.Enqueue(packet);
                while (_analysisBuffer.Count > MaxBufferedSamples)
                {
                    _analysisBuffer.Dequeue();
                }
            }

            IncrementCounter();
            EmitPacket(0, packet);
            EmitPacket(1, packet);
            UpdateBadge($"MCP [{packet.Channels.Length}CH]: {packet.ToFireWaterLine()}");
        }

        private void OnEsp32TelemetryReceived(TelemetryPacketData data)
        {
            if (!_listenToggleParam.BoolValue) return;
            if (_sourceModeParam.Value == ModeTapOnly) return;

            var ch = new[]
            {
                data.BusVoltage,
                data.BusCurrent,
                data.XdsVoltage,
                data.XdsCurrent,
                data.DapVoltage,
                data.DapCurrent
            };
            string raw = string.Create(
                CultureInfo.InvariantCulture,
                $"{data.BusVoltage:F2},{data.BusCurrent:F0},{data.XdsVoltage:F2},{data.XdsCurrent:F0},{data.DapVoltage:F2},{data.DapCurrent:F0}");

            var pkt = new BlueprintPacket(ch, raw, "ESP32_INA236", data.Timestamp);
            lock (_bufferLock)
            {
                _analysisBuffer.Enqueue(pkt);
                while (_analysisBuffer.Count > MaxBufferedSamples)
                {
                    _analysisBuffer.Dequeue();
                }
            }

            IncrementCounter();
            EmitPacket(0, pkt);
            EmitPacket(1, pkt);

            if (PacketCounter == 1 || PacketCounter % 10 == 0)
            {
                UpdateBadge($"INA236: {data.BusVoltage:F2}V / {data.BusCurrent:F0}mA");
            }
        }

        /// <summary>
        /// 导出该 MCP 节点已采样的指定数据流统计特征与近期序列，供 AI Agent 分析信号噪声与动态调参
        /// </summary>
        public Dictionary<string, object?> BuildAnalysisReport(int maxRecentSamples = 48)
        {
            BlueprintPacket[] snapshot;
            string note;
            lock (_bufferLock)
            {
                snapshot = _analysisBuffer.ToArray();
                note = _lastAgentNote;
            }

            int count = snapshot.Length;
            int maxCh = count > 0 ? snapshot.Max(p => p.Channels.Length) : 0;
            double sampleRateHz = 0.0;
            if (count >= 2)
            {
                double spanSec = (snapshot[^1].Timestamp - snapshot[0].Timestamp).TotalSeconds;
                if (spanSec > 0.001)
                {
                    sampleRateHz = Math.Round((count - 1) / spanSec, 2);
                }
            }

            var channelStats = new List<object>();
            for (int c = 0; c < maxCh; c++)
            {
                var vals = snapshot.Where(p => p.Channels.Length > c).Select(p => p.Channels[c]).ToArray();
                if (vals.Length == 0) continue;

                double min = vals.Min();
                double max = vals.Max();
                double mean = vals.Average();
                double sumSqDiff = 0.0;
                double sumSq = 0.0;
                double sumDiff = 0.0;
                for (int i = 0; i < vals.Length; i++)
                {
                    double d = vals[i] - mean;
                    sumSqDiff += d * d;
                    sumSq += vals[i] * vals[i];
                    if (i > 0)
                    {
                        sumDiff += Math.Abs(vals[i] - vals[i - 1]);
                    }
                }

                double stdDev = Math.Sqrt(sumSqDiff / vals.Length);
                double rms = Math.Sqrt(sumSq / vals.Length);
                double meanStepDiff = vals.Length > 1 ? sumDiff / (vals.Length - 1) : 0.0;
                double jitterRatio = stdDev > 1e-9 ? meanStepDiff / stdDev : 0.0;

                channelStats.Add(new
                {
                    channelIndex = c,
                    sampleCount = vals.Length,
                    min = Math.Round(min, 5),
                    max = Math.Round(max, 5),
                    peakToPeak = Math.Round(max - min, 5),
                    mean = Math.Round(mean, 5),
                    stdDev = Math.Round(stdDev, 5),
                    rms = Math.Round(rms, 5),
                    meanStepDiff = Math.Round(meanStepDiff, 5),
                    highFreqJitterRatio = Math.Round(jitterRatio, 4),
                    latest = Math.Round(vals[^1], 5)
                });
            }

            int takeCount = Math.Clamp(maxRecentSamples, 1, MaxBufferedSamples);
            var recentFrames = snapshot
                .Skip(Math.Max(0, snapshot.Length - takeCount))
                .Select(p => new
                {
                    timestamp = p.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                    sourceTag = p.SourceTag,
                    streamPrefix = p.StreamPrefix,
                    channels = p.Channels,
                    raw = p.RawText
                })
                .ToList();

            return new Dictionary<string, object?>
            {
                ["nodeId"] = NodeId,
                ["analysisTag"] = _tagParam.Value,
                ["mode"] = _sourceModeParam.Value,
                ["isListening"] = _listenToggleParam.BoolValue,
                ["bufferedFrameCount"] = count,
                ["channelCount"] = maxCh,
                ["estimatedSampleRateHz"] = sampleRateHz,
                ["lastAgentDiagnosticNote"] = note,
                ["channelStatistics"] = channelStats,
                ["recentSamples"] = recentFrames
            };
        }

        public override Dictionary<string, object?> ExportMcpMetadata() => BuildAnalysisReport(maxRecentSamples: 12);

        private void UpdateBadge(string text)
        {
            string clipped = text.Length > 34 ? text[..34] + "…" : text;
            Application.Current?.Dispatcher.BeginInvoke(() => _analysisBadgeParam.SetValueSilently(clipped));
        }

        public override void Dispose()
        {
            McpBlueprintBus.PacketPublished -= OnMcpPacketPublished;
            Esp32CommService.Instance.TelemetryReceived -= OnEsp32TelemetryReceived;
            base.Dispose();
        }
    }
}
