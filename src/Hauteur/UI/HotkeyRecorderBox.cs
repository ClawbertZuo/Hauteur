using Hauteur.Core;
using Hauteur.Interop;

namespace Hauteur.UI;

/// <summary>
/// 录制式热键输入框:获得焦点后,按下任意组合键(含鼠标侧键)即被记录并显示。
/// 鼠标侧键双态:按一次 = 作为修饰键(等待主键,可组成"侧键 + 按键"和弦);再按同一侧键 = 侧键本身作为主键。
/// 有效组合要求:至少一个修饰键(Ctrl/Alt/Shift/Win/侧键),或使用 F1~F24、鼠标侧键。
/// 普通按键不允许裸键,否则会抢占系统级按键输入。
/// </summary>
internal sealed class HotkeyRecorderBox : TextBox
{
    private const int VK_F1 = 0x70;
    private const int VK_F24 = 0x87;
    private const int WM_XBUTTONDOWN = 0x020B;
    private const uint SideButtonMask = NativeMethods.MOD_XBUTTON1 | NativeMethods.MOD_XBUTTON2;

    private uint _modifiers;
    private uint _key;
    private uint _confirmedModifiers; // 最近一次完整组合(主键非空),失焦/未完成时回退
    private uint _confirmedKey;
    private bool _capturing;

    public HotkeyRecorderBox()
    {
        ReadOnly = true;
        Cursor = Cursors.Hand;
    }

    public uint Modifiers => _modifiers;
    public uint Key => _key;

    /// <summary>当前录制的组合键是否有效。</summary>
    public bool IsValid => _key != 0
        && (_modifiers != 0
            || (_key >= VK_F1 && _key <= VK_F24)
            || _key is (uint)Keys.XButton1 or (uint)Keys.XButton2);

    public void SetCombo(uint modifiers, uint key)
    {
        _modifiers = modifiers;
        _key = key;
        _confirmedModifiers = modifiers;
        _confirmedKey = key;
        Text = HotkeyText.Format(modifiers, key);
    }

    protected override void OnEnter(EventArgs e)
    {
        base.OnEnter(e);
        _capturing = true;
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        _capturing = false;
        // 未完成主键的录制(如只按了修饰键)回退到最近一次完整组合
        if (_key == 0)
        {
            _modifiers = _confirmedModifiers;
            _key = _confirmedKey;
        }
        Text = HotkeyText.Format(_modifiers, _key);
    }

    /// <summary>鼠标侧键按下:按一次 = 作为修饰键(等待主键);再按同一侧键 = 侧键本身作为主键。</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_XBUTTONDOWN && _capturing)
        {
            int button = (int)(m.WParam.ToInt64() >> 16); // 高字:1 = XBUTTON1,2 = XBUTTON2
            uint side = button == 1 ? NativeMethods.MOD_XBUTTON1 : NativeMethods.MOD_XBUTTON2;
            if ((_modifiers & side) != 0)
            {
                // 再次按同一侧键:侧键本身为主键(键盘修饰键保留)
                Confirm(ModifiersOf(ModifierKeys) | WinKeyState(),
                    button == 1 ? (uint)Keys.XButton1 : (uint)Keys.XButton2);
            }
            else
            {
                // 侧键作为修饰键,等待主键
                _modifiers = ModifiersOf(ModifierKeys) | WinKeyState() | side;
                _key = 0;
                Text = HotkeyText.Format(_modifiers, 0);
            }
            return; // 不再交默认处理(只读框无需点击行为)
        }
        base.WndProc(ref m);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_capturing) return;
        e.SuppressKeyPress = true;

        var key = e.KeyCode;
        uint side = _modifiers & SideButtonMask; // 已按住的侧键修饰位

        // 只按下了修饰键:暂存并等待主键(保留侧键位)
        if (key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            uint mods = ModifiersOf(ModifierKeys) | WinKeyState();
            if (mods != 0 || side != 0)
            {
                _modifiers = mods | side;
                _key = 0;
                Text = HotkeyText.Format(mods | side, 0);
            }
            else
            {
                ClearCapture();
            }
            return;
        }

        // Esc 清空录制
        if (key == Keys.Escape)
        {
            ClearCapture();
            return;
        }

        // 主键:ModifierKeys 不含 Win 键,需用 GetKeyState 单独检测;侧键位保留
        Confirm(ModifiersOf(ModifierKeys) | WinKeyState() | side, (uint)key);
    }

    /// <summary>确认完整组合(含主键)并显示。</summary>
    private void Confirm(uint mods, uint key)
    {
        _modifiers = mods;
        _key = key;
        _confirmedModifiers = mods;
        _confirmedKey = key;
        Text = HotkeyText.Format(mods, key);
    }

    private void ClearCapture()
    {
        _modifiers = 0;
        _key = 0;
        Text = string.Empty;
    }

    private static uint ModifiersOf(Keys mods)
    {
        uint result = 0;
        if ((mods & Keys.Control) != 0) result |= NativeMethods.MOD_CONTROL;
        if ((mods & Keys.Alt) != 0) result |= NativeMethods.MOD_ALT;
        if ((mods & Keys.Shift) != 0) result |= NativeMethods.MOD_SHIFT;
        return result;
    }

    private static uint WinKeyState()
    {
        uint result = 0;
        if ((NativeMethods.GetKeyState(NativeMethods.VK_LWIN) & 0x8000) != 0) result |= NativeMethods.MOD_WIN;
        if ((NativeMethods.GetKeyState(NativeMethods.VK_RWIN) & 0x8000) != 0) result |= NativeMethods.MOD_WIN;
        return result;
    }
}
