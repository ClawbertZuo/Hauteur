namespace TOPPEUR.Core;

/// <summary>异常日志:%APPDATA%\TOPPEUR\error.log。托盘程序无控制台,崩溃信息落盘便于排查。</summary>
internal static class ErrorLog
{
    public static void Write(string context, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TOPPEUR");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch
        {
            // 日志写入失败时放弃,不能因日志再抛异常
        }
    }
}
