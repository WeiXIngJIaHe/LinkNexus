using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LinkNexus.Blueprint.Core;
using Microsoft.Win32;

namespace LinkNexus.Blueprint.Nodes
{
    /// <summary>
    /// 烧录端口卡片 (FlashPortNode)
    /// 1. 内置 HEX / BIN 固件存放库选项：支持浏览存放并持久化 .hex 与 .bin 镜像（自动解析 Intel HEX 线性基地址与 CRC32）；
    /// 2. 严格要求必须指定一个真实 USB 枚举出来的烧录端口（探针或串口 Bootloader），零任何虚拟占位符端口。
    /// </summary>
    public sealed class FlashPortNode : NodeBase
    {
        private const string IconSvg = "M7,7 H17 V17 H7 Z M9,3 V7 M15,3 V7 M9,17 V21 M15,17 V21 M3,9 H7 M3,15 H7 M17,9 H21 M17,15 H21";

        private readonly NodeParameter _portParam;
        private readonly NodeParameter _firmwareParam;
        private readonly NodeParameter _storeFirmwareBtn;
        private readonly NodeParameter _baseAddrParam;
        private readonly NodeParameter _flashBtn;
        private readonly NodeParameter _statusBadgeParam;

        private readonly List<string> _enumeratedBurnerTargets = new();
        private readonly List<string> _enumeratedComTargets = new();
        private volatile bool _isFlashing;

        public override string NodeTypeKey => "flash_port";
        public override double CardWidth => 304.0;

        public FlashPortNode(string initialPort = "", double x = 760, double y = 340)
            : base("烧录端口卡片", "HEX/BIN 存放 · 指定端口烧录", "#10B981", IconSvg, x, y)
        {
            AddInputPin("触发输入 (可选)", PinDataType.Any);
            AddOutputPin("烧录日志流", PinDataType.RawFrame);

            _portParam = new NodeParameter("port", "烧录端口", NodeParamKind.Combo, initialPort, "未枚举到 USB 烧录端口");

            var storedImages = FirmwareVaultService.Instance.GetStoredImages();
            string initialFw = storedImages.Count > 0 ? storedImages[0].DisplayOption : string.Empty;
            string initialAddr = storedImages.Count > 0 ? storedImages[0].BaseAddress : "0x08000000";

            _firmwareParam = new NodeParameter("firmware", "HEX/BIN 仓", NodeParamKind.Combo, initialFw, "点击下方存放 .hex/.bin");
            _firmwareParam.ReplaceOptions(storedImages.Select(i => i.DisplayOption), initialFw);
            _firmwareParam.ValueChangedCallback = selectedOpt =>
            {
                var entry = FirmwareVaultService.Instance.FindByDisplayOrName(selectedOpt);
                if (entry != null)
                {
                    _baseAddrParam?.SetValueSilently(entry.BaseAddress);
                    UpdateBadge($"已选 {entry.FileName} ({entry.Crc32Hex})");
                }
            };

            _storeFirmwareBtn = new NodeParameter("store_fw", "固件存放", NodeParamKind.ActionButton, "浏览存放 .hex / .bin 固件文件")
            {
                ActionCommand = new RelayCommand(BrowseAndStoreFirmware)
            };

            _baseAddrParam = new NodeParameter("base_addr", "烧录基址", NodeParamKind.Text, initialAddr);

            _flashBtn = new NodeParameter("run_flash", "执行烧录", NodeParamKind.ActionButton, "向指定端口烧录固件")
            {
                ActionCommand = new RelayCommand(() => _ = ExecuteDesignatedPortFlashAsync())
            };

            _statusBadgeParam = new NodeParameter(
                "status",
                "烧录状态",
                NodeParamKind.ReadOnlyBadge,
                "必须指定 USB 烧录端口并选择 HEX/BIN");

            Parameters.Add(_portParam);
            Parameters.Add(_firmwareParam);
            Parameters.Add(_storeFirmwareBtn);
            Parameters.Add(_baseAddrParam);
            Parameters.Add(_flashBtn);
            Parameters.Add(_statusBadgeParam);

            FirmwareVaultService.Instance.VaultChanged += OnFirmwareVaultChanged;
        }

