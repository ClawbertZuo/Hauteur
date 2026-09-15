using Hauteur.App;
using Hauteur.Core;
using Hauteur.Interop;

namespace Hauteur.UI;

/// <summary>设置窗口:标签页(快捷键 / 光晕 / 常规)。修改在点"保存"后才生效。</summary>
internal sealed class SettingsForm : Form
{
    private static readonly Font SmallFont =
        new(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 8.5f);

    private readonly AppSettings _working; // 快照,保存时才写回
    private readonly Func<AppSettings, bool> _validateHotkeys; // 三条自定义热键一起校验(支持互相交换)
    private readonly HotkeyRecorderBox _hotkeyBox = new();
    private readonly HotkeyRecorderBox _stackHotkeyBox = new();
    private readonly HotkeyRecorderBox _dismissHotkeyBox = new();
    private readonly List<HotkeyRecorderBox> _layerHotkeyBoxes = new();
    private readonly CheckBox _wheelCtrl = new() { Text = "Ctrl", AutoSize = true };
    private readonly CheckBox _wheelAlt = new() { Text = "Alt", AutoSize = true };
    private readonly CheckBox _wheelShift = new() { Text = "Shift", AutoSize = true };
    private readonly CheckBox _wheelWin = new() { Text = "Win", AutoSize = true };
    private readonly CheckBox _wheelXb1 = new() { Text = "鼠标侧键1", AutoSize = true };
    private readonly CheckBox _wheelXb2 = new() { Text = "鼠标侧键2", AutoSize = true };
    private readonly CheckBox _autoStartBox = new() { Text = "开机自动启动", AutoSize = true };
    private readonly CheckBox _pauseBox = new() { Text = "暂停捕获(热键与层级保持失效)", AutoSize = true };
    private readonly CheckBox _keepLayersBox = new() { Text = "保持层级(点击窗口不改变其层级)", AutoSize = true };
    private readonly List<Panel> _swatches = new();
    private readonly List<Button> _colorButtons = new();
    private readonly GradientPreviewPanel _preview = new();
    private readonly Panel _groupSwatch = new();

    private Color[] _layerColors = Array.Empty<Color>();
    private Color _groupGlowColor;
    private bool _saved;

    public event Action<AppSettings>? Saved;
    public event Action? Cancelled;

    public SettingsForm(AppSettings settings, Func<AppSettings, bool> validateHotkeys)
    {
        _working = settings.Clone();
        _validateHotkeys = validateHotkeys;

        // 各层级颜色:按配置取,数量不足时用默认渐变补齐
        int colorCount = Math.Clamp(_working.LayerCount, 3, 9);
        _working.EnsureLayerColors(colorCount);
        var defaults = AppSettings.DefaultLayerColors(colorCount);
        var configured = _working.GlowLayerColors!;
        _layerColors = new Color[colorCount];
        for (int i = 0; i < colorCount; i++)
            _layerColors[i] = Color.FromArgb(unchecked((int)(i < configured.Length ? configured[i] : defaults[i])));
        _groupGlowColor = Color.FromArgb(unchecked((int)_working.GroupGlowColor));

        Text = "Hauteur 设置";
        ClientSize = new Size(460, 630);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Create();

        _hotkeyBox.SetCombo(_working.HotkeyModifiers, _working.HotkeyKey);
        _stackHotkeyBox.SetCombo(_working.StackHotkeyModifiers, _working.StackHotkeyKey);
        _dismissHotkeyBox.SetCombo(_working.DismissHotkeyModifiers, _working.DismissHotkeyKey);
        _working.EnsureLayerHotkeys(_working.LayerCount);
        for (int i = 0; i < _working.LayerCount; i++)
        {
            var box = new HotkeyRecorderBox();
            box.SetCombo(_working.LayerHotkeyModifiers![i], _working.LayerHotkeyKeys![i]);
            _layerHotkeyBoxes.Add(box);
        }
        uint wheel = _working.WheelFlipModifiers;
        _wheelCtrl.Checked = (wheel & NativeMethods.MOD_CONTROL) != 0;
        _wheelAlt.Checked = (wheel & NativeMethods.MOD_ALT) != 0;
        _wheelShift.Checked = (wheel & NativeMethods.MOD_SHIFT) != 0;
        _wheelWin.Checked = (wheel & NativeMethods.MOD_WIN) != 0;
        _wheelXb1.Checked = (wheel & NativeMethods.MOD_XBUTTON1) != 0;
        _wheelXb2.Checked = (wheel & NativeMethods.MOD_XBUTTON2) != 0;
        _autoStartBox.Checked = _working.AutoStart;
        _pauseBox.Checked = _working.Paused;
        _keepLayersBox.Checked = _working.KeepLayers;
        _preview.SetColors(_layerColors);

        BuildLayout();
    }

