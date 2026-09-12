using System.Text.Json.Serialization;

namespace Hauteur.Core;

/// <summary>用户配置,持久化到 %APPDATA%\Hauteur\settings.json。</summary>
public sealed class AppSettings
{
    /// <summary>置顶切换热键的修饰键(MOD_CONTROL | MOD_ALT 等)。</summary>
    public uint HotkeyModifiers { get; set; } = Interop.NativeMethods.MOD_CONTROL | Interop.NativeMethods.MOD_ALT;

    /// <summary>置顶切换热键的主键虚拟键码(默认 T)。</summary>
    public uint HotkeyKey { get; set; } = (uint)Keys.T;

    /// <summary>开机自启(注册表 Run 键)。</summary>
    public bool AutoStart { get; set; }

    /// <summary>暂停捕获(热键暂时失效)。</summary>
    public bool Paused { get; set; }

    /// <summary>层级数量(3~9,默认 5)。设置界面暂未提供编辑,可手改配置文件。</summary>
    public int LayerCount { get; set; } = 5;

    /// <summary>各层级光晕颜色(ARGB),索引 0 = 层级 1(置顶),默认高饱和紫 → 粉渐变。</summary>
    public uint[]? GlowLayerColors { get; set; } = DefaultLayerColors(5);

    /// <summary>层级保持(默认开启):被设过层级的窗口被点击激活时,自动拉回设定层级。</summary>
    public bool KeepLayers { get; set; } = true;

    /// <summary>默认层级颜色:高饱和紫 #9D00FF → 粉 #FF007F 均匀插值。</summary>
    public static uint[] DefaultLayerColors(int count)
    {
        const uint top = 0xFF9D00FF, bottom = 0xFFFF007F;
        var arr = new uint[count];
        for (int i = 0; i < count; i++)
        {
            float t = count <= 1 ? 0f : i / (float)(count - 1);
            arr[i] = LerpColor(top, bottom, t);
        }
        return arr;
    }

    /// <summary>保证层级颜色数组长度与层级数一致(长度不符/为空时重置为默认)。</summary>
    public void EnsureLayerColors(int layerCount)
    {
        if (GlowLayerColors is null || GlowLayerColors.Length != layerCount)
            GlowLayerColors = DefaultLayerColors(layerCount);
    }

    private static uint LerpColor(uint a, uint b, float t)
    {
        int r = (int)(((a >> 16) & 0xFF) + (((b >> 16) & 0xFF) - ((a >> 16) & 0xFF)) * t + 0.5f);
        int g = (int)(((a >> 8) & 0xFF) + (((b >> 8) & 0xFF) - ((a >> 8) & 0xFF)) * t + 0.5f);
        int bl = (int)((a & 0xFF) + ((b & 0xFF) - (a & 0xFF)) * t + 0.5f);
        return 0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)bl;
    }

    public AppSettings Clone() => new()
    {
        HotkeyModifiers = HotkeyModifiers,
        HotkeyKey = HotkeyKey,
        AutoStart = AutoStart,
        Paused = Paused,
        LayerCount = LayerCount,
        GlowLayerColors = GlowLayerColors is null ? null : (uint[])GlowLayerColors.Clone(),
        KeepLayers = KeepLayers,
    };
}

/// <summary>System.Text.Json 源生成上下文:裁剪发布(PublishTrimmed)下不依赖反射。</summary>
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
