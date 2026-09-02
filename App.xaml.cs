using System;
using System.Windows;
using Microsoft.Toolkit.Uwp.Notifications;

namespace DesktopCalendarWidget
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // 必须在主窗口创建前触发 Toolkit 的 Win32 Toast 初始化。
            // 这样无论从 VS、普通 EXE 还是发布后的 win-x64 EXE 启动，
            // Windows 通知的 COM/AUMID 注册都会先完成。
            try
            {
                ToastNotificationManagerCompat.OnActivated += _ => { };
                _ = ToastNotificationManagerCompat.CreateToastNotifier();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Toast initialization failed: {ex}");
            }

            base.OnStartup(e);
        }
    }
}
