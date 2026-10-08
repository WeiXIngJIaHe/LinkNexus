using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LinkNexus.Mcp
{
    /// <summary>
    /// LinkNexus 内部 MCP 本地通信 IPC 服务端
    /// 基于 Localhost Loopback TCP，允许外部 AI Agent 或 Stdio 命令行秒级直连获取运行中的软件数据
    /// </summary>
    public class McpIpcServer : IDisposable
    {
        private readonly McpServerBridge _mcpBridge;
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private bool _isRunning = false;

        public McpIpcServer(McpServerBridge mcpBridge)
        {
            _mcpBridge = mcpBridge ?? throw new ArgumentNullException(nameof(mcpBridge));
        }

        /// <summary>
        /// 启动后台本地监听服务
        /// </summary>
        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _cts = new CancellationTokenSource();

            try
            {
                _listener = new TcpListener(IPAddress.Loopback, McpIpcConstants.DefaultPort);
                _listener.Start();
                Task.Run(() => ListenLoopAsync(_cts.Token));
            }
            catch (Exception ex)
            {
                // 若端口已被占用，标记降级
                _isRunning = false;
                System.Diagnostics.Debug.WriteLine($"[McpIpcServer] 启动监听异常: {ex.Message}");
            }
        }

        /// <summary>
        /// 停止监听并释放资源
        /// </summary>
        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;

            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }

            _cts?.Dispose();
            _cts = null;
            _listener = null;
        }

        private async Task ListenLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _isRunning && _listener != null)
            {
                try
                {
                    var tcpClient = await _listener.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => HandleClientAsync(tcpClient, token), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    if (token.IsCancellationRequested) break;
                    await Task.Delay(100, token);
                }
            }
        }

        private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken token)
        {
            using (tcpClient)
            using (var stream = tcpClient.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            using (var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true })
            {
                try
                {
                    while (tcpClient.Connected && !token.IsCancellationRequested)
                    {
                        string? line = await reader.ReadLineAsync(token);
                        if (line == null) break;
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        // 调用 LinkNexus GUI 当前挂接的 MCP 桥接中枢处理
                        string response = await _mcpBridge.HandleJsonRpcMessageAsync(line, token);

                        // 将响应单行返回给客户端并冲刷
                        await writer.WriteLineAsync(response);
                        await writer.FlushAsync();
                    }
                }
                catch
                {
                    // 客户端断开连接正常退出
                }
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
