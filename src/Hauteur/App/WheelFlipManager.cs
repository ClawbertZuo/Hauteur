using System.Runtime.InteropServices;
using Hauteur.Interop;

namespace Hauteur.App;

/// <summary>
/// 堆叠翻页:按住配置的修饰键(默认 Ctrl+Alt,可含鼠标侧键)滚动鼠标滚轮时,
/// 轮换光标处窗口所在组的 Z 序(下滚 = 首页移到最后一页,上滚 = 最后一页提到最上)。
/// 常驻 WH_MOUSE_LL 钩子仅对滚轮事件做修饰键检测,普通鼠标事件零开销直通;
/// 轮换成功时拦截滚轮事件,避免应用同时滚动。
/// </summary>
internal sealed class WheelFlipManager : IDisposable
{
    private readonly Func<IntPtr, bool, bool> _rotate; // (光标处窗口, 是否下一页) → 是否已轮换
    private readonly Func<bool> _suspended; // 暂停/多选模式期间挂起
    private readonly Func<uint> _modifiers; // 当前配置的翻页修饰键(实时读取)

    private readonly NativeMethods.LowLevelHookProc _mouseProc; // 常驻字段防止委托被 GC
    private readonly IntPtr _mouseHook;

    public WheelFlipManager(Func<IntPtr, bool, bool> rotate, Func<bool> suspended, Func<uint> modifiers)
    {
        _rotate = rotate;
        _suspended = suspended;
        _modifiers = modifiers;
        _mouseProc = OnMouseHook;
        _mouseHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_MOUSE_LL, _mouseProc, IntPtr.Zero, 0);
    }

    private IntPtr OnMouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (int)wParam == NativeMethods.WM_MOUSEWHEEL && !_suspended())
        {
            uint mods = _modifiers();
            if (mods != 0 && NativeMethods.ModifiersMatch(mods))
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                int delta = unchecked((short)(data.mouseData >> 16));
                if (delta != 0)
                {
                    IntPtr hwnd = NativeMethods.WindowFromPoint(data.pt);
                    if (hwnd != IntPtr.Zero)
                    {
                        hwnd = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
                        // 下滚(delta < 0)= 下一页;上滚 = 上一页
                        if (hwnd != IntPtr.Zero && _rotate(hwnd, delta < 0))
                            return (IntPtr)1;
                    }
                }
            }
        }
        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose() => NativeMethods.UnhookWindowsHookEx(_mouseHook);
}
