# LinkNexus — 多协议桌面硬件调试站与智能供电管理中枢

<div align="center">

![Platform](https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011%20(x64)-blue?style=for-the-badge&logo=windows)
![.NET Version](https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet)
![Architecture](https://img.shields.io/badge/Architecture-WPF%20%7C%20MVVM%20%7C%20Win32%20PnP-orange?style=for-the-badge)
![License](https://img.shields.io/badge/License-MIT-green?style=for-the-badge)
![Version](https://img.shields.io/badge/Release-v0107.2639%20(PRE)-38BDF8?style=for-the-badge)

**面向嵌入式软硬件调试的高质感桌面中枢，支持 Windows PnP 动态设备流、流式固件烧录、智能串口 CLI 与硬件拓扑安全控制**

[功能规格说明书](README_FUNCTIONAL.md) • [硬件端口重映射指南](README_PortMapping.md) • [快速构建](#快速构建与运行) • [开发者调试模式](#开发者维护模式-debug-mode-与虚拟接入)

</div>

---

## 项目背景与设计重点

在日常嵌入式硬件研发与打样调试中，桌面常堆叠 JTAG、CMSIS-DAP、TI XDS 探针及各类 USB 转串口板卡，既挤占上位机端口，又因传统工具界面杂乱、缺乏硬件状态互锁而增加操作成本。

**LinkNexus** 聚焦以下核心体验：
- **Windows PnP 真实枚举与动态流**：直接展示 Windows 设备管理器识别的友好名称，不添加虚假通道前缀；设备插拔毫秒级同步，未连接设备不占位。
- **全量 USB 设备全景大厅**：关闭具体工作台或刚启动时，主区域完整平铺所有已连接 USB 硬件的毛玻璃卡片，支持一键直达对应工作页面；无设备时呈现精致磨砂引导。
- **专属工作台自适应路由**：
  * **固件烧录与内核调试**（FT2232 / XDS110 / DAPLink）：固件拖拽、CRC32 校验、架构解析、流式写入、256 字节 Flash 扇区 Hex Dump、整片擦除、CoreID 读取与复位；
  * **串口与 Linux 终端**（CH343P / 通用串口）：暗黑波特率自适应切换、DTR/RTS 硬件流控、CTS/DSR 状态指示、MCU 硬件复位脉冲、文本/HEX/Linux CLI/AT 智能自适应识别分流；
  * **通用设备与外部存储**：展示设备实例路径与底层驱动参数。
- **ESP32-S3 后台守护**：管理总线隐匿于后台运行，主界面左下角以纯粹的绿（在线）/ 红（离线）/ 黄（异常）三态圆点反馈拓扑健康度。
- **开发者维护总控与虚拟仿真**：连续点击版本号 5 次切入全屏维护视图，提供固件一键救砖刷写、4 路 MOS 供电开通/隔离矩阵以及脱机全功能虚拟仿真。
- **战术机能美学与毛玻璃质感**：纯矢量手绘二次元少女与战术拓扑线稿底层背景，配合半透明磨砂亚克力卡片、柔和微光边缘与深浅双主题热切换。

---

## 🏛️ 系统拓扑与分层架构

```mermaid
flowchart TD
    subgraph UI["表现层: 极简工业暗黑 UI (MainWindow.xaml)"]
        TitleBar["沉浸式标题栏 (留白拖拽区 + 刷新总线 + 窗口控制)"]
        PortCards["动态设备卡片平铺 (Windows 真实主名 + 拓扑抽屉收拢)"]
        Workspaces["选定设备专属独立工作台 (烧录内核 / 串口智能终端 / 通用存储)"]
        DebugCenter["全屏开发者维护总台 (固件救砖 / 4路MOS供电 / 虚拟仿真接入)"]
        DaemonBar["底部状态栏 (ESP32 守护服务三态圆点 + 5次点击暗门版本号)"]
    end

    subgraph ViewModel["业务层: MVVM 反应式调度 (MainViewModel.cs)"]
        DeviceManager["动态外设集合与拓扑过滤路由"]
        BurnerEngine["内核烧录流水线 & 扇区 Hex Dump 生成器"]
        SmartInputEngine["智能输入自动识别引擎 (Linux CLI / HEX / AT / 字符串)"]
        HardwarePinManager["DTR/RTS 硬件流控 & MCU 脉冲复位状态机"]
        DebugSimulationHub["虚拟硬件仿真总线 & 固件救砖管理"]
    end

    subgraph Service["系统底层驱动与通信层 (UsbMonitorService.cs)"]
        WndProcHook["Win32 原生消息泵 (WM_DEVICECHANGE 监听)"]
        WmiEngine["WMI 拓扑枚举器 (250ms 动态消抖 + 真实 FriendlyName & 硬件序列号抓取)"]
    end

    subgraph Hardware["硬件层: LinkNexus 物理多协议工作站"]
        Hub["CH338X 工业级 USB Hub 拓扑中枢"]
        P1["FT2232HQ 双通道 JTAG/串口"]
        P2["DAPLink (CH32V305) 仿真器"]
        P3["XDS110 (TM4C1294) 仿真器"]
        P4["CH343P 高速串口 / Linux 终端"]
        P5["ESP32-S3 后台守护主控 & 4 路 MOS 供电矩阵"]
    end

    Hardware --> Service
    Service --> ViewModel
    ViewModel --> UI
```

---

## ⚡ 核心功能与使用指南

### 1. 动态设备卡片与专属工作台
- 软件启动后通过 Win32 PnP 广播与 WMI 全量枚举硬件，顶部平铺当前连接的设备卡片（标题为 Windows 真实友好名称，拔出即消失）；
- 点击卡片即可在下方展开对应专属工作台：
  * **烧录探针 (FT2232 / XDS110 / DAPLink)**：固件拖拽放入、CRC32 校验、芯片架构识别、扇区 Hex 转储、整片擦除、CoreID 捕获、软复位；
  * **串口与 Linux 终端 (CH343P)**：纯暗黑波特率切换、DTR/RTS/CTS/DSR 硬件引脚控制、一键 MCU 脉冲复位、Linux CLI 快捷命令注入条与智能输入栏；
  * **通用设备 (U盘/外设)**：展示硬件 ID 与实例属性。

### 2. 串口智能输入自动识别与双视窗分流
- **无须频繁切换模式**：输入框自动识别用户意图：
  * 识别到常用 Shell 指令（`uname`、`ifconfig`、`dmesg`、`ls`、`top`、`reboot` 等）$\rightarrow$ 标记 `[🐧 Linux CLI]` 并回车执行；
  * 识别到成对十六进制（如 `01 03 00 00 00 02 C4 0B`）$\rightarrow$ 标记 `[🔢 HEX Frame]` 并以原始二进制帧下发；
  * 识别到 `AT+...` $\rightarrow$ 标记 `[📡 AT Command]`；
  * 其他普通文本 $\rightarrow$ 标记 `[📝 Plain Text]`；
- **智能双视窗**：同时展示 Linux CLI 控制台与串口数据监视器，并支持通过中缝 GridSplitter 自由拖拽视窗宽度。

### 3. 开发者维护模式 (Debug Mode) 与虚拟接入
- **唤醒暗门**：1.5 秒内连续点击右下角版本号 5 次唤醒；
- **专属总台**：全屏切入维护视图，提供固件一键救砖刷入与 4 路 CH338X MOS 供电通断控制；
- **虚拟仿真接入**：点击 `[🚀 开启虚拟硬件接入]`，立即弹出 4 大核心仿真引擎卡片，可无硬件离线验证全部烧录、Hex Dump、串口与 Linux CLI 功能。

---

## 🏷️ 版本号规范体系

LinkNexus 采用严格的工程规范版本号定义：

$$\mathbf{v[XX][xx].[YY][WW]\ (XXX)}$$

| 字段 | 含义 | 说明 | 示例 |
| :---: | :--- | :--- | :---: |
| `XX` | 主版本号 (Major) | 架构级或重大重构主版本 (两位补零) | `01` |
| `xx` | 次版本号 (Minor) | 功能特性迭代或小版本更新 (两位补零) | `00` |
| `YY` | 年份后两位 (Year) | 如 2026 年记为 `26` | `26` |
| `WW` | 自然周数 (Week) | ISO 8601 标准自然周 (01 ~ 53) | `39` |
| `XXX` | 发布阶段代码 | `PRE` (预发布/工程版) / `REL` (正式版) / `RC_` (候选版) | `PRE` |

> 当前基线版本：**`v0107.2639 (PRE)`**

---

## 🚀 快速构建与运行

### 环境准备
- 操作系统：Windows 10 / 11 (x64)
- 运行框架：[.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) 或更高版本
- IDE 推荐：Visual Studio 2022+ / Visual Studio Code (C# Dev Kit)

### 编译源码
```bash
# 1. 克隆代码仓库
git clone https://github.com/WeiXIngJIaHe/LinkNexus.git
cd LinkNexus

# 2. 还原 NuGet 依赖
dotnet restore LinkNexus/LinkNexus.csproj

# 3. 编译工程 (Release 优化配置)
dotnet build LinkNexus/LinkNexus.csproj -c Release
```

### 一键脚本发布 (Publish.bat)
仓库根目录下提供了自动化打包脚本 [`Publish.bat`](file:///C:/Users/17368/source/repos/LinkNexus/Publish.bat)：
```bat
# 运行自动化发布脚本生成以下两套交付件：
# 1. 框架依赖轻量包 (约 300 KB): Publish_FrameworkDependent/
# 2. 独立自包含免安装运行包 (内置 .NET 运行时，即开即用): Publish_SelfContained/
Publish.bat
```

---

## 📂 工程核心目录结构

```text
LinkNexus/
├── LinkNexus/
│   ├── MainWindow.xaml              # 极简暗黑工业风格主界面布局 (三态卡片流/互锁区/调试台)
│   ├── MainWindow.xaml.cs           # Win32 原生 WndProc 接管、拖拽事件与功能选项卡路由
│   ├── MainViewModel.cs             # MVVM 反应式调度、CRC32 计算、仿真器状态机与命令绑定
│   ├── PortDeviceModel.cs           # 物理端口数据模型 (端口 #1~#4、三态枚举、MOS 开关)
│   ├── UsbMonitorService.cs         # Win32 RegisterDeviceNotification 注册与 WMI 消抖枚举
│   ├── HardwareConfig.cs            # 硬件配置反序列化实体与热重载管理器
│   ├── HardwareConfig.json          # 物理端口拓扑映射、引脚分配与解耦配置文件
│   ├── ProtocolEngine.cs            # ESP32-S3 全双工二进制防粘包协议编解码引擎
│   └── LinkNexus.csproj             # .NET 10.0 WPF 现代化工程定义文件
├── docs/
│   ├── README_FUNCTIONAL.md         # 详细功能规格与界面、监听转发底层技术规范书
│   └── README_PortMapping.md        # PCB 硬件工程师端口映射与跳线适配指南
├── Publish.bat                      # Windows 一键全量编译与双版本发布批处理脚本
└── README.md                        # 本说明文档
```

---

## ⌨️ 常用快捷键与交互一览

| 触发操作 | 目标动作 | 作用说明 |
| :--- | :--- | :--- |
| **连击 5 次版本号** | 激活开发者调试模式 | 1.5 秒内快速点击右下角规范版本号区域 (开启/关闭) |
| **全窗口拖拽文件** | 固件载入与架构识别 | 拖入 `.bin` / `.hex` / `.json` 文件，自动激活调试模式并解析 CRC32 |
| **标题栏刷新按钮** | 强制刷新总线 | 立即触发一次系统全量 Windows PnP 设备树扫描 |

---

## 📄 开源许可证 (License)

本项目基于 [MIT License](LICENSE) 开源。欢迎各类商业与非商业嵌入式开发、测试仪器集成与教学科研使用。
# LinkNexus