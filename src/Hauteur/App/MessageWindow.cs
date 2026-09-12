using Hauteur.Core;

namespace Hauteur.App;

/// <summary>隐藏的顶层消息窗口:接收 WM_HOTKEY 与单实例广播消息,驱动托盘逻辑。</summary>
internal sealed class MessageWindow : NativeWindow
{
    private const int WM_HOTKEY = 0x0312;
    private const int WM_CLOSE = 0x0010;
    private const int WM_QUERYENDSESSION = 0x0011;
    private const int WM_ENDSESSION = 0x0016;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>热键触发,参数为热键 ID(HotkeyManager.ToggleHotkeyId / LayerHotkeyBase + layer - 1)。</summary>
    public event Action<int>? HotkeyPressed;

    /// <summary>收到关闭/注销请求(任务管理器结束任务、注销/关机)。走正常退出路径以完成层级状态注销。</summary>
    public event Action? ExitRequested;

    public event Action? ShowSettingsRequested;

    /// <summary>消息窗口标题:排障/自动化测试按此识别。</summary>
    internal const string WindowTitle = "Hauteur.MessageWindow";

    public MessageWindow()
    {
        // 无边框、不占任务栏/Alt-Tab 的隐藏窗口
        CreateHandle(new CreateParams { Caption = WindowTitle, Style = WS_POPUP, ExStyle = WS_EX_TOOLWINDOW });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            HotkeyPressed?.Invoke((int)m.WParam);
            return;
        }

        if (m.Msg == SingleInstanceMessenger.MessageId)
        {
            ShowSettingsRequested?.Invoke();
            return;
        }

        // 任务管理器"结束任务"发 WM_CLOSE;注销/关机发 WM_QUERYENDSESSION/WM_ENDSESSION。
        // 响应这些消息走正常退出路径,保证退出时恢复被调整的窗口层级。
        if (m.Msg == WM_CLOSE)
        {
            ExitRequested?.Invoke();
            return;
        }
        if (m.Msg == WM_QUERYENDSESSION)
        {
            m.Result = (IntPtr)1; // 允许注销
            ExitRequested?.Invoke();
            return;
        }
        if (m.Msg == WM_ENDSESSION && m.WParam != IntPtr.Zero)
        {
            ExitRequested?.Invoke();
            return;
        }

        base.WndProc(ref m);
    }
}
