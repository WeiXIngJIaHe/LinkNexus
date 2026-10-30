using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Python.Runtime;

namespace LinkNexus.Blueprint.Core
{
    /// <summary>
    /// Python.NET 高性能进程内互操作桥接器 (PythonNetBridge)
    /// 基于开源 pythonnet 库，将 C# 数组与标量参数直接在内存堆中传递给 Python 算法函数，
    /// 消除子进程创建与 IPC 序列化开销，实现微秒级低延迟算法流转；若本机未配置 Python DLL 则透明回退至隔离子进程。
    /// </summary>
    public static class PythonNetBridge
    {
        private static readonly object _initLock = new();
        private static bool _initialized = false;
        private static bool _initFailed = false;
        private static string _initError = string.Empty;

        /// <summary>
        /// 查询 Python.NET 进程内互操作引擎是否就绪
        /// </summary>
        public static bool IsEngineReady
        {
            get
            {
                EnsureInitialized();
                return _initialized && !_initFailed;
            }
        }

        public static string EngineStatusMessage => _initialized
            ? (_initFailed ? $"Python.NET 就绪失败 (已启用子进程隔离容灾): {_initError}" : "Python.NET 内存直连模式 (活跃)")
            : "Python.NET 未初始化";

        private static void EnsureInitialized()
        {
            if (_initialized) return;

            lock (_initLock)
            {
                if (_initialized) return;

                try
                {
                    // 探测可用的 Python DLL 路径 (如 PlatformIO 隔离虚拟环境或系统 Python)
                    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string[] candidateDirs = new[]
                    {
                        Path.Combine(userProfile, @".platformio\penv\Scripts"),
                        Path.Combine(userProfile, @".platformio\penv"),
                        @"C:\Python313",
                        @"C:\Python312",
                        @"C:\Python311"
                    };

                    string? foundDll = null;
                    foreach (var dir in candidateDirs)
                    {
                        if (Directory.Exists(dir))
                        {
                            var dlls = Directory.GetFiles(dir, "python3*.dll", SearchOption.TopDirectoryOnly);
                            if (dlls.Length > 0)
                            {
                                foundDll = dlls[0];
                                break;
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(foundDll) && File.Exists(foundDll))
                    {
                        Runtime.PythonDLL = foundDll;
                    }

                    PythonEngine.Initialize();
                    PythonEngine.BeginAllowThreads();
                    _initialized = true;
                    _initFailed = false;
                }
                catch (Exception ex)
                {
                    _initialized = true;
                    _initFailed = true;
                    _initError = ex.Message;
                }
            }
        }

        /// <summary>
        /// 尝试通过 Python.NET 内存直连执行 Python 算法脚本中的 process(channels, params) 函数
        /// </summary>
        public static bool TryExecuteInProcess(string scriptPath, double[] inputChannels, string scalarParamsJson, out double[] outputChannels, out string errorMessage)
        {
            outputChannels = Array.Empty<double>();
            errorMessage = string.Empty;

            if (!IsEngineReady)
            {
                return false;
            }

            try
            {
                using (Py.GIL())
                {
                    using dynamic sys = Py.Import("sys");
                    string scriptDir = Path.GetDirectoryName(scriptPath) ?? string.Empty;
                    sys.path.append(scriptDir);

                    string moduleName = Path.GetFileNameWithoutExtension(scriptPath);
                    using dynamic module = Py.Import(moduleName);

                    // 传递 channels 列表与 params 字典
                    using dynamic pyChannels = new PyList();
                    foreach (var ch in inputChannels)
                    {
                        pyChannels.Append(new PyFloat(ch));
                    }

                    using dynamic jsonMod = Py.Import("json");
                    using dynamic pyParams = jsonMod.loads(scalarParamsJson);

                    using dynamic pyResult = module.process(pyChannels, pyParams);

                    // 解析返回值
                    var resultList = new List<double>();
                    foreach (dynamic item in pyResult)
                    {
                        resultList.Add((double)item);
                    }
                    outputChannels = resultList.ToArray();
                    return true;
                }
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }
    }
}

