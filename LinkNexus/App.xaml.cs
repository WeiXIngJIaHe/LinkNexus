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
            var cmdArgs = Environment.GetCommandLineArgs();
            bool isMcp = cmdArgs.Any(a => a.Equals("--mcp", StringComparison.OrdinalIgnoreCase) || a.Equals("/mcp", StringComparison.OrdinalIgnoreCase));

            if (isMcp)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Task.Run(async () =>
                {
                    try
                    {
                        await McpStdioHandler.RunAsync(cmdArgs);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine(ex);
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
            new MainWindow().Show();
        }
    }
}
