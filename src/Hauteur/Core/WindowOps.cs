using System.Text;

namespace Hauteur.Core;

/// <summary>目标窗口的 Z 序操作与过滤。</summary>
/// <remarks>
/// Windows Z 序只有三个带:置底(其实也是普通带底部)、普通、置顶。
/// 本项目的多层语义按"从顶部数":层级 1 = 置顶(最前),层级 N = 置底(最后),
/// 中间层(2~N-1)是普通带内按比例的近似相对位置,随桌面可见窗口数量略有浮动。
/// </remarks>
internal static class WindowOps
{
    /// <summary>窗口当前是否置顶(检查扩展样式的 WS_EX_TOPMOST)。</summary>
    public static bool IsTopmost(IntPtr hwnd)
    {
        long exStyle = Interop.NativeMethods.GetWindowLongPtr(hwnd, Interop.NativeMethods.GWL_EXSTYLE).ToInt64();
        return (exStyle & Interop.NativeMethods.WS_EX_TOPMOST) != 0;
    }

    /// <summary>窗口是否位于普通带最底(即"置底"成功)。</summary>
    public static bool IsAtBottom(IntPtr hwnd)
    {
        var normal = EnumerateVisibleNormalWindows(); // top→bottom,含自身
        return normal.Count > 0 && normal[^1] == hwnd;
    }

    /// <summary>是否存在其他可见普通窗口作为分层参照。</summary>
    public static bool HasNormalBandReference(IntPtr hwnd) =>
        EnumerateVisibleNormalWindows(exclude: hwnd).Count > 0;

    /// <summary>
    /// 设置窗口层级:1 = 置顶(最前),2~N = 普通带内从顶部数的近似位置。
    /// 注意:第 N 层是普通带内最低的离散层,并非垫底——真正的最后一层由 <see cref="ToggleTopmostBottom"/> 完成。
    /// </summary>
    public static void SetLayer(IntPtr hwnd, int layer, int layerCount)
    {
        // 实测 Win11:HWND_BOTTOM/HWND_TOP 不会清除 WS_EX_TOPMOST,
        // 置顶窗口会被放到置顶带底部而不是普通带。目标层非置顶时必须先移出置顶带。
        if (layer > 1 && IsTopmost(hwnd))
        {
            SetWindowPos(hwnd, Interop.NativeMethods.HWND_NOTOPMOST);
        }

        if (layer <= 1)
        {
            SetWindowPos(hwnd, Interop.NativeMethods.HWND_TOPMOST);
            return;
        }

        // 普通带:在可见普通窗口中按比例选插入位置(层级 2~N 均分普通带)。
        var normal = EnumerateVisibleNormalWindows(exclude: hwnd); // top→bottom
        int count = normal.Count;
        if (count == 0)
        {
            SetWindowPos(hwnd, Interop.NativeMethods.HWND_TOP);
            return;
        }

        double f = (double)(layer - 2) / (layerCount - 1); // layer 2 → 0(带顶),layer N → (N-2)/(N-1)(带底之上)
        int r = (int)Math.Round(f * count, MidpointRounding.AwayFromZero);
        r = Math.Clamp(r, 0, count - 1);

        // r = 目标从顶部起算的排名(0 = 带顶);插入到排名 r-1 的窗口之后
        SetWindowPos(hwnd, r == 0 ? Interop.NativeMethods.HWND_TOP : normal[r - 1]);
    }

    /// <summary>置顶/置底切换:未置顶 → 置顶;已置顶 → 垫底(真正的最后一层)。</summary>
    public static void ToggleTopmostBottom(IntPtr hwnd)
    {
        if (IsTopmost(hwnd)) SetBottom(hwnd);
        else SetWindowPos(hwnd, Interop.NativeMethods.HWND_TOPMOST);
    }

    /// <summary>垫底:先移出置顶带(HWND_BOTTOM 不清 topmost),再放到普通带最底。</summary>
    public static void SetBottom(IntPtr hwnd)
    {
        if (IsTopmost(hwnd))
            SetWindowPos(hwnd, Interop.NativeMethods.HWND_NOTOPMOST);
        SetWindowPos(hwnd, Interop.NativeMethods.HWND_BOTTOM);
    }

