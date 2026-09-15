using Hauteur.Interop;

namespace Hauteur.App;

/// <summary>
/// 光晕管理:为被设层级的窗口创建并维护跟随的光晕覆盖窗口(见 <see cref="GlowOverlay"/>)。
/// 层级光晕与窗口组光晕是独立的两个槽位,可同时显示(组光晕画在外圈环带)。
/// 跟随机制:SetWinEventHook 监听目标窗口移动/缩放/最小化/隐藏/销毁 + 200ms 轮询兜底。
/// </summary>
internal sealed class GlowManager : IDisposable
{
    private sealed record OverlayEntry(GlowOverlay Overlay, uint Argb, float Opacity);

    private readonly Dictionary<IntPtr, OverlayEntry> _overlays = new();       // 层级光晕槽位
    private readonly Dictionary<IntPtr, OverlayEntry> _groupOverlays = new();  // 窗口组光晕槽位(独立)
    private readonly IntPtr _objectHook;
    private readonly IntPtr _systemHook;
    private readonly GlowInterop.WinEventDelegate _eventDelegate; // 常驻字段防止委托被 GC
    private readonly System.Windows.Forms.Timer _pollTimer;

    /// <summary>目标窗口被销毁(外部关闭),需要同步清理层级记录。</summary>
    public event Action<IntPtr>? TargetDestroyed;

    public GlowManager()
    {
        // OBJECT 事件覆盖 DESTROY/HIDE/SHOW/LOCATIONCHANGE,SYSTEM 事件覆盖 MOVESIZEEND/最小化
        _eventDelegate = OnWinEvent;
        _objectHook = GlowInterop.SetWinEventHook(
            GlowInterop.EVENT_OBJECT_DESTROY, GlowInterop.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _eventDelegate, 0, 0, GlowInterop.WINEVENT_OUTOFCONTEXT);
        _systemHook = GlowInterop.SetWinEventHook(
            GlowInterop.EVENT_SYSTEM_MOVESIZEEND, GlowInterop.EVENT_SYSTEM_MINIMIZEEND,
            IntPtr.Zero, _eventDelegate, 0, 0, GlowInterop.WINEVENT_OUTOFCONTEXT);

        // 低频轮询兜底:事件可能漏发(快速拖动等)
        _pollTimer = new System.Windows.Forms.Timer { Interval = 200 };
        _pollTimer.Tick += (_, _) => PollAll();
        _pollTimer.Start();
    }

    /// <summary>为目标窗口创建/更新层级光晕(已有则复用覆盖窗口,只刷新位置与绘制)。颜色由调用方按层级决定。</summary>
    public void Attach(IntPtr target, System.Drawing.Color color, float opacity)
    {
        uint argb = (uint)color.ToArgb();
        if (!_overlays.TryGetValue(target, out var entry))
        {
            entry = new OverlayEntry(new GlowOverlay(), argb, opacity);
            _overlays[target] = entry;
        }
        else
        {
            _overlays[target] = entry with { Argb = argb, Opacity = opacity };
        }
        _overlays[target].Overlay.Sync(target, argb, opacity);
    }

    /// <summary>移除并销毁指定窗口的层级光晕(不触发 TargetDestroyed)。</summary>
    public void Detach(IntPtr target)
    {
        if (_overlays.Remove(target, out var entry))
            entry.Overlay.Dispose();
    }

    /// <summary>为目标窗口创建/更新窗口组光晕(外圈环带,与层级光晕独立并存)。</summary>
    public void AttachGroup(IntPtr target, System.Drawing.Color color, float opacity)
    {
        uint argb = (uint)color.ToArgb();
        if (!_groupOverlays.TryGetValue(target, out var entry))
        {
            entry = new OverlayEntry(new GlowOverlay(GlowOverlay.GlowWidth), argb, opacity);
            _groupOverlays[target] = entry;
        }
        else
        {
            _groupOverlays[target] = entry with { Argb = argb, Opacity = opacity };
        }
        _groupOverlays[target].Overlay.Sync(target, argb, opacity);
    }

    /// <summary>移除并销毁指定窗口的窗口组光晕(不触发 TargetDestroyed)。</summary>
    public void DetachGroup(IntPtr target)
    {
        if (_groupOverlays.Remove(target, out var entry))
            entry.Overlay.Dispose();
    }

    /// <summary>销毁全部光晕(退出/一键恢复时调用)。</summary>
    public void ClearAll()
    {
        foreach (var entry in _overlays.Values)
            entry.Overlay.Dispose();
        _overlays.Clear();
        foreach (var entry in _groupOverlays.Values)
            entry.Overlay.Dispose();
        _groupOverlays.Clear();
    }

    /// <summary>暂停时隐藏全部光晕。</summary>
    public void HideAll()
    {
        foreach (var entry in _overlays.Values)
            entry.Overlay.Hide();
        foreach (var entry in _groupOverlays.Values)
            entry.Overlay.Hide();
    }

    /// <summary>恢复显示(重新同步各目标当前状态)。</summary>
    public void ShowAll() => PollAll();


    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0) return; // OBJID_WINDOW:只看窗口本身的事件

        if (eventType == GlowInterop.EVENT_OBJECT_DESTROY)
        {
            bool removed = false;
            if (_overlays.Remove(hwnd, out var entry))
            {
                entry.Overlay.Dispose();
                removed = true;
            }
            if (_groupOverlays.Remove(hwnd, out var groupEntry))
            {
                groupEntry.Overlay.Dispose();
                removed = true;
            }
            if (removed) TargetDestroyed?.Invoke(hwnd);
            return;
        }

        if (_overlays.TryGetValue(hwnd, out var e))
            e.Overlay.Sync(hwnd, e.Argb, e.Opacity);
        if (_groupOverlays.TryGetValue(hwnd, out var ge))
            ge.Overlay.Sync(hwnd, ge.Argb, ge.Opacity);
    }

    private void PollAll()
    {
        foreach (var (target, entry) in _overlays.ToList())
        {
            // 兜底清理:目标窗口已销毁但事件未收到
            if (!NativeMethods.IsWindow(target))
            {
                _overlays.Remove(target);
                entry.Overlay.Dispose();
                TargetDestroyed?.Invoke(target);
                continue;
            }
            entry.Overlay.Sync(target, entry.Argb, entry.Opacity);
        }
        foreach (var (target, entry) in _groupOverlays.ToList())
        {
            if (!NativeMethods.IsWindow(target))
            {
                _groupOverlays.Remove(target);
                entry.Overlay.Dispose();
                TargetDestroyed?.Invoke(target);
                continue;
            }
            entry.Overlay.Sync(target, entry.Argb, entry.Opacity);
        }
    }

    public void Dispose()
    {
        _pollTimer.Stop();
        _pollTimer.Dispose();
        GlowInterop.UnhookWinEvent(_objectHook);
        GlowInterop.UnhookWinEvent(_systemHook);
        ClearAll();
    }
}
