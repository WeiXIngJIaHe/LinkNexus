using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LinkNexus.Blueprint.Core
{
    public sealed class PythonAlgoDescriptor
    {
        public string FileName { get; init; } = string.Empty;
        public string DisplayTitle { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public Dictionary<string, double> DefaultParams { get; init; } = new();
    }

    /// <summary>
    /// 外部独立 Python 算法仓库管理与安全沙箱边界服务 (PythonAlgoRepoService)
    /// 1. 算法仓库物理目录固定存放在软件工程及 Git 仓库之外 (%LOCALAPPDATA%\LinkNexus\PythonAlgoRepo)，用户可手动切换外部目录；
    /// 2. 严格白名单校验：仅允许加载仓库根目录下的顶层 *.py 算法文件，禁止路径穿越 (.. / \) 与任意内联代码执行；
    /// 3. 参数调优安全边界：AI Agent 仅可通过 MCP 修改白名单算法的标量数值参数 (double/bool)，彻底杜绝乱调用与代码注入。
    /// </summary>
    public sealed class PythonAlgoRepoService
    {
        private static readonly Regex SafeFileNameRegex = new(@"^[A-Za-z0-9_\-]{1,64}\.py$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SafeParamKeyRegex = new(@"^[A-Za-z_][A-Za-z0-9_]{0,31}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        private static readonly Lazy<PythonAlgoRepoService> LazyInstance = new(() => new PythonAlgoRepoService());
        public static PythonAlgoRepoService Instance => LazyInstance.Value;

        public const string DefaultKalmanFile = "kalman_1d.py";
        public const string DefaultEmaFile = "ema_lowpass.py";
        public const string DefaultMovingAvgFile = "moving_avg.py";
        public const string DefaultImuSmoothFile = "imu_attitude_smooth.py";
        public const string DefaultLinearScaleFile = "linear_scale_offset.py";

        private readonly object _syncLock = new();
        private readonly string _configFilePath;
        private string _repoDirectory;

        public string RepoDirectory
        {
            get
            {
                lock (_syncLock)
                {
                    return _repoDirectory;
                }
            }
        }

        public event Action? RepositoryChanged;

        private PythonAlgoRepoService()
        {
            string appDataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LinkNexus");
            Directory.CreateDirectory(appDataRoot);

            _configFilePath = Path.Combine(appDataRoot, "python_repo_config.json");
            string defaultExternalRepo = Path.Combine(appDataRoot, "PythonAlgoRepo");
            _repoDirectory = LoadConfiguredRepoPath(defaultExternalRepo);

            EnsureExternalRepoProvisioned(_repoDirectory);
        }

        private string LoadConfiguredRepoPath(string fallbackPath)
        {
            try
            {
                if (File.Exists(_configFilePath))
                {
                    string json = File.ReadAllText(_configFilePath, Encoding.UTF8);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("repoDirectory", out var prop))
                    {
                        string? candidate = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(candidate) && IsDirectoryIsolatedFromSoftware(candidate, out _))
                        {
                            return Path.GetFullPath(candidate);
                        }
                    }
                }
            }
            catch
            {
            }
            return Path.GetFullPath(fallbackPath);
        }

        /// <summary>
        /// 仅限本机 UI 用户手动切换外部 Python 算法仓库目录 (MCP Agent 无权调用此方法)
        /// </summary>
        public bool TrySetExternalRepoDirectoryFromUi(string directoryPath, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                errorMessage = "目录路径不能为空。";
                return false;
            }

            if (!IsDirectoryIsolatedFromSoftware(directoryPath, out errorMessage))
            {
                return false;
            }

            try
            {
                string fullDir = Path.GetFullPath(directoryPath);
                Directory.CreateDirectory(fullDir);
                EnsureExternalRepoProvisioned(fullDir);

                lock (_syncLock)
                {
                    _repoDirectory = fullDir;
                    string cfgJson = JsonSerializer.Serialize(new { repoDirectory = fullDir }, JsonOpts);
                    File.WriteAllText(_configFilePath, cfgJson, Encoding.UTF8);
                }

                RepositoryChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = $"无法初始化外部算法目录: {ex.Message}";
                return false;
            }
        }

        public static bool IsDirectoryIsolatedFromSoftware(string candidateDir, out string reason)
        {
            reason = string.Empty;
            try
            {
                string fullCandidate = Path.GetFullPath(candidateDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string appBase = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

                if (fullCandidate.StartsWith(appBase, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "Python 算法仓库必须与软件程序目录区分开，不能放置在软件运行目录内。";
                    return false;
                }

                // 向上探测是否位于包含 .git 的源码仓库中
                DirectoryInfo? cursor = new DirectoryInfo(fullCandidate);
                while (cursor != null)
                {
                    if (Directory.Exists(Path.Combine(cursor.FullName, ".git")))
                    {
                        reason = "Python 算法仓库必须放置在 Git 代码仓库之外，避免测试脚本混入版本控制。";
                        return false;
                    }
                    cursor = cursor.Parent;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        private static void EnsureExternalRepoProvisioned(string dir)
        {
            Directory.CreateDirectory(dir);

            WriteIfMissing(Path.Combine(dir, DefaultKalmanFile),
@"# TITLE: 一维卡尔曼滤波 (1D Kalman)
# DESC: 抑制传感器高频测量毛刺，可由 MCP Agent 根据噪声方差动态调优 q (过程噪声) 与 r (观测噪声)
DEFAULT_PARAMS = {""q"": 0.022, ""r"": 0.180}

def process(channels, params, state):
    q = max(1e-6, float(params.get(""q"", 0.022)))
    r = max(1e-6, float(params.get(""r"", 0.180)))
    xs = state.get(""x"")
    ps = state.get(""p"")
    n = len(channels)
    if xs is None or len(xs) != n:
        xs = [float(v) for v in channels]
        ps = [1.0] * n
    out = []
    for i in range(n):
        p_pred = ps[i] + q
        k = p_pred / (p_pred + r)
        xs[i] = xs[i] + k * (float(channels[i]) - xs[i])
        ps[i] = (1.0 - k) * p_pred
        out.append(round(xs[i], 4))
    state[""x""] = xs
    state[""p""] = ps
    return out
");

            WriteIfMissing(Path.Combine(dir, DefaultEmaFile),
@"# TITLE: 一阶指数低通滤波 (EMA Low-Pass)
# DESC: 轻量级指数平滑算子，alpha 越小平滑力度越强，alpha 越大响应越快 (0.01 ~ 1.0)
DEFAULT_PARAMS = {""alpha"": 0.250}

def process(channels, params, state):
    alpha = min(1.0, max(0.005, float(params.get(""alpha"", 0.250))))
    prev = state.get(""ema"")
    n = len(channels)
    if prev is None or len(prev) != n:
        prev = [float(v) for v in channels]
    out = []
    for i in range(n):
        prev[i] = alpha * float(channels[i]) + (1.0 - alpha) * prev[i]
        out.append(round(prev[i], 4))
    state[""ema""] = prev
    return out
");

            WriteIfMissing(Path.Combine(dir, DefaultMovingAvgFile),
@"# TITLE: 滑动窗口均值滤波 (Moving Average)
# DESC: 周期纹波均值抑制，window 控制滑动采样窗口长度 (2 ~ 64)
DEFAULT_PARAMS = {""window"": 8.0}

def process(channels, params, state):
    win = int(min(64, max(1, round(float(params.get(""window"", 8))))))
    bufs = state.get(""bufs"")
    n = len(channels)
    if bufs is None or len(bufs) != n:
        bufs = [[] for _ in range(n)]
    out = []
    for i in range(n):
        b = bufs[i]
        b.append(float(channels[i]))
        while len(b) > win:
            b.pop(0)
        out.append(round(sum(b) / len(b), 4))
    state[""bufs""] = bufs
    return out
");

            WriteIfMissing(Path.Combine(dir, DefaultImuSmoothFile),
@"# TITLE: IMU 欧拉角跨界平滑 (Attitude Smooth)
# DESC: 针对 Roll/Pitch/Yaw ±180 度跳变保护平滑，支持调优 gain 与 limit_deg
DEFAULT_PARAMS = {""gain"": 0.350, ""limit_deg"": 180.0}

def process(channels, params, state):
    gain = min(1.0, max(0.01, float(params.get(""gain"", 0.350))))
    lim = max(10.0, float(params.get(""limit_deg"", 180.0)))
    prev = state.get(""att"")
    n = len(channels)
    if prev is None or len(prev) != n:
        prev = [float(v) for v in channels]
    out = []
    for i in range(n):
        delta = float(channels[i]) - prev[i]
        if delta > 180.0:
            delta -= 360.0
        elif delta < -180.0:
            delta += 360.0
        val = prev[i] + gain * delta
        while val > lim:
            val -= 2.0 * lim
        while val < -lim:
            val += 2.0 * lim
        prev[i] = val
        out.append(round(val, 3))
    state[""att""] = prev
    return out
");

            WriteIfMissing(Path.Combine(dir, DefaultLinearScaleFile),
@"# TITLE: 线性增益与零偏校准 (Scale & Offset)
# DESC: 对传感器原始物理量执行 y = scale * x + offset 标定变换
DEFAULT_PARAMS = {""scale"": 1.000, ""offset"": 0.000}

def process(channels, params, state):
    scale = float(params.get(""scale"", 1.0))
    offset = float(params.get(""offset"", 0.0))
    return [round(float(v) * scale + offset, 4) for v in channels]
");
        }

        private static void WriteIfMissing(string filePath, string content)
        {
            if (!File.Exists(filePath))
            {
                File.WriteAllText(filePath, content.Replace("\r\n", "\n"), new UTF8Encoding(false));
            }
        }

        public IReadOnlyList<PythonAlgoDescriptor> ListWhitelistedAlgorithms()
        {
            string dir = RepoDirectory;
            if (!Directory.Exists(dir))
            {
                EnsureExternalRepoProvisioned(dir);
            }

            var result = new List<PythonAlgoDescriptor>();
            foreach (string path in Directory.GetFiles(dir, "*.py", SearchOption.TopDirectoryOnly).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                string fileName = Path.GetFileName(path);
                if (!SafeFileNameRegex.IsMatch(fileName) || fileName.StartsWith("_", StringComparison.Ordinal))
                {
                    continue;
                }

                result.Add(ParseDescriptor(path, fileName));
            }
            return result;
        }

        public bool TryResolveWhitelistedFile(string? fileName, out string fullPath, out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(fileName))
            {
                error = "算法文件名不能为空。";
                return false;
            }

            string cleanName = fileName.Trim();
            if (!SafeFileNameRegex.IsMatch(cleanName) || cleanName.StartsWith("_", StringComparison.Ordinal))
            {
                error = $"非法算法文件名 '{cleanName}'：仅允许外部仓库根目录下的白名单 .py 文件名，禁止路径跳转。";
                return false;
            }

            string repoRoot = Path.GetFullPath(RepoDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string candidate = Path.GetFullPath(Path.Combine(repoRoot, cleanName));
            string candidateParent = Path.GetDirectoryName(candidate)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;

            if (!string.Equals(repoRoot, candidateParent, StringComparison.OrdinalIgnoreCase))
            {
                error = "安全拦截：禁止访问外部算法仓库目录以外的路径。";
                return false;
            }

            if (!File.Exists(candidate))
            {
                error = $"外部算法仓库中不存在 '{cleanName}'。";
                return false;
            }

            fullPath = candidate;
            return true;
        }

        public Dictionary<string, double> GetDefaultParamsFor(string? fileName)
        {
            var found = ListWhitelistedAlgorithms().FirstOrDefault(a => string.Equals(a.FileName, fileName, StringComparison.OrdinalIgnoreCase));
            return found != null
                ? new Dictionary<string, double>(found.DefaultParams, StringComparer.Ordinal)
                : new Dictionary<string, double>(StringComparer.Ordinal);
        }

        public static string SerializeParams(IReadOnlyDictionary<string, double> parameters)
        {
            var ordered = new Dictionary<string, double>();
            foreach (var kv in parameters)
            {
                ordered[kv.Key] = Math.Round(kv.Value, 6);
            }
            return JsonSerializer.Serialize(ordered, JsonOpts);
        }

        public static bool TryValidateScalarParamsJson(string? jsonText, out Dictionary<string, double> validated, out string error)
        {
            validated = new Dictionary<string, double>(StringComparer.Ordinal);
            error = string.Empty;

            if (string.IsNullOrWhiteSpace(jsonText))
            {
                return true;
            }

            try
            {
                using var doc = JsonDocument.Parse(jsonText);
                return TryValidateScalarParamsElement(doc.RootElement, out validated, out error);
            }
            catch (Exception ex)
            {
                error = $"参数 JSON 格式无效: {ex.Message}";
                return false;
            }
        }

        public static bool TryValidateScalarParamsElement(JsonElement element, out Dictionary<string, double> validated, out string error)
        {
            validated = new Dictionary<string, double>(StringComparer.Ordinal);
            error = string.Empty;

            if (element.ValueKind != JsonValueKind.Object)
            {
                error = "算法参数必须为扁平 JSON 对象（如 {\"q\": 0.02, \"r\": 0.18}）。";
                return false;
            }

            int count = 0;
            foreach (var prop in element.EnumerateObject())
            {
                count++;
                if (count > 16)
                {
                    error = "单次修改的标量参数数量不得超过 16 项。";
                    return false;
                }

                if (!SafeParamKeyRegex.IsMatch(prop.Name) || prop.Name.StartsWith("__", StringComparison.Ordinal))
                {
                    error = $"非法参数名 '{prop.Name}'：禁止双下划线或特殊字符。";
                    return false;
                }

                double numVal;
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out numVal))
                {
                    if (double.IsNaN(numVal) || double.IsInfinity(numVal) || Math.Abs(numVal) > 1e9)
                    {
                        error = $"参数 '{prop.Name}' 数值超出安全范围。";
                        return false;
                    }
                    validated[prop.Name] = numVal;
                }
                else if (prop.Value.ValueKind == JsonValueKind.True)
                {
                    validated[prop.Name] = 1.0;
                }
                else if (prop.Value.ValueKind == JsonValueKind.False)
                {
                    validated[prop.Name] = 0.0;
                }
                else
                {
                    error = $"参数 '{prop.Name}' 类型被拦截：为防止 Agent 注入代码或路径，仅允许数值或布尔标量。";
                    return false;
                }
            }

            return true;
        }

        public static string? ResolvePythonExecutable()
        {
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string pioPython = Path.Combine(userProfile, ".platformio", "penv", "Scripts", "python.exe");
            if (File.Exists(pioPython)) return pioPython;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programsPyDir = Path.Combine(localAppData, "Programs", "Python");
            if (Directory.Exists(programsPyDir))
            {
                string? found = Directory.GetFiles(programsPyDir, "python.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(found)) return found;
            }

            return "python";
        }

        private static PythonAlgoDescriptor ParseDescriptor(string fullPath, string fileName)
        {
            string title = fileName;
            string desc = "外部 Python 信号处理算子";
            var defaults = new Dictionary<string, double>(StringComparer.Ordinal);

            try
            {
                foreach (string rawLine in File.ReadLines(fullPath, Encoding.UTF8).Take(25))
                {
                    string line = rawLine.Trim();
                    if (line.StartsWith("# TITLE:", StringComparison.OrdinalIgnoreCase))
                    {
                        title = line["# TITLE:".Length..].Trim();
                    }
                    else if (line.StartsWith("# DESC:", StringComparison.OrdinalIgnoreCase))
                    {
                        desc = line["# DESC:".Length..].Trim();
                    }
                    else if (line.StartsWith("DEFAULT_PARAMS", StringComparison.Ordinal))
                    {
                        int braceStart = line.IndexOf('{');
                        int braceEnd = line.LastIndexOf('}');
                        if (braceStart >= 0 && braceEnd > braceStart)
                        {
                            string dictJson = line[braceStart..(braceEnd + 1)];
                            if (TryValidateScalarParamsJson(dictJson, out var parsed, out _))
                            {
                                foreach (var kv in parsed)
                                {
                                    defaults[kv.Key] = kv.Value;
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
            }

            return new PythonAlgoDescriptor
            {
                FileName = fileName,
                DisplayTitle = title,
                Description = desc,
                DefaultParams = defaults
            };
        }
    }
}

