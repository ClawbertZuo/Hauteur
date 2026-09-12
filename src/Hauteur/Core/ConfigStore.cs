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
                return JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
        }
        catch
        {
            // 配置损坏(手改/写入中断)时静默回退默认值
        }
        return new AppSettings();
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
