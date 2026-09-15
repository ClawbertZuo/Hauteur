using Hauteur.Interop;

namespace Hauteur.App;

/// <summary>
/// 窗口组:堆叠后的窗口绑定为一组,拖动任一组员时其余组员跟随移动,保持建组时的相对位置不散开。
/// 跟随用 16ms 定时器轮询:每拍对比组员矩形,发现位移后把其余组员异步移动到对应绝对位置
/// (SWP_ASYNCWINDOWPOS 跨进程不阻塞 UI 线程,避免拖尾)。
/// 被投递过异步移动的组员在 200ms 在途窗口内不当作拖动源,在途结束后以实际稳定位置对齐,
/// 避免落点 1~2px 偏差(DPI 网格/应用自调整)反馈成振荡抖动。
/// 组员被销毁时自动移出组;组为会话级状态,退出程序即解散。
/// </summary>
internal sealed class WindowGroupManager : IDisposable
{
    /// <summary>异步移动的在途窗口时长:超过该时长未再有投递,认为落点已稳定。</summary>
    private const long InFlightMs = 200;

    private readonly List<HashSet<IntPtr>> _groups = new();
    private readonly Dictionary<IntPtr, int> _indexByHwnd = new();       // 窗口 → 组下标
    private readonly Dictionary<IntPtr, NativeMethods.RECT> _baseRects = new();   // 建组时的矩形(相对位置基准)
    private readonly Dictionary<IntPtr, NativeMethods.RECT> _lastRects = new();  // 上次已知矩形
    private readonly Dictionary<IntPtr, long> _inFlightUntil = new(); // 已投递异步移动的组员:在此时间戳前不当作拖动源
    private readonly HashSet<IntPtr> _wasIconic = new(); // 上一拍处于最小化的组员(恢复时反向拉回,不作为拖动源)

    private readonly System.Windows.Forms.Timer _followTimer;

    public WindowGroupManager()
    {
        _followTimer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60fps 跟随
        _followTimer.Tick += (_, _) => PollAll();
    }

    /// <summary>组员集合变化(建组/解散/移出/自动解散),外部据此同步组光晕。</summary>
    public event Action? Changed;

    /// <summary>当前窗口组数量。</summary>
    public int GroupCount => _groups.Count;

    /// <summary>所有窗口组的全部成员。</summary>
    public IEnumerable<IntPtr> Members => _groups.SelectMany(g => g);

    /// <summary>窗口是否在某个组里。</summary>
    public bool Contains(IntPtr hwnd) => _indexByHwnd.ContainsKey(hwnd);

    /// <summary>窗口所在组的全部成员(不在任何组时返回 null)。</summary>
    public IReadOnlyCollection<IntPtr>? MembersOf(IntPtr hwnd) =>
        _indexByHwnd.TryGetValue(hwnd, out int index) ? _groups[index] : null;

