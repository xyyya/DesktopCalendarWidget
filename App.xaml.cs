using System;
using System.Windows;

namespace DesktopCalendarWidget
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += (_, args) =>
            {
                MessageBox.Show(
                    $"{Localization.T("程序启动/运行时发生未处理错误")}：\n\n{args.Exception}",
                    "Desktop Calendar Widget",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args.Handled = true;
            };

            try
            {
                var window = new MainWindow
                {
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };
                MainWindow = window;
                window.Show();
                window.Activate();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"{Localization.T("程序启动失败")}：\n\n{ex}",
                    "Desktop Calendar Widget",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(-1);
            }
        }
    }
}
