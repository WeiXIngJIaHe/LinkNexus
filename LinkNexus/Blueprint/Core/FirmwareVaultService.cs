using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LinkNexus.Blueprint.Core
{
    public sealed class FirmwareImageEntry
    {
        public string FileName { get; set; } = string.Empty;
        public string StoredPath { get; set; } = string.Empty;
        public string Format { get; set; } = "BIN";
        public long PayloadBytes { get; set; }
        public string BaseAddress { get; set; } = "0x08000000";
        public string Crc32Hex { get; set; } = "0x00000000";
        public DateTime AddedAt { get; set; } = DateTime.Now;

        public string DisplayOption =>
            $"{FileName} [{Format} · {FormatSize(PayloadBytes)} · {BaseAddress}]";

        private static string FormatSize(long bytes)
        {
            return bytes >= 1024 * 1024
                ? $"{bytes / (1024.0 * 1024.0):F2} MB"
                : $"{Math.Max(0.1, bytes / 1024.0):F1} KB";
        }
    }

    /// <summary>
    /// HEX / BIN 固件镜像存放库服务 (FirmwareVaultService)
    /// 供蓝图 [烧录端口卡片] 与 [固件烧录工作台] 统一存放、持久化与校验 .hex / .bin 固件镜像
    /// </summary>
    public sealed class FirmwareVaultService
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static readonly Lazy<FirmwareVaultService> LazyInstance = new(() => new FirmwareVaultService());
        public static FirmwareVaultService Instance => LazyInstance.Value;

        private readonly object _vaultLock = new();
        private readonly string _imagesDir;
        private readonly string _manifestPath;
        private readonly List<FirmwareImageEntry> _entries = new();

        public event Action? VaultChanged;

        private FirmwareVaultService()
        {
            string vaultRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LinkNexus",
                "FirmwareVault");
            _imagesDir = Path.Combine(vaultRoot, "Images");
            _manifestPath = Path.Combine(vaultRoot, "vault.json");

            Directory.CreateDirectory(_imagesDir);
            LoadManifest();
        }

        public IReadOnlyList<FirmwareImageEntry> GetStoredImages()
        {
            lock (_vaultLock)
            {
                return _entries
                    .Where(e => !string.IsNullOrWhiteSpace(e.StoredPath) && File.Exists(e.StoredPath))
                    .ToList();
            }
        }

        public FirmwareImageEntry? FindByDisplayOrName(string? query)
        {
            if (string.IsNullOrWhiteSpace(query)) return null;
            string trimmed = query.Trim();
            lock (_vaultLock)
            {
                return _entries.FirstOrDefault(e =>
                    File.Exists(e.StoredPath) &&
                    (string.Equals(e.DisplayOption, trimmed, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(e.FileName, trimmed, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(e.StoredPath, trimmed, StringComparison.OrdinalIgnoreCase)));
            }
        }

        public bool TryStoreFirmwareFile(string sourceFilePath, out FirmwareImageEntry? storedEntry, out string error)
        {
            storedEntry = null;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(sourceFilePath) || !File.Exists(sourceFilePath))
            {
                error = "所选固件文件不存在。";
                return false;
            }

            string ext = Path.GetExtension(sourceFilePath).ToLowerInvariant();
            if (ext != ".hex" && ext != ".bin")
            {
                error = "烧录固件库仅支持存放 .hex 或 .bin 格式镜像文件。";
                return false;
            }

            try
            {
                string fileName = Path.GetFileName(sourceFilePath);
                string targetPath = Path.Combine(_imagesDir, fileName);
                if (!string.Equals(Path.GetFullPath(sourceFilePath), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(sourceFilePath, targetPath, overwrite: true);
                }

                byte[] rawBytes = File.ReadAllBytes(targetPath);
                string format = ext == ".hex" ? "HEX" : "BIN";
                long payloadBytes = rawBytes.LongLength;
                string baseAddress = DetectDefaultBaseAddress(fileName);
                uint crc;

                if (ext == ".hex")
                {
                    if (!TryParseIntelHexMetadata(targetPath, out long hexPayloadBytes, out string? hexBaseAddr, out uint hexCrc, out string hexErr))
                    {
                        error = hexErr;
                        return false;
                    }
                    payloadBytes = hexPayloadBytes;
                    if (!string.IsNullOrWhiteSpace(hexBaseAddr))
                    {
                        baseAddress = hexBaseAddr!;
                    }
                    crc = hexCrc;
                }
                else
                {
                    crc = ComputeCrc32(rawBytes);
                }

                var entry = new FirmwareImageEntry
                {
                    FileName = fileName,
                    StoredPath = targetPath,
                    Format = format,
                    PayloadBytes = payloadBytes,
                    BaseAddress = baseAddress,
                    Crc32Hex = $"0x{crc:X8}",
                    AddedAt = DateTime.Now
                };

                lock (_vaultLock)
                {
                    _entries.RemoveAll(e => string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase));
                    _entries.Insert(0, entry);
                    SaveManifestCore();
                }

                storedEntry = entry;
                VaultChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                error = $"存放固件失败: {ex.Message}";
                return false;
            }
        }

        private static string DetectDefaultBaseAddress(string fileName)
        {
            string upper = fileName.ToUpperInvariant();
            if (upper.Contains("ESP32") || upper.Contains("S3")) return "0x00010000";
            if (upper.Contains("TM4C") || upper.Contains("XDS")) return "0x00000000";
            return "0x08000000";
        }

        private static bool TryParseIntelHexMetadata(string hexPath, out long totalDataBytes, out string? firstLinearAddress, out uint payloadCrc, out string error)
        {
            totalDataBytes = 0;
            firstLinearAddress = null;
            payloadCrc = 0;
            error = string.Empty;

            uint upperLinear = 0;
            bool capturedBase = false;
            var payloadBuffer = new List<byte>(65536);
            int validRecords = 0;

            foreach (string rawLine in File.ReadLines(hexPath, Encoding.ASCII))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;
                if (!line.StartsWith(":", StringComparison.Ordinal) || line.Length < 11)
                {
                    error = "Intel HEX 格式校验失败：存在非 ':' 起始或长度不足的记录行。";
                    return false;
                }

                if (!byte.TryParse(line.AsSpan(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte byteCount) ||
                    !ushort.TryParse(line.AsSpan(3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort offsetAddr) ||
                    !byte.TryParse(line.AsSpan(7, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte recordType))
                {
                    error = "Intel HEX 记录头十六进制解析失败。";
                    return false;
                }

                if (line.Length < 11 + byteCount * 2)
                {
                    error = "Intel HEX 数据段字节长度不匹配。";
                    return false;
                }

                validRecords++;

                if (recordType == 0x00) // Data Record
                {
                    if (!capturedBase)
                    {
                        uint fullAddr = (upperLinear << 16) | offsetAddr;
                        firstLinearAddress = $"0x{fullAddr:X8}";
                        capturedBase = true;
                    }

                    for (int i = 0; i < byteCount; i++)
                    {
                        if (byte.TryParse(line.AsSpan(9 + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
                        {
                            payloadBuffer.Add(b);
                        }
                    }
                    totalDataBytes += byteCount;
                }
                else if (recordType == 0x04 && byteCount == 2) // Extended Linear Address
                {
                    if (ushort.TryParse(line.AsSpan(9, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort hi))
                    {
                        upperLinear = hi;
                    }
                }
                else if (recordType == 0x02 && byteCount == 2) // Extended Segment Address
                {
                    if (ushort.TryParse(line.AsSpan(9, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort seg) && !capturedBase)
                    {
                        firstLinearAddress = $"0x{((uint)seg << 4):X8}";
                    }
                }
                else if (recordType == 0x01) // EOF
                {
                    break;
                }
            }

            if (validRecords == 0)
            {
                error = "Intel HEX 文件为空。";
                return false;
            }

            payloadCrc = ComputeCrc32(payloadBuffer.ToArray());
            return true;
        }

        public static uint ComputeCrc32(byte[] data)
        {
            uint crc = 0xFFFFFFFF;
            for (int i = 0; i < data.Length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0u);
                }
            }
            return ~crc;
        }

        private void LoadManifest()
        {
            lock (_vaultLock)
            {
                _entries.Clear();
                try
                {
                    if (File.Exists(_manifestPath))
                    {
                        string json = File.ReadAllText(_manifestPath, Encoding.UTF8);
                        var loaded = JsonSerializer.Deserialize<List<FirmwareImageEntry>>(json);
                        if (loaded != null)
                        {
                            foreach (var item in loaded)
                            {
                                if (!string.IsNullOrWhiteSpace(item.StoredPath) && File.Exists(item.StoredPath))
                                {
                                    _entries.Add(item);
                                }
                            }
                        }
                    }
                }
                catch
                {
                }
            }
        }

        private void SaveManifestCore()
        {
            try
            {
                string json = JsonSerializer.Serialize(_entries, JsonOpts);
                File.WriteAllText(_manifestPath, json, Encoding.UTF8);
            }
            catch
            {
            }
        }
    }
}

