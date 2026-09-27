# LinkNexus 硬件端口映射与引脚重映射配置指南

> **面向对象**：PCB 硬件工程师、嵌入式固件开发人员、生产测试工程师  
> **适用机型**：LinkNexus 多通道硬件调试、隔离烧录与桌面 USB 集线器系列  
> **配置核心**：`HardwareConfig.json`（支持上位机运行期热重载与免编译适配）

---

## 1. 为什么需要进行硬件端口解耦与重映射？

在多通道硬件调试与高速隔离集线器（如搭载 **CH338X** / USB2517 等工业级 Hub 芯片）的 PCB 设计中：

1. **差分对阻抗与等长最佳布线（PCB Routing Optimization）**：
   - 高速 USB 差分走线（$D+/D-$）对拓扑、层叠与过孔极其敏感。为了降低串扰、避免层间换层并实现最平滑的走线，**硬件工程师通常会根据芯片引脚与外设接口在板卡上的物理方位就近扇出连线**。
   - 这会导致 **Hub 的物理 Port 编号（如 Port 1 ~ Port 7）与逻辑功能通道（如 XDS110、DAPLink、FT2232、扩展 USB 接口）无法保持严格的按序对应**。
2. **PCB 版本迭代与打样跳线（Hardware Revisions & ECO）**：
   - 当硬件改版、更替烧录芯片或由于引脚冲突进行割线/飞线跳换时，物理端口对应的逻辑功能会发生改变。
3. **软硬件解耦目标**：
   - **严禁在上位机 C# 源码或 ESP32 固件中写死物理端口号与通道索引**。
   - 所有物理端口与逻辑卡片、INA236 采样通道、ESP32 供电开关控制引脚全部交由外部配置文件 `HardwareConfig.json` 驱动。**改版硬件时只需修改 JSON 配置文件，上位机无需重新编译，秒级平滑适配！**

```
┌────────────────────────────────────────────────────────────────────────┐
│                        LinkNexus 硬件与逻辑拓扑解耦架构图                        │
└────────────────────────────────────────────────────────────────────────┘

    [ 板载芯片 / 外部插口 ]                [ CH338X 物理引脚 ]             [ 上位机逻辑卡片与屏幕 ]
    
    TI XDS110 隔离烧录器  ────(PCB最佳走线)───► Hub Port 1  ──────(映射)─────► CH 01 卡片 [INA236 #1]
    ARM DAPLink 仿真器    ────(PCB最佳走线)───► Hub Port 2  ──────(映射)─────► CH 02 卡片 [INA236 #2]
    FT2232 高速调试器     ────(PCB最佳走线)───► Hub Port 3  ──────(映射)─────► CH 03 卡片 [5V 固定基准]
    高速虚拟串口 (VCP)    ────(PCB最佳走线)───► Hub Port 4  ──────(映射)─────► CH 04 卡片 [串口终端]
    USB 下行扩展口 1      ────(PCB最佳走线)───► Hub Port 5  ──────(映射)─────► EXT 1 卡片 [设备插拔感应]
    USB 下行扩展口 2      ────(PCB最佳走线)───► Hub Port 6  ──────(映射)─────► EXT 2 卡片 [设备插拔感应]
    ESP32-S3 管理控制器   ────(板载通信CDC)───► Hub Port 7  ──────(映射)─────► 全双工协议总线 & 1.9"TFT
```

---

## 2. 如何在 Windows 中确定设备的物理端口路径（Location Paths）

在调试新硬件或验证端口映射时，需要确认某个物理端口在 Windows 设备管理器中所对应的拓扑节点：

### 步骤详解：

1. **打开设备管理器**：
   - 按下快捷键 `Win + X`，选择 **设备管理器**（或运行 `devmgmt.msc`）。
2. **切换为连接排序视图**：
   - 点击顶部菜单栏的 **查看 (View)** -> 选择 **依连接排序设备 (Devices by connection)**。
   - 顺着主干展开：`基于 ACPI 的 PC` -> `PCI Express 根联合体` -> `USB 3.0/3.1 eXtensible 主机控制器` -> `USB 根集线器 (USB Root Hub)`。
