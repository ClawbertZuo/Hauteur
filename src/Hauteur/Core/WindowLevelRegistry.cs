namespace Hauteur.Core;

/// <summary>
/// 被 Hauteur 调整过层级的窗口记录。首次调整前快照原始状态
/// (是否原本置顶 + Z 序中紧挨的前邻居),退出/一键恢复时还原,实现"注销层级状态";
/// 同时保存当前应用层级(Kind/Layer),供"层级保持"功能在窗口被点击激活时拉回。
/// </summary>
internal sealed class WindowLevelRegistry
{
    public enum AppliedLayerKind
    {
        Layer,   // 层级 2~N:普通带内位置
        Topmost, // 置顶(层级 1 / 切换热键置顶)
        Bottom,  // 垫底(切换热键置底)
    }

    public sealed record WindowState(IntPtr Hwnd, bool WasTopmost, IntPtr PrevInsertAfter, AppliedLayerKind Kind, int Layer);

    private readonly Dictionary<IntPtr, WindowState> _states = new();

    /// <summary>
    /// 记录/更新窗口的被管理状态。首次记录时快照原始状态(退出恢复用,之后保持不变);
    /// 再次调用时更新当前层级(Kind/Layer),供层级保持使用。
    /// </summary>
    public WindowState Track(IntPtr hwnd, AppliedLayerKind kind, int layer)
    {
        if (_states.TryGetValue(hwnd, out var existing))
        {
            var updated = existing with { Kind = kind, Layer = layer };
            _states[hwnd] = updated;
            return updated;
        }

        var state = new WindowState(
            hwnd,
            WindowOps.IsTopmost(hwnd),
            WindowOps.GetWindowAbove(hwnd),
            kind, layer);
        _states[hwnd] = state;
        return state;
    }

    /// <summary>查询窗口的被管理状态(未管理返回 null)。</summary>
    public WindowState? Find(IntPtr hwnd) =>
        _states.TryGetValue(hwnd, out var s) ? s : null;

    /// <summary>当前所有被管理窗口状态的快照(供换色重绘等遍历)。</summary>
    public List<WindowState> Snapshot() => _states.Values.ToList();

    public void Remove(IntPtr hwnd) => _states.Remove(hwnd);

    /// <summary>
    /// 恢复单个窗口到最初层级状态并移出记录(解绑热键用):
    /// 原本置顶的重新置顶,其余移出置顶带并尽量插回原前邻居之后。窗口未被管理时返回 false。
    /// </summary>
    public bool Restore(IntPtr hwnd)
    {
        if (!_states.Remove(hwnd, out var state)) return false;
        if (!Interop.NativeMethods.IsWindow(hwnd)) return true;

        if (state.WasTopmost)
        {
            SetWindowPos(hwnd, Interop.NativeMethods.HWND_TOPMOST);
            return true;
        }

        SetWindowPos(hwnd, Interop.NativeMethods.HWND_NOTOPMOST);
        var prev = state.PrevInsertAfter;
        if (prev != IntPtr.Zero && prev != hwnd
            && Interop.NativeMethods.IsWindow(prev)
            && Interop.NativeMethods.IsWindowVisible(prev))
        {
            SetWindowPos(hwnd, prev);
        }
        return true;
    }

    /// <summary>
    /// 恢复所有窗口到最初层级状态:原本置顶的重新置顶,
    /// 其余移出置顶带并尽量插回原前邻居之后(窗口已销毁的跳过)。
    /// </summary>
    public void RestoreAll()
    {
        foreach (var (hwnd, state) in _states.ToList())
        {
            if (!Interop.NativeMethods.IsWindow(hwnd))
            {
                _states.Remove(hwnd);
                continue;
            }

            if (state.WasTopmost)
            {
                SetWindowPos(hwnd, Interop.NativeMethods.HWND_TOPMOST);
            }
            else
            {
                // 先移出置顶带(HWND_BOTTOM 不清 topmost,见 WindowOps 注释)
                SetWindowPos(hwnd, Interop.NativeMethods.HWND_NOTOPMOST);

                var prev = state.PrevInsertAfter;
                if (prev != IntPtr.Zero && prev != hwnd
                    && Interop.NativeMethods.IsWindow(prev)
                    && Interop.NativeMethods.IsWindowVisible(prev))
                {
                    SetWindowPos(hwnd, prev);
                }
            }
        }
        _states.Clear();
    }

    private static void SetWindowPos(IntPtr hwnd, IntPtr insertAfter)
    {
        Interop.NativeMethods.SetWindowPos(
            hwnd, insertAfter, 0, 0, 0, 0,
            Interop.NativeMethods.SWP_NOMOVE | Interop.NativeMethods.SWP_NOSIZE | Interop.NativeMethods.SWP_NOACTIVATE);
    }
}
