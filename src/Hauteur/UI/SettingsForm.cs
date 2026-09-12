using Hauteur.App;
using Hauteur.Core;

namespace Hauteur.UI;

/// <summary>设置窗口:标签页(快捷键 / 光晕 / 常规)。修改在点"保存"后才生效。</summary>
internal sealed class SettingsForm : Form
{
    private static readonly Font SmallFont =
        new(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif, 8.5f);

    private readonly AppSettings _working; // 快照,保存时才写回
    private readonly Func<uint, uint, bool> _validateHotkey;
    private readonly HotkeyRecorderBox _hotkeyBox = new();
    private readonly CheckBox _autoStartBox = new() { Text = "开机自动启动", AutoSize = true };
    private readonly CheckBox _pauseBox = new() { Text = "暂停捕获(热键与层级保持失效)", AutoSize = true };
    private readonly CheckBox _keepLayersBox = new() { Text = "保持层级(点击窗口不改变其层级)", AutoSize = true };
    private readonly List<Panel> _swatches = new();
    private readonly List<Button> _colorButtons = new();
    private readonly GradientPreviewPanel _preview = new();

    private Color[] _layerColors = Array.Empty<Color>();
    private bool _saved;

    public event Action<AppSettings>? Saved;
    public event Action? Cancelled;

    public SettingsForm(AppSettings settings, Func<uint, uint, bool> validateHotkey)
    {
        _working = settings.Clone();
        _validateHotkey = validateHotkey;

        // 各层级颜色:按配置取,数量不足时用默认渐变补齐
        int colorCount = Math.Clamp(_working.LayerCount, 3, 9);
        _working.EnsureLayerColors(colorCount);
        var defaults = AppSettings.DefaultLayerColors(colorCount);
        var configured = _working.GlowLayerColors!;
        _layerColors = new Color[colorCount];
        for (int i = 0; i < colorCount; i++)
            _layerColors[i] = Color.FromArgb(unchecked((int)(i < configured.Length ? configured[i] : defaults[i])));

        Text = "Hauteur 设置";
        ClientSize = new Size(460, 396);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = AppIcon.Create();

        _hotkeyBox.SetCombo(_working.HotkeyModifiers, _working.HotkeyKey);
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
            Size = new Size(436, 330),
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
            Text = "点击输入框后按下新组合键即可录制(需含 Ctrl / Alt / Shift / Win 之一,或使用 F1~F24)。",
            Location = new Point(16, 90),
            Size = new Size(392, 26),
            ForeColor = SystemColors.GrayText,
            Font = SmallFont,
        };
        hotkeyGroup.Controls.AddRange(new Control[] { actionLabel, comboLabel, _hotkeyBox, hint });

        var layerGroup = new GroupBox
        {
            Text = "层级快捷键(暂固定,后续版本可自定义)",
            Location = new Point(8, 132),
            Size = new Size(420, 158),
        };
        var layerTop = new Label
        {
            Text = "Ctrl + Alt + 1   —   层级 1(最上):置顶",
            Location = new Point(16, 26),
            AutoSize = true,
        };
        var layerMiddle = new Label
        {
            Text = $"Ctrl + Alt + 2~{n}   —   层级 2~{n}:普通带内从顶部数的近似位置",
            Location = new Point(16, 50),
            AutoSize = true,
        };
        var layerHint = new Label
        {
            Text = $"数字几 = 从顶部数第几层;层级 {n} 是第 {n} 层、不是垫底,真正的最后一层(垫底)由切换热键完成。",
            Location = new Point(16, 74),
            Size = new Size(392, 72),
            ForeColor = SystemColors.GrayText,
            Font = SmallFont,
        };
        layerGroup.Controls.AddRange(new Control[] { layerTop, layerMiddle, layerHint });
        hotkeyPage.Controls.AddRange(new Control[] { hotkeyGroup, layerGroup });

        // ---- 光晕页:每个层级独立选择颜色 ----
        var glowPage = new TabPage("光晕");
        var glowScroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        int colorCount = _layerColors.Length;
        var glowGroup = new GroupBox
        {
            Text = "各层级光晕颜色",
            Location = new Point(8, 8),
            Size = new Size(404, 26 + colorCount * 30 + 74),
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
        _preview.Location = new Point(16, 26 + colorCount * 30 + 8);
        _preview.Size = new Size(372, 30);
        var glowHint = new Label
        {
            Text = "每个层级可单独指定光晕颜色(置底使用最后一个层级的颜色)。保存后立即应用到全部光晕。",
            Location = new Point(16, 26 + colorCount * 30 + 46),
            Size = new Size(372, 26),
            ForeColor = SystemColors.GrayText,
            Font = SmallFont,
        };
        glowGroup.Controls.AddRange(new Control[] { _preview, glowHint });
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
            Location = new Point(254, 352),
        };
        cancelButton.Click += (_, _) => Close();

        var saveButton = new Button
        {
            Text = "保存",
            Size = new Size(88, 30),
            Location = new Point(348, 352),
        };
        saveButton.Click += (_, _) => Save();

        var versionLabel = new Label
        {
            Text = $"Hauteur v{Application.ProductVersion}",
            Location = new Point(12, 372),
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

    private void Save()
    {
        if (!_hotkeyBox.IsValid)
        {
            MessageBox.Show(this,
                "请录制一个有效的组合键:至少包含 Ctrl / Alt / Shift / Win 之一,或使用 F1~F24。",
                "热键无效", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        bool hotkeyChanged =
            _hotkeyBox.Modifiers != _working.HotkeyModifiers ||
            _hotkeyBox.Key != _working.HotkeyKey;

        // 暂停状态下不注册热键,也无需校验;未暂停且热键变化时先试注册(注册失败=冲突)
        if (hotkeyChanged && !_pauseBox.Checked && !_validateHotkey(_hotkeyBox.Modifiers, _hotkeyBox.Key))
        {
            MessageBox.Show(this,
                "该组合键注册失败,可能已被其他程序占用,请换一个组合键。",
                "热键冲突", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _working.HotkeyModifiers = _hotkeyBox.Modifiers;
        _working.HotkeyKey = _hotkeyBox.Key;
        _working.AutoStart = _autoStartBox.Checked;
        _working.Paused = _pauseBox.Checked;
        _working.KeepLayers = _keepLayersBox.Checked;
        _working.GlowLayerColors = _layerColors.Select(c => (uint)c.ToArgb()).ToArray();

        _saved = true;
        Saved?.Invoke(_working);
        Close();
    }

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
