using System;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Mcp
{
    /// <summary>
    /// 标准 MCP Stdio 管道交互处理器
    /// 供外部 AI Agent (Claude Desktop / Cursor / Cline 等) 通过标准输入输出调度 LinkNexus
    /// </summary>
    public static class McpStdioHandler
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int dwProcessId);
        private const int ATTACH_PARENT_PROCESS = -1;

        public static async Task RunAsync(string[] args, CancellationToken cancellationToken = default)
        {
            if (!Console.IsInputRedirected)
            {
                AttachConsole(ATTACH_PARENT_PROCESS);
            }

            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            // 1. 优先探测是否有运行中的 LinkNexus GUI 实例 (通过 Localhost Loopback TCP)
            TcpClient? tcpClient = null;
            try
            {
                tcpClient = new TcpClient();
                using var cts = new CancellationTokenSource(McpIpcConstants.ConnectTimeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken);
                await tcpClient.ConnectAsync(System.Net.IPAddress.Loopback, McpIpcConstants.DefaultPort, linkedCts.Token);
            }
            catch
            {
                tcpClient?.Dispose();
                tcpClient = null;
            }

            if (tcpClient != null && tcpClient.Connected)
            {
                // 场景 A: GUI 实例在线，作为透传代理，将 Stdio 数据与 GUI 实时数据穿透互通
                await RunProxyModeAsync(tcpClient, cancellationToken);
            }
            else
            {
                // 场景 B: 独立无头模式 (GUI 未运行)，独立构建硬件监控与 MCP 中枢
                await RunStandaloneModeAsync(cancellationToken);
            }
        }

        private static async Task RunProxyModeAsync(TcpClient tcpClient, CancellationToken cancellationToken)
        {
            using (tcpClient)
            using (var netStream = tcpClient.GetStream())
            using (var netReader = new StreamReader(netStream, Encoding.UTF8))
            using (var netWriter = new StreamWriter(netStream, Encoding.UTF8) { AutoFlush = true })
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    string? line = await Console.In.ReadLineAsync(cancellationToken);
                    if (line == null) break; // EOF
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // 发送给 GUI 服务端并冲刷
                    await netWriter.WriteLineAsync(line);
                    await netWriter.FlushAsync();

                    // 从 GUI 服务端读取响应并输出到 stdout
                    string? response = await netReader.ReadLineAsync(cancellationToken);
                    if (response != null)
                    {
                        Console.Out.WriteLine(response);
                        Console.Out.Flush();
                    }
                }
            }
        }

        private static async Task RunStandaloneModeAsync(CancellationToken cancellationToken)
        {
            var bridge = new McpServerBridge();
            var standaloneLogs = new List<LogMessageItem>
            {
                new LogMessageItem
                {
                    Timestamp = DateTime.Now,
                    Level = "INFO",
                    Message = $"LinkNexus 独立无头 MCP 服务已就绪 (版本：{VersionInfo.CurrentVersion})"
                }
            };

            List<PortDeviceModel>? cachedDevices = null;
            DateTime lastScanTime = DateTime.MinValue;

            List<PortDeviceModel> GetRealDevices()
            {
                if (cachedDevices != null && (DateTime.UtcNow - lastScanTime).TotalSeconds < 3)
                {
                    return cachedDevices;
                }

                var list = new List<PortDeviceModel>();
                try
                {
                    using var monitor = new UsbMonitorService();
                    var discovered = monitor.ScanCurrentDevices();
                    foreach (var d in discovered)
                    {
                        list.Add(new PortDeviceModel
                        {
                            DeviceName = string.IsNullOrWhiteSpace(d.Name) ? d.Description : d.Name,
                            DeviceDescription = d.Description,
                            VidPid = d.VidPid,
                            ComPort = string.IsNullOrWhiteSpace(d.ComPort) ? "--" : d.ComPort,
                            SerialNumber = string.IsNullOrWhiteSpace(d.SerialNumber) ? "--" : d.SerialNumber,
                            HardwarePath = d.HardwarePath,
                            State = d.HasDriverIssue ? DeviceState.Warning : DeviceState.Ready,
                            FunctionType = !string.IsNullOrWhiteSpace(d.ComPort) ? DeviceFunctionType.Uart_Serial : DeviceFunctionType.GenericUsb
                        });
                    }
                }
                catch
                {
                    foreach (var port in System.IO.Ports.SerialPort.GetPortNames())
                    {
                        list.Add(new PortDeviceModel
                        {
                            DeviceName = $"串行硬件设备 ({port})",
                            ComPort = port,
                            FunctionType = DeviceFunctionType.Uart_Serial,
                            State = DeviceState.Ready
                        });
                    }
                }

                cachedDevices = list;
                lastScanTime = DateTime.UtcNow;
                return cachedDevices;
            }

            bridge.BindHardwareContext(
                devicesProvider: GetRealDevices,
                isEsp32OnlineProvider: () =>
                {
                    var devs = GetRealDevices();
                    return devs.Any(d => d.DeviceName.Contains("ESP32", StringComparison.OrdinalIgnoreCase) || d.VidPid.Contains("303A"));
                },
                daemonStatusTextProvider: () => "LinkNexus 物理拓扑守护服务正常 (独立无头模式)",
                logsProvider: () => standaloneLogs,
                cliExecutor: async (cmd, timeout) => await Esp32CommService.Instance.ExecuteCliCommandAsync(cmd, timeout));

            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await Console.In.ReadLineAsync(cancellationToken);
                if (line == null) break; // EOF
                if (string.IsNullOrWhiteSpace(line)) continue;

                string response = await bridge.HandleJsonRpcMessageAsync(line, cancellationToken);
                Console.Out.WriteLine(response);
                Console.Out.Flush();
            }
        }
    }
}
