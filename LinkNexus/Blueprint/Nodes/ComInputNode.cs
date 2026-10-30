using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// COM 串口流输入卡片 (ComInputNode)
    /// 1. 严格仅列出真实 USB 枚举出来的物理 COM 端口，零虚拟/仿真占位符端口；
    /// 2. 必须指定有效 USB 枚举端口方可启动采集；
    /// 3. 支持右键动态拆分与合并：在下位机打印单行多通道或带前缀多数据流 (如 BUS:/XDS:/DAP:) 时，按需拆分为独立 CH 通道输出端口。
    /// </summary>
    public sealed class ComInputNode : NodeBase
    {
        private sealed class SplitChannelRule
        {
            public PinViewModel Pin { get; }
            public string StreamPrefix { get; } // 空字符串表示匹配任意流
            public int? ChannelIndex { get; }   // null 表示输出该前缀整条子流，非 null 表示提取指定 CHk

            public string Label =>
                !string.IsNullOrEmpty(StreamPrefix) && ChannelIndex.HasValue
                    ? $"{StreamPrefix}.CH{ChannelIndex.Value}"
                    : (!string.IsNullOrEmpty(StreamPrefix)
                        ? $"流:{StreamPrefix}"
                        : $"CH{ChannelIndex ?? 0}");

            public SplitChannelRule(PinViewModel pin, string streamPrefix, int? channelIndex)
            {
                Pin = pin;
                StreamPrefix = streamPrefix ?? string.Empty;
                ChannelIndex = channelIndex;
            }
        }

        private const string IconSvg = "M4,7 H16 L19,12 L16,17 H4 Z M7,10.5 A1,1 0 1,1 7,10.51 M11,10.5 A1,1 0 1,1 11,10.51 M15,10.5 A1,1 0 1,1 15,10.51 M9,13.8 A1,1 0 1,1 9,13.81 M13,13.8 A1,1 0 1,1 13,13.81";

        private readonly NodeParameter _portParam;
        private readonly NodeParameter _baudParam;
        private readonly NodeParameter _enableParam;
        private readonly NodeParameter _rateBadgeParam;

        private readonly object _streamLock = new();
        private readonly object _splitLock = new();
        private readonly List<string> _enumeratedPorts = new();
        private readonly List<SplitChannelRule> _splitRules = new();
        private readonly HashSet<string> _observedPrefixes = new(StringComparer.OrdinalIgnoreCase);

        private CancellationTokenSource? _streamCts;
        private SerialPort? _serialPort;
        private volatile int _lastObservedChannelCount;

        public override string NodeTypeKey => "com_input";
        public string DesignatedPort => _portParam.Value;

        public ComInputNode(string initialPort = "", double x = 36, double y = 48)
            : base("COM 串口输入", "硬件采集源 · 右键拆分CH", "#0EA5E9", IconSvg, x, y)
        {
            AddOutputPin("全通道合流", PinDataType.ChannelStream);
            AddOutputPin("原始文本帧", PinDataType.RawFrame);

            _portParam = new NodeParameter("port", "指定端口", NodeParamKind.Combo, initialPort, "未枚举到 USB 串口");
            PopulatePortOptions(SerialPort.GetPortNames(), initialPort);
            _portParam.ValueChangedCallback = _ =>
            {
                if (_enableParam != null && _enableParam.BoolValue)
                {
                    RestartStream();
                }
            };

            _baudParam = new NodeParameter("baud", "波特率", NodeParamKind.Combo, "115200");
            foreach (var b in new[] { "115200", "921600", "460800", "230400", "57600", "9600" })
            {
                _baudParam.Options.Add(b);
            }
            _baudParam.ValueChangedCallback = _ =>
            {
                if (_enableParam != null && _enableParam.BoolValue)
                {
                    RestartStream();
                }
            };

            _enableParam = new NodeParameter("enable", "流式采集", NodeParamKind.Toggle)
            {
                BoolChangedCallback = enabled =>
                {
                    if (enabled) StartStream();
                    else StopStream();
                }
            };

            _rateBadgeParam = new NodeParameter(
                "rate",
                "链路状态",
                NodeParamKind.ReadOnlyBadge,
                _enumeratedPorts.Count > 0 ? "就绪 · 右键卡片可按需拆分 CH" : "等待 USB 枚举物理串口");

            Parameters.Add(_portParam);
            Parameters.Add(_baudParam);
            Parameters.Add(_enableParam);
            Parameters.Add(_rateBadgeParam);
        }

        public override void RefreshAvailableComPorts(IReadOnlyList<string> comPorts)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                string current = _portParam.Value;
                PopulatePortOptions(comPorts, current);
            });
        }

        public override void SetStreamingEnabled(bool enabled)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _enableParam.BoolValue = enabled;
            });
        }

        private void PopulatePortOptions(IEnumerable<string>? usbPorts, string? preferred)
        {
            _enumeratedPorts.Clear();
            if (usbPorts != null)
            {
                foreach (var p in usbPorts.Where(s => !string.IsNullOrWhiteSpace(s) && s != "--").Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    _enumeratedPorts.Add(p.Trim());
                }
            }

            _portParam.ReplaceOptions(_enumeratedPorts, preferred);

            if (_enumeratedPorts.Count == 0 && _enableParam != null && _enableParam.BoolValue)
            {
                _enableParam.SetBoolSilently(false);
                StopStream();
                UpdateStatusOnUi("无USB端口", false);
                UpdateRateBadge("USB 串口已拔出 · 采集已停止");
            }
        }

        public override IReadOnlyList<NodeContextAction> GetContextMenuActions()
        {
            var actions = new List<NodeContextAction>();
            int observedCh = Math.Max(0, _lastObservedChannelCount);

            string[] observedStreams;
            SplitChannelRule[] activeSplits;
            lock (_splitLock)
            {
                observedStreams = _observedPrefixes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
                activeSplits = _splitRules.ToArray();
            }

            string autoHeader = observedStreams.Length > 1
                ? $"按实时多数据流自动拆分 ({string.Join("/", observedStreams)})"
                : (observedCh > 0
                    ? $"按实时帧自动拆分为 {observedCh} 路 CH 通道"
                    : "按默认 4 路自动拆分 (CH0 ~ CH3)");

            actions.Add(new NodeContextAction
            {
                Header = autoHeader,
                Execute = AutoSplitFromObservedData
            });

            var batchSub = new NodeContextAction { Header = "按通道数批量拆分 CH 端口" };
            foreach (int count in new[] { 2, 3, 4, 6, 9 })
            {
                int capturedCount = count;
                batchSub.Children.Add(new NodeContextAction
                {
                    Header = capturedCount == 9
                        ? "拆分为 9 路 (CH0 ~ CH8 · INA236 全通道)"
                        : $"拆分为 {capturedCount} 路 (CH0 ~ CH{capturedCount - 1})",
                    Execute = () => SplitChannels(Enumerable.Range(0, capturedCount), string.Empty, replaceExisting: true)
                });
            }
            actions.Add(batchSub);

            var singleChSub = new NodeContextAction { Header = "按需勾选拆分单个 CH 通道" };
            int maxMenuCh = Math.Max(9, observedCh);
            for (int chIdx = 0; chIdx < maxMenuCh; chIdx++)
            {
                int capturedIdx = chIdx;
                bool exists = activeSplits.Any(r => string.IsNullOrEmpty(r.StreamPrefix) && r.ChannelIndex == capturedIdx);
                singleChSub.Children.Add(new NodeContextAction
                {
                    Header = exists ? $"移除已拆分端口 CH{capturedIdx}" : $"追加独立端口 CH{capturedIdx}",
                    IsChecked = exists,
                    Execute = () => ToggleSingleChannelSplit(string.Empty, capturedIdx)
                });
            }
            actions.Add(singleChSub);

            var streamSub = new NodeContextAction { Header = "按下位机多数据流标签拆分 (如 BUS / XDS / DAP)" };
            var candidateStreams = observedStreams
                .Concat(new[] { "BUS", "XDS", "DAP", "IMU" })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (observedStreams.Length > 0)
            {
                streamSub.Children.Add(new NodeContextAction
                {
                    Header = $"拆分全部已识别数据流 ({string.Join(", ", observedStreams)})",
                    Execute = () => SplitStreams(observedStreams, expandPerChannel: false)
                });
                streamSub.Children.Add(new NodeContextAction
                {
                    Header = $"展开已识别数据流各自 CH0~CH2 通道",
                    Execute = () => SplitStreams(observedStreams, expandPerChannel: true)
                });
                streamSub.Children.Add(NodeContextAction.Separator());
            }

            foreach (string prefix in candidateStreams)
            {
                string capturedPrefix = prefix.ToUpperInvariant();
                bool streamPinExists = activeSplits.Any(r => string.Equals(r.StreamPrefix, capturedPrefix, StringComparison.OrdinalIgnoreCase) && !r.ChannelIndex.HasValue);

                var perPrefixMenu = new NodeContextAction
                {
                    Header = $"数据流 [{capturedPrefix}]",
                    IsChecked = streamPinExists
                };
                perPrefixMenu.Children.Add(new NodeContextAction
                {
                    Header = streamPinExists ? $"移除 [{capturedPrefix}] 整流输出端口" : $"拆分 [{capturedPrefix}] 整流输出端口",
                    IsChecked = streamPinExists,
                    Execute = () => ToggleStreamWholeSplit(capturedPrefix)
                });
                for (int k = 0; k < 4; k++)
                {
                    int capturedCh = k;
                    bool subExists = activeSplits.Any(r => string.Equals(r.StreamPrefix, capturedPrefix, StringComparison.OrdinalIgnoreCase) && r.ChannelIndex == capturedCh);
                    perPrefixMenu.Children.Add(new NodeContextAction
                    {
                        Header = subExists ? $"移除 {capturedPrefix}.CH{capturedCh}" : $"提取 {capturedPrefix}.CH{capturedCh}",
                        IsChecked = subExists,
                        Execute = () => ToggleSingleChannelSplit(capturedPrefix, capturedCh)
                    });
                }
                streamSub.Children.Add(perPrefixMenu);
            }
            actions.Add(streamSub);

            actions.Add(NodeContextAction.Separator());
            actions.Add(new NodeContextAction
            {
                Header = $"合并收起全部拆分端口 (当前 {activeSplits.Length} 路)",
                IsEnabled = activeSplits.Length > 0,
                Execute = CollapseSplitChannels
            });

            return actions;
        }

        public void AutoSplitFromObservedData()
        {
            string[] streams;
            lock (_splitLock)
            {
                streams = _observedPrefixes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
            }

            if (streams.Length > 1)
            {
                SplitStreams(streams, expandPerChannel: false);
                return;
            }

            int count = _lastObservedChannelCount > 0 ? Math.Min(12, _lastObservedChannelCount) : 4;
            SplitChannels(Enumerable.Range(0, count), string.Empty, replaceExisting: true);
        }

        public void SplitChannels(IEnumerable<int> channelIndices, string streamPrefix = "", bool replaceExisting = true)
        {
            void ApplyOnUi()
            {
                if (replaceExisting)
                {
                    CollapseSplitChannelsCore();
                }

                string normPrefix = (streamPrefix ?? string.Empty).Trim().ToUpperInvariant();
                foreach (int idx in channelIndices.Where(i => i >= 0 && i < 32).Distinct())
                {
                    AddSplitRuleOnUi(normPrefix, idx);
                }

                UpdateSplitSummaryBadge();
            }

            if (Application.Current == null || Application.Current.Dispatcher.CheckAccess())
            {
                ApplyOnUi();
            }
            else
            {
                Application.Current.Dispatcher.Invoke(ApplyOnUi);
            }
        }

        public void SplitStreams(IEnumerable<string> prefixes, bool expandPerChannel)
        {
            void ApplyOnUi()
            {
                CollapseSplitChannelsCore();
                int chCount = _lastObservedChannelCount > 0 ? Math.Min(4, _lastObservedChannelCount) : 3;

                foreach (string raw in prefixes.Where(p => !string.IsNullOrWhiteSpace(p)))
                {
                    string norm = raw.Trim().ToUpperInvariant();
                    if (expandPerChannel)
                    {
                        for (int c = 0; c < chCount; c++)
                        {
                            AddSplitRuleOnUi(norm, c);
                        }
                    }
                    else
                    {
                        AddSplitRuleOnUi(norm, null);
                    }
                }

                UpdateSplitSummaryBadge();
            }

            if (Application.Current == null || Application.Current.Dispatcher.CheckAccess())
            {
                ApplyOnUi();
            }
            else
            {
                Application.Current.Dispatcher.Invoke(ApplyOnUi);
            }
        }

        public void CollapseSplitChannels()
        {
            if (Application.Current == null || Application.Current.Dispatcher.CheckAccess())
            {
                CollapseSplitChannelsCore();
                UpdateRateBadge("已合并收起全部拆分 CH 通道");
            }
            else
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    CollapseSplitChannelsCore();
                    UpdateRateBadge("已合并收起全部拆分 CH 通道");
                });
            }
        }

        private void ToggleSingleChannelSplit(string prefix, int channelIndex)
        {
            string normPrefix = (prefix ?? string.Empty).Trim().ToUpperInvariant();
            SplitChannelRule? existing;
            lock (_splitLock)
            {
                existing = _splitRules.FirstOrDefault(r =>
                    string.Equals(r.StreamPrefix, normPrefix, StringComparison.OrdinalIgnoreCase) &&
                    r.ChannelIndex == channelIndex);
            }

            if (existing != null)
            {
                lock (_splitLock)
                {
                    _splitRules.Remove(existing);
                }
                RemoveOutputPin(existing.Pin);
            }
            else
            {
                AddSplitRuleOnUi(normPrefix, channelIndex);
            }
            UpdateSplitSummaryBadge();
        }

        private void ToggleStreamWholeSplit(string prefix)
        {
            string normPrefix = prefix.Trim().ToUpperInvariant();
            SplitChannelRule? existing;
            lock (_splitLock)
            {
                existing = _splitRules.FirstOrDefault(r =>
                    string.Equals(r.StreamPrefix, normPrefix, StringComparison.OrdinalIgnoreCase) &&
                    !r.ChannelIndex.HasValue);
            }

            if (existing != null)
            {
                lock (_splitLock)
                {
                    _splitRules.Remove(existing);
                }
                RemoveOutputPin(existing.Pin);
            }
            else
            {
                AddSplitRuleOnUi(normPrefix, null);
            }
            UpdateSplitSummaryBadge();
        }

        private void AddSplitRuleOnUi(string normPrefix, int? channelIndex)
        {
            lock (_splitLock)
            {
                bool duplicate = _splitRules.Any(r =>
                    string.Equals(r.StreamPrefix, normPrefix, StringComparison.OrdinalIgnoreCase) &&
                    r.ChannelIndex == channelIndex);
                if (duplicate) return;
            }

            string pinLabel = !string.IsNullOrEmpty(normPrefix) && channelIndex.HasValue
                ? $"{normPrefix}.CH{channelIndex.Value}"
                : (!string.IsNullOrEmpty(normPrefix)
                    ? $"流:{normPrefix}"
                    : $"CH{channelIndex ?? 0}");

            var createdPin = AddOutputPin(pinLabel, PinDataType.ChannelStream);
            lock (_splitLock)
            {
                _splitRules.Add(new SplitChannelRule(createdPin, normPrefix, channelIndex));
            }
        }

        private void CollapseSplitChannelsCore()
        {
            List<SplitChannelRule> toRemove;
            lock (_splitLock)
            {
                toRemove = _splitRules.ToList();
                _splitRules.Clear();
            }

            for (int i = toRemove.Count - 1; i >= 0; i--)
            {
                RemoveOutputPin(toRemove[i].Pin);
            }
        }

        private void UpdateSplitSummaryBadge()
        {
            int count;
            lock (_splitLock)
            {
                count = _splitRules.Count;
            }
            UpdateRateBadge(count > 0 ? $"已拆分 {count} 路独立 CH 输出端口" : "未拆分扩展端口");
        }

        private void RestartStream()
        {
            StopStream();
            StartStream();
        }

        private void StartStream()
        {
            string selectedPort = _portParam?.Value?.Trim() ?? string.Empty;
            int baud = int.TryParse(_baudParam?.Value, out int parsedBaud) ? parsedBaud : 115200;

            if (string.IsNullOrWhiteSpace(selectedPort) ||
                !_enumeratedPorts.Any(p => string.Equals(p, selectedPort, StringComparison.OrdinalIgnoreCase)))
            {
                Application.Current?.Dispatcher.BeginInvoke(() => _enableParam.SetBoolSilently(false));
                UpdateStatusOnUi("未指定端口", false);
                UpdateRateBadge("请先指定一个 USB 枚举物理串口");
                return;
            }

            lock (_streamLock)
            {
                StopStreamCore();
                _streamCts = new CancellationTokenSource();
                var token = _streamCts.Token;
                _ = Task.Run(() => RunPhysicalSerialLoopAsync(selectedPort, baud, token));
            }
        }

        private void StopStream()
        {
            lock (_streamLock)
            {
                StopStreamCore();
            }
            UpdateStatusOnUi("已停止", false);
            UpdateRateBadge("已停止 · 端口空闲");
        }

        private void StopStreamCore()
        {
            if (_streamCts != null)
            {
                try { _streamCts.Cancel(); } catch { }
                _streamCts.Dispose();
                _streamCts = null;
            }

            if (_serialPort != null)
            {
                try
                {
                    if (_serialPort.IsOpen) _serialPort.Close();
                }
                catch { }
                _serialPort.Dispose();
                _serialPort = null;
            }
        }

        private async Task RunPhysicalSerialLoopAsync(string portName, int baudRate, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(portName))
            {
                UpdateStatusOnUi("未指定端口", false);
                UpdateRateBadge("未选择 USB 枚举物理串口");
                return;
            }

            SerialPort? port = null;
            try
            {
                port = new SerialPort(portName, baudRate, Parity.None, 8, StopBits.One)
                {
                    Encoding = Encoding.UTF8,
                    ReadTimeout = 500,
                    WriteTimeout = 500,
                    DtrEnable = false,
                    RtsEnable = false
                };
                port.Open();

                lock (_streamLock)
                {
                    _serialPort = port;
                }

                UpdateStatusOnUi($"{portName} 采集中", true);
                UpdateRateBadge($"{portName} @ {baudRate} · 右键可拆CH");

                var buffer = new byte[1024];
                var lineBuilder = new StringBuilder(256);

                while (!ct.IsCancellationRequested && port.IsOpen)
                {
                    int readCount = await port.BaseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (readCount <= 0) continue;

                    string chunk = Encoding.UTF8.GetString(buffer, 0, readCount);
                    foreach (char c in chunk)
                    {
                        if (c == '\n' || c == '\r')
                        {
                            if (lineBuilder.Length > 0)
                            {
                                string line = lineBuilder.ToString();
                                lineBuilder.Clear();
                                DispatchSerialLine(portName, line);
                            }
                        }
                        else if (lineBuilder.Length < 2048)
                        {
                            lineBuilder.Append(c);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                UpdateStatusOnUi("打开失败", false);
                UpdateRateBadge($"串口异常: {ex.Message}");
                Application.Current?.Dispatcher.BeginInvoke(() => _enableParam.SetBoolSilently(false));
            }
        }

        /// <summary>
        /// 处理单行串口数据流 (同时向全通道合流 Pin0、原始文本帧 Pin1 以及所有右键拆分的 CH 通道端口分发)
        /// </summary>
        public void DispatchSerialLine(string portName, string line)
        {
            IncrementCounter();
            if (BlueprintPacket.TryParseLine(line, portName, out var parsed))
            {
                _lastObservedChannelCount = Math.Max(_lastObservedChannelCount, parsed.Channels.Length);
                if (!string.IsNullOrEmpty(parsed.StreamPrefix))
                {
                    lock (_splitLock)
                    {
                        _observedPrefixes.Add(parsed.StreamPrefix);
                    }
                }

                EmitPacket(0, parsed);
                EmitPacket(1, parsed);

                SplitChannelRule[] rulesSnapshot;
                lock (_splitLock)
                {
                    rulesSnapshot = _splitRules.Count > 0 ? _splitRules.ToArray() : Array.Empty<SplitChannelRule>();
                }

                for (int i = 0; i < rulesSnapshot.Length; i++)
                {
                    var rule = rulesSnapshot[i];
                    if (!string.IsNullOrEmpty(rule.StreamPrefix) &&
                        !string.Equals(rule.StreamPrefix, parsed.StreamPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (rule.ChannelIndex.HasValue)
                    {
                        int chIdx = rule.ChannelIndex.Value;
                        if (chIdx >= 0 && chIdx < parsed.Channels.Length)
                        {
                            double val = parsed.Channels[chIdx];
                            string subRaw = val.ToString("F4", CultureInfo.InvariantCulture);
                            var subPkt = new BlueprintPacket(
                                new[] { val },
                                subRaw,
                                $"{portName}:{rule.Label}",
                                parsed.Timestamp,
                                rule.StreamPrefix);
                            EmitPacket(rule.Pin.PinIndex, subPkt);
                        }
                    }
                    else
                    {
                        var streamPkt = new BlueprintPacket(
                            parsed.Channels,
                            parsed.RawText,
                            $"{portName}:{rule.StreamPrefix}",
                            parsed.Timestamp,
                            parsed.StreamPrefix);
                        EmitPacket(rule.Pin.PinIndex, streamPkt);
                    }
                }

                if (PacketCounter % 15 == 1)
                {
                    string prefixInfo = string.IsNullOrEmpty(parsed.StreamPrefix) ? "" : $"[{parsed.StreamPrefix}] ";
                    UpdateRateBadge($"{portName} · {prefixInfo}{parsed.Channels.Length}CH (拆{rulesSnapshot.Length}路)");
                }
            }
            else
            {
                var rawPkt = new BlueprintPacket(Array.Empty<double>(), line, portName);
                EmitPacket(1, rawPkt);
            }
        }

        public override Dictionary<string, object?> ExportMcpMetadata()
        {
            lock (_splitLock)
            {
                return new Dictionary<string, object?>
                {
                    ["designatedPort"] = _portParam.Value,
                    ["baudRate"] = _baudParam.Value,
                    ["isStreaming"] = _enableParam.BoolValue,
                    ["lastObservedChannelCount"] = _lastObservedChannelCount,
                    ["observedStreamPrefixes"] = _observedPrefixes.ToArray(),
                    ["splitPins"] = _splitRules.Select(r => new
                    {
                        pinIndex = r.Pin.PinIndex,
                        label = r.Label,
                        streamPrefix = r.StreamPrefix,
                        channelIndex = r.ChannelIndex
                    }).ToList()
                };
            }
        }

        private void UpdateRateBadge(string text)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => _rateBadgeParam.SetValueSilently(text));
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
            => Task.CompletedTask;

        public override void Dispose()
        {
            StopStream();
            base.Dispose();
        }
    }
}
