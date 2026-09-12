using TOPPEUR.App;
using TOPPEUR.Core;

namespace TOPPEUR;

internal static class Program
{
    private const string MutexName = "TOPPEUR.SingleInstance.Mutex";

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        // UI 线程未处理异常:记录日志并继续常驻,不静默崩溃
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ErrorLog.Write("UI 线程未处理异常", e.Exception);

        // 单实例:重复启动时通知已有实例打开设置窗口,本进程直接退出
        using var mutex = new Mutex(true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            SingleInstanceMessenger.NotifyExisting();
            return;
        }

        try
        {
            using var context = new TrayAppContext();
            Application.Run(context);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("致命错误", ex);
            MessageBox.Show(
                $"TOPPEUR 启动失败,详情见 %APPDATA%\\TOPPEUR\\error.log:{Environment.NewLine}{ex.Message}",
                "TOPPEUR", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
