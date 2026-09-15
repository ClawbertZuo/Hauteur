using System.Runtime.InteropServices;
using Hauteur.Core;
using Hauteur.Interop;

namespace Hauteur.App;

/// <summary>
/// 窗口堆叠多选:按住自定义热键(默认 Ctrl+Alt+G,WM_HOTKEY 触发 <see cref="Arm"/>)期间进入多选模式,
/// 左键点击窗口 = 选中/取消选中(点击被拦截,不传递给应用),松开热键(键盘低层钩子检测)时
/// 把选中窗口堆叠到第一个选中窗口(锚点)的位置与大小。
/// WH_MOUSE_LL / WH_KEYBOARD_LL 钩子仅在按住期间安装,松开立即卸载;所有状态仅在 UI 线程访问。
/// </summary>
internal sealed class StackModeManager : IDisposable
{
    private const uint SideButtonMask = NativeMethods.MOD_XBUTTON1 | NativeMethods.MOD_XBUTTON2;

    private readonly Func<(uint Modifiers, uint Key)> _combo; // 当前配置的组合键(实时读取,松开检测用)

    private readonly NativeMethods.LowLevelHookProc _mouseProc; // 常驻字段防止委托被 GC
    private readonly NativeMethods.LowLevelHookProc _keyboardProc;

    private readonly List<IntPtr> _selection = new();
    private IntPtr _mouseHook;
    private IntPtr _keyboardHook;

    public StackModeManager(Func<(uint Modifiers, uint Key)> combo)
    {
        _combo = combo;
        _mouseProc = OnMouseHook;
        _keyboardProc = OnKeyboardHook;
    }

    /// <summary>是否处于多选模式(按住热键期间)。</summary>
    public bool Armed => _mouseHook != IntPtr.Zero;

    /// <summary>当前选中的窗口(顺序 = 点击顺序,第一个是锚点)。</summary>
    public IReadOnlyList<IntPtr> Selection => _selection;

    /// <summary>选中集合变化(增删/清空),外部据此刷新选中光晕。</summary>
    public event Action? SelectionChanged;

    /// <summary>堆叠部分失败,参数为气泡提示文案(由托盘弹气泡)。</summary>
    public event Action<string>? Failed;

    /// <summary>堆叠成功,参数为本次堆叠的窗口集合(第一个是锚点),外部据此建立窗口组。</summary>
    public event Action<IReadOnlyList<IntPtr>>? Stacked;

    /// <summary>进入多选模式:清空选中并安装钩子。已处于模式时忽略(热键不会重复触发,仅防御)。</summary>
    public void Arm()
    {
        if (Armed) return;
        _selection.Clear();

        // 组合键已松开(极快按下即松开的竞态):不进入模式,避免钩子空转
        if (!IsHotkeyHeld()) return;

        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
        _keyboardHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _keyboardProc, IntPtr.Zero, 0);
        if (_mouseHook == IntPtr.Zero || _keyboardHook == IntPtr.Zero)
        {
            Cancel(); // 钩子安装失败(极少见):立即清理
            return;
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>退出多选模式(不执行堆叠)。未处于模式时无操作;暂停/设置变更/退出时调用。</summary>
    public void Cancel()
    {
        if (!Armed) return;
        Unhook();
        _selection.Clear();
        SelectionChanged?.Invoke();
    }

    /// <summary>选中窗口被销毁:移出选中(锚点消失时下一顺位自动补位)。</summary>
    public void OnTargetDestroyed(IntPtr hwnd)
    {
        if (!Armed) return;
        if (_selection.Remove(hwnd)) SelectionChanged?.Invoke();
    }

    // ---- 钩子回调 ----

    /// <summary>鼠标钩子:按住热键期间拦截左键,左键按下 = 切换光标处窗口的选中状态;
    /// 侧键作主键时键盘钩子收不到其抬起,在此检测松开。</summary>
    private IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg == NativeMethods.WM_XBUTTONUP)
            {
                // 抬起瞬间按键状态已为 false,必须在 IsHotkeyHeld 之外判断
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                int button = (int)(data.mouseData >> 16); // 高字:1 = XBUTTON1,2 = XBUTTON2
                if (IsHotkeyKey(button == 1 ? (uint)Keys.XButton1 : (uint)Keys.XButton2)) Finish();
            }
            else if (IsHotkeyHeld())
            {
                if (msg == NativeMethods.WM_LBUTTONDOWN)
                {
                    var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                    ToggleWindowAt(data.pt);
                }
                if (msg is NativeMethods.WM_LBUTTONDOWN or NativeMethods.WM_LBUTTONUP)
                    return (IntPtr)1; // 拦截左键按下与抬起:选中期间点击不传递给应用,避免误触按钮/切换焦点
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    /// <summary>键盘钩子:组合键的任一组成键抬起 = 松开热键,结束模式并执行堆叠。</summary>
    private IntPtr OnKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = (int)wParam;
            if (msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
            {
                var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                if (IsHotkeyKey(data.vkCode)) Finish();
            }
        }
        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    // ---- 选中 ----

    /// <summary>切换光标处窗口的选中状态;桌面/任务栏/自身窗口等不可选目标忽略。</summary>
    private void ToggleWindowAt(NativeMethods.POINT pt)
    {
        IntPtr hwnd = NativeMethods.WindowFromPoint(pt);
        if (hwnd == IntPtr.Zero) return;
        hwnd = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)
            || !NativeMethods.IsWindowVisible(hwnd) || GlowInterop.IsIconic(hwnd)
            || WindowOps.IsShellWindow(hwnd) || WindowOps.IsOwnProcess(hwnd))
            return;