    private void BuildLayout()
    {
        int n = _working.LayerCount;

        var tabs = new TabControl
        {
            Location = new Point(12, 12),
            Size = new Size(436, 570),
        };

        // ---- 快捷键页 ----
        var hotkeyPage = new TabPage("快捷键");
        var hotkeyGroup = new GroupBox
        {
            Text = "置顶 / 置底 切换热键",
            Location = new Point(8, 8),
            Size = new Size(420, 118),
        };
        var actionLabel = new Label
        {
            Text = "动作:未置顶 → 置顶;已置顶 → 垫底",
            Location = new Point(16, 26),
            AutoSize = true,
        };
        var comboLabel = new Label
        {
            Text = "组合键:",
            Location = new Point(16, 58),
            AutoSize = true,
        };
        _hotkeyBox.Location = new Point(78, 54);
        _hotkeyBox.Size = new Size(230, 27);
        var hint = new Label
        {
            Text = "点击输入框后按下组合键即可录制(需含修饰键,或使用 F1~F24、鼠标侧键);侧键按一次 = 修饰键、再按一次 = 主键。",
            Location = new Point(16, 90),
            Size = new Size(392, 26),
            ForeColor = SystemColors.GrayText,
            Font = SmallFont,
        };
        hotkeyGroup.Controls.AddRange(new Control[] { actionLabel, comboLabel, _hotkeyBox, hint });

        var stackGroup = new GroupBox
        {
            Text = "窗口堆叠 快捷键",
            Location = new Point(8, 132),
            Size = new Size(420, 118),
        };
        var stackAction = new Label
        {
            Text = "动作:按住热键时左键点选窗口,松开后堆叠到第一个点选窗口",
            Location = new Point(16, 26),
            AutoSize = true,
        };
        var stackComboLabel = new Label
        {
            Text = "组合键:",
            Location = new Point(16, 58),
            AutoSize = true,
        };
        _stackHotkeyBox.Location = new Point(78, 54);
        _stackHotkeyBox.Size = new Size(230, 27);
        var stackHint = new Label
        {
            Text = "点选时显示窗口组颜色的光晕;再次点击取消选中;不可缩放窗口只移动不缩放。",
            Location = new Point(16, 90),
            Size = new Size(392, 26),
            ForeColor = SystemColors.GrayText,
            Font = SmallFont,
        };
        stackGroup.Controls.AddRange(new Control[] { stackAction, stackComboLabel, _stackHotkeyBox, stackHint });

        var dismissGroup = new GroupBox
        {
            Text = "取消置顶 / 移出组 快捷键",
            Location = new Point(8, 256),
            Size = new Size(420, 118),
        };
        var dismissAction = new Label
        {
            Text = "动作:取消前台窗口的置顶,并将其移出所在窗口组(解绑)",
            Location = new Point(16, 26),
            AutoSize = true,
        };
        var dismissComboLabel = new Label
        {
            Text = "组合键:",
            Location = new Point(16, 58),
            AutoSize = true,
        };
        _dismissHotkeyBox.Location = new Point(78, 54);
        _dismissHotkeyBox.Size = new Size(230, 27);
        var dismissHint = new Label
        {
            Text = "窗口留在原位置,不再置顶、不再与同组窗口联动。注:Ctrl+Alt+Del 被系统保留,无法注册。",
            Location = new Point(16, 90),
            Size = new Size(392, 26),
            ForeColor = SystemColors.GrayText,
            Font = SmallFont,
        };
        dismissGroup.Controls.AddRange(new Control[] { dismissAction, dismissComboLabel, _dismissHotkeyBox, dismissHint });

        // 堆叠翻页修饰键:勾选后按住修饰键滚动滚轮即翻页
        var wheelGroup = new GroupBox
        {
            Text = "堆叠翻页修饰键(按住修饰键 + 滚轮)",
            Location = new Point(8, 380),
            Size = new Size(420, 74),
        };
        _wheelCtrl.Location = new Point(16, 24);
        _wheelAlt.Location = new Point(72, 24);
        _wheelShift.Location = new Point(122, 24);
        _wheelWin.Location = new Point(190, 24);
        _wheelXb1.Location = new Point(16, 48);
        _wheelXb2.Location = new Point(120, 48);
        wheelGroup.Controls.AddRange(new Control[] { _wheelCtrl, _wheelAlt, _wheelShift, _wheelWin, _wheelXb1, _wheelXb2 });

        // 层级快捷键:每层一个录制框(层级数 3~9,内容可滚动)
        var layerGroup = new GroupBox
        {
            Text = "层级快捷键(每层一个)",
            Location = new Point(8, 460),
            Size = new Size(420, 26 + n * 30 + 42),
        };
        for (int i = 0; i < n; i++)
        {
            var label = new Label
            {
                Text = $"层级 {i + 1}:",
                Location = new Point(16, 26 + i * 30),
                AutoSize = true,
            };
            _layerHotkeyBoxes[i].Location = new Point(76, 21 + i * 30);
            _layerHotkeyBoxes[i].Size = new Size(160, 27);
            layerGroup.Controls.AddRange(new Control[] { label, _layerHotkeyBoxes[i] });
        }
        var layerHint = new Label
        {
            Text = $"数字层 = 从顶部数第几层;层级 {n} 不是垫底,真正的最后一层(垫底)由置顶切换热键完成。",
            Location = new Point(16, 26 + n * 30 + 8),
            Size = new Size(392, 26),
            ForeColor = SystemColors.GrayText,
            Font = SmallFont,
        };
        layerGroup.Controls.Add(layerHint);

        var hotkeyScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        hotkeyScroll.Controls.AddRange(new Control[] { hotkeyGroup, stackGroup, dismissGroup, wheelGroup, layerGroup });
        hotkeyPage.Controls.Add(hotkeyScroll);

        // ---- 光晕页:每个层级独立选择颜色 ----
        var glowPage = new TabPage("光晕");
        var glowScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        int colorCount = _layerColors.Length;
        var glowGroup = new GroupBox
        {
            Text = "光晕颜色",
            Location = new Point(8, 8),
            Size = new Size(404, 26 + colorCount * 30 + 112),
        };
        for (int i = 0; i < colorCount; i++)
        {
            int idx = i; // 捕获循环变量
            var label = new Label
            {
                Text = $"层级 {i + 1}:",
                Location = new Point(16, 26 + i * 30),
                AutoSize = true,
            };
            var swatch = new Panel
            {
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(76, 21 + i * 30),
                Size = new Size(28, 28),
                BackColor = _layerColors[i],
            };
            var button = new Button
            {
                Text = "选择…",
                Location = new Point(114, 21 + i * 30),
                Size = new Size(66, 28),
            };
            button.Click += (_, _) => PickColor(idx);
            _swatches.Add(swatch);
            _colorButtons.Add(button);
            glowGroup.Controls.AddRange(new Control[] { label, swatch, button });
        }
        var groupGlowLabel = new Label
        {
            Text = "窗口组:",
            Location = new Point(16, 26 + colorCount * 30 + 8),
            AutoSize = true,
        };
        _groupSwatch.BorderStyle = BorderStyle.FixedSingle;
        _groupSwatch.Location = new Point(76, 21 + colorCount * 30 + 8);
        _groupSwatch.Size = new Size(28, 28);
        _groupSwatch.BackColor = _groupGlowColor;
        var groupGlowButton = new Button
        {
            Text = "选择…",
            Location = new Point(114, 21 + colorCount * 30 + 8),
            Size = new Size(66, 28),
        };
        groupGlowButton.Click += (_, _) => PickGroupGlowColor();

        _preview.Location = new Point(16, 26 + colorCount * 30 + 44);
        _preview.Size = new Size(372, 30);
        var glowHint = new Label
        {
            Text = "层级光晕为内圈、窗口组光晕为外圈,两者独立并存;保存后立即应用。",
            Location = new Point(16, 26 + colorCount * 30 + 82),
            Size = new Size(372, 26),
            ForeColor = SystemColors.GrayText,
            Font = SmallFont,
        };
        glowGroup.Controls.AddRange(new Control[] { _preview, glowHint, groupGlowLabel, _groupSwatch, groupGlowButton });
        glowScroll.Controls.Add(glowGroup);
        glowPage.Controls.Add(glowScroll);

        // ---- 常规页 ----
        var generalPage = new TabPage("常规");
        var generalGroup = new GroupBox
        {
            Text = "常规",
            Location = new Point(8, 8),
            Size = new Size(420, 168),
        };
        _autoStartBox.Location = new Point(16, 30);
        _pauseBox.Location = new Point(16, 56);
        _keepLayersBox.Location = new Point(16, 82);
        var generalHint = new Label
        {
            Text = "保持层级:被设过层级的窗口被点击激活时自动回到设定层级;托盘右键菜单可随时恢复所有窗口层级。",
            Location = new Point(16, 112),
            Size = new Size(392, 46),
            ForeColor = SystemColors.GrayText,
            Font = SmallFont,
        };
        generalGroup.Controls.AddRange(new Control[] { _autoStartBox, _pauseBox, _keepLayersBox, generalHint });
        generalPage.Controls.Add(generalGroup);

        tabs.TabPages.Add(hotkeyPage);
        tabs.TabPages.Add(glowPage);
        tabs.TabPages.Add(generalPage);
        tabs.SelectedIndex = 0;

        var cancelButton = new Button
        {
            Text = "取消",
            Size = new Size(88, 30),
            Location = new Point(254, 592),
        };
        cancelButton.Click += (_, _) => Close();

        var saveButton = new Button
        {
            Text = "保存",
            Size = new Size(88, 30),
            Location = new Point(348, 592),
        };
        saveButton.Click += (_, _) => Save();

        var versionLabel = new Label
        {
            Text = $"Hauteur v{Application.ProductVersion}",
            Location = new Point(12, 612),
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
        };

        AcceptButton = saveButton;
        CancelButton = cancelButton;

        Controls.AddRange(new Control[] { tabs, cancelButton, saveButton, versionLabel });
    }