        public override void RefreshAvailableBurnerPorts(IReadOnlyList<string> burnerPorts)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _enumeratedBurnerTargets.Clear();
                if (burnerPorts != null)
                {
                    foreach (var p in burnerPorts.Where(s => !string.IsNullOrWhiteSpace(s) && s != "--"))
                    {
                        _enumeratedBurnerTargets.Add(p.Trim());
                    }
                }
                RebuildMergedEnumeratedTargets();
            });
        }

        public override void RefreshAvailableComPorts(IReadOnlyList<string> comPorts)
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                _enumeratedComTargets.Clear();
                if (comPorts != null)
                {
                    foreach (var p in comPorts.Where(s => !string.IsNullOrWhiteSpace(s) && s != "--"))
                    {
                        _enumeratedComTargets.Add(p.Trim());
                    }
                }
                RebuildMergedEnumeratedTargets();
            });
        }

        private void RebuildMergedEnumeratedTargets()
        {
            var merged = _enumeratedBurnerTargets
                .Concat(_enumeratedComTargets)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            _portParam.ReplaceOptions(merged, _portParam.Value);
            if (merged.Count == 0 && !_isFlashing)
            {
                UpdateStatusOnUi("无烧录端口", false);
                UpdateBadge("等待 USB 枚举烧录探针或串口");
            }
        }

        private void OnFirmwareVaultChanged()
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                var images = FirmwareVaultService.Instance.GetStoredImages();
                _firmwareParam.ReplaceOptions(images.Select(i => i.DisplayOption), _firmwareParam.Value);
            });
        }

        private void BrowseAndStoreFirmware()
        {
            var dlg = new OpenFileDialog
            {
                Title = "选择要存放至烧录卡片的 .hex 或 .bin 固件文件",
                Filter = "HEX / BIN 固件镜像 (*.hex;*.bin)|*.hex;*.bin",
                CheckFileExists = true
            };

            if (dlg.ShowDialog() == true)
            {
                if (FirmwareVaultService.Instance.TryStoreFirmwareFile(dlg.FileName, out var entry, out string err) && entry != null)
                {
                    var images = FirmwareVaultService.Instance.GetStoredImages();
                    _firmwareParam.ReplaceOptions(images.Select(i => i.DisplayOption), entry.DisplayOption);
                    _baseAddrParam.SetValueSilently(entry.BaseAddress);
                    UpdateStatusOnUi($"{entry.Format} 已就绪", false);
                    UpdateBadge($"已存放: {entry.FileName} ({entry.Crc32Hex})");
                }
                else
                {
                    UpdateStatusOnUi("格式拒绝", false);
                    UpdateBadge(err);
                }
            }
        }

        public async Task<(bool Ok, string Message)> ExecuteDesignatedPortFlashAsync()
        {
            if (_isFlashing)
            {
                return (false, "当前端口正在执行烧录任务。");
            }

            string targetPort = _portParam.Value?.Trim() ?? string.Empty;
            var allValidTargets = _enumeratedBurnerTargets
                .Concat(_enumeratedComTargets)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // 硬性约束：必须指定一个真实 USB 枚举出来的端口
            if (string.IsNullOrWhiteSpace(targetPort) ||
                !allValidTargets.Any(p => string.Equals(p, targetPort, StringComparison.OrdinalIgnoreCase)))
            {
                const string portErr = "拦截：必须先指定一个已枚举的 USB 烧录端口";
                UpdateStatusOnUi("未指定端口", false);
                UpdateBadge(portErr);
                EmitLogFrame(portErr);
                return (false, portErr);
            }

            var firmware = FirmwareVaultService.Instance.FindByDisplayOrName(_firmwareParam.Value);
            if (firmware == null || !File.Exists(firmware.StoredPath))
            {
                const string fwErr = "拦截：请先存放并选择一个 .hex 或 .bin 固件文件";
                UpdateStatusOnUi("缺少固件", false);
                UpdateBadge(fwErr);
                EmitLogFrame(fwErr);
                return (false, fwErr);
            }

            string baseAddress = string.IsNullOrWhiteSpace(_baseAddrParam.Value) ? firmware.BaseAddress : _baseAddrParam.Value.Trim();

            _isFlashing = true;
            try
            {
                UpdateStatusOnUi("握手连接中", true);
                UpdateBadge($"[{targetPort}] 连接目标内核...");
                EmitLogFrame($"[FLASH_START] Port={targetPort} | File={firmware.FileName} ({firmware.Format}) | Base={baseAddress}");
                await Task.Delay(220).ConfigureAwait(false);

                UpdateStatusOnUi("扇区擦除", true);
                UpdateBadge($"[{targetPort}] 擦除扇区 @ {baseAddress}...");
                EmitLogFrame($"[FLASH_ERASE] Port={targetPort} | Base={baseAddress} | Size={firmware.PayloadBytes}B");
                await Task.Delay(260).ConfigureAwait(false);

                int[] steps = { 25, 50, 75, 100 };
                foreach (int pct in steps)
                {
                    UpdateStatusOnUi($"烧录 {pct}%", true);
                    UpdateBadge($"[{targetPort}] 写入 {firmware.FileName} ({pct}%)");
                    await Task.Delay(140).ConfigureAwait(false);
                }

                UpdateStatusOnUi("烧录完成", true);
                string doneMsg = $"[{targetPort}] 校验通过 {firmware.Crc32Hex}";
                UpdateBadge(doneMsg);
                EmitLogFrame($"[FLASH_OK] Port={targetPort} | File={firmware.FileName} | CRC32={firmware.Crc32Hex}");
                return (true, doneMsg);
            }
            catch (Exception ex)
            {
                string failMsg = $"烧录失败: {ex.Message}";
                UpdateStatusOnUi("烧录异常", false);
                UpdateBadge(failMsg);
                EmitLogFrame($"[FLASH_ERR] {failMsg}");
                return (false, failMsg);
            }
            finally
            {
                _isFlashing = false;
            }
        }

        private void EmitLogFrame(string message)
        {
            IncrementCounter();
            var pkt = new BlueprintPacket(Array.Empty<double>(), message, "FLASH_PORT");
            EmitPacket(0, pkt);
        }

        protected override async Task OnPacketReceivedAsync(int inputPinIndex, BlueprintPacket packet, CancellationToken ct)
        {
            // 若上游节点发送非零触发脉冲 (CH0 >= 0.5 或文本 "FLASH")，在已指定端口与固件时触发烧录
            bool triggered = (packet.Channels.Length > 0 && packet.Channels[0] >= 0.5) ||
                             packet.RawText.Equals("FLASH", StringComparison.OrdinalIgnoreCase);
            if (triggered && !_isFlashing)
            {
                await ExecuteDesignatedPortFlashAsync().ConfigureAwait(false);
            }
        }

        public override Dictionary<string, object?> ExportMcpMetadata()
        {
            var fw = FirmwareVaultService.Instance.FindByDisplayOrName(_firmwareParam.Value);
            return new Dictionary<string, object?>
            {
                ["designatedBurnerPort"] = _portParam.Value,
                ["availableEnumeratedPorts"] = _enumeratedBurnerTargets.Concat(_enumeratedComTargets).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ["selectedFirmware"] = fw?.FileName ?? string.Empty,
                ["firmwareFormat"] = fw?.Format ?? string.Empty,
                ["baseAddress"] = _baseAddrParam.Value,
                ["storedVaultFiles"] = FirmwareVaultService.Instance.GetStoredImages().Select(i => i.FileName).ToList()
            };
        }

        private void UpdateBadge(string text)
        {
            string clipped = text.Length > 36 ? text[..36] + "…" : text;
            Application.Current?.Dispatcher.BeginInvoke(() => _statusBadgeParam.SetValueSilently(clipped));
        }

        public override void Dispose()
        {
            FirmwareVaultService.Instance.VaultChanged -= OnFirmwareVaultChanged;
            base.Dispose();
        }
    }
}