        if (_selection.Remove(hwnd))
        {
            SelectionChanged?.Invoke();
            return;
        }
        _selection.Add(hwnd);
        SelectionChanged?.Invoke();
    }

    // ---- 松开与堆叠 ----

    /// <summary>松开热键:卸载钩子并执行堆叠(选中不足两个则仅取消)。</summary>
    private void Finish()
    {
        var selected = _selection.ToList();
        Unhook();
        _selection.Clear();
        SelectionChanged?.Invoke();
        if (selected.Count >= 2) Apply(selected);
    }

    /// <summary>把选中窗口堆叠到锚点(第一个选中)的位置:其余窗口移动到锚点矩形,
    /// 可缩放窗口按自身 min/max track size 钳制尺寸,不可缩放窗口只移动;
    /// 置顶窗口加入堆叠时自动移出置顶带(避免整组悬浮遮挡,也保证组内翻页在同一带内生效),
    /// 最后把锚点提到普通带顶部。</summary>
    private void Apply(List<IntPtr> selected)
    {
        IntPtr anchor = selected[0];
        if (!NativeMethods.IsWindow(anchor) || !NativeMethods.GetWindowRect(anchor, out var target)) return;

        // 入组自动取消置顶:全部选中窗口移出置顶带
        foreach (var hwnd in selected)
        {
            if (!NativeMethods.IsWindow(hwnd) || !WindowOps.IsTopmost(hwnd)) continue;
            NativeMethods.SetWindowPos(
                hwnd, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }

        int x = target.Left, y = target.Top, w = target.Right - target.Left, h = target.Bottom - target.Top;
        bool failed = false;
        for (int i = 1; i < selected.Count; i++)
        {
            if (!NativeMethods.IsWindow(selected[i])) continue;
            if (!MoveToRect(selected[i], x, y, w, h)) failed = true;
        }

        NativeMethods.SetWindowPos(
            anchor, NativeMethods.HWND_TOP,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        Stacked?.Invoke(selected);

        if (!failed) return;

        bool elevated = false;
        for (int i = 1; i < selected.Count; i++)
        {
            if (NativeMethods.IsWindow(selected[i]) && WindowOps.IsTargetProcessElevated(selected[i]))
            {
                elevated = true;
                break;
            }
        }
        Failed?.Invoke(elevated
            ? "部分窗口以管理员权限运行,当前权限的 Hauteur 无法移动它们。请以管理员身份重新启动 Hauteur 后重试。"
            : "部分窗口未完成堆叠,目标窗口可能拒绝该操作。");
    }

    /// <summary>把窗口移动到目标矩形:可缩放窗口尺寸钳制到 min/max track size,不可缩放窗口保持自身尺寸只移动;
    /// 最大化窗口用 SetWindowPlacement(SW_SHOWNOACTIVATE) 一步还原+定位且不激活。
    /// 返回是否成功(完全没移动且与目标不同 = 失败)。</summary>
    private static bool MoveToRect(IntPtr hwnd, int x, int y, int w, int h)
    {
        NativeMethods.GetWindowRect(hwnd, out var before);

        long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
        bool resizable = (style & NativeMethods.WS_THICKFRAME) != 0;

        int width = w, height = h;
        if (resizable)
        {
            var wp = new NativeMethods.WINDOWPLACEMENT { length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
            if (NativeMethods.GetWindowPlacement(hwnd, out wp))
            {
                // ptMinPosition/ptMaxPosition 存的是最小/最大 track size(字段名有误导性),仅在数值有效时钳制
                int minW = wp.ptMinPosition.X, minH = wp.ptMinPosition.Y;
                int maxW = wp.ptMaxPosition.X, maxH = wp.ptMaxPosition.Y;
                if (minW > 0 && maxW >= minW) width = Math.Clamp(width, minW, maxW);
                if (minH > 0 && maxH >= minH) height = Math.Clamp(height, minH, maxH);
            }
        }
        else
        {
            width = before.Right - before.Left;
            height = before.Bottom - before.Top;
        }

        if (NativeMethods.IsZoomed(hwnd))
        {
            var wp = new NativeMethods.WINDOWPLACEMENT { length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>() };
            if (!NativeMethods.GetWindowPlacement(hwnd, out wp)) return true; // 取不到状态:放弃该窗口(按成功处理,避免误报失败)
            wp.showCmd = NativeMethods.SW_SHOWNOACTIVATE;
            wp.rcNormalPosition = new NativeMethods.RECT
            {
                Left = x, Top = y, Right = x + width, Bottom = y + height,
            };
            NativeMethods.SetWindowPlacement(hwnd, ref wp);
        }
        else
        {
            uint flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_NOZORDER;
            if (!resizable) flags |= NativeMethods.SWP_NOSIZE;
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, flags);
        }

        // UIPI 下 SetWindowPos/SetWindowPlacement 会静默无效:前后矩形完全一致且与目标不同 → 失败;
        // 若窗口恰好已在目标位置(如本来就重叠)算成功
        NativeMethods.GetWindowRect(hwnd, out var after);
        bool unchanged = after.Left == before.Left && after.Top == before.Top
            && after.Right == before.Right && after.Bottom == before.Bottom;
        if (!unchanged) return true;
        return after.Left == x && after.Top == y
            && after.Right - after.Left == width && after.Bottom - after.Top == height;
    }

    // ---- 组合键状态 ----

    /// <summary>虚拟键是否为组合键的"按住组成部分"(键盘修饰键、键盘式组合的主键,以及侧键修饰位——供鼠标钩子检测侧键松开)。
    /// 侧键和弦的主键只是进入模式的触发键,其抬起不结束模式。</summary>
    private bool IsHotkeyKey(uint vk)
    {
        var (mods, key) = _combo();
        if ((mods & SideButtonMask) == 0 && vk == key) return true;
        if ((mods & NativeMethods.MOD_CONTROL) != 0 && vk is NativeMethods.VK_LCONTROL or NativeMethods.VK_RCONTROL) return true;
        if ((mods & NativeMethods.MOD_ALT) != 0 && vk is NativeMethods.VK_LMENU or NativeMethods.VK_RMENU) return true;
        if ((mods & NativeMethods.MOD_SHIFT) != 0 && vk is NativeMethods.VK_LSHIFT or NativeMethods.VK_RSHIFT) return true;
        if ((mods & NativeMethods.MOD_WIN) != 0 && vk is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN) return true;
        if ((mods & NativeMethods.MOD_XBUTTON1) != 0 && vk == NativeMethods.VK_XBUTTON1) return true;
        if ((mods & NativeMethods.MOD_XBUTTON2) != 0 && vk == NativeMethods.VK_XBUTTON2) return true;
        return false;
    }

    /// <summary>组合键当前是否处于"按住"状态(GetAsyncKeyState 实时检测;钩子负责松开收尾,此检测防御按下即松开的竞态)。
    /// 侧键和弦形式下主键只是触发键,只需按住侧键与键盘修饰键;键盘式组合则全部组成键都要按住。</summary>
    private bool IsHotkeyHeld()
    {
        var (mods, key) = _combo();
        if ((mods & SideButtonMask) == 0 && !NativeMethods.IsDown((int)key)) return false;
        if ((mods & NativeMethods.MOD_CONTROL) != 0 && !NativeMethods.IsDown(NativeMethods.VK_CONTROL)) return false;
        if ((mods & NativeMethods.MOD_ALT) != 0 && !NativeMethods.IsDown(NativeMethods.VK_MENU)) return false;
        if ((mods & NativeMethods.MOD_SHIFT) != 0 && !NativeMethods.IsDown(NativeMethods.VK_SHIFT)) return false;
        if ((mods & NativeMethods.MOD_WIN) != 0 && !NativeMethods.IsDown(NativeMethods.VK_LWIN) && !NativeMethods.IsDown(NativeMethods.VK_RWIN)) return false;
        if ((mods & NativeMethods.MOD_XBUTTON1) != 0 && !NativeMethods.IsDown(NativeMethods.VK_XBUTTON1)) return false;
        if ((mods & NativeMethods.MOD_XBUTTON2) != 0 && !NativeMethods.IsDown(NativeMethods.VK_XBUTTON2)) return false;
        return true;
    }

    // ---- 清理 ----

    private void Unhook()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (!Armed) return;
        Unhook();
        _selection.Clear();
        SelectionChanged?.Invoke();
    }
}
