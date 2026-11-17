using System.Runtime.InteropServices;

namespace DesktopClone.Interop;

/// <summary>全局热键(RegisterHotKey)封装:窗口失焦或收进托盘时依然有效。</summary>
internal static class GlobalHotkey
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    /// <summary>注册全局热键;失败时通过 errorCode 返回 Win32 错误码(1409 表示已被占用)。</summary>
    public static bool TryRegister(
        IntPtr window,
        int id,
        uint modifiers,
        uint virtualKey,
        out int errorCode)
    {
        if (RegisterHotKey(window, id, modifiers, virtualKey))
        {
            errorCode = 0;
            return true;
        }

        errorCode = Marshal.GetLastWin32Error();
        return false;
    }

    public static void Unregister(IntPtr window, int id)
    {
        if (window != IntPtr.Zero)
        {
            _ = UnregisterHotKey(window, id);
        }
    }
}
