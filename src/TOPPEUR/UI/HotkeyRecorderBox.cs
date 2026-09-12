using TOPPEUR.Core;
using TOPPEUR.Interop;

namespace TOPPEUR.UI;

/// <summary>
/// 录制式热键输入框:获得焦点后,按下任意组合键即被记录并显示。
/// 有效组合要求:至少一个修饰键(Ctrl/Alt/Shift/Win),或使用 F1~F24。
/// </summary>
internal sealed class HotkeyRecorderBox : TextBox
{
    private const int VK_F1 = 0x70;
    private const int VK_F24 = 0x87;

    private uint _modifiers;
    private uint _key;
    private bool _capturing;

    public HotkeyRecorderBox()
    {
        ReadOnly = true;
        Cursor = Cursors.Hand;
    }

    public uint Modifiers => _modifiers;
    public uint Key => _key;

    /// <summary>当前录制的组合键是否有效。</summary>
    public bool IsValid => _modifiers != 0 || (_key >= VK_F1 && _key <= VK_F24);

    public void SetCombo(uint modifiers, uint key)
    {
        _modifiers = modifiers;
        _key = key;
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
        Text = HotkeyText.Format(_modifiers, _key);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!_capturing) return;
        e.SuppressKeyPress = true;

        var key = e.KeyCode;

        // 只按下了修饰键:暂存并等待主键
        if (key is Keys.ShiftKey or Keys.ControlKey or Keys.Menu or Keys.LWin or Keys.RWin)
        {
            uint mods = ModifiersOf(ModifierKeys) | WinKeyState();
            if (mods != 0)
            {
                _modifiers = mods;
                _key = 0;
                Text = HotkeyText.Format(mods, 0);
            }
            else
            {
                _modifiers = 0;
                _key = 0;
                Text = string.Empty;
            }
            return;
        }

        // Esc 清空录制
        if (key == Keys.Escape)
        {
            _modifiers = 0;
            _key = 0;
            Text = string.Empty;
            return;
        }

        // 主键:ModifierKeys 不含 Win 键,需用 GetKeyState 单独检测
        uint allMods = ModifiersOf(ModifierKeys) | WinKeyState();
        _modifiers = allMods;
        _key = (uint)key;
        Text = HotkeyText.Format(allMods, (uint)key);
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
