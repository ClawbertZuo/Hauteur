using System.Runtime.InteropServices;
using System.Text;

namespace Hauteur.Interop;

/// <summary>本项目用到的 Win32 API 与常量(MVP 范围:置顶切换 / 全局热键 / 单实例)。</summary>
internal static class NativeMethods
{
    // ---- 窗口 ----

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    /// <summary>取根窗口,避免拿到子控件(GA_ROOT = 2)。</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    /// <summary>
    /// 调整窗口 Z 序。hWndInsertAfter 传 HWND_TOPMOST / HWND_NOTOPMOST;
    /// 带 SWP_NOACTIVATE 避免激活、移动目标窗口。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>Z 序最顶层的顶层窗口(GW_HWNDNEXT 向下遍历可得完整 Z 序)。</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr GetTopWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    // ---- 全局热键 ----

    /// <summary>注册全局热键;失败(组合键被占用)返回 false。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    internal static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int vKey);

    /// <summary>读取按键实时按住状态(GetAsyncKeyState 高位置位;鼠标侧键同样有效)。</summary>
    internal static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>修饰键(含侧键位)按住状态与配置完全一致(与 RegisterHotKey 语义相同)。</summary>
    internal static bool ModifiersMatch(uint mods) =>
        IsDown(VK_CONTROL) == ((mods & MOD_CONTROL) != 0)
        && IsDown(VK_MENU) == ((mods & MOD_ALT) != 0)
        && IsDown(VK_SHIFT) == ((mods & MOD_SHIFT) != 0)
        && (IsDown(VK_LWIN) || IsDown(VK_RWIN)) == ((mods & MOD_WIN) != 0)
        && IsDown(VK_XBUTTON1) == ((mods & MOD_XBUTTON1) != 0)
        && IsDown(VK_XBUTTON2) == ((mods & MOD_XBUTTON2) != 0);

    // ---- 低层钩子与窗口摆放(窗口堆叠多选)----

    internal delegate IntPtr LowLevelHookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelHookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    /// <summary>光标处可见窗口(穿透 WS_EX_TRANSPARENT 窗口如光晕覆盖)。参数为屏幕坐标。</summary>
    [DllImport("user32.dll")]
    internal static extern IntPtr WindowFromPoint(POINT pt);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowPlacement(IntPtr hWnd, out WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsZoomed(IntPtr hWnd);

    // ---- 单实例广播 ----

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // ---- 进程提权检测 ----

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    /// <summary>TokenElevation 返回 TOKEN_ELEVATION { DWORD TokenIsElevated },即 4 字节。</summary>
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(
        IntPtr tokenHandle, TOKEN_INFORMATION_CLASS tokenInformationClass,
        out uint tokenInformation, uint tokenInformationLength, out uint returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr hObject);

    internal enum TOKEN_INFORMATION_CLASS : int
    {
        TokenElevation = 20,
    }

    // ---- 结构体 ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>WINDOWPLACEMENT:length 需先设为结构体大小;ptMinPosition/ptMaxPosition 实为最小/最大 track size。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    // ---- 常量 ----

    internal const int GWL_EXSTYLE = -20;
    internal const int GWL_STYLE = -16;
    internal const long WS_EX_TOPMOST = 0x00000008L;
    internal const long WS_THICKFRAME = 0x00040000L;

    internal const uint GA_ROOT = 2;
    internal const uint GW_HWNDNEXT = 2;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal static readonly IntPtr HWND_NOTOPMOST = new(-2);
    internal static readonly IntPtr HWND_TOP = IntPtr.Zero; // 0 = 当前带内顶部
    internal static readonly IntPtr HWND_BOTTOM = new(1);   // 1 = 当前带内底部
    internal static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>跨进程异步移动:请求投递到目标线程队列,调用线程不阻塞(组跟随高频移动用,避免拖尾)。</summary>
    internal const uint SWP_ASYNCWINDOWPOS = 0x4000;

    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;

    /// <summary>鼠标侧键作修饰键(按住侧键再按主键的和弦形式)。RegisterHotKey 不支持此位,
    /// 仅用于配置存储与低层钩子检测;注册时需先剥离。</summary>
    internal const uint MOD_XBUTTON1 = 0x0010;
    internal const uint MOD_XBUTTON2 = 0x0020;

    /// <summary>RegisterHotKey 支持的标准修饰键掩码。</summary>
    internal const uint MOD_MASK_STANDARD = MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN;

    internal const int VK_XBUTTON1 = 0x05;
    internal const int VK_XBUTTON2 = 0x06;

    internal const int VK_LWIN = 0x5B;
    internal const int VK_RWIN = 0x5C;
    internal const int VK_SHIFT = 0x10;
    internal const int VK_CONTROL = 0x11;
    internal const int VK_MENU = 0x12;
    internal const int VK_LSHIFT = 0xA0;
    internal const int VK_RSHIFT = 0xA1;
    internal const int VK_LCONTROL = 0xA2;
    internal const int VK_RCONTROL = 0xA3;
    internal const int VK_LMENU = 0xA4;
    internal const int VK_RMENU = 0xA5;

    internal const int WH_KEYBOARD_LL = 13;
    internal const int WH_MOUSE_LL = 14;
    internal const int WM_KEYDOWN = 0x0100;
    internal const int WM_KEYUP = 0x0101;
    internal const int WM_SYSKEYDOWN = 0x0104;
    internal const int WM_SYSKEYUP = 0x0105;
    internal const int WM_LBUTTONDOWN = 0x0201;
    internal const int WM_LBUTTONUP = 0x0202;
    internal const int WM_MOUSEWHEEL = 0x020A;
    internal const int WM_XBUTTONUP = 0x020C;

    internal const int SW_SHOWNOACTIVATE = 4;

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint TOKEN_QUERY = 0x0008;
}
