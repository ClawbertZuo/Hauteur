using Microsoft.Win32;

namespace TOPPEUR.Core;

/// <summary>开机自启:写入/删除注册表 HKCU\...\CurrentVersion\Run,无需管理员权限。</summary>
internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TOPPEUR";

    public static bool IsEnabled()
    {
        try
        {
            return Registry.GetValue($@"HKEY_CURRENT_USER\{RunKeyPath}", ValueName, null) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            if (enabled)
            {
                // 路径加引号,兼容含空格目录
                key.SetValue(ValueName, $"\"{Application.ExecutablePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 组策略等限制导致写入失败时静默忽略,设置界面以实际读取状态为准
        }
    }
}