3. **定位 LinkNexus 主 Hub 节点（CH338X）**：
   - 找到名为 `Generic USB Hub` 或 `USB 2.0 Hub` 的节点，展开即可看到其下挂接的 7 个下行端口。
4. **读取“位置路径 (Location Paths)”与“端口号”**：
   - 右键任意下行子设备 -> 选择 **属性 (Properties)** -> 切换至 **详细信息 (Details)** 选项卡。
   - 在“属性”下拉菜单中选择 **位置路径 (Location Paths)**，格式通常为：
     ```text
     PCIROOT(0)#PCI(1400)#USBROOT(0)#USB(1)#USB(4)
     ```
     其中末尾的 `#USB(4)` 即代表该设备挂载在 CH338X Hub 的 **第 4 物理端口（Port 4）**。
   - 或查看属性中的 **硬件 ID (Hardware Ids)**，获取 VID 与 PID（如 `USB\VID_0451&PID_BEF3` 对应 TI XDS110，`USB\VID_0D28&PID_0204` 对应 DAPLink）。

---

## 3. `HardwareConfig.json` 字段规范与配置范例

配置文件位于上位机执行程序同级目录下（`HardwareConfig.json`），上位机内置文件监听器（`FileSystemWatcher`），**修改保存后上位机自动热重载，无需重启软件**。

### 核心配置文件完整格式：

```json
{
  "Version": "26739.0100",
  "DeviceModel": "LinkNexus-Pro-CH338X",
  "Esp32CdcPort": "COM_AUTO",
  "Esp32BaudRate": 115200,
  "HubPortMappings": [
    {
      "PhysicalPortNumber": 1,
      "LogicalChannelType": "XDS110",
      "PowerControlChannelIndex": 0,
      "TelemetryChannelIndex": 1,
      "NominalVoltage": 3.3,
      "DisplayName": "TI XDS110 烧录器",
      "LocationPathKeywords": [ "Port1", "VID_0451" ],
      "HasCurrentSensor": true
    },
    {
      "PhysicalPortNumber": 2,
      "LogicalChannelType": "DAPLink",
      "PowerControlChannelIndex": 1,
      "TelemetryChannelIndex": 2,
      "NominalVoltage": 3.3,
      "DisplayName": "ARM DAPLink 仿真器",
      "LocationPathKeywords": [ "Port2", "VID_0D28" ],
      "HasCurrentSensor": true
    },
    {
      "PhysicalPortNumber": 3,
      "LogicalChannelType": "FT2232",
      "PowerControlChannelIndex": 2,
      "TelemetryChannelIndex": null,
      "NominalVoltage": 5.0,
      "DisplayName": "FT2232 多功能调试器",
      "LocationPathKeywords": [ "Port3", "VID_0403" ],
      "HasCurrentSensor": false
    },
    {
      "PhysicalPortNumber": 4,
      "LogicalChannelType": "UART",
      "PowerControlChannelIndex": 3,
      "TelemetryChannelIndex": null,
      "NominalVoltage": 3.3,
      "DisplayName": "高速虚拟串口 (VCP)",
      "LocationPathKeywords": [ "Port4", "VID_1A86", "VID_10C4" ],
      "HasCurrentSensor": false
    },
    {
      "PhysicalPortNumber": 5,
      "LogicalChannelType": "Downstream_1",
      "PowerControlChannelIndex": 4,
      "TelemetryChannelIndex": null,
      "NominalVoltage": 5.0,
      "DisplayName": "USB 2.0 下行端口 1",
      "LocationPathKeywords": [ "Port5" ],
      "HasCurrentSensor": false
    },
    {
      "PhysicalPortNumber": 6,
      "LogicalChannelType": "Downstream_2",
      "PowerControlChannelIndex": 5,
      "TelemetryChannelIndex": null,
      "NominalVoltage": 5.0,
      "DisplayName": "USB 2.0 下行端口 2",
      "LocationPathKeywords": [ "Port6" ],
      "HasCurrentSensor": false
    },
    {
      "PhysicalPortNumber": 7,
      "LogicalChannelType": "ESP32_CDC",
      "PowerControlChannelIndex": -1,
      "TelemetryChannelIndex": null,
      "NominalVoltage": 3.3,
      "DisplayName": "ESP32-S3 管理控制器",
      "LocationPathKeywords": [ "Port7", "VID_303A" ],
      "HasCurrentSensor": false
    }
  ],
  "TelemetrySensors": [
    {
      "TelemetryIndex": 0,
      "SensorName": "整机主总线",
      "ChipType": "INA236",
      "I2CAddress": "0x40",
      "MaxVoltage": 24.0,
      "MaxCurrent": 5000.0
    },
    {
      "TelemetryIndex": 1,
      "SensorName": "XDS110 独立采样",
      "ChipType": "INA236",
      "I2CAddress": "0x41",
      "MaxVoltage": 5.5,
      "MaxCurrent": 2000.0
    },
    {
      "TelemetryIndex": 2,
      "SensorName": "DAPLink 独立采样",
      "ChipType": "INA236",
      "I2CAddress": "0x42",
      "MaxVoltage": 5.5,
      "MaxCurrent": 2000.0
    }
  ]
}
```

