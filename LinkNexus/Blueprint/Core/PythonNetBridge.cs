using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Python.Runtime;

namespace LinkNexus.Blueprint.Core
{
    /// <summary>
    /// Python.NET 高性能进程内互操作桥接器 (PythonNetBridge)
    /// 基于开源 pythonnet 库，按 process(channels, params, state) 三参规范在内存堆中直连调用 Python 算子，
    /// 持久化复用每节点实例的 state 字典，并支持 FileSystemWatcher 修改后热重载模块。
    /// </summary>
    public static class PythonNetBridge
    {
        private static readonly object _initLock = new();
        private static readonly ConcurrentDictionary<string, PyObject> _stateDicts = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, bool> _reloadFlags = new(StringComparer.OrdinalIgnoreCase);

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
                    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    string[] candidateDirs = new[]
                    {
                        Path.Combine(userProfile, @".platformio\penv\Scripts"),
                        Path.Combine(userProfile, @".platformio\penv"),
                        Path.Combine(localAppData, @"Programs\Python\Python313"),
                        Path.Combine(localAppData, @"Programs\Python\Python312"),
                        Path.Combine(localAppData, @"Programs\Python\Python311"),
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
                            foreach (var dll in dlls)
                            {
                                string name = Path.GetFileName(dll);
                                // 排除 python3.dll 转发桩，选择具体版本如 python311.dll / python312.dll
                                if (name.Length > "python3.dll".Length)
                                {
                                    foundDll = dll;
                                    break;
                                }
                            }
                            if (foundDll != null) break;
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
        /// 标记指定脚本或节点实例状态需要热重载清空
        /// </summary>
        public static void NotifyScriptReloaded(string scriptPath, string? stateKey = null)
        {
            if (!string.IsNullOrWhiteSpace(scriptPath))
            {
                _reloadFlags[Path.GetFullPath(scriptPath)] = true;
            }

            string key = string.IsNullOrWhiteSpace(stateKey) ? (scriptPath ?? string.Empty) : stateKey!;
            if (_stateDicts.TryRemove(key, out PyObject? oldState) && IsEngineReady)
            {
                try
                {
                    using (Py.GIL())
                    {
                        oldState.Dispose();
                    }
                }
                catch
                {
                }
            }
        }

        /// <summary>
        /// 通过 Python.NET 内存直连执行 Python 算法脚本中的 process(channels, params, state) 函数
        /// </summary>
        public static bool TryExecuteInProcess(
            string scriptPath,
            double[] inputChannels,
            string scalarParamsJson,
            out double[] outputChannels,
            out string errorMessage,
            string? stateKey = null)
        {
            outputChannels = Array.Empty<double>();
            errorMessage = string.Empty;

            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
            {
                errorMessage = "Python 脚本文件不存在。";
                return false;
            }

            if (!IsEngineReady)
            {
                return false;
            }

            try
            {
                string fullPath = Path.GetFullPath(scriptPath);
                string resolvedStateKey = string.IsNullOrWhiteSpace(stateKey)
                    ? fullPath
                    : $"{stateKey}:{fullPath}";

                using (Py.GIL())
                {
                    using dynamic importlibUtil = Py.Import("importlib.util");
                    using dynamic sys = Py.Import("sys");

                    string moduleKey = "lnx_mod_" + Math.Abs(fullPath.GetHashCode(StringComparison.OrdinalIgnoreCase));
                    bool forceReload = _reloadFlags.TryRemove(fullPath, out _);

                    dynamic module;
                    if (!forceReload && (bool)sys.modules.__contains__(moduleKey))
                    {
                        module = sys.modules[moduleKey];
                    }
                    else
                    {
                        using dynamic spec = importlibUtil.spec_from_file_location(moduleKey, fullPath);
                        module = importlibUtil.module_from_spec(spec);
                        sys.modules[moduleKey] = module;
                        spec.loader.exec_module(module);
                    }

                    using (module)
                    {
                        using dynamic pyChannels = new PyList();
                        for (int i = 0; i < inputChannels.Length; i++)
                        {
                            using var f = new PyFloat(inputChannels[i]);
                            pyChannels.Append(f);
                        }

                        using dynamic jsonMod = Py.Import("json");
                        string safeJson = string.IsNullOrWhiteSpace(scalarParamsJson) ? "{}" : scalarParamsJson;
                        using dynamic pyParams = jsonMod.loads(safeJson);

                        if (!_stateDicts.TryGetValue(resolvedStateKey, out PyObject? stateObj) || stateObj == null)
                        {
                            stateObj = new PyDict();
                            _stateDicts[resolvedStateKey] = stateObj;
                        }

                        dynamic pyResult = module.process(pyChannels, pyParams, stateObj);
                        using (pyResult)
                        {
                            var resultList = new List<double>(inputChannels.Length);
                            foreach (dynamic item in pyResult)
                            {
                                using (item)
                                {
                                    resultList.Add((double)item);
                                }
                            }
                            outputChannels = resultList.ToArray();
                        }
                    }

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
