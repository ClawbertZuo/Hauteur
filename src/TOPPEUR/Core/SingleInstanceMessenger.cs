namespace TOPPEUR.Core;

/// <summary>单实例:重复启动时,第二个实例向已有实例广播"打开设置"消息后退出。</summary>
internal static class SingleInstanceMessenger
{
    private const string MessageName = "TOPPEUR.ShowSettings.v1";

    public static readonly int MessageId =
        unchecked((int)Interop.NativeMethods.RegisterWindowMessage(MessageName));

    public static void NotifyExisting()
    {
        if (MessageId == 0) return;
        Interop.NativeMethods.PostMessage(
            Interop.NativeMethods.HWND_BROADCAST, (uint)MessageId, IntPtr.Zero, IntPtr.Zero);
    }
}
