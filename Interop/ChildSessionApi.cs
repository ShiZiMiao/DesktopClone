using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;

namespace DesktopClone.Interop;

/// <summary>
/// Windows 子会话(Child Session)与 RDP 环境的底层封装:
/// wtsapi32 的子会话 API、注册表环境读取、RDP ActiveX 输入窗口的管理。
/// </summary>
internal static class ChildSessionApi
{
    private const uint NoChildSessionId = uint.MaxValue;
    private const int DefaultRdpPort = 3389;
    private const string RdpWrapperLibraryName = "rdpwrap.dll";

    private const string RdpTcpRegistryPath =
        @"SYSTEM\CurrentControlSet\Control\Terminal Server\WinStations\RDP-Tcp";
    private const string TerminalServerRegistryPath =
        @"SYSTEM\CurrentControlSet\Control\Terminal Server";
    private const string TerminalServerPolicyRegistryPath =
        @"SOFTWARE\Policies\Microsoft\Windows NT\Terminal Services";
    private const string TermServiceParametersRegistryPath =
        @"SYSTEM\CurrentControlSet\Services\TermService\Parameters";
    private const string PasswordLessRegistryPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device";

    /// <summary>「仅允许 Windows Hello 登录」开启时该值的取值(社区逆向结论,非官方文档)。</summary>
    private const int PasswordLessHelloOnlyValue = 2;

    private static readonly IntPtr WtsCurrentServerHandle = IntPtr.Zero;

    // RDP 输入窗口缓存:同一时刻只有一个宿主窗口,校验有效性后复用。
    private static IntPtr _cachedHostWindow;
    private static IntPtr _cachedInputWindow;

    #region wtsapi32 子会话

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSEnableChildSessions([MarshalAs(UnmanagedType.Bool)] bool enable);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSIsChildSessionsEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSGetChildSessionId(out uint sessionId);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSLogoffSession(
        IntPtr serverHandle,
        uint sessionId,
        [MarshalAs(UnmanagedType.Bool)] bool wait);

