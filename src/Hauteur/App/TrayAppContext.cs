using Hauteur.Core;
using Hauteur.Interop;
using Hauteur.UI;

namespace Hauteur.App;

/// <summary>
/// 托盘常驻上下文:程序启动后不弹主窗口,仅驻留系统托盘。
/// 单击托盘图标打开设置;全局热键:
///   Ctrl+Alt+1~5  将前台窗口设到"从顶部数第 N 层"(1 置顶/最前,2~N 普通带内近似位置;N 不是垫底)
///   Ctrl+Alt+T(可自定义)置顶 ↔ 置底(垫底,真正的最后一层)切换
/// </summary>
internal sealed class TrayAppContext : ApplicationContext
{
    /// <summary>层级热键固定为 Ctrl+Alt+数字(后续版本可在设置中自定义)。</summary>
    private const uint LayerModifiers = NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT;

    /// <summary>光晕亮度:所有层级一致(鲜艳度一致),层级区分完全由颜色渐变承担。</summary>
    private const float GlowOpacity = 0.9f;

    private readonly MessageWindow _messageWindow;
    private readonly HotkeyManager _hotkeys;
    private readonly GlowManager _glow = new();
    private readonly WindowLevelRegistry _registry = new();
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
        StartupManager.MigrateLegacyRunKey(); // 清理旧项目名的开机自启注册表项
        _settings.AutoStart = StartupManager.IsEnabled(); // 以注册表实际状态为准

        _messageWindow = new MessageWindow();
        _messageWindow.HotkeyPressed += OnHotkeyPressed;
        _messageWindow.ShowSettingsRequested += OpenSettings;
        _messageWindow.ExitRequested += ExitThread; // 正常退出路径:Dispose 中注销层级状态
        _glow.TargetDestroyed += hwnd => _registry.Remove(hwnd);

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

            // 退出注销:销毁全部光晕覆盖窗口,并恢复所有被调整窗口的原始层级状态
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

    /// <summary>组装全部热键:置顶切换(可自定义)+ 各层级(Ctrl+Alt+数字)。</summary>
    private IEnumerable<HotkeyEntry> BuildHotkeyEntries()
    {
        yield return new HotkeyEntry(
            HotkeyManager.ToggleHotkeyId,
            _settings.HotkeyModifiers, _settings.HotkeyKey,
            HotkeyText.Format(_settings.HotkeyModifiers, _settings.HotkeyKey));

        for (int layer = 1; layer <= _settings.LayerCount; layer++)
        {
            yield return new HotkeyEntry(
                HotkeyManager.LayerHotkeyBase + layer - 1,
                LayerModifiers, (uint)('1' + layer - 1),
                $"Ctrl + Alt + {layer}");
        }
    }

    /// <summary>按当前配置(重新)注册热键;暂停时全部注销并隐藏光晕。注册失败弹气泡提示,不静默。</summary>
    private void ApplyHotkey()
    {
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

        IntPtr hwnd = GetTargetWindow();
        if (hwnd == IntPtr.Zero) return;

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

    /// <summary>设层并验证结果;失败通常是目标窗口提权(UIPI)。</summary>
    /// <remarks>
    /// 层级 1(置顶)是绝对位置,精确校验;2~N 是近似值,±1 容差,
    /// 且桌面没有其他普通窗口作参照时不校验。
    /// </remarks>
    private void SetLayerWithFeedback(IntPtr hwnd, int layer)
    {
        // 先快照原始状态(退出恢复用),并记录当前层级(层级保持用)
        var kind = layer <= 1 ? WindowLevelRegistry.AppliedLayerKind.Topmost : WindowLevelRegistry.AppliedLayerKind.Layer;
        _registry.Track(hwnd, kind, layer);
        WindowOps.SetLayer(hwnd, layer, _settings.LayerCount);

        bool ok = layer switch
        {
            1 => WindowOps.IsTopmost(hwnd),
            _ => !WindowOps.HasNormalBandReference(hwnd)
                 || Math.Abs(WindowOps.GetLayer(hwnd, _settings.LayerCount) - layer) <= 1,
        };

        if (ok)
            _glow.Attach(hwnd, GlowColorForState(kind, layer), GlowOpacity);
        else
        {
            _registry.Remove(hwnd); // 没改成:不记录、不发光
            ShowFailureBalloon(hwnd);
        }
    }

    /// <summary>置顶/置底切换并验证:置顶后应 topmost,置底后应位于普通带最底。</summary>
    private void ToggleTopmostBottomWithFeedback(IntPtr hwnd)
    {
        bool wasTopmost = WindowOps.IsTopmost(hwnd);
        var kind = wasTopmost ? WindowLevelRegistry.AppliedLayerKind.Bottom : WindowLevelRegistry.AppliedLayerKind.Topmost;
        _registry.Track(hwnd, kind, wasTopmost ? _settings.LayerCount : 1);
        WindowOps.ToggleTopmostBottom(hwnd);

        bool ok = wasTopmost ? WindowOps.IsAtBottom(hwnd) : WindowOps.IsTopmost(hwnd);
        if (ok)
        {
            _glow.Attach(hwnd, GlowColorForState(kind, wasTopmost ? _settings.LayerCount : 1), GlowOpacity);
        }
        else
        {
            _registry.Remove(hwnd);
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

    // ---- 托盘菜单 ----

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("打开设置(&O)");
        openItem.Click += (_, _) => OpenSettings();

        var restoreItem = new ToolStripMenuItem("恢复所有窗口层级(&R)");
        restoreItem.Click += (_, _) => RestoreAllWindows();

        var exitItem = new ToolStripMenuItem("退出(&X)");
        exitItem.Click += (_, _) => ExitThread();

        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(restoreItem);
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
        return _settings.Paused
            ? "Hauteur — 已暂停"
            : $"Hauteur — 置顶/置底 {combo} · 设层 Ctrl+Alt+1~{_settings.LayerCount}";
    }

    // ---- 设置窗口 ----

    public void OpenSettings()
    {
        if (_settingsForm is null || _settingsForm.IsDisposed)
        {
            _settingsForm = new SettingsForm(
                _settings,
                (mods, key) => _hotkeys.TryReplace(HotkeyManager.ToggleHotkeyId, mods, key));
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
        ConfigStore.Save(_settings);
        StartupManager.SetEnabled(_settings.AutoStart);
        _pauseItem.Checked = _settings.Paused;
        _autoStartItem.Checked = _settings.AutoStart;
        ApplyHotkey();
        // 光晕换色即时生效:按各窗口当前层级重新着色
        foreach (var state in _registry.Snapshot())
        {
            if (NativeMethods.IsWindow(state.Hwnd))
                _glow.Attach(state.Hwnd, GlowColorForState(state.Kind, state.Layer), GlowOpacity);
        }
    }
}
