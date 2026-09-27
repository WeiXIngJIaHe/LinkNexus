using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LinkNexus
{
    /// <summary>
    /// 逻辑通道类型枚举
    /// </summary>
    public enum LogicalChannelType
    {
        XDS110,
        DAPLink,
        FT2232,
        UART,
        Downstream_1,
        Downstream_2,
        ESP32_CDC,
        Unknown
    }

    /// <summary>
    /// 物理端口映射条目
    /// 解耦 CH338X Hub 物理端口与上位机逻辑卡片
    /// </summary>
    public class PortMappingEntry
    {
        /// <summary>
        /// 物理 Hub Port 编号（如 1 ~ 7）
        /// </summary>
        [JsonPropertyName("PhysicalPortNumber")]
        public int PhysicalPortNumber { get; set; }

        /// <summary>
        /// 逻辑通道类型字符串
        /// </summary>
        [JsonPropertyName("LogicalChannelType")]
        public string LogicalChannelTypeString { get; set; } = "Unknown";

        [JsonIgnore]
        public LogicalChannelType LogicalType =>
            Enum.TryParse<LogicalChannelType>(LogicalChannelTypeString, true, out var result)
                ? result
                : LogicalChannelType.Unknown;

        /// <summary>
        /// 对应 ESP32 硬件供电使能控制索引 (0x30 命令中的 Channel Index, -1 表示受总线常供电)
        /// </summary>
        [JsonPropertyName("PowerControlChannelIndex")]
        public int PowerControlChannelIndex { get; set; } = -1;

        /// <summary>
        /// 对应的 INA236 遥测数据通道映射 (0: 总线, 1: XDS110, 2: DAPLink, null: 无电流采样)
        /// </summary>
        [JsonPropertyName("TelemetryChannelIndex")]
        public int? TelemetryChannelIndex { get; set; }

        /// <summary>
        /// 标称参考供电电压 (V)
        /// </summary>
        [JsonPropertyName("NominalVoltage")]
        public double NominalVoltage { get; set; } = 3.3;

        /// <summary>
        /// 上位机卡片与屏幕显示名称
        /// </summary>
        [JsonPropertyName("DisplayName")]
        public string DisplayName { get; set; } = string.Empty;

        /// <summary>
        /// Windows 设备路径匹配关键字（VID/PID 或 LocationPath）
        /// </summary>
        [JsonPropertyName("LocationPathKeywords")]
        public List<string> LocationPathKeywords { get; set; } = new();

        /// <summary>
        /// 硬件约束：是否具备物理电流检测传感器 (INA236)
        /// </summary>
        [JsonPropertyName("HasCurrentSensor")]
        public bool HasCurrentSensor { get; set; } = false;
    }

    /// <summary>
    /// INA236 电气采样传感器配置
    /// </summary>
    public class TelemetrySensorConfig
    {
        [JsonPropertyName("TelemetryIndex")]
        public int TelemetryIndex { get; set; }

        [JsonPropertyName("SensorName")]
        public string SensorName { get; set; } = string.Empty;

        [JsonPropertyName("ChipType")]
        public string ChipType { get; set; } = "INA236";

        [JsonPropertyName("I2CAddress")]
        public string I2CAddress { get; set; } = "0x40";

        [JsonPropertyName("MaxVoltage")]
        public double MaxVoltage { get; set; } = 24.0;

        [JsonPropertyName("MaxCurrent")]
        public double MaxCurrent { get; set; } = 3000.0;
    }

    /// <summary>
    /// 硬件总线与端口映射根配置
    /// </summary>
    public class HardwareConfigRoot
    {
        [JsonPropertyName("Version")]
        public string Version { get; set; } = VersionInfo.CurrentVersion;

        [JsonPropertyName("DeviceModel")]
        public string DeviceModel { get; set; } = "LinkNexus-Pro-CH338X";

        [JsonPropertyName("Esp32CdcPort")]
        public string Esp32CdcPort { get; set; } = "COM_AUTO";

        [JsonPropertyName("Esp32BaudRate")]
        public int Esp32BaudRate { get; set; } = 115200;

        [JsonPropertyName("HubPortMappings")]
        public List<PortMappingEntry> HubPortMappings { get; set; } = new();

        [JsonPropertyName("TelemetrySensors")]
        public List<TelemetrySensorConfig> TelemetrySensors { get; set; } = new();
    }

    /// <summary>
    /// 硬件配置加载与热更新服务
    /// </summary>
    public class HardwareConfigManager
    {
        private static readonly Lazy<HardwareConfigManager> _instance = new(() => new HardwareConfigManager());
        public static HardwareConfigManager Instance => _instance.Value;

        public HardwareConfigRoot CurrentConfig { get; private set; } = new();

        public event Action? ConfigReloaded;

        private string _configFilePath = "HardwareConfig.json";
        private FileSystemWatcher? _fileWatcher;

        public HardwareConfigManager()
        {
            LoadConfig();
            SetupWatcher();
        }

        /// <summary>
        /// 加载或重新加载 HardwareConfig.json
        /// </summary>
        public void LoadConfig(string? customPath = null)
        {
            if (!string.IsNullOrWhiteSpace(customPath))
            {
                _configFilePath = customPath;
            }

            try
            {
                string fullPath = Path.IsPathRooted(_configFilePath)
                    ? _configFilePath
                    : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, _configFilePath);

                if (File.Exists(fullPath))
                {
                    string json = File.ReadAllText(fullPath);
                    var options = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        ReadCommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };
                    var config = JsonSerializer.Deserialize<HardwareConfigRoot>(json, options);
                    if (config != null && config.HubPortMappings.Count > 0)
                    {
                        CurrentConfig = config;
                        ConfigReloaded?.Invoke();
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"加载硬件映射配置文件失败: {ex.Message}，采用默认硬件映射。");
            }

            // 若读取失败或文件不存在，加载默认配置以保系统绝对稳定
            CurrentConfig = CreateDefaultConfig();
            ConfigReloaded?.Invoke();
        }

        /// <summary>
        /// 监听配置文件变更，实现热重载 (Hot-Reload)
        /// </summary>
        private void SetupWatcher()
        {
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                string fileName = Path.GetFileName(_configFilePath);

                _fileWatcher = new FileSystemWatcher(dir, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true
                };

                _fileWatcher.Changed += (s, e) =>
                {
                    // 简易去抖动
                    System.Threading.Thread.Sleep(100);
                    LoadConfig();
                };
            }
            catch
            {
                // 忽略监视器异常
            }
        }

        /// <summary>
        /// 根据逻辑通道类型查找映射条目
        /// </summary>
        public PortMappingEntry? GetEntryByLogicalType(LogicalChannelType type)
        {
            return CurrentConfig.HubPortMappings.FirstOrDefault(m => m.LogicalType == type);
        }

        /// <summary>
        /// 根据卡片唯一标识 Key (CH1, CH2, CH3, CH4, EXT1, EXT2) 查找映射
        /// </summary>
        public PortMappingEntry? GetEntryByCardKey(string cardKey)
        {
            return cardKey switch
            {
                "CH1" => GetEntryByLogicalType(LogicalChannelType.XDS110),
                "CH2" => GetEntryByLogicalType(LogicalChannelType.DAPLink),
                "CH3" => GetEntryByLogicalType(LogicalChannelType.FT2232),
                "CH4" => GetEntryByLogicalType(LogicalChannelType.UART),
                "EXT1" => GetEntryByLogicalType(LogicalChannelType.Downstream_1),
                "EXT2" => GetEntryByLogicalType(LogicalChannelType.Downstream_2),
                _ => null
            };
        }

        /// <summary>
        /// 根据物理 Hub Port 编号查找映射
        /// </summary>
        public PortMappingEntry? GetEntryByPhysicalPort(int portNumber)
        {
            return CurrentConfig.HubPortMappings.FirstOrDefault(m => m.PhysicalPortNumber == portNumber);
        }

        /// <summary>
        /// 生成默认硬件布线映射 (CH338X 标准布线)
        /// </summary>
        public static HardwareConfigRoot CreateDefaultConfig()
        {
            return new HardwareConfigRoot
            {
                Version = VersionInfo.CurrentVersion,
                DeviceModel = "LinkNexus-Pro-CH338X",
                Esp32CdcPort = "COM_AUTO",
                Esp32BaudRate = 115200,
                HubPortMappings = new List<PortMappingEntry>
                {
                    new()
                    {
                        PhysicalPortNumber = 1,
                        LogicalChannelTypeString = "XDS110",
                        PowerControlChannelIndex = 0,
                        TelemetryChannelIndex = 1,
                        NominalVoltage = 3.3,
                        DisplayName = "TI XDS110 烧录器",
                        LocationPathKeywords = new() { "Port1", "VID_0451" },
                        HasCurrentSensor = true
                    },
                    new()
                    {
                        PhysicalPortNumber = 2,
                        LogicalChannelTypeString = "DAPLink",
                        PowerControlChannelIndex = 1,
                        TelemetryChannelIndex = 2,
                        NominalVoltage = 3.3,
                        DisplayName = "ARM DAPLink 仿真器",
                        LocationPathKeywords = new() { "Port2", "VID_0D28" },
                        HasCurrentSensor = true
                    },
                    new()
                    {
                        PhysicalPortNumber = 3,
                        LogicalChannelTypeString = "FT2232",
                        PowerControlChannelIndex = 2,
                        TelemetryChannelIndex = null,
                        NominalVoltage = 5.0,
                        DisplayName = "FT2232 多功能调试器",
                        LocationPathKeywords = new() { "Port3", "VID_0403" },
                        HasCurrentSensor = false
                    },
                    new()
                    {
                        PhysicalPortNumber = 4,
                        LogicalChannelTypeString = "UART",
                        PowerControlChannelIndex = 3,
                        TelemetryChannelIndex = null,
                        NominalVoltage = 3.3,
                        DisplayName = "高速虚拟串口 (VCP)",
                        LocationPathKeywords = new() { "Port4", "VID_1A86", "VID_10C4" },
                        HasCurrentSensor = false
                    },
                    new()
                    {
                        PhysicalPortNumber = 5,
                        LogicalChannelTypeString = "Downstream_1",
                        PowerControlChannelIndex = 4,
                        TelemetryChannelIndex = null,
                        NominalVoltage = 5.0,
                        DisplayName = "USB 2.0 下行端口 1",
                        LocationPathKeywords = new() { "Port5" },
                        HasCurrentSensor = false
                    },
                    new()
                    {
                        PhysicalPortNumber = 6,
                        LogicalChannelTypeString = "Downstream_2",
                        PowerControlChannelIndex = 5,
                        TelemetryChannelIndex = null,
                        NominalVoltage = 5.0,
                        DisplayName = "USB 2.0 下行端口 2",
                        LocationPathKeywords = new() { "Port6" },
                        HasCurrentSensor = false
                    },
                    new()
                    {
                        PhysicalPortNumber = 7,
                        LogicalChannelTypeString = "ESP32_CDC",
                        PowerControlChannelIndex = -1,
                        TelemetryChannelIndex = null,
                        NominalVoltage = 3.3,
                        DisplayName = "ESP32-S3 管理控制器",
                        LocationPathKeywords = new() { "Port7", "VID_303A" },
                        HasCurrentSensor = false
                    }
                },
                TelemetrySensors = new List<TelemetrySensorConfig>
                {
                    new()
                    {
                        TelemetryIndex = 0,
                        SensorName = "整机主总线",
                        ChipType = "INA236",
                        I2CAddress = "0x40",
                        MaxVoltage = 24.0,
                        MaxCurrent = 5000.0
                    },
                    new()
                    {
                        TelemetryIndex = 1,
                        SensorName = "XDS110 独立采样",
                        ChipType = "INA236",
                        I2CAddress = "0x41",
                        MaxVoltage = 5.5,
                        MaxCurrent = 2000.0
                    },
                    new()
                    {
                        TelemetryIndex = 2,
                        SensorName = "DAPLink 独立采样",
                        ChipType = "INA236",
                        I2CAddress = "0x42",
                        MaxVoltage = 5.5,
                        MaxCurrent = 2000.0
                    }
                }
            };
        }
    }
}

