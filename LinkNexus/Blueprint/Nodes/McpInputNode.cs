using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LinkNexus.Blueprint.Core;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// MCP 数据输入卡片 (McpInputNode)
    /// 订阅 MCP Agent 的 push_blueprint_data 工具推流，并可直连 ESP32-S3 底层 INA236 6 通道实时母线电气遥测流
    /// </summary>
    public sealed class McpInputNode : NodeBase
    {
        private const string IconSvg = "M12,3 L20,7.5 V16.5 L12,21 L4,16.5 V7.5 Z M12,8 A4,4 0 1,0 12,16 A4,4 0 1,0 12,8";

        private const string ModeBoth = "MCP Agent + ESP32 遥测";
        private const string ModeMcpOnly = "仅 MCP Agent 推流";
        private const string ModeEsp32Only = "仅 ESP32 INA236 遥测";

        private readonly NodeParameter _sourceModeParam;
        private readonly NodeParameter _listenToggleParam;
        private readonly NodeParameter _injectBtnParam;
        private readonly NodeParameter _payloadBadgeParam;

        private readonly Random _testRand = new();

        public McpInputNode(double x = 36, double y = 340)
            : base("MCP 数据输入", "智能总线源", "#14B8A6", IconSvg, x, y)
        {
            AddOutputPin("MCP 通道流", PinDataType.ChannelStream);
            AddOutputPin("MCP 原始帧", PinDataType.RawFrame);

            _sourceModeParam = new NodeParameter("mode", "总线订阅", NodeParamKind.Combo, ModeBoth);
            _sourceModeParam.Options.Add(ModeBoth);
            _sourceModeParam.Options.Add(ModeMcpOnly);
            _sourceModeParam.Options.Add(ModeEsp32Only);

            _listenToggleParam = new NodeParameter("listen", "总线监听", NodeParamKind.Toggle)
            {
                BoolChangedCallback = active =>
                {
                    UpdateStatusOnUi(active ? "总线监听中" : "已挂起", active);
                }
            };
            _listenToggleParam.SetBoolSilently(true);
            UpdateStatusOnUi("总线监听中", true);

            _injectBtnParam = new NodeParameter("inject", "手动触发", NodeParamKind.ActionButton, "注入 MCP 测试数据帧")
            {
                ActionCommand = new RelayCommand(InjectSyntheticMcpFrame)
            };

            _payloadBadgeParam = new NodeParameter("last", "最近帧", NodeParamKind.ReadOnlyBadge, "等待 MCP / ESP32 遥测...");

            Parameters.Add(_sourceModeParam);
            Parameters.Add(_listenToggleParam);
            Parameters.Add(_injectBtnParam);
            Parameters.Add(_payloadBadgeParam);

            McpBlueprintBus.PacketPublished += OnMcpPacketPublished;
            Esp32CommService.Instance.TelemetryReceived += OnEsp32TelemetryReceived;
        }

        public override void SetStreamingEnabled(bool enabled)
        {
            Application.Current?.Dispatcher.BeginInvoke(() => _listenToggleParam.BoolValue = enabled);
        }

        private void InjectSyntheticMcpFrame()
        {
            double v0 = Math.Round(11.95 + _testRand.NextDouble() * 0.15, 2);
            double v1 = Math.Round(410.0 + _testRand.NextDouble() * 35.0, 1);
            double v2 = Math.Round(3.30 + (_testRand.NextDouble() - 0.5) * 0.04, 3);
            McpBlueprintBus.Publish(new[] { v0, v1, v2 }, $"{v0},{v1},{v2}", "MCP_TEST");
        }

        private void OnMcpPacketPublished(BlueprintPacket packet)
        {
            if (!_listenToggleParam.BoolValue) return;
            if (_sourceModeParam.Value == ModeEsp32Only) return;

            IncrementCounter();
            EmitPacket(0, packet);
            EmitPacket(1, packet);
            UpdateBadge($"MCP [{packet.Channels.Length}CH]: {packet.ToFireWaterLine()}");
        }

        private void OnEsp32TelemetryReceived(TelemetryPacketData data)
        {
            if (!_listenToggleParam.BoolValue) return;
            if (_sourceModeParam.Value == ModeMcpOnly) return;

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
            IncrementCounter();
            EmitPacket(0, pkt);
            EmitPacket(1, pkt);

            if (PacketCounter == 1 || PacketCounter % 10 == 0)
            {
                UpdateBadge($"INA236: {data.BusVoltage:F2}V / {data.BusCurrent:F0}mA");
            }
        }

        private void UpdateBadge(string text)
        {
            string clipped = text.Length > 32 ? text[..32] + "…" : text;
            Application.Current?.Dispatcher.BeginInvoke(() => _payloadBadgeParam.SetValueSilently(clipped));
        }

        protected override Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
            => Task.CompletedTask;

        public override void Dispose()
        {
            McpBlueprintBus.PacketPublished -= OnMcpPacketPublished;
            Esp32CommService.Instance.TelemetryReceived -= OnEsp32TelemetryReceived;
            base.Dispose();
        }
    }
}
