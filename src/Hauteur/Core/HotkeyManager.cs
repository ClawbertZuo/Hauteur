using System.Runtime.InteropServices;
using Hauteur.Interop;

namespace Hauteur.Core;

/// <summary>一条热键注册项。</summary>
internal sealed record HotkeyEntry(int Id, uint Modifiers, uint Key, string Label);

/// <summary>
/// 全局热键注册/注销(支持多条:置顶切换 + 各层级)。
/// 热键注册到 UI 线程的消息窗口(MessageWindow),触发时以 WM_HOTKEY 投递,wParam 为热键 ID;
/// 注册失败(组合键被其他程序占用)会被收集到 <see cref="Failed"/>。
/// 含鼠标侧键修饰位(MOD_XBUTTON1/2)的"和弦"组合(按住侧键再按主键)RegisterHotKey 不支持,
/// 由常驻键盘低层钩子检测,匹配时同样投递 WM_HOTKEY;和弦不受其他程序占用影响。
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    /// <summary>置顶/取消置顶切换热键的 ID。</summary>
    public const int ToggleHotkeyId = 0x544F; // "TO"

    /// <summary>窗口堆叠多选热键的 ID。</summary>
    public const int StackHotkeyId = 0x5354; // "ST"

    /// <summary>取消置顶并移出窗口组热键的 ID。</summary>
    public const int DismissHotkeyId = 0x4449; // "DI"

    /// <summary>层级热键 ID 基址:层级 layer 对应 LayerHotkeyBase + layer - 1。</summary>
    public const int LayerHotkeyBase = 0x4C01; // "L" + layer

    private const int WM_HOTKEY = 0x0312;
    private const uint SideButtonMask = NativeMethods.MOD_XBUTTON1 | NativeMethods.MOD_XBUTTON2;

    private readonly IntPtr _hwnd;
    private readonly Dictionary<int, HotkeyEntry> _registered = new(); // RegisterHotKey 注册的常规热键
    private readonly List<HotkeyEntry> _failed = new();
    private readonly Dictionary<int, HotkeyEntry> _chords = new(); // 侧键和弦(低层钩子检测)

    private readonly NativeMethods.LowLevelHookProc _keyboardProc; // 常驻字段防止委托被 GC
    private IntPtr _keyboardHook;
    private uint _swallowedVk; // 已拦截的和弦主键,抬起时一并拦截

    public HotkeyManager(IntPtr messageWindow)
    {
        _hwnd = messageWindow;
        _keyboardProc = OnChordKeyboardHook;
    }

    /// <summary>本次注册失败的热键(组合键被占用等)。和弦热键不受占用影响,不会失败。</summary>
    public IReadOnlyList<HotkeyEntry> Failed => _failed;

    /// <summary>注销全部旧热键,重新注册给定集合。失败项记入 <see cref="Failed"/>。</summary>
    public void RegisterAll(IEnumerable<HotkeyEntry> entries)
    {
        UnregisterAll();
        foreach (var e in entries)
        {
            if (!Register(e.Id, e.Modifiers, e.Key, e.Label))
                _failed.Add(e);
        }
    }

    /// <summary>注册指定 ID 的热键(与 <see cref="Unregister"/> 配合,供设置窗口对多条热键一起校验)。
    /// 含侧键修饰位的组合走和弦钩子,始终注册成功。</summary>
    public bool Register(int id, uint modifiers, uint key, string label = "")
    {
        if ((modifiers & SideButtonMask) != 0)
        {
            _chords[id] = new HotkeyEntry(id, modifiers, key, label);
            _failed.RemoveAll(f => f.Id == id);
            EnsureChordHook();
            return true;
        }

        // 注册前剥离侧键位(RegisterHotKey 只认标准修饰键)
        if (!NativeMethods.RegisterHotKey(_hwnd, id, modifiers & NativeMethods.MOD_MASK_STANDARD, key))
            return false;
        _registered[id] = new HotkeyEntry(id, modifiers, key, label);
        _failed.RemoveAll(f => f.Id == id);
        return true;
    }

    /// <summary>注销指定 ID 的热键(未注册时无操作)。</summary>
    public void Unregister(int id)
    {
        if (_registered.Remove(id))
            NativeMethods.UnregisterHotKey(_hwnd, id);
        if (_chords.Remove(id) && _chords.Count == 0)
            UnhookChordHook();
    }

    public void UnregisterAll()
    {
        foreach (var id in _registered.Keys.ToList())
            NativeMethods.UnregisterHotKey(_hwnd, id);
        _registered.Clear();
        _failed.Clear();
        _chords.Clear();
        UnhookChordHook();
    }

    // ---- 侧键和弦钩子 ----

    private void EnsureChordHook()
    {
        if (_keyboardHook != IntPtr.Zero) return;
        _keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _keyboardProc, IntPtr.Zero, 0);
    }

    private void UnhookChordHook()
    {
        if (_keyboardHook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        _keyboardHook = IntPtr.Zero;
        _swallowedVk = 0;
    }

    /// <summary>键盘钩子:按住侧键(及配置的键盘修饰键)按下主键时投递 WM_HOTKEY 并拦截该键。</summary>
    private IntPtr OnChordKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN)
            {
                foreach (var e in _chords.Values)
                {
                    if (data.vkCode == e.Key && NativeMethods.ModifiersMatch(e.Modifiers))
                    {
                        NativeMethods.PostMessage(_hwnd, WM_HOTKEY, (IntPtr)e.Id, IntPtr.Zero);
                        _swallowedVk = data.vkCode;
                        return (IntPtr)1; // 拦截:主键不传递给应用
                    }
                }
            }
            else if (msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            {
                if (_swallowedVk != 0 && data.vkCode == _swallowedVk)
                {
                    _swallowedVk = 0;
                    return (IntPtr)1; // 拦截抬起,与应用收到的按键成对
                }
            }
        }
        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    public void Dispose() => UnregisterAll();
}