    /// <summary>把给定窗口绑定为一组(已在旧组中的先移出,旧组其余窗口保留)。</summary>
    public void CreateGroup(IEnumerable<IntPtr> windows)
    {
        var set = new HashSet<IntPtr>();
        foreach (var hwnd in windows)
        {
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd)) continue;
            RemoveMember(hwnd);
            set.Add(hwnd);
        }
        if (set.Count < 2)
        {
            Changed?.Invoke(); // 移出旧组可能已改变成员关系
            return;
        }

        int index = _groups.Count;
        _groups.Add(set);
        foreach (var hwnd in set)
        {
            _indexByHwnd[hwnd] = index;
            NativeMethods.GetWindowRect(hwnd, out var r);
            _baseRects[hwnd] = r;
            _lastRects[hwnd] = r;
            _inFlightUntil.Remove(hwnd);
            _wasIconic.Remove(hwnd);
        }
        if (!_followTimer.Enabled) _followTimer.Start();
        Changed?.Invoke();
    }

    /// <summary>解散所有窗口组,返回解散的组数。</summary>
    public int DissolveAll()
    {
        int count = _groups.Count;
        _groups.Clear();
        _indexByHwnd.Clear();
        _baseRects.Clear();
        _lastRects.Clear();
        _inFlightUntil.Clear();
        _wasIconic.Clear();
        _followTimer.Stop();
        if (count > 0) Changed?.Invoke();
        return count;
    }

    /// <summary>解散指定窗口所在的窗口组;窗口不在任何组时返回 false。</summary>
    public bool Dissolve(IntPtr hwnd)
    {
        if (!_indexByHwnd.TryGetValue(hwnd, out int index)) return false;

        var group = _groups[index];
        _groups.RemoveAt(index);
        foreach (var member in group)
        {
            _indexByHwnd.Remove(member);
            _baseRects.Remove(member);
            _lastRects.Remove(member);
            _inFlightUntil.Remove(member);
            _wasIconic.Remove(member);
        }
        foreach (var key in _indexByHwnd.Keys.ToList())
            if (_indexByHwnd[key] > index) _indexByHwnd[key]--;
        Changed?.Invoke();
        return true;
    }

    /// <summary>把窗口移出其窗口组(组不足两个成员时自动解散);不在任何组时无操作。</summary>
    public void Remove(IntPtr hwnd) => RemoveMember(hwnd);

    /// <summary>窗口所在组成员按全局 Z 序自底向上排列;窗口不在任何组时返回 null。
    /// 整组置顶/设层按此顺序逐个处理可保持组内页面顺序。</summary>
    public List<IntPtr>? MembersBottomFirst(IntPtr hwnd)
    {
        if (!_indexByHwnd.TryGetValue(hwnd, out int index)) return null;
        var group = _groups[index];

        var ordered = new List<IntPtr>(group.Count); // 自顶向下收集
        for (IntPtr w = NativeMethods.GetTopWindow(IntPtr.Zero);
             w != IntPtr.Zero;
             w = NativeMethods.GetWindow(w, NativeMethods.GW_HWNDNEXT))
        {
            if (group.Contains(w)) ordered.Add(w);
        }
        ordered.Reverse(); // 自底向上
        return ordered;
    }

    /// <summary>
    /// 轮换窗口组的 Z 序(堆叠翻页):forward = 首页移到最后一页(下一页),
    /// 否则最后一页提到最上(上一页)。窗口不在任何组时返回 false。
    /// </summary>
    public bool Rotate(IntPtr hwnd, bool forward)
    {
        var members = MembersBottomFirst(hwnd); // 自底向上:members[0]=最底,members[^1]=最上
        if (members is null || members.Count < 2) return false;
        IntPtr top = members[^1], bottom = members[0];

        if (forward)
        {
            // 首页移到最后一页:最上的组员插到最底组员之后
            NativeMethods.SetWindowPos(
                top, bottom, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        else
        {
            // 上一页:最底的组员提到最上组员的正上方(找不到上方窗口时用 HWND_TOP)
            IntPtr above = IntPtr.Zero;
            for (IntPtr w = NativeMethods.GetTopWindow(IntPtr.Zero);
                 w != IntPtr.Zero;
                 w = NativeMethods.GetWindow(w, NativeMethods.GW_HWNDNEXT))
            {
                if (w == top) break;
                above = w;
            }
            NativeMethods.SetWindowPos(
                bottom, above, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
        return true;
    }

    // ---- 跟随 ----

    /// <summary>每拍:清理已销毁组员,并对位置变化的组员执行跟随。</summary>
    private void PollAll()
    {
        if (_groups.Count == 0)
        {
            _followTimer.Stop();
            return;
        }
        long now = Environment.TickCount64;
        foreach (var hwnd in _indexByHwnd.Keys.ToList())
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                RemoveMember(hwnd);
                continue;
            }
            // 最小化窗口的 GetWindowRect 返回 (-32000, -32000) 之类的图标位置,必须跳过,否则会把整组拖出屏幕
            if (GlowInterop.IsIconic(hwnd))
            {
                _wasIconic.Add(hwnd);
                continue;
            }
            // 从最小化恢复:把它拉回组的当前位置(其保存的恢复位置可能已过期),不当作拖动源
            if (_wasIconic.Remove(hwnd))
            {
                ReAnchor(hwnd, now);
                continue;
            }

            bool inFlight = _inFlightUntil.TryGetValue(hwnd, out long until) && until > now;
            if (inFlight) continue; // 我们投递的异步移动尚未稳定,不当作拖动源
            bool wasInFlight = _inFlightUntil.Remove(hwnd); // 在途已过期(不存在也无妨)

            if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            {
                RemoveMember(hwnd);
                continue;
            }
            // 在途刚结束:以实际稳定位置对齐(落点可能有 1~2px 偏差,不作为拖动源,避免反馈振荡)
            if (wasInFlight)
            {
                _lastRects[hwnd] = rect;
                continue;
            }

            if (_lastRects.TryGetValue(hwnd, out var last) && SameRect(last, rect)) continue;

            // 真正的移动(用户拖动或程序移动):记录新位置并带动其余组员
            _lastRects[hwnd] = rect;
            if (!_baseRects.TryGetValue(hwnd, out var baseRect)) continue;
            // 组下标即时查询:本拍内 RemoveMember 可能已压缩过下标
            foreach (var member in _groups[_indexByHwnd[hwnd]])
            {
                if (member == hwnd || !NativeMethods.IsWindow(member) || GlowInterop.IsIconic(member)) continue;
                if (!_baseRects.TryGetValue(member, out var mb)) continue;

                int tx = rect.Left + (mb.Left - baseRect.Left);
                int ty = rect.Top + (mb.Top - baseRect.Top);
                NativeMethods.SetWindowPos(
                    member, IntPtr.Zero, tx, ty, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER
                    | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_ASYNCWINDOWPOS);
                _inFlightUntil[member] = now + InFlightMs;
            }
        }
    }

    /// <summary>把刚从最小化恢复的组员拉回组的当前位置(以另一名可见组员为参照,保持建组时的相对位置)。</summary>
    private void ReAnchor(IntPtr hwnd, long now)
    {
        if (!_indexByHwnd.TryGetValue(hwnd, out int index)) return;
        if (!_baseRects.TryGetValue(hwnd, out var baseRect)) return;

        foreach (var other in _groups[index])
        {
            if (other == hwnd || !NativeMethods.IsWindow(other) || GlowInterop.IsIconic(other)) continue;
            if (!_baseRects.TryGetValue(other, out var ob) || !NativeMethods.GetWindowRect(other, out var or)) continue;

            int tx = or.Left + (baseRect.Left - ob.Left);
            int ty = or.Top + (baseRect.Top - ob.Top);
            NativeMethods.SetWindowPos(
                hwnd, IntPtr.Zero, tx, ty, 0, 0,
                NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER
                | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_ASYNCWINDOWPOS);
            _inFlightUntil[hwnd] = now + InFlightMs;
            return;
        }
    }

    private static bool SameRect(NativeMethods.RECT a, NativeMethods.RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    /// <summary>移出组员;组只剩 0~1 个成员时无意义,整体解散。</summary>
    private void RemoveMember(IntPtr hwnd)
    {
        if (!_indexByHwnd.Remove(hwnd, out int index)) return;
        var group = _groups[index];
        group.Remove(hwnd);
        _baseRects.Remove(hwnd);
        _lastRects.Remove(hwnd);
        _inFlightUntil.Remove(hwnd);
        _wasIconic.Remove(hwnd);

        if (group.Count < 2)
        {
            foreach (var leftover in group)
            {
                _indexByHwnd.Remove(leftover);
                _baseRects.Remove(leftover);
                _lastRects.Remove(leftover);
                _inFlightUntil.Remove(leftover);
                _wasIconic.Remove(leftover);
            }
            _groups.RemoveAt(index);
            foreach (var key in _indexByHwnd.Keys.ToList())
                if (_indexByHwnd[key] > index) _indexByHwnd[key]--;
        }
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _followTimer.Stop();
        _followTimer.Dispose();
        _groups.Clear();
        _indexByHwnd.Clear();
        _baseRects.Clear();
        _lastRects.Clear();
        _inFlightUntil.Clear();
        _wasIconic.Clear();
    }
}