### 字段说明表：

| 字段名称 | 类型 | 说明与硬件约束 |
| :--- | :--- | :--- |
| `PhysicalPortNumber` | 整数 (1~7) | CH338X 芯片引脚对应的下行物理端口号。 |
| `LogicalChannelType` | 字符串 | 绑定的上位机逻辑功能：`XDS110` / `DAPLink` / `FT2232` / `UART` / `Downstream_1` / `Downstream_2` / `ESP32_CDC`。 |
| `PowerControlChannelIndex` | 整数 (0~5) | ESP32-S3 控制 MOS 管通断的 `0x30` 指令通道索引（`-1` 表示板载直通不控电）。 |
| `TelemetryChannelIndex` | 整数或 null | **硬件采样约束**：仅总线(0)、XDS110(1)、DAPLink(2) 具备 INA236 物理传感器；无采样电路的端口**必须显式填 `null`**。 |
| `HasCurrentSensor` | 布尔值 | 是否具备物理电流检测。为 `false` 时上位机完全隐去电流/功率占位，界面更整洁。 |
| `NominalVoltage` | 浮点数 | 供电基准标称电压（如 `3.3` 或 `5.0`）。 |
| `DisplayName` | 字符串 | 上位机卡片标题及推送到 ESP32 1.9 寸屏幕上显示的中文名称。 |
| `LocationPathKeywords` | 字符串数组 | Windows 即插即用（PnP）检测外设匹配的关键字（VID/PID 或位置路径）。 |

---

## 4. 实战改版迁移案例

### 场景：PCB V2.2 改版走线微调
> **硬件变动说明**：在 V2.2 硬件设计中，为了优化高频走线，硬件工程师将原接在 Hub Port 3 的 **FT2232** 与原接在 Hub Port 5 的 **下行端口 1** 互换了走线位置；同时 FT2232 的 MOS 管供电控制引脚切到了 ESP32 的 Channel 4。

### 操作步骤（无需重新编译 C# 代码）：

1. 打开软件目录下的 `HardwareConfig.json`。
2. 找到 `PhysicalPortNumber: 3` 与 `PhysicalPortNumber: 5` 的配置块。
3. **互换逻辑绑定与控制索引**：
   ```json
   // 将 Port 3 修改为下行端口 1
   {
     "PhysicalPortNumber": 3,
     "LogicalChannelType": "Downstream_1",
     "PowerControlChannelIndex": 4,
     "TelemetryChannelIndex": null,
     "NominalVoltage": 5.0,
     "DisplayName": "USB 2.0 下行端口 1",
     "LocationPathKeywords": [ "Port3" ],
     "HasCurrentSensor": false
   },
   // 将 Port 5 修改为 FT2232
   {
     "PhysicalPortNumber": 5,
     "LogicalChannelType": "FT2232",
     "PowerControlChannelIndex": 2,
     "TelemetryChannelIndex": null,
     "NominalVoltage": 5.0,
     "DisplayName": "FT2232 多功能调试器",
     "LocationPathKeywords": [ "Port5", "VID_0403" ],
     "HasCurrentSensor": false
   }
   ```
