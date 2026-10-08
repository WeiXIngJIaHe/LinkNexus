using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using LinkNexus.Mcp;

namespace LinkNexus
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// 具备 Dual-Mode 架构：
    /// 1. 桌面 GUI 模式 (默认)：加载战术机能界面，并在后台常驻 MCP 本地管道 IPC 服务；
    /// 2. MCP Stdio 模式 (--mcp)：供外部 AI Agent (Claude Desktop / Cursor 等) 挂接与标准化调用。
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 检查是否由外部 AI Agent 以 --mcp 参数拉起
            if (e.Args.Any(a => a.Equals("--mcp", StringComparison.OrdinalIgnoreCase) || a.Equals("/mcp", StringComparison.OrdinalIgnoreCase)))
            {
                // 无头模式启动，清空 StartupUri 阻止弹出桌面窗口
                StartupUri = null;
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Task.Run(async () =>
                {
                    try
                    {
                        await McpStdioHandler.RunAsync(e.Args);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        Dispatcher.Invoke(() => Shutdown(0));
                    }
                });
                return;
            }

            base.OnStartup(e);
            ThemeManager.ApplyTheme(true);
        }
    }
}
