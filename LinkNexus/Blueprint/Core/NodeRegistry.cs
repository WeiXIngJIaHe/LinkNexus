using System;
using System.Collections.Generic;
using System.Linq;
using LinkNexus.Blueprint.Core;
using LinkNexus.Blueprint.Nodes;

namespace LinkNexus.Blueprint.Core
{
    /// <summary>
    /// 节点元数据定义 (用于 Blender/UE5 模糊搜索菜单与拉线自适应匹配)
    /// </summary>
    public sealed class NodeRegistryItem
    {
        public string Key { get; }
        public string Title { get; }
        public string Category { get; }
        public string Description { get; }
        public string ColorHex { get; }
        public string IconSvg { get; }
        public string[] Keywords { get; }
        public PinDataType[] InputPinTypes { get; }
        public PinDataType[] OutputPinTypes { get; }
        public Func<double, double, NodeBase> Factory { get; }

        public NodeRegistryItem(
            string key,
            string title,
            string category,
            string description,
            string colorHex,
            string iconSvg,
            string[] keywords,
            PinDataType[] inputPinTypes,
            PinDataType[] outputPinTypes,
            Func<double, double, NodeBase> factory)
        {
            Key = key;
            Title = title;
            Category = category;
            Description = description;
            ColorHex = colorHex;
            IconSvg = iconSvg;
            Keywords = keywords ?? Array.Empty<string>();
            InputPinTypes = inputPinTypes ?? Array.Empty<PinDataType>();
            OutputPinTypes = outputPinTypes ?? Array.Empty<PinDataType>();
            Factory = factory;
        }

        public bool Matches(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            string q = query.Trim().ToLowerInvariant();

            if (Title.ToLowerInvariant().Contains(q)) return true;
            if (Key.ToLowerInvariant().Contains(q)) return true;
            if (Category.ToLowerInvariant().Contains(q)) return true;
            if (Description.ToLowerInvariant().Contains(q)) return true;

            foreach (var kw in Keywords)
            {
                if (kw.ToLowerInvariant().Contains(q)) return true;
            }

            // 拼音缩写特例匹配 (如 "jf" 匹配 "积分", "km" 匹配 "卡尔曼", "ys" 匹配 "运算")
            if (q == "jf" && (Title.Contains("积分") || Key.Contains("calculus"))) return true;
            if (q == "ds" && (Title.Contains("导数") || Title.Contains("微分"))) return true;
            if (q == "km" && (Title.Contains("卡尔曼") || Key.Contains("kalman"))) return true;
            if (q == "jz" && (Title.Contains("矩阵") || Key.Contains("matrix"))) return true;
            if (q == "nh" && (Title.Contains("拟合") || Key.Contains("fit"))) return true;
            if (q == "bl" && (Title.Contains("标量") || Key.Contains("scalar"))) return true;
            if ((q == "+" || q == "add") && Title.Contains("标量数学")) return true;

            return false;
        }
    }

    /// <summary>
    /// 全局全栈卡片库中央注册表 (NodeRegistry)
    /// 包含高阶数学、母舰硬件、数据转换、控制联锁与示波等全部卡片
    /// </summary>
    public static class NodeRegistry
    {
        private static readonly List<NodeRegistryItem> _items = new();

        public static IReadOnlyList<NodeRegistryItem> AllItems => _items;

        static NodeRegistry()
        {
            RegisterDefaults();
        }

