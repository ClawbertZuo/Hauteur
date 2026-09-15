namespace Hauteur.Core;

/// <summary>热键组合的显示文本格式化(如 "Ctrl + Alt + T"),设置界面与托盘提示共用。</summary>
internal static class HotkeyText
{
    public static string Format(uint modifiers, uint key)
    {
        var parts = new List<string>(4);
        if ((modifiers & Interop.NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & Interop.NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & Interop.NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & Interop.NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        if ((modifiers & Interop.NativeMethods.MOD_XBUTTON1) != 0) parts.Add("鼠标侧键1");
        if ((modifiers & Interop.NativeMethods.MOD_XBUTTON2) != 0) parts.Add("鼠标侧键2");
        if (key != 0) parts.Add(KeyName(key));
        return string.Join(" + ", parts);
    }

    private static string KeyName(uint key)
    {
        var k = (Keys)key;
        return k switch
        {
            Keys.D0 => "0", Keys.D1 => "1", Keys.D2 => "2", Keys.D3 => "3", Keys.D4 => "4",
            Keys.D5 => "5", Keys.D6 => "6", Keys.D7 => "7", Keys.D8 => "8", Keys.D9 => "9",
            Keys.Oemcomma => ",", Keys.OemPeriod => ".", Keys.OemMinus => "-", Keys.Oemplus => "=",
            Keys.OemQuestion => "/", Keys.OemOpenBrackets => "[", Keys.OemCloseBrackets => "]",
            Keys.OemSemicolon => ";", Keys.OemQuotes => "'", Keys.Oemtilde => "`", Keys.OemBackslash => "\\",
            Keys.XButton1 => "鼠标侧键1", Keys.XButton2 => "鼠标侧键2",
            _ => k.ToString(),
        };
    }
}