    private void PickColor(int layerIndex)
    {
        using var dlg = new ColorDialog { Color = _layerColors[layerIndex], FullOpen = true, AnyColor = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _layerColors[layerIndex] = dlg.Color;
        _swatches[layerIndex].BackColor = dlg.Color;
        _preview.SetColors(_layerColors);
    }

    private void PickGroupGlowColor()
    {
        using var dlg = new ColorDialog { Color = _groupGlowColor, FullOpen = true, AnyColor = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _groupGlowColor = dlg.Color;
        _groupSwatch.BackColor = dlg.Color;
    }

    private void Save()
    {
        if (!_hotkeyBox.IsValid || !_stackHotkeyBox.IsValid || !_dismissHotkeyBox.IsValid
            || _layerHotkeyBoxes.Any(b => !b.IsValid))
        {
            MessageBox.Show(this,
                "请录制有效的组合键:至少包含 Ctrl / Alt / Shift / Win 之一,或使用 F1~F24、鼠标侧键。",
                "热键无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 全部热键(置顶/堆叠/解绑 + 各层级)两两不能相同
        var all = new List<(HotkeyRecorderBox Box, string Name)>
        {
            (_hotkeyBox, "置顶/置底"),
            (_stackHotkeyBox, "窗口堆叠"),
            (_dismissHotkeyBox, "取消置顶/移出组"),
        };
        for (int i = 0; i < _layerHotkeyBoxes.Count; i++)
            all.Add((_layerHotkeyBoxes[i], $"层级 {i + 1}"));
        for (int i = 0; i < all.Count; i++)
        {
            for (int j = i + 1; j < all.Count; j++)
            {
                if (SameCombo(all[i].Box, all[j].Box))
                {
                    MessageBox.Show(this,
                        $"「{all[i].Name}」与「{all[j].Name}」的快捷键相同,请更换其中一个。",
                        "热键冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
        }

        uint wheelMods =
            (_wheelCtrl.Checked ? NativeMethods.MOD_CONTROL : 0)
            | (_wheelAlt.Checked ? NativeMethods.MOD_ALT : 0)
            | (_wheelShift.Checked ? NativeMethods.MOD_SHIFT : 0)
            | (_wheelWin.Checked ? NativeMethods.MOD_WIN : 0)
            | (_wheelXb1.Checked ? NativeMethods.MOD_XBUTTON1 : 0)
            | (_wheelXb2.Checked ? NativeMethods.MOD_XBUTTON2 : 0);
        if (wheelMods == 0)
        {
            MessageBox.Show(this,
                "请至少勾选一种翻页修饰键,否则每次滚动鼠标滚轮都会触发翻页。",
                "翻页修饰键", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        bool hotkeysChanged =
            _hotkeyBox.Modifiers != _working.HotkeyModifiers ||
            _hotkeyBox.Key != _working.HotkeyKey ||
            _stackHotkeyBox.Modifiers != _working.StackHotkeyModifiers ||
            _stackHotkeyBox.Key != _working.StackHotkeyKey ||
            _dismissHotkeyBox.Modifiers != _working.DismissHotkeyModifiers ||
            _dismissHotkeyBox.Key != _working.DismissHotkeyKey ||
            Enumerable.Range(0, _layerHotkeyBoxes.Count).Any(i =>
                _layerHotkeyBoxes[i].Modifiers != _working.LayerHotkeyModifiers![i]
                || _layerHotkeyBoxes[i].Key != _working.LayerHotkeyKeys![i])
            || wheelMods != _working.WheelFlipModifiers;

        // 暂停状态下不注册热键,也无需校验;未暂停且热键变化时先试注册(注册失败=冲突)
        if (hotkeysChanged && !_pauseBox.Checked && !_validateHotkeys(BuildCandidate()))
        {
            MessageBox.Show(this,
                "组合键注册失败,可能已被其他程序占用,请更换组合键。",
                "热键冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _working.HotkeyModifiers = _hotkeyBox.Modifiers;
        _working.HotkeyKey = _hotkeyBox.Key;
        _working.StackHotkeyModifiers = _stackHotkeyBox.Modifiers;
        _working.StackHotkeyKey = _stackHotkeyBox.Key;
        _working.DismissHotkeyModifiers = _dismissHotkeyBox.Modifiers;
        _working.DismissHotkeyKey = _dismissHotkeyBox.Key;
        _working.LayerHotkeyModifiers = _layerHotkeyBoxes.Select(b => b.Modifiers).ToArray();
        _working.LayerHotkeyKeys = _layerHotkeyBoxes.Select(b => b.Key).ToArray();
        _working.WheelFlipModifiers = wheelMods;
        _working.AutoStart = _autoStartBox.Checked;
        _working.Paused = _pauseBox.Checked;
        _working.KeepLayers = _keepLayersBox.Checked;
        _working.GlowLayerColors = _layerColors.Select(c => (uint)c.ToArgb()).ToArray();
        _working.GroupGlowColor = (uint)_groupGlowColor.ToArgb();

        _saved = true;
        Saved?.Invoke(_working);
        Close();
    }

    /// <summary>含新热键组合的候选配置快照(校验用)。</summary>
    private AppSettings BuildCandidate()
    {
        var candidate = _working.Clone();
        candidate.HotkeyModifiers = _hotkeyBox.Modifiers;
        candidate.HotkeyKey = _hotkeyBox.Key;
        candidate.StackHotkeyModifiers = _stackHotkeyBox.Modifiers;
        candidate.StackHotkeyKey = _stackHotkeyBox.Key;
        candidate.DismissHotkeyModifiers = _dismissHotkeyBox.Modifiers;
        candidate.DismissHotkeyKey = _dismissHotkeyBox.Key;
        candidate.LayerHotkeyModifiers = _layerHotkeyBoxes.Select(b => b.Modifiers).ToArray();
        candidate.LayerHotkeyKeys = _layerHotkeyBoxes.Select(b => b.Key).ToArray();
        return candidate;
    }

    private static bool SameCombo(HotkeyRecorderBox a, HotkeyRecorderBox b) =>
        a.Modifiers == b.Modifiers && a.Key == b.Key;

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        // 校验热键时可能已临时改注册,取消/关闭需通知托盘恢复原热键
        if (!_saved) Cancelled?.Invoke();
    }

    /// <summary>渐变预览条:水平绘制各层级颜色序列。</summary>
    private sealed class GradientPreviewPanel : Panel
    {
        private Color[] _colors = { Color.Black, Color.White };

        public GradientPreviewPanel()
        {
            DoubleBuffered = true;
            BorderStyle = BorderStyle.FixedSingle;
        }

        public void SetColors(Color[] colors)
        {
            _colors = colors.Length > 0 ? colors : new[] { Color.Black };
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using var brush = new System.Drawing.Drawing2D.LinearGradientBrush(
                ClientRectangle, Color.Black, Color.White, System.Drawing.Drawing2D.LinearGradientMode.Horizontal);
            brush.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend
            {
                Colors = _colors,
                Positions = _colors.Length == 1
                    ? new[] { 0f }
                    : Enumerable.Range(0, _colors.Length).Select(i => i / (float)(_colors.Length - 1)).ToArray(),
            };
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }
}