        private static void RegisterDefaults()
        {
            // ==========================================
            // 1. Math & Calculus (Amber/Gold Gradient #D97706)
            // ==========================================
            _items.Add(new NodeRegistryItem(
                "scalar_math",
                "标量数学算子 (ScalarMath)",
                "Math & Calculus",
                "仿 Blender 一卡多模标量运算：Add, Subtract, Multiply, Divide, MapRange, Deadzone, Atan2",
                "#D97706",
                "M19,13H13V19H11V13H5V11H11V5H13V11H19V13Z",
                new[] { "math", "scalar", "add", "subtract", "multiply", "divide", "+", "-", "*", "/", "jia", "jian", "cheng", "chu", "ys", "bl" },
                new[] { PinDataType.FloatStream },
                new[] { PinDataType.FloatStream },
                (x, y) => new ScalarMathNode(x, y)
            ));

            _items.Add(new NodeRegistryItem(
                "realtime_calculus",
                "硬件时序微积分 (Calculus)",
                "Math & Calculus",
                "基于微秒时间戳 dt 执行梯形积分 (防温漂抗饱和) 与不完全一阶/二阶滤波微分 (抑制量化噪声)",
                "#D97706",
                "M19,3H5C3.89,3 3,3.89 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5C21,3.89 20.1,3 19,3M9,17H7V7H9V17M13,17H11V11H13V17M17,17H15V13H17V17Z",
                new[] { "calculus", "integral", "derivative", "jifen", "daoshu", "weifen", "jf", "ds", "dirty derivative" },
                new[] { PinDataType.FloatStream, PinDataType.Trigger },
                new[] { PinDataType.FloatStream },
                (x, y) => new RealtimeCalculusNode(x, y)
            ));

            _items.Add(new NodeRegistryItem(
                "matrix_transform",
                "矩阵与空间变换 (Matrix 3x3/4x4)",
                "Math & Calculus",
                "基于 MathNet.Numerics.LinearAlgebra，解算 Y = M * X + B，内置旋转、磁力计校准逆矩阵与麦轮底盘分配",
                "#D97706",
                "M4,4H20A2,2 0 0,1 22,6V18A2,2 0 0,1 20,20H4A2,2 0 0,1 2,18V6A2,2 0 0,1 4,4M4,6V18H20V6H4M6,8H8V10H6V8M10,8H14V10H10V8M16,8H18V10H16V8M6,12H8V14H6V12M10,12H14V14H10V12M16,12H18V14H16V12M6,16H8V18H6V16M10,16H14V18H10V16M16,16H18V18H16V16Z",
                new[] { "matrix", "linear", "algebra", "transform", "vector", "juzhen", "jz", "space", "kinematics" },
                new[] { PinDataType.Vector3Stream, PinDataType.FloatStream },
                new[] { PinDataType.Vector3Stream, PinDataType.FloatStream },
                (x, y) => new MatrixTransformNode(x, y)
            ));

            _items.Add(new NodeRegistryItem(
                "data_to_function_fit",
                "多项式拟合与代码生成 (CurveFit)",
                "Math & Calculus",
                "调用 MathNet 最小二乘拟合函数 f(x) 并计算 R²，一键复制霍纳法则 C 校准函数至剪贴板",
                "#D97706",
                "M16,6L18.29,8.29L13.41,13.17L9.41,9.17L2,16.59L3.41,18L9.41,12L13.41,16L19.71,9.71L22,12V6H16Z",
                new[] { "fit", "polynomial", "spline", "r2", "curve", "nihe", "nh", "horner", "c code" },
                new[] { PinDataType.FloatStream },
                new[] { PinDataType.FloatStream },
                (x, y) => new DataToFunctionFitNode(x, y)
            ));

            // ==========================================
            // 2. Motherbase & Hardware (Crimson Gradient #E11D48)
            // ==========================================
            _items.Add(new NodeRegistryItem(
                "motherbase_hub",
                "母舰全能源总线底座 (MotherbaseHub)",
                "Motherbase & Hardware",
                "封装 CH343P 6Mbps UART、FT2232 与 INA236 功耗流，输出 RawBytes、Vbus、Ibus 与硬件微秒戳",
                "#E11D48",
                "M12,2L1,21H23L12,2M12,6L19.5,19H4.5L12,6M11,10V14H13V10H11M11,16V18H13V16H11Z",
                new[] { "motherbase", "hub", "hardware", "ch343", "ft2232", "ina236", "mj", "dizuo" },
                Array.Empty<PinDataType>(),
                new[] { PinDataType.ByteStream, PinDataType.FloatStream, PinDataType.ChannelStream },
                (x, y) => new MotherbaseHubNode(x, y)
            ));

            _items.Add(new NodeRegistryItem(
                "swd_memory_peeker",
                "SWD 变量内存读写器 (SwdPeeker)",
                "Motherbase & Hardware",
                "DAPLink / XDS110 无侵入读取单片机 RAM 变量，具备内联 WriteValueIn 引脚闭环写回内存",
                "#BE123C",
                "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M13,13H11V18H13V13M12,9A1.5,1.5 0 0,1 13.5,10.5A1.5,1.5 0 0,1 12,12A1.5,1.5 0 0,1 10.5,10.5A1.5,1.5 0 0,1 12,9Z",
                new[] { "swd", "dap", "daplink", "xds110", "peeker", "ram", "memory", "tzt", "cankao" },
                new[] { PinDataType.FloatStream },
                new[] { PinDataType.FloatStream },
                (x, y) => new SwdMemoryPeekerNode(x, y)
            ));

            _items.Add(new NodeRegistryItem(
                "captouch_slider",
                "CAP1208 物理滑条映射 (TouchSlider)",
                "Motherbase & Hardware",
                "读取底座电容滑条 0~1 亚毫米物理位置，带平滑阻尼低通与 [Min, Max] 映射，支持物理手搓调参",
                "#DC2626",
                "M3,5H21V7H3V5M3,11H21V13H3V11M3,17H21V19H3V17M7,4A2,2 0 0,0 5,6A2,2 0 0,0 7,8A2,2 0 0,0 9,6A2,2 0 0,0 7,4M15,10A2,2 0 0,0 13,12A2,2 0 0,0 15,14A2,2 0 0,0 17,12A2,2 0 0,0 15,10M11,16A2,2 0 0,0 9,18A2,2 0 0,0 11,20A2,2 0 0,0 13,18A2,2 0 0,0 11,16Z",
                new[] { "touch", "cap1208", "slider", "huatiao", "ht", "shoucuo", "tiaocan" },
                Array.Empty<PinDataType>(),
                new[] { PinDataType.FloatStream },
                (x, y) => new CapTouchSliderNode(x, y)
            ));

            // ==========================================
            // 3. Converter & Math (Cyan/Amber Gradient #06B6D4)
            // ==========================================
            _items.Add(new NodeRegistryItem(
                "protocol_demux",
                "协议分流解包器 (ProtocolDemux)",
                "Converter & Math",
                "仿 Blender Separate XYZ：解包 VOFA_FireWater / JustFloat / Hex 结构体，动态生成 CH0-3 与 Vector3Stream",
                "#06B6D4",
                "M12,2L2,7L12,12L22,7L12,2M2,17L12,22L22,17L12,12L2,17M2,12L12,17L22,12L12,7L2,12Z",
                new[] { "demux", "separate", "protocol", "vofa", "justfloat", "fenliu", "fl", "jiebao" },
                new[] { PinDataType.ByteStream, PinDataType.ChannelStream },
                new[] { PinDataType.FloatStream, PinDataType.Vector3Stream },
                (x, y) => new ProtocolDemuxNode(x, y)
            ));

            _items.Add(new NodeRegistryItem(
                "time_aligner",
                "异构总线微秒时序对齐 (TimeAligner)",
                "Converter & Math",
                "对齐异构采样率 (如 1kHz SPI 与 100Hz 串口)，基于微秒时间戳线性插值重采样，输出同频同相数据元组",
                "#0891B2",
                "M15,1H9V3H15V1M11,14H13V8H11V14M19.03,7.39L20.45,5.97C20,5.46 19.55,5 19.04,4.56L17.62,6C16.07,4.74 14.12,4 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20A8,8 0 0,0 20,12C20,9.88 19.26,7.93 18.03,6.39L19.03,7.39M12,18A6,6 0 0,1 6,12A6,6 0 0,1 12,6A6,6 0 0,1 18,12A6,6 0 0,1 12,18Z",
                new[] { "align", "sync", "time", "shixu", "duiqi", "interpolator", "sx" },
                new[] { PinDataType.FloatStream },
                new[] { PinDataType.FloatStream },
                (x, y) => new MultiStreamTimeAlignerNode(x, y)
            ));

            _items.Add(new NodeRegistryItem(
                "inline_math_python",
                "表达式与 Python 沙盒 (InlineSandbox)",
                "Converter & Math",
                "轻量模式支持 CodingSeb 动态公式求值，重载模式支持本地 .py 脚本毫秒热重载与 [M] Bypass 直通对比",
                "#F59E0B",
                "M19,3H5A2,2 0 0,0 3,5V19A2,2 0 0,0 5,21H19A2,2 0 0,0 21,19V5A2,2 0 0,0 19,3M19,19H5V5H19V19M13.5,13.5L10,17L8.5,15.5L11,13L8.5,10.5L10,9L13.5,12.5L15,11V16H13.5V13.5Z",
                new[] { "math", "python", "py", "sandbox", "expression", "eval", "suanzi", "bds" },
                new[] { PinDataType.FloatStream },
                new[] { PinDataType.FloatStream },
                (x, y) => new InlineMathAndPythonNode(x, y)
            ));

            // ==========================================
            // 4. Control & AI MCP (Purple Gradient #9333EA)
            // ==========================================
            _items.Add(new NodeRegistryItem(
                "interlock_fuse",
                "软硬联锁断路保护器 (InterlockFuse)",
                "Control & AI MCP",
                "微秒级监测母线电流与转速，过流/堵转消抖判定触发白色 ExecTrigger 切断供电、冻结快照转储 CrashDump CSV",
                "#9333EA",
                "M11,15H13V17H11V15M11,7H13V13H11V7M12,2C6.47,2 2,6.5 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M12,20A8,8 0 0,1 4,12A8,8 0 0,1 12,4A8,8 0 0,1 20,12A8,8 0 0,1 12,20Z",
                new[] { "fuse", "interlock", "protect", "crashdump", "baohu", "lianlock", "duanlu", "bh", "dl" },
                new[] { PinDataType.FloatStream, PinDataType.Trigger },
                new[] { PinDataType.Trigger, PinDataType.FloatStream },
                (x, y) => new HardwareInterlockFuseNode(x, y)
            ));

            _items.Add(new NodeRegistryItem(
                "mcp_probe_tuner",
                "AI MCP 断面探针与调参器 (McpProbeTuner)",
                "Control & AI MCP",
                "维持 4096 点滑动窗口，实时解算均值/标准差/超调量/FFT 主频，支持反向通道闭环参数自动调优",
                "#7E22CE",
                "M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M12,4A8,8 0 0,1 20,12A8,8 0 0,1 12,20A8,8 0 0,1 4,12A8,8 0 0,1 12,4M12,6A6,6 0 0,0 6,12A6,6 0 0,0 12,18A6,6 0 0,0 18,12A6,6 0 0,0 12,6M12,8A4,4 0 0,1 16,12A4,4 0 0,1 12,16A4,4 0 0,1 8,12A4,4 0 0,1 12,8Z",
                new[] { "mcp", "ai", "agent", "probe", "tuner", "fft", "tanzen", "tz", "tiaocan" },
                new[] { PinDataType.FloatStream, PinDataType.ChannelStream },
                new[] { PinDataType.FloatStream },
                (x, y) => new McpAgentProbeAndTunerNode(x, y)
            ));

            // ==========================================
            // 5. 基础示波、滤波与 IO
            // ==========================================
            _items.Add(new NodeRegistryItem(
                "dsp_filter",
                "NWaves 信号处理 (DspFilter)",
                "Signal & Visualizer",
                "巴特沃斯低通/高通/带通、滑动均值、中值滤波与 FFT 频谱特征提取",
                "#06B6D4",
                "M12,2L2,7L12,12L22,7L12,2M2,17L12,22L22,17L12,12L2,17M2,12L12,17L22,12L12,7L2,12Z",
                new[] { "dsp", "filter", "nwaves", "butterworth", "fft", "lvbo", "lb" },
                new[] { PinDataType.ChannelStream, PinDataType.FloatStream },
                new[] { PinDataType.ChannelStream, PinDataType.FloatStream },
                (x, y) => new DspFilterNode(x, y)
            ));

            _items.Add(new NodeRegistryItem(
                "vofa_plot",
                "VOFA+ 实时波形示波器 (VofaPlot)",
                "Signal & Visualizer",
                "多通道实时示波与波形追踪视窗",
                "#06B6D4",
                "M3,3V21H21V19H5V3H3M7,17H9V10H7V17M11,17H13V7H11V17M15,17H17V13H15V17M19,17H21V4H19V17Z",
                new[] { "vofa", "plot", "scope", "waveform", "shibo", "sb", "bx" },
                new[] { PinDataType.ChannelStream, PinDataType.FloatStream },
                Array.Empty<PinDataType>(),
                (x, y) => new VofaPlotNode(x, y)
            ));

            _items.Add(new NodeRegistryItem(
                "imu_3d",
                "IMU 三维姿态视窗 (Imu3D)",
                "Signal & Visualizer",
                "基于四元数实时驱动 3D 飞行载板三维旋转动捕与相空间轨迹",
                "#A855F7",
                "M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2Z",
                new[] { "imu", "3d", "attitude", "quaternion", "euler", "zitai", "zt" },
                new[] { PinDataType.Vector3Stream, PinDataType.ChannelStream },
                Array.Empty<PinDataType>(),
                (x, y) => new Imu3DPlotNode(x, y)
            ));

            _items.Add(new NodeRegistryItem(
                "com_input",
                "COM 串口数据输入 (ComInput)",
                "I/O & Hardware",
                "必须绑定真实 USB 枚举端口，支持右键动态拆分多通道 CH 数据流",
                "#0EA5E9",
                "M4,2H20A2,2 0 0,1 22,4V20A2,2 0 0,1 20,22H4A2,2 0 0,1 2,20V4A2,2 0 0,1 4,2Z",
                new[] { "com", "uart", "serial", "input", "chuankou", "ck" },
                Array.Empty<PinDataType>(),
                new[] { PinDataType.ChannelStream, PinDataType.FloatStream, PinDataType.ByteStream },
                (x, y) => new ComInputNode("COM3", x, y)
            ));

            _items.Add(new NodeRegistryItem(
                "csv_save",
                "CSV 高频数据刷盘 (CsvSave)",
                "I/O & Hardware",
                "高频无锁环形刷盘，转储时序多通道数据至 CSV",
                "#F59E0B",
                "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M18,20H6V4H13V9H18V20Z",
                new[] { "csv", "save", "log", "file", "shupan", "baocun" },
                new[] { PinDataType.ChannelStream, PinDataType.FloatStream },
                Array.Empty<PinDataType>(),
                (x, y) => new CsvSaveNode(x, y)
            ));
        }

        public static IEnumerable<NodeRegistryItem> Filter(string? query, PinDataType? filterByInputCompatibleType = null)
        {
            var res = _items.AsEnumerable();

            if (filterByInputCompatibleType.HasValue)
            {
                var targetType = filterByInputCompatibleType.Value;
                res = res.Where(item =>
                    item.InputPinTypes.Contains(targetType) ||
                    item.InputPinTypes.Contains(PinDataType.Any) ||
                    (targetType == PinDataType.FloatStream && item.InputPinTypes.Contains(PinDataType.ChannelStream)) ||
                    (targetType == PinDataType.ChannelStream && item.InputPinTypes.Contains(PinDataType.FloatStream)));
            }

            if (!string.IsNullOrWhiteSpace(query))
            {
                res = res.Where(item => item.Matches(query));
            }

            return res;
        }
    }
}
