using Hauteur.Core;
using Hauteur.Interop;
using Hauteur.UI;

namespace Hauteur.App;

/// <summary>
/// 托盘常驻上下文:程序启动后不弹主窗口,仅驻留系统托盘。
/// 单击托盘图标打开设置;全局热键(全部可在设置中自定义,支持鼠标侧键):
///   层级热键(默认 Ctrl+Alt+1~N)将前台窗口设到"从顶部数第 N 层"(1 置顶/最前,2~N 普通带内近似位置;N 不是垫底)
///   Ctrl+Alt+T(可自定义)置顶 ↔ 置底(垫底,真正的最后一层)切换
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    /// <summary>光晕亮度:所有层级一致(鲜艳度一致),层级区分完全由颜色渐变承担。</summary>
    private const float GlowOpacity = 0.9f;

    private readonly MessageWindow _messageWindow;
    private readonly HotkeyManager _hotkeys;
    private readonly GlowManager _glow = new();
    private readonly WindowLevelRegistry _registry = new();
    private readonly StackModeManager _stackMode;
    private readonly WindowGroupManager _groups;
    private readonly WheelFlipManager _wheelFlip;

    /// <summary>当前戴着选中光晕的窗口(与 <see cref="StackModeManager.Selection"/> 同步)。</summary>
    private List<IntPtr> _selectionGlowTargets = new();

    /// <summary>当前戴着窗口组光晕的窗口(与 WindowGroupManager.Members 同步)。</summary>
    private List<IntPtr> _groupGlowTargets = new();
    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly GlowInterop.WinEventDelegate _foregroundDelegate; // 常驻字段防止委托被 GC
    private readonly IntPtr _foregroundHook;

    private readonly SplashWindow? _splash;

    private AppSettings _settings;
    private SettingsForm? _settingsForm;

    /// <summary>各层级光晕颜色(索引 0 = 层级 1/置顶,末尾 = 层级 N/垫底)。</summary>
    private uint[] GlowLayerColors => _settings.GlowLayerColors!;

    public TrayAppContext()
    {
        // 启动加载界面:logo 居中浮现,短暂显示后淡出(不抢焦点,失败不影响启动)
        _splash = SplashWindow.TryShow();
        _splash?.CloseAfter(TimeSpan.FromMilliseconds(1100), TimeSpan.FromMilliseconds(380));

        _settings = ConfigStore.Load();
        _settings.LayerCount = Math.Clamp(_settings.LayerCount, 3, 9); // 防止手改配置出现非法值
        _settings.EnsureLayerColors(_settings.LayerCount);
        _settings.EnsureLayerHotkeys(_settings.LayerCount);
        StartupManager.MigrateLegacyRunKey(); // 清理旧项目名的开机自启注册表项
        _settings.AutoStart = StartupManager.IsEnabled(); // 以注册表实际状态为准

        _messageWindow = new MessageWindow();
        _messageWindow.HotkeyPressed += OnHotkeyPressed;
        _messageWindow.ShowSettingsRequested += OpenSettings;
        _messageWindow.ExitRequested += ExitThread; // 正常退出路径:Dispose 中注销层级状态
        _glow.TargetDestroyed += OnGlowTargetDestroyed;

        // 窗口堆叠多选:按住热键期间左键点选,松开后堆叠;堆叠成功 → 绑定为窗口组(拖动跟随 + 组光晕)
        _groups = new WindowGroupManager();
        _groups.Changed += SyncGroupGlows;
        _stackMode = new StackModeManager(() => (_settings.StackHotkeyModifiers, _settings.StackHotkeyKey));
        _stackMode.SelectionChanged += RefreshSelectionGlows;
        _stackMode.Failed += ShowStackFailureBalloon;
        _stackMode.Stacked += OnStacked;

        // 堆叠翻页:修饰键(可自定义)+ 滚轮轮换窗口组 Z 序(暂停/多选模式期间挂起)
        _wheelFlip = new WheelFlipManager(
            _groups.Rotate,
            () => _settings.Paused || _stackMode.Armed,
            () => _settings.WheelFlipModifiers);

        // 层级保持:监听前台窗口切换,被管理窗口被点击激活时自动拉回设定层级
        _foregroundDelegate = OnForegroundChanged;
        _foregroundHook = GlowInterop.SetWinEventHook(
            GlowInterop.EVENT_SYSTEM_FOREGROUND, GlowInterop.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _foregroundDelegate, 0, 0, GlowInterop.WINEVENT_OUTOFCONTEXT);

        _hotkeys = new HotkeyManager(_messageWindow.Handle);

        _pauseItem = new ToolStripMenuItem("暂停(&P)") { Checked = _settings.Paused };
        _pauseItem.Click += (_, _) => TogglePaused(_pauseItem.Checked);
        _autoStartItem = new ToolStripMenuItem("开机自启(&A)") { Checked = _settings.AutoStart };
        _autoStartItem.Click += (_, _) => ToggleAutoStart(_autoStartItem.Checked);

        _trayIcon = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = BuildTooltip(),
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        // 单击打开设置(右键由 ContextMenuStrip 自动弹出菜单)
        _trayIcon.Click += (_, _) => OpenSettings();
        _trayIcon.DoubleClick += (_, _) => OpenSettings();
        _trayIcon.BalloonTipClicked += (_, _) => OpenSettings();

        ApplyHotkey();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _splash?.Dispose();
            GlowInterop.UnhookWinEvent(_foregroundHook);

            // 退出注销:先结束堆叠多选(卸载钩子、清理选中光晕)、卸载翻页钩子、解散窗口组,再销毁全部光晕覆盖窗口并恢复所有被调整窗口的原始层级状态
            _stackMode.Dispose();
            _wheelFlip.Dispose();
            _groups.Dispose();
            _glow.Dispose();
            _registry.RestoreAll();

            _messageWindow.DestroyHandle();
            _hotkeys.Dispose();
            _trayIcon.Visible = false;
            var icon = _trayIcon.Icon; // NotifyIcon 不负责释放 Icon,需手动释放
            _trayIcon.Dispose();
            icon?.Dispose();
            _settingsForm?.Dispose();
        }
        base.Dispose(disposing);
    }

    // ---- 热键 ----

    /// <summary>组装全部热键:置顶切换 + 窗口堆叠 + 解绑 + 各层级(全部可自定义)。</summary>
    private IEnumerable<HotkeyEntry> BuildHotkeyEntries()
    {
        yield return new HotkeyEntry(
            HotkeyManager.ToggleHotkeyId,
            _settings.HotkeyModifiers, _settings.HotkeyKey,
            HotkeyText.Format(_settings.HotkeyModifiers, _settings.HotkeyKey));

        yield return new HotkeyEntry(
            HotkeyManager.StackHotkeyId,
            _settings.StackHotkeyModifiers, _settings.StackHotkeyKey,
            HotkeyText.Format(_settings.StackHotkeyModifiers, _settings.StackHotkeyKey));

        yield return new HotkeyEntry(
            HotkeyManager.DismissHotkeyId,
            _settings.DismissHotkeyModifiers, _settings.DismissHotkeyKey,
            HotkeyText.Format(_settings.DismissHotkeyModifiers, _settings.DismissHotkeyKey));

        for (int layer = 1; layer <= _settings.LayerCount; layer++)
        {
            int idx = layer - 1;
            uint mods = _settings.LayerHotkeyModifiers![idx];
            uint key = _settings.LayerHotkeyKeys![idx];
            yield return new HotkeyEntry(
                HotkeyManager.LayerHotkeyBase + layer - 1,
                mods, key, HotkeyText.Format(mods, key));
        }
    }

    /// <summary>按当前配置(重新)注册热键;暂停时全部注销并隐藏光晕。注册失败弹气泡提示,不静默。</summary>
    private void ApplyHotkey()
    {
        // 热键集合变化(暂停/设置变更)时结束可能进行中的多选模式
        _stackMode.Cancel();

        if (_settings.Paused)
        {
            _hotkeys.UnregisterAll();
            _glow.HideAll();
        }
        else
        {
            _hotkeys.RegisterAll(BuildHotkeyEntries());
            _glow.ShowAll();
            if (_hotkeys.Failed.Count > 0)
            {
                string labels = string.Join("、", _hotkeys.Failed.Select(f => f.Label));
                _trayIcon.ShowBalloonTip(
                    5000, "Hauteur",
                    $"以下热键注册失败,可能被其他程序占用:{labels}。请打开设置更换组合键。",
                    ToolTipIcon.Warning);
            }
        }
        _trayIcon.Text = BuildTooltip();
    }

    private void OnHotkeyPressed(int id)
    {
        if (_settings.Paused) return;

        if (id == HotkeyManager.StackHotkeyId)
        {
            // 堆叠多选:WM_HOTKEY 表示组合键已按下,进入多选模式;松开由 StackModeManager 的键盘钩子检测
            _stackMode.Arm();
            return;
        }

        IntPtr hwnd = GetTargetWindow();
        if (hwnd == IntPtr.Zero) return;

        if (id == HotkeyManager.DismissHotkeyId)
        {
            // 取消置顶 + 移出窗口组
            DismissWindow(hwnd);
            return;
        }

        if (id == HotkeyManager.ToggleHotkeyId)
        {
            // 置顶 ↔ 置底(垫底)切换
            ToggleTopmostBottomWithFeedback(hwnd);
            return;
        }

        int layer = id - HotkeyManager.LayerHotkeyBase + 1;
        if (layer < 1 || layer > _settings.LayerCount) return;
        SetLayerWithFeedback(hwnd, layer);
    }

    /// <summary>取前台窗口(根窗口),过滤桌面/任务栏/自身窗口。</summary>
    private static IntPtr GetTargetWindow()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;

        hwnd = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        if (WindowOps.IsOwnProcess(hwnd) || WindowOps.IsShellWindow(hwnd)) return IntPtr.Zero;
        return hwnd;
    }

    /// <summary>设层并验证结果;目标在窗口组中时整组视为一个层级单位(保持组内页面顺序)。</summary>
    /// <remarks>
    /// 层级 1(置顶)是绝对位置,精确校验;2~N 是近似值,±1 容差,
    /// 且桌面没有其他普通窗口作参照时不校验。
    /// </remarks>
    private void SetLayerWithFeedback(IntPtr hwnd, int layer)
    {
        var kind = layer <= 1 ? WindowLevelRegistry.AppliedLayerKind.Topmost : WindowLevelRegistry.AppliedLayerKind.Layer;

        // 组内窗口:整组一起设层,按自底向上顺序处理保持组内页面顺序
        var members = _groups.MembersBottomFirst(hwnd);
        if (members is not null)
        {
            foreach (var m in members)
            {
                _registry.Track(m, kind, layer); // 先快照原始状态(退出恢复用),并记录当前层级(层级保持用)
                WindowOps.SetLayer(m, layer, _settings.LayerCount);
            }

            bool ok = members.All(m => layer switch
            {
                1 => WindowOps.IsTopmost(m),
                _ => !WindowOps.HasNormalBandReference(m)
                     || Math.Abs(WindowOps.GetLayer(m, _settings.LayerCount) - layer) <= 1,
            });

            if (ok)
            {
                foreach (var m in members)
                    _glow.Attach(m, GlowColorForState(kind, layer), GlowOpacity);
            }
            else
            {
                foreach (var m in members) _registry.Remove(m); // 没改成:不记录、不发光
                ShowFailureBalloon(hwnd);
            }
            return;
        }

        _registry.Track(hwnd, kind, layer);
        WindowOps.SetLayer(hwnd, layer, _settings.LayerCount);

        bool singleOk = layer switch
        {
            1 => WindowOps.IsTopmost(hwnd),
            _ => !WindowOps.HasNormalBandReference(hwnd)
                 || Math.Abs(WindowOps.GetLayer(hwnd, _settings.LayerCount) - layer) <= 1,
        };

        if (singleOk)
            _glow.Attach(hwnd, GlowColorForState(kind, layer), GlowOpacity);
        else
        {
            _registry.Remove(hwnd); // 没改成:不记录、不发光
            ShowFailureBalloon(hwnd);
        }
    }

    /// <summary>置顶/置底切换并验证:置顶后应 topmost,置底后应位于普通带最底;
    /// 目标在窗口组中时整组一起切换(置顶自底向上、垫底自顶向下,保持组内页面顺序)。</summary>
    private void ToggleTopmostBottomWithFeedback(IntPtr hwnd)
    {
        bool wasTopmost = WindowOps.IsTopmost(hwnd);
        var kind = wasTopmost ? WindowLevelRegistry.AppliedLayerKind.Bottom : WindowLevelRegistry.AppliedLayerKind.Topmost;
        int layer = wasTopmost ? _settings.LayerCount : 1;

        var members = _groups.MembersBottomFirst(hwnd); // 自底向上
        var targets = members ?? new List<IntPtr> { hwnd };
        if (members is not null && wasTopmost)
        {
            // 整组垫底:自顶向下逐个垫底,保持组内页面顺序
            for (int i = members.Count - 1; i >= 0; i--)
            {
                _registry.Track(members[i], kind, layer);
                WindowOps.SetBottom(members[i]);
            }
        }
        else
        {
            foreach (var m in targets)
            {
                _registry.Track(m, kind, layer);
                if (members is not null) WindowOps.SetTopmost(m);
                else WindowOps.ToggleTopmostBottom(m);
            }
        }

        bool ok = targets.All(m => wasTopmost ? WindowOps.IsAtBottom(m) : WindowOps.IsTopmost(m));
        if (ok)
        {
            foreach (var m in targets)
                _glow.Attach(m, GlowColorForState(kind, layer), GlowOpacity);
        }
        else
        {
            foreach (var m in targets) _registry.Remove(m);
            ShowFailureBalloon(hwnd);
        }
    }

    /// <summary>取指定层级状态对应的光晕颜色:置顶 = 层级 1 色,垫底 = 层级 N 色。</summary>
    private Color GlowColorForState(WindowLevelRegistry.AppliedLayerKind kind, int layer)
    {
        var colors = GlowLayerColors;
        int idx = kind switch
        {
            WindowLevelRegistry.AppliedLayerKind.Topmost => 0,
            WindowLevelRegistry.AppliedLayerKind.Bottom => colors.Length - 1,
            _ => Math.Clamp(layer - 1, 0, colors.Length - 1),
        };
        return Color.FromArgb(unchecked((int)colors[idx]));
    }

    // ---- 层级保持 ----

    /// <summary>前台窗口切换回调:被管理窗口被点击激活时,自动拉回设定层级。</summary>
    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero) return;
        EnforceLayer(hwnd);
    }

    /// <summary>
    /// 层级保持:开关开启且未暂停时,把被管理窗口重新放到其设定层级。
    /// SWP_NOACTIVATE 不抢焦点,不移动不缩放,不影响用户继续操作。
    /// </summary>
    private void EnforceLayer(IntPtr hwnd)
    {
        if (!_settings.KeepLayers || _settings.Paused) return;
        if (!NativeMethods.IsWindow(hwnd) || WindowOps.IsOwnProcess(hwnd)) return;

        var state = _registry.Find(hwnd);
        if (state is null) return;

        switch (state.Kind)
        {
            case WindowLevelRegistry.AppliedLayerKind.Topmost:
                WindowOps.SetLayer(hwnd, 1, _settings.LayerCount);
                break;
            case WindowLevelRegistry.AppliedLayerKind.Bottom:
                WindowOps.SetBottom(hwnd);
                break;
            default:
                WindowOps.SetLayer(hwnd, state.Layer, _settings.LayerCount);
                break;
        }
    }

    private void ShowFailureBalloon(IntPtr hwnd)
    {
        string reason = WindowOps.IsTargetProcessElevated(hwnd)
            ? "目标窗口以管理员权限运行,当前权限的 Hauteur 无法调整它的层级。请以管理员身份重新启动 Hauteur 后重试。"
            : "层级调整未生效,目标窗口可能拒绝该操作。";
        _trayIcon.ShowBalloonTip(5000, "Hauteur", reason, ToolTipIcon.Warning);
    }

    /// <summary>撤销层级状态并解散窗口组:目标窗口所在组的全部成员恢复原始 Z 序
    /// (层级光晕一并移除),并解散该窗口组;窗口都留在原位置。</summary>
    private void DismissWindow(IntPtr hwnd)
    {
        var targets = _groups.MembersOf(hwnd) ?? new[] { hwnd };
        foreach (var member in targets)
        {
            if (!NativeMethods.IsWindow(member)) continue;
            if (_registry.Restore(member))
            {
                // 被 Hauteur 管理(任意层级):恢复原始层级状态并移除光晕
                _glow.Detach(member);
                continue;
            }
            // 未被管理但置顶:移出置顶带
            if (WindowOps.IsTopmost(member))
            {
                NativeMethods.SetWindowPos(
                    member, NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
            }
        }

        // 解散所在窗口组(不在组中则无操作)
        _groups.Dissolve(hwnd);
    }

    // ---- 堆叠多选 ----

    /// <summary>选中光晕刷新:全部选中窗口显示窗口组颜色(与组光晕一致);
    /// 退出选中的窗口移除选中光晕,仍被层级管理的恢复其层级光晕。</summary>
    private void RefreshSelectionGlows()
    {
        var selected = _stackMode.Selection;
        var current = new HashSet<IntPtr>(selected);

        // 退出选中的窗口:移除选中光晕(选中光晕占层级光晕槽位,组光晕为独立槽位不受影响)
        foreach (var hwnd in _selectionGlowTargets)
        {
            if (current.Contains(hwnd)) continue;
            _glow.Detach(hwnd);
            var state = _registry.Find(hwnd);
            if (state is not null && NativeMethods.IsWindow(hwnd))
                _glow.Attach(hwnd, GlowColorForState(state.Kind, state.Layer), GlowOpacity);
        }

        for (int i = 0; i < selected.Count; i++)
            _glow.Attach(selected[i], GroupGlowColor, GlowOpacity);

        _selectionGlowTargets = selected.ToList();
    }

    /// <summary>组光晕同步:组员显示外圈组光晕(独立槽位,与层级光晕并存),退出组的窗口移除组光晕。</summary>
    private void SyncGroupGlows()
    {
        var members = new HashSet<IntPtr>(_groups.Members);

        foreach (var hwnd in _groupGlowTargets)
        {
            if (members.Contains(hwnd)) continue;
            _glow.DetachGroup(hwnd);
        }

        foreach (var hwnd in members)
            _glow.AttachGroup(hwnd, GroupGlowColor, GlowOpacity);

        _groupGlowTargets = members.ToList();
    }

    /// <summary>窗口组光晕颜色(设置可自定义)。</summary>
    private Color GroupGlowColor => Color.FromArgb(unchecked((int)_settings.GroupGlowColor));

    /// <summary>选中窗口被销毁(光晕跟随清理):同步移除层级记录与堆叠选中。</summary>
    private void OnGlowTargetDestroyed(IntPtr hwnd)
    {
        _registry.Remove(hwnd);
        _stackMode.OnTargetDestroyed(hwnd);
    }

    private void ShowStackFailureBalloon(string reason)
    {
        _trayIcon.ShowBalloonTip(5000, "Hauteur", reason, ToolTipIcon.Warning);
    }

    /// <summary>堆叠成功:曾被置顶管理的成员(已移出置顶带)注销其层级状态与光晕,再绑定为窗口组。</summary>
    private void OnStacked(IReadOnlyList<IntPtr> windows)
    {
        foreach (var hwnd in windows)
        {
            if (_registry.Find(hwnd) is { Kind: WindowLevelRegistry.AppliedLayerKind.Topmost })
            {
                _registry.Remove(hwnd);
                _glow.Detach(hwnd);
            }
        }
        _groups.CreateGroup(windows);
    }

    // ---- 托盘菜单 ----

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("打开设置(&O)");
        openItem.Click += (_, _) => OpenSettings();

        var restoreItem = new ToolStripMenuItem("恢复所有窗口层级(&R)");
        restoreItem.Click += (_, _) => RestoreAllWindows();

        var dissolveGroupsItem = new ToolStripMenuItem("解散窗口组(&D)");
        dissolveGroupsItem.Click += (_, _) => DissolveGroups();

        var exitItem = new ToolStripMenuItem("退出(&X)");
        exitItem.Click += (_, _) => ExitThread();

        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(restoreItem);
        menu.Items.Add(dissolveGroupsItem);
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);
        return menu;
    }

    /// <summary>一键恢复:销毁全部光晕,把所有被调整的窗口还原到最初层级状态。</summary>
    private void RestoreAllWindows()
    {
        _glow.ClearAll();
        _registry.RestoreAll();
        _trayIcon.ShowBalloonTip(3000, "Hauteur", "已恢复所有窗口的原始层级。", ToolTipIcon.Info);
    }

    /// <summary>解散全部窗口组:窗口留在当前位置,之后可自由拖动。</summary>
    private void DissolveGroups()
    {
        int count = _groups.DissolveAll();
        _trayIcon.ShowBalloonTip(
            3000, "Hauteur",
            count > 0 ? $"已解散 {count} 个窗口组,窗口可自由拖动。" : "当前没有窗口组。",
            ToolTipIcon.Info);
    }

    private void TogglePaused(bool paused)
    {
        _settings.Paused = paused;
        ConfigStore.Save(_settings);
        ApplyHotkey();
    }

    private void ToggleAutoStart(bool enabled)
    {
        _settings.AutoStart = enabled;
        StartupManager.SetEnabled(enabled);
        ConfigStore.Save(_settings);
    }

    private string BuildTooltip()
    {
        string combo = HotkeyText.Format(_settings.HotkeyModifiers, _settings.HotkeyKey);
        string stack = HotkeyText.Format(_settings.StackHotkeyModifiers, _settings.StackHotkeyKey);
        string dismiss = HotkeyText.Format(_settings.DismissHotkeyModifiers, _settings.DismissHotkeyKey);
        return _settings.Paused
            ? "Hauteur — 已暂停"
            : $"Hauteur — 置顶/置底 {combo} · 设层 {_settings.LayerCount} 层 · 堆叠 {stack}+左键点选 · 翻页 Ctrl+Alt+滚轮 · 解绑 {dismiss}";
    }

    // ---- 设置窗口 ----

    public void OpenSettings()
    {
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(_settings, ValidateHotkeys);
            _settingsForm.Saved += OnSettingsSaved;
            // 表单里校验热键会临时改注册;取消/关闭时恢复原热键
            _settingsForm.Cancelled += ApplyHotkey;
        }

        if (!_settingsForm.Visible) _settingsForm.Show();
        _settingsForm.Activate();
        if (_settingsForm.WindowState == FormWindowState.Minimized)
            _settingsForm.WindowState = FormWindowState.Normal;
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        _settings = settings;
        _settings.LayerCount = Math.Clamp(_settings.LayerCount, 3, 9);
        _settings.EnsureLayerColors(_settings.LayerCount);
        _settings.EnsureLayerHotkeys(_settings.LayerCount);
        ConfigStore.Save(_settings);
        StartupManager.SetEnabled(_settings.AutoStart);
        _pauseItem.Checked = _settings.Paused;
        _autoStartItem.Checked = _settings.AutoStart;
        ApplyHotkey();
        // 光晕换色即时生效:按各窗口当前层级重新着色;组光晕同步新颜色
        foreach (var state in _registry.Snapshot())
        {
            if (NativeMethods.IsWindow(state.Hwnd))
                _glow.Attach(state.Hwnd, GlowColorForState(state.Kind, state.Layer), GlowOpacity);
        }
        SyncGroupGlows();
    }

    /// <summary>设置保存前校验全部自定义热键:临时注销后尝试注册候选组合(支持互相交换),失败恢复原组合并返回 false。</summary>
    private bool ValidateHotkeys(AppSettings candidate)
    {
        var current = _settings;

        UnregisterCustomHotkeys();
        bool ok = RegisterCustomHotkeys(candidate);
        if (!ok)
        {
            // 恢复原组合(若此时仍失败,说明原组合也被抢了,交由 ApplyHotkey 的失败提示兜底)
            UnregisterCustomHotkeys();
            RegisterCustomHotkeys(current);
        }
        return ok;
    }

    private void UnregisterCustomHotkeys()
    {
        _hotkeys.Unregister(HotkeyManager.ToggleHotkeyId);
        _hotkeys.Unregister(HotkeyManager.StackHotkeyId);
        _hotkeys.Unregister(HotkeyManager.DismissHotkeyId);
        for (int layer = 1; layer <= _settings.LayerCount; layer++)
            _hotkeys.Unregister(HotkeyManager.LayerHotkeyBase + layer - 1);
    }

    private bool RegisterCustomHotkeys(AppSettings s)
    {
        if (!_hotkeys.Register(HotkeyManager.ToggleHotkeyId, s.HotkeyModifiers, s.HotkeyKey)) return false;
        if (!_hotkeys.Register(HotkeyManager.StackHotkeyId, s.StackHotkeyModifiers, s.StackHotkeyKey)) return false;
        if (!_hotkeys.Register(HotkeyManager.DismissHotkeyId, s.DismissHotkeyModifiers, s.DismissHotkeyKey)) return false;
        for (int layer = 1; layer <= s.LayerCount; layer++)
        {
            int idx = layer - 1;
            if (!_hotkeys.Register(HotkeyManager.LayerHotkeyBase + layer - 1,
                    s.LayerHotkeyModifiers![idx], s.LayerHotkeyKeys![idx])) return false;
        }
        return true;
    }
}