    /// <summary>计算窗口当前所处层级(用于设置后的验证与失败提示)。中间层为近似值。</summary>
    public static int GetLayer(IntPtr hwnd, int layerCount)
    {
        if (IsTopmost(hwnd)) return 1;

        var normal = EnumerateVisibleNormalWindows(); // top→bottom,含自身
        int count = normal.Count;
        int index = normal.IndexOf(hwnd);
        if (count <= 1 || index < 0) return 2; // 唯一窗口:带顶

        double f = (double)index / (count - 1); // 0 = 带顶(第 2 层),1 = 带底(第 N 层)
        int layer = (int)Math.Round(f * (layerCount - 1), MidpointRounding.AwayFromZero) + 2;
        return Math.Clamp(layer, 2, layerCount);
    }

    /// <summary>Z 序中紧挨在该窗口"前面"的顶层窗口(用于退出时恢复相对位置);跳过自身光晕窗口,找不到返回 Zero。</summary>
    public static IntPtr GetWindowAbove(IntPtr hwnd)
    {
        IntPtr above = IntPtr.Zero;
        for (IntPtr w = Interop.NativeMethods.GetTopWindow(IntPtr.Zero);
             w != IntPtr.Zero;
             w = Interop.NativeMethods.GetWindow(w, Interop.NativeMethods.GW_HWNDNEXT))
        {
            if (w == hwnd) return above;
            if (!IsOwnProcess(w)) above = w; // 光晕覆盖窗口不计入层级参照
        }
        return IntPtr.Zero;
    }

    /// <summary>桌面/任务栏等外壳窗口,不允许操作。</summary>
    public static bool IsShellWindow(IntPtr hwnd)
    {
        var sb = new StringBuilder(64);
        if (Interop.NativeMethods.GetClassName(hwnd, sb, sb.Capacity) == 0) return true;
        var cls = sb.ToString();
        return cls is "Progman" or "WorkerW" or "Shell_TrayWnd";
    }

    /// <summary>窗口是否属于本进程(Hauteur 自己的窗口)。</summary>
    public static bool IsOwnProcess(IntPtr hwnd)
    {
        Interop.NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == Environment.ProcessId;
    }

    /// <summary>
    /// 目标窗口进程是否提权(UIPI:非提权进程无法调整提权窗口的 Z 序)。
    /// 无法查询(通常因为权限不够)时按提权处理,宁可多提示也不误判。
    /// </summary>
    public static bool IsTargetProcessElevated(IntPtr hwnd)
    {
        Interop.NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return true;

        var handle = Interop.NativeMethods.OpenProcess(
            Interop.NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return true;

        try
        {
            if (!Interop.NativeMethods.OpenProcessToken(handle, Interop.NativeMethods.TOKEN_QUERY, out var token))
                return true;

            try
            {
                return Interop.NativeMethods.GetTokenInformation(
                    token, Interop.NativeMethods.TOKEN_INFORMATION_CLASS.TokenElevation,
                    out uint elevated, sizeof(uint), out _) && elevated != 0;
            }
            finally
            {
                Interop.NativeMethods.CloseHandle(token);
            }
        }
        finally
        {
            Interop.NativeMethods.CloseHandle(handle);
        }
    }

    private static void SetWindowPos(IntPtr hwnd, IntPtr insertAfter)
    {
        Interop.NativeMethods.SetWindowPos(
            hwnd, insertAfter, 0, 0, 0, 0,
            Interop.NativeMethods.SWP_NOMOVE | Interop.NativeMethods.SWP_NOSIZE | Interop.NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>枚举可见、非置顶的顶层窗口,按 Z 序 top→bottom;exclude 用于排除目标自身。</summary>
    private static List<IntPtr> EnumerateVisibleNormalWindows(IntPtr exclude = default)
    {
        var result = new List<IntPtr>();
        for (IntPtr w = Interop.NativeMethods.GetTopWindow(IntPtr.Zero);
             w != IntPtr.Zero;
             w = Interop.NativeMethods.GetWindow(w, Interop.NativeMethods.GW_HWNDNEXT))
        {
            // 排除自身的光晕覆盖窗口(它们在目标窗口附近,不得污染层级计算)
            if (w == exclude || IsOwnProcess(w)
                || !Interop.NativeMethods.IsWindowVisible(w) || IsTopmost(w)) continue;
            result.Add(w);
        }
        return result;
    }
}
