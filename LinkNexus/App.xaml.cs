using System.Windows;

namespace LinkNexus
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// 启动时自动初始化 ThemeManager 并挂载默认赛博暗黑主题
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ThemeManager.ApplyTheme(true);
        }
    }
}

