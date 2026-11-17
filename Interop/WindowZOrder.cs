using System.Runtime.InteropServices;

namespace DesktopClone.Interop;

/// <summary>窗口 Z 序调整:「窗口置底」需要持续压制系统的置顶/激活行为。</summary>
internal static class WindowZOrder
{
    private const int WmWindowPosChanging = 0x0046;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndBottom = new(1);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X;
        public int Y;
        public int Cx;
        public int Cy;
        public uint Flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint flags);

    /// <summary>把窗口压到 Z 序最底层(仍位于桌面之上)。</summary>
    public static void MoveToBottom(IntPtr window)
    {
        if (window != IntPtr.Zero)
        {
            _ = SetWindowPos(window, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    /// <summary>恢复到普通窗口的 Z 序顶部(取消置底后使用)。</summary>
    public static void MoveToTop(IntPtr window)
    {
        if (window != IntPtr.Zero)
        {
            _ = SetWindowPos(window, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    /// <summary>
    /// 置底期间拦截系统的 Z 序变更(激活、显示、SetWindowPos 等),强制改成 HWND_BOTTOM;
    /// 否则窗口一经点击或重新显示就会浮回最前。
    /// </summary>
    public static void ForceBottom(ref Message message)
    {
        if (message.Msg != WmWindowPosChanging || message.LParam == IntPtr.Zero)
        {
            return;
        }

        var position = Marshal.PtrToStructure<WindowPos>(message.LParam);
        if ((position.Flags & SwpNoZOrder) != 0)
        {
            // 调用方明确表示不改 Z 序,保持原样。
            return;
        }

        position.HwndInsertAfter = HwndBottom;
        Marshal.StructureToPtr(position, message.LParam, fDeleteOld: false);
    }
}
