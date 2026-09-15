using System.Text.Json;

namespace Hauteur.Core;

/// <summary>配置读写:%APPDATA%\Hauteur\settings.json。任何读写失败都回退默认值,不打断运行。</summary>
internal static class ConfigStore
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Hauteur");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        // 一次性迁移:旧项目名 TOPPEUR 的配置(若有)带到 Hauteur
        try
        {
            if (!File.Exists(FilePath))
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var legacy = Path.Combine(appData, "TOPPEUR", "settings.json");
                if (File.Exists(legacy))
                {
                    Directory.CreateDirectory(Dir);
                    File.Copy(legacy, FilePath);
                }
            }
        }
        catch
        {
            // 迁移失败忽略,继续用默认值
        }

        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
                EnsureNewHotkeyDefaults(settings);
                return settings;
            }
        }
        catch
        {
            // 配置损坏(手改/写入中断)时静默回退默认值
        }
        return new AppSettings();
    }

    /// <summary>兼容旧配置:JSON 缺少新增字段时反序列化为 0,补回默认值。
    /// 修饰键为 0 是合法配置(F1~F24 无修饰键),不能据此重置;光晕颜色为 0 则全透明,必须补默认。</summary>
    private static void EnsureNewHotkeyDefaults(AppSettings settings)
    {
        if (settings.StackHotkeyKey == 0)
        {
            settings.StackHotkeyModifiers = Interop.NativeMethods.MOD_CONTROL | Interop.NativeMethods.MOD_ALT;
            settings.StackHotkeyKey = (uint)Keys.G;
        }
        if (settings.DismissHotkeyKey == 0)
        {
            settings.DismissHotkeyModifiers = Interop.NativeMethods.MOD_CONTROL | Interop.NativeMethods.MOD_ALT;
            settings.DismissHotkeyKey = (uint)Keys.D;
        }
        if (settings.GroupGlowColor == 0)
        {
            settings.GroupGlowColor = AppSettings.DefaultGroupGlowColor;
        }
        if (settings.WheelFlipModifiers == 0) // 无修饰键会让每次滚轮都触发翻页,必须补默认
        {
            settings.WheelFlipModifiers = Interop.NativeMethods.MOD_CONTROL | Interop.NativeMethods.MOD_ALT;
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);

            // 先写临时文件再原子替换,避免写入中断产生损坏配置
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch
        {
            // 磁盘/权限问题不致命,下次修改时重试
        }
    }
}
