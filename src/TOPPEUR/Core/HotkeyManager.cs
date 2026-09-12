using TOPPEUR.Interop;

namespace TOPPEUR.Core;

/// <summary>一条热键注册项。</summary>
internal sealed record HotkeyEntry(int Id, uint Modifiers, uint Key, string Label);

/// <summary>
/// 全局热键注册/注销(支持多条:置顶切换 + 各层级)。
/// 热键注册到 UI 线程的消息窗口(MessageWindow),触发时以 WM_HOTKEY 投递,
/// wParam 为热键 ID;注册失败(组合键被其他程序占用)会被收集到 <see cref="Failed"/>。
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    /// <summary>置顶/取消置顶切换热键的 ID。</summary>
    public const int ToggleHotkeyId = 0x544F; // "TO"

    /// <summary>层级热键 ID 基址:层级 layer 对应 LayerHotkeyBase + layer - 1。</summary>
    public const int LayerHotkeyBase = 0x4C01; // "L" + layer

    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, HotkeyEntry> _registered = new();
    private readonly List<HotkeyEntry> _failed = new();

    public HotkeyManager(IntPtr messageWindow)
    {
        _hwnd = messageWindow;
    }

    /// <summary>本次注册失败的热键(组合键被占用等)。</summary>
    public IReadOnlyList<HotkeyEntry> Failed => _failed;

    /// <summary>注销全部旧热键,重新注册给定集合。失败项记入 <see cref="Failed"/>。</summary>
    public void RegisterAll(IEnumerable<HotkeyEntry> entries)
    {
        UnregisterAll();
        foreach (var e in entries)
        {
            if (NativeMethods.RegisterHotKey(_hwnd, e.Id, e.Modifiers, e.Key))
                _registered[e.Id] = e;
            else
                _failed.Add(e);
        }
    }

    /// <summary>
    /// 替换指定 ID 的热键(设置窗口校验用)。失败时尽力恢复原组合并返回 false。
    /// </summary>
    public bool TryReplace(int id, uint modifiers, uint key)
    {
        HotkeyEntry? old = _registered.TryGetValue(id, out var o) ? o : null;
        if (old is not null) NativeMethods.UnregisterHotKey(_hwnd, id);

        if (NativeMethods.RegisterHotKey(_hwnd, id, modifiers, key))
        {
            _registered[id] = new HotkeyEntry(id, modifiers, key, string.Empty);
            _failed.RemoveAll(f => f.Id == id);
            return true;
        }

        // 恢复原组合(若此时仍失败,说明原组合也被抢了,交由 ApplyHotkey 的失败提示兜底)
        if (old is not null)
            NativeMethods.RegisterHotKey(_hwnd, id, old.Modifiers, old.Key);
        return false;
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys.ToList())
            NativeMethods.UnregisterHotKey(_hwnd, id);
        _registered.Clear();
        _failed.Clear();
    }

    public void Dispose() => UnregisterAll();
}