    /// <summary>启用子会话功能。失败时抛出携带 Win32 错误码的异常。</summary>
    public static void EnableChildSessions()
    {
        if (!WTSEnableChildSessions(true))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用 RDP 子会话功能。");
        }
    }

    /// <summary>子会话功能当前是否已启用。</summary>
    public static bool IsChildSessionsEnabled()
    {
        if (!WTSIsChildSessionsEnabled(out var enabled))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取 RDP 子会话功能状态。");
        }

        return enabled;
    }

    /// <summary>当前子会话 ID;没有子会话时返回 null。</summary>
    public static uint? TryGetChildSessionId()
    {
        return WTSGetChildSessionId(out var sessionId) && sessionId != NoChildSessionId
            ? sessionId
            : null;
    }

    /// <summary>注销指定子会话(其中的所有程序都会被结束)。</summary>
    public static void LogoffSession(uint sessionId)
    {
        if (!WTSLogoffSession(WtsCurrentServerHandle, sessionId, true))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法注销分身会话 {sessionId}。");
        }
    }

    #endregion

    #region 注册表环境

    /// <summary>本机 RDP 监听端口(注册表可改,默认 3389)。</summary>
    public static int GetConfiguredRdpPort()
    {
        var value = ReadDword(RdpTcpRegistryPath, "PortNumber");
        return value is > 0 and <= ushort.MaxValue ? value.Value : DefaultRdpPort;
    }

    /// <summary>
    /// 远程桌面主机是否被关闭(fDenyTSConnections = 1)。
    /// 组策略键存在时会覆盖本机设置,fromPolicy 指明取值来源。
    /// </summary>
    public static bool IsRemoteDesktopDisabled(out bool fromPolicy)
    {
        var policyValue = ReadDword(TerminalServerPolicyRegistryPath, "fDenyTSConnections");
        if (policyValue.HasValue)
        {
            fromPolicy = true;
            return policyValue.Value == 1;
        }

        fromPolicy = false;
        return ReadDword(TerminalServerRegistryPath, "fDenyTSConnections") == 1;
    }

    /// <summary>
    /// 是否开启了「仅允许 Windows Hello 登录」。开启后微软账户没有可用的密码凭据,
    /// 子会话的密码登录会一直失败。
    /// </summary>
    public static bool IsHelloOnlySignIn()
    {
        return ReadDword(PasswordLessRegistryPath, "DevicePasswordLessBuildVersion")
               == PasswordLessHelloOnlyValue;
    }

    /// <summary>
    /// TermService 的 ServiceDll 是否指向 rdpwrap.dll(RDP Wrapper 多用户工具)。
    /// 该工具与子会话机制互斥,会导致连接失败。
    /// </summary>
    public static bool IsRdpWrapperInstalled()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(TermServiceParametersRegistryPath);
            var serviceDll = key?.GetValue("ServiceDll") as string;
            return serviceDll?.Contains(RdpWrapperLibraryName, StringComparison.OrdinalIgnoreCase) == true;
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            return false;
        }
    }

    private static int? ReadDword(string path, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(path);
            return key?.GetValue(name) as int?;
        }
        catch (Exception exception) when (exception is SecurityException
                                              or UnauthorizedAccessException
                                              or IOException)
        {
            return null;
        }
    }

    #endregion

    #region RDP 输入窗口

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parentWindow, EnumChildProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximumLength);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsChild(IntPtr parentWindow, IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool enable);

    private delegate bool EnumChildProc(IntPtr window, IntPtr parameter);

    /// <summary>键鼠焦点当前是否在 RDP 输入窗口上。</summary>
    public static bool IsInputWindowFocused(IntPtr hostWindow)
    {
        var inputWindow = FindInputWindow(hostWindow);
        return inputWindow != IntPtr.Zero && GetFocus() == inputWindow;
    }

    /// <summary>把键鼠焦点切到 RDP 输入窗口;失败返回 false。</summary>
    public static bool TryFocusInputWindow(IntPtr hostWindow)
    {
        var inputWindow = FindInputWindow(hostWindow);
        if (inputWindow == IntPtr.Zero)
        {
            return false;
        }

        _ = SetFocus(inputWindow);
        return GetFocus() == inputWindow;
    }

    /// <summary>
    /// 启用/禁用 RDP 输入窗口。禁用后本机键鼠不再进入分身,但画面渲染不受影响(观看模式)。
    /// </summary>
    public static void SetInputWindowEnabled(IntPtr hostWindow, bool enabled)
    {
        var inputWindow = FindInputWindow(hostWindow);
        if (inputWindow != IntPtr.Zero)
        {
            _ = EnableWindow(inputWindow, enabled);
        }
    }

    /// <summary>宿主窗口销毁或重连后清除缓存,强制重新枚举。</summary>
    public static void ClearInputWindowCache(IntPtr hostWindow)
    {
        if (_cachedHostWindow == hostWindow)
        {
            _cachedHostWindow = IntPtr.Zero;
            _cachedInputWindow = IntPtr.Zero;
        }
    }

    /// <summary>
    /// 在 RDP ActiveX 宿主窗口下查找标题为「Input Capture Window」的子窗口——
    /// 该控件的所有键鼠输入都从这里进入。只在 UI 线程调用。
    /// </summary>
    private static IntPtr FindInputWindow(IntPtr hostWindow)
    {
        if (hostWindow == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        if (_cachedHostWindow == hostWindow
            && IsWindow(_cachedInputWindow)
            && IsChild(hostWindow, _cachedInputWindow))
        {
            return _cachedInputWindow;
        }

        var found = IntPtr.Zero;
        EnumChildWindows(
            hostWindow,
            (window, _) =>
            {
                const int capacity = 256;
                var text = new StringBuilder(capacity);
                _ = GetWindowText(window, text, capacity);
                if (!string.Equals(text.ToString(), "Input Capture Window", StringComparison.Ordinal))
                {
                    return true;
                }

                found = window;
                return false;
            },
            IntPtr.Zero);

        _cachedHostWindow = hostWindow;
        _cachedInputWindow = found;
        return found;
    }

    #endregion
}