4. 保存 `HardwareConfig.json`。
5. **上位机即时响应**：
   - 界面上点击“USB 2.0 下行端口 1”时，上位机将准确监听 Port 3 的插入/拔出事件；
   - 点击供电开关时，ESP32 准确切断对应的 MOS 管通路；
   - 屏幕同步信息准确上报。

---

## 5. ESP32-S3 全双工二进制通信协议规范

上位机与 ESP32-S3 之间通过 USB-CDC 虚拟串口进行全双工防粘包二进制帧交互：

### 帧格式：
```
+---------------+---------------+---------------+---------------+-------------------+---------------+
| 帧头 1 (0xAA) | 帧头 2 (0x55) | 命令字 (Cmd)  | 载荷长度 (Len)| 有效载荷 (Payload)| 校验和 (Sum)  |
| 1 Byte        | 1 Byte        | 1 Byte        | 1 Byte        | N Bytes           | 1 Byte        |
+---------------+---------------+---------------+---------------+-------------------+---------------+
```
- **校验和计算公式**：
  $$\text{Checksum} = (0xAA + 0x55 + \text{Cmd} + \text{Len} + \sum_{i=0}^{N-1} \text{Payload}[i]) \ \& \ 0xFF$$

### 核心命令字列表：

1. **`0x01` 握手与心跳包**：
   - **Host -> ESP32**：`[0xAA, 0x55, 0x01, 0x01, Seq, Sum]`
   - **ESP32 -> Host**：`[0xAA, 0x55, 0x01, 0x01, 0x06 (ACK), Sum]`
2. **`0x10` INA236 遥测数据上报 (ESP32 -> Host)**：
   - 载荷长度：定长 12 字节（3 组传感器，低字节在前 Little-Endian）：
     - `Byte 0..1`: 总线电压（uint16_t mV）
     - `Byte 2..3`: 总线电流（int16_t mA）
     - `Byte 4..5`: XDS110 电压（uint16_t mV）
     - `Byte 6..7`: XDS110 电流（int16_t mA）
     - `Byte 8..9`: DAPLink 电压（uint16_t mV）
     - `Byte 10..11`: DAPLink 电流（int16_t mA）
3. **`0x20` 屏幕状态同步推送 (Host -> ESP32)**：
   - 载荷格式：
     - `Byte 0`: 当前选中的激活通道索引（0~5）
     - `Byte 1`: 当前固件烧录进度（0~100 %）
     - `Byte 2`: 运行状态标记（Bit 0: 烧录中, Bit 1: 正常连接）
     - `Byte 3`: 设备名称字符串长度 $M$
     - `Byte 4..4+M-1`: UTF-8 编码的外设友好名称（用于 ST7789V3 液晶屏显示）
4. **`0x30` 通道供电控制 (Host -> ESP32)**：
   - 载荷格式：
     - `Byte 0`: 控制通道索引（0~5）
     - `Byte 1`: 状态（`0x01`: 供电导通，`0x00`: 断电隔离）
   - **ESP32 反馈 ACK**：原样返回控制通道及执行状态确认。

---

## 6. 常见问题排查与技术支持

- **Q: 为什么插入 USB 存储设备后，卡片没有变色或依然显示“等待设备接入...”？**  
  **A**: 请进入设备管理器查看该设备实际挂载的 Hub 端口，并核对 `HardwareConfig.json` 中该端口的 `LocationPathKeywords` 是否包含了正确的识别符；若 PCB 调换了端口，请更新对应的 `PhysicalPortNumber`。
- **Q: 未连接 ESP32 下位机时上位机能否用于界面演示或独立串口调试？**  
  **A**: 可以。上位机内置了高精度电气仿真引擎，未接入硬件时自动进入“仿真演示模式”，所有 INA236 遥测仪表均会生成高度逼真的工业扰动波形；同时右侧“高速虚拟串口”工作台可直接连接任意第三方 USB 串口独立工作。

