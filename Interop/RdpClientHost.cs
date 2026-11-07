using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DesktopClone.Interop;

/// <summary>RDP 连接失败(或异常断开)的诊断信息。</summary>
internal sealed class RdpConnectionFailureEventArgs : EventArgs
{
    public RdpConnectionFailureEventArgs(string message, int errorCode, int? extendedErrorCode = null)
    {
        Message = message;
        ErrorCode = errorCode;
        ExtendedErrorCode = extendedErrorCode;
    }

    public string Message { get; }
    public int ErrorCode { get; }
    public int? ExtendedErrorCode { get; }
}

/// <summary>
/// 宿主 Windows 自带的 RDP ActiveX 控件(MsRdpClient10)。
/// 通过 IMsRdpExtendedSettings 的「ConnectToChildSession」属性把连接路由到本机子会话,
/// 这是整个桌面分身的核心开关。COM 全部走 late-binding,无需 interop 程序集。
/// </summary>
internal sealed class RdpClientHost : AxHost
{
    // Windows 10/11 自带的非脚本化 RDP ActiveX 控件(MsRdpClient10)。
    private const string RdpClientClsid = "A0C63C30-F08D-4AB4-907C-34905D770C7D";
    private const short VariantFalse = 0;
    private const short VariantTrue = -1;
    private const int AudioRedirectToClient = 0;
    private const int AudioDisableRemote = 2;
    private const int ExtendedKeyFlag = 0x100;
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1);

    private static readonly RemoteKey LeftWindowsKey = new(0x5B, IsExtended: true);
    private static readonly RemoteKey DKey = new(0x20, IsExtended: false);
    private static readonly RemoteKey TabKey = new(0x0F, IsExtended: false);

    private ConnectionPointCookie? _eventCookie;
    private RdpEventSink? _eventSink;
    private bool _connectAttemptInProgress;
    private bool _failureReported;
    private bool _disconnectRequested;
    private bool _smartSizing = true;
    private bool _audioMuted;
    private bool _shortcutsToRemote = true;
    private bool _inputBlocked;
    private Size? _pendingReconnectSize;
    private RdpConnectionFailureEventArgs? _lastDiagnostic;

    public event EventHandler? LoginCompleted;

    public event EventHandler<RdpConnectionFailureEventArgs>? ConnectionFailed;

    public RdpClientHost()
        : base(RdpClientClsid)
    {
        Dock = DockStyle.Fill;
    }

    /// <summary>最近一次登录阶段 ActiveX 报告的非致命诊断(超时时辅助定位原因)。</summary>
    public RdpConnectionFailureEventArgs? LastDiagnostic => _lastDiagnostic;

    /// <summary>0 未连接 / 1 已连接 / 2 连接中。</summary>
    public int ConnectedState
    {
        get
        {
            if (!IsHandleCreated)
            {
                return 0;
            }

            return Convert.ToInt32(
                GetComProperty(GetRequiredOcx(), "Connected"),
                CultureInfo.InvariantCulture);
        }
    }

    /// <summary>配置连接参数并发起连接。目标固定为 localhost 的子会话。</summary>
    public void ConnectToChildSession(Size desktopSize)
    {
        if (ConnectedState != 0)
        {
            return;
        }

        ChildSessionApi.ClearInputWindowCache(Handle);
        var client = GetRequiredOcx();
        var width = Math.Clamp(desktopSize.Width, 200, 8192);
        var height = Math.Clamp(desktopSize.Height, 200, 8192);

        SetComProperty(client, "Server", "localhost");
        SetComProperty(client, "DesktopWidth", width);
        SetComProperty(client, "DesktopHeight", height);
        SetComProperty(client, "ColorDepth", 32);
        SetComProperty(client, "ConnectingText", "正在创建桌面分身会话…");
        SetComProperty(client, "DisconnectedText", "桌面分身连接已断开");

        var securedSettings = GetComProperty(client, "SecuredSettings2")
            ?? throw new COMException("RDP ActiveX 未返回 SecuredSettings2。");
        RunComStep("设置系统组合键归属", () =>
            SetComProperty(securedSettings, "KeyboardHookMode", _shortcutsToRemote ? 1 : 0));
        RunComStep("设置远程音频", () =>
            SetComProperty(securedSettings, "AudioRedirectionMode",
                _audioMuted ? AudioDisableRemote : AudioRedirectToClient));

        var advancedSettings = GetComProperty(client, "AdvancedSettings7")
            ?? throw new COMException("RDP ActiveX 未返回 AdvancedSettings7。");
        RunComStep("设置 RDP 端口", () =>
            SetComProperty(advancedSettings, "RDPPort", ChildSessionApi.GetConfiguredRdpPort()));
        RunComStep("启用 CredSSP", () =>
            SetComProperty(advancedSettings, "EnableCredSspSupport", true));
        RunComStep("启用远程 Win 键", () =>
            SetComProperty(advancedSettings, "EnableWindowsKey", 1));
        RunComStep("设置显示缩放", () =>
            SetComProperty(advancedSettings, "SmartSizing", _smartSizing));

        object connectToChildSession = true;
        RunComStep("设置 ConnectToChildSession", () =>
        {
            var extendedSettings = (IMsRdpExtendedSettings)client;
            TrySetExtendedProperty(extendedSettings, "EnableZoom", true);
            extendedSettings.set_Property("ConnectToChildSession", ref connectToChildSession);
        });

        _connectAttemptInProgress = true;
        _failureReported = false;
        _lastDiagnostic = null;
        _disconnectRequested = false;
        try
        {
            RunComStep("发起 RDP 连接", () => InvokeComMethod(client, "Connect"));
        }
        catch
        {
            _connectAttemptInProgress = false;
            throw;
        }
    }

    /// <summary>断开当前连接并重新发起(设置切换、失败重试用)。若未连接则直接连。</summary>
    public void ReconnectToChildSession(Size desktopSize)
    {
        if (_disconnectRequested || _pendingReconnectSize.HasValue)
        {
            _pendingReconnectSize = desktopSize;
            return;
        }

        if (ConnectedState == 0)
        {
            ConnectToChildSession(desktopSize);
            return;
        }

        _pendingReconnectSize = desktopSize;
        try
        {
            // OnLogonError 之后 ActiveX 可能仍处于连接状态,必须先断开再连。
            if (!DisconnectCore())
            {
                _pendingReconnectSize = null;
                ConnectToChildSession(desktopSize);
            }
        }
        catch
        {
            _pendingReconnectSize = null;
            throw;
        }
    }

    /// <summary>断开 RDP 连接。分身会话本身不受影响,继续在后台运行。</summary>
    public void DisconnectSession()
    {
        _pendingReconnectSize = null;
        _ = DisconnectCore();
    }

    private bool DisconnectCore()
    {
        if (IsHandleCreated)
        {
            ChildSessionApi.ClearInputWindowCache(Handle);
        }

        if (ConnectedState == 0)
        {
            return false;
        }

        _disconnectRequested = true;
        try
        {
            InvokeComMethod(GetRequiredOcx(), "Disconnect");
            return true;
        }
        catch
        {
            _disconnectRequested = false;
            throw;
        }
    }

    /// <summary>屏蔽/恢复本机键鼠进入分身(观看模式)。状态会被记住,重连后需重新施加。</summary>
    public void SetInputBlocked(bool blocked)
    {
        _inputBlocked = blocked;
        if (!IsHandleCreated)
        {
            return;
        }

        ChildSessionApi.SetInputWindowEnabled(Handle, !blocked);
    }

    public void SetSmartSizing(bool enabled)
    {
        _smartSizing = enabled;
        if (!IsHandleCreated)
        {
            return;
        }

        var advancedSettings = GetComProperty(GetRequiredOcx(), "AdvancedSettings7")
            ?? throw new COMException("RDP ActiveX 未返回 AdvancedSettings7。");
        RunComStep("设置显示缩放", () =>
            SetComProperty(advancedSettings, "SmartSizing", enabled));
    }

    // 以下两项是连接期参数,连接建立后修改需要重连才生效。

    public void SetAudioMuted(bool muted) => _audioMuted = muted;

    public void SetShortcutsToRemote(bool toRemote) => _shortcutsToRemote = toRemote;

    public bool IsInputWindowFocused()
    {
        return ConnectedState == 1 && ChildSessionApi.IsInputWindowFocused(Handle);
    }

    /// <summary>向分身发送 Win+D(显示桌面)。</summary>
    public void SendShowDesktop()
    {
        SendShortcut(
        [
            new KeyStroke(LeftWindowsKey, IsKeyUp: false),
            new KeyStroke(DKey, IsKeyUp: false),
            new KeyStroke(DKey, IsKeyUp: true),
            new KeyStroke(LeftWindowsKey, IsKeyUp: true)
        ], "Win+D");
    }

    /// <summary>向分身发送 Win+Tab(任务视图)。</summary>
    public void SendTaskView()
    {
        SendShortcut(
        [
            new KeyStroke(LeftWindowsKey, IsKeyUp: false),
            new KeyStroke(TabKey, IsKeyUp: false),
            new KeyStroke(TabKey, IsKeyUp: true),
            new KeyStroke(LeftWindowsKey, IsKeyUp: true)
        ], "Win+Tab");
    }

    protected override void CreateSink()
    {
        base.CreateSink();

        _eventSink = new RdpEventSink(this);
        _eventCookie = new ConnectionPointCookie(GetOcx(), _eventSink, typeof(IMsTscAxEvents));
    }

    protected override void DetachSink()
    {
        try
        {
            _eventCookie?.Disconnect();
        }
        finally
        {
            _eventCookie = null;
            _eventSink = null;
            base.DetachSink();
        }
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        ChildSessionApi.ClearInputWindowCache(Handle);
        base.OnHandleDestroyed(e);
    }

    private void SendShortcut(KeyStroke[] strokes, string name)
    {
        if (ConnectedState != 1)
        {
            throw new InvalidOperationException($"桌面分身尚未连接,无法发送 {name}。");
        }

        // 显式发起的组合键优先:观看模式下短暂恢复输入窗口,发完立刻屏蔽回去。
        var restoreInput = _inputBlocked;
        if (restoreInput)
        {
            ChildSessionApi.SetInputWindowEnabled(Handle, true);
        }

        try
        {
            if (!ChildSessionApi.TryFocusInputWindow(Handle))
            {
                throw new InvalidOperationException("无法把键盘焦点切换到桌面分身。");
            }

            SendKeyStrokes(strokes);
        }
        finally
        {
            if (restoreInput)
            {
                ChildSessionApi.SetInputWindowEnabled(Handle, false);
            }
        }
    }

    private void SendKeyStrokes(KeyStroke[] strokes)
    {
        var keyUpStates = new short[strokes.Length];
        var keyData = new int[strokes.Length];
        for (var index = 0; index < strokes.Length; index++)
        {
            keyUpStates[index] = strokes[index].IsKeyUp ? VariantTrue : VariantFalse;
            keyData[index] = strokes[index].Key.ScanCode
                             | (strokes[index].Key.IsExtended ? ExtendedKeyFlag : 0);
        }

        var nonScriptable = (IMsRdpClientNonScriptable)GetRequiredOcx();
        nonScriptable.SendKeys(strokes.Length, ref keyUpStates[0], ref keyData[0]);
    }

    #region 事件处理

    private void OnLoginComplete()
    {
        _connectAttemptInProgress = false;
        _failureReported = false;
        _lastDiagnostic = null;
        LoginCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void OnDisconnected(int disconnectReason)
    {
        var failedWhileConnecting = _connectAttemptInProgress;
        _connectAttemptInProgress = false;

        if (_disconnectRequested)
        {
            _disconnectRequested = false;
            if (_pendingReconnectSize.HasValue && !IsDisposed && !Disposing)
            {
                try
                {
                    BeginInvoke(new Action(ConnectPendingReconnect));
                }
                catch (InvalidOperationException)
                {
                    _pendingReconnectSize = null;
                }
            }

            return;
        }

        var extendedReason = TryGetExtendedDisconnectReason();
        if (_failureReported
            || (!failedWhileConnecting && IsNormalDisconnect(disconnectReason, extendedReason)))
        {
            return;
        }

        var description = TryGetErrorDescription(disconnectReason, extendedReason);
        var title = failedWhileConnecting ? "RDP 连接失败" : "RDP 连接意外断开";
        var message = string.IsNullOrWhiteSpace(description)
            ? $"{title}。(断开原因 {FormatCode(disconnectReason)},扩展原因 {FormatCode(extendedReason)})"
            : $"{title}:{description}(断开原因 {FormatCode(disconnectReason)},扩展原因 {FormatCode(extendedReason)})";
        ReportFailure(message, disconnectReason, extendedReason);
    }

    private async void ConnectPendingReconnect()
    {
        await Task.Delay(ReconnectDelay);

        var size = _pendingReconnectSize;
        _pendingReconnectSize = null;
        if (!size.HasValue || IsDisposed || Disposing)
        {
            return;
        }

        try
        {
            ConnectToChildSession(size.Value);
        }
        catch (Exception exception) when (exception is COMException
                                              or TargetInvocationException
                                              or InvalidOperationException)
        {
            ReportFailure(
                $"RDP 自动重连失败:{exception.GetBaseException().Message}",
                exception is COMException comException ? comException.ErrorCode : 0);
        }
    }

    private void OnFatalError(int errorCode)
    {
        _connectAttemptInProgress = false;
        if (_disconnectRequested)
        {
            return;
        }

        ReportFailure(
            $"RDP 客户端发生致命错误:{DescribeFatalError(errorCode)}({FormatCode(errorCode)})",
            errorCode);
    }

    private void OnLogonError(int errorCode)
    {
        if (_disconnectRequested)
        {
            return;
        }

        var description = DescribeLogonError(errorCode);

        // 这些代码表示登录仍在继续或控件正在展示交互选项,不能提前判死。
        if (errorCode is -5 or -4 or -2 or 3)
        {
            _lastDiagnostic = new RdpConnectionFailureEventArgs(
                $"RDP 登录:{description}({FormatCode(errorCode)})",
                errorCode);
            return;
        }

        _connectAttemptInProgress = false;
        ReportFailure($"RDP 登录失败:{description}({FormatCode(errorCode)})", errorCode);
    }

    private void ReportFailure(string message, int errorCode, int? extendedErrorCode = null)
    {
        if (_failureReported)
        {
            return;
        }

        _failureReported = true;
        ConnectionFailed?.Invoke(
            this,
            new RdpConnectionFailureEventArgs(message, errorCode, extendedErrorCode));
    }

    private int TryGetExtendedDisconnectReason()
    {
        try
        {
            return Convert.ToInt32(
                GetComProperty(GetRequiredOcx(), "ExtendedDisconnectReason"),
                CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is COMException
                                              or TargetInvocationException
                                              or InvalidOperationException)
        {
            return 0;
        }
    }

    private string? TryGetErrorDescription(int disconnectReason, int extendedDisconnectReason)
    {
        try
        {
            return Convert.ToString(
                InvokeComMethod(
                    GetRequiredOcx(),
                    "GetErrorDescription",
                    disconnectReason,
                    extendedDisconnectReason),
                CultureInfo.CurrentCulture);
        }
        catch (Exception exception) when (exception is COMException
                                              or TargetInvocationException
                                              or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsNormalDisconnect(int disconnectReason, int extendedDisconnectReason)
    {
        // 1~3:本地主动断开、远端用户断开、服务端主动断开;扩展 0~2:API 断开/注销。
        return disconnectReason is 1 or 2 or 3
               && extendedDisconnectReason is 0 or 1 or 2;
    }

    private static string FormatCode(int errorCode)
    {
        return $"{errorCode} (0x{unchecked((uint)errorCode):X8})";
    }

    private static string DescribeFatalError(int errorCode)
    {
        return errorCode switch
        {
            0 => "未知错误",
            1 or 4 or 6 => "内部错误",
            2 => "内存不足",
            3 => "无法创建 RDP 窗口",
            5 => "RDP 客户端进入无效状态",
            7 => "建立连接时发生不可恢复的错误",
            100 => "Windows 套接字初始化失败",
            _ => "未识别的致命错误"
        };
    }

    private static string DescribeLogonError(int errorCode)
    {
        return errorCode switch
        {
            -7 => "Winlogon 正在显示「拒绝断开现有会话」对话框",
            -6 => "Winlogon 正在显示「无权限」对话框",
            -5 => "Winlogon 正在显示会话争用选项",
            -4 => "Winlogon 正在显示重新连接选项",
            -3 => "Winlogon 已静默终止登录",
            -1 => "访问被拒绝",
            0 => "登录凭据无效",
            1 => "密码已过期,必须先修改密码",
            2 => "登录或登录后的处理发生错误",
            3 => "RDP 客户端正在显示登录警告",
            unchecked((int)0xC000006D) => "用户名或身份验证信息无效",
            unchecked((int)0xC000006E) => "身份验证受到用户账户限制",
            unchecked((int)0xC0000224) => "密码已过期,必须先修改密码",
            _ => "登录阶段发生未识别的错误"
        };
    }

    #endregion

    #region late-binding COM 辅助

    private object GetRequiredOcx()
    {
        if (!IsHandleCreated)
        {
            // 读取 Handle 强制 AxHost 创建底层窗口与 ActiveX 实例(不受可见性影响)。
            _ = Handle;
        }

        return GetOcx()
               ?? throw new InvalidOperationException("RDP ActiveX 控件尚未完成初始化。");
    }

    private static object? GetComProperty(object target, string propertyName)
    {
        return target.GetType().InvokeMember(
            propertyName,
            System.Reflection.BindingFlags.GetProperty,
            binder: null,
            target,
            args: null,
            CultureInfo.InvariantCulture);
    }

    private static void SetComProperty(object target, string propertyName, object value)
    {
        target.GetType().InvokeMember(
            propertyName,
            System.Reflection.BindingFlags.SetProperty,
            binder: null,
            target,
            [value],
            CultureInfo.InvariantCulture);
    }

    private static object? InvokeComMethod(object target, string methodName, params object[]? args)
    {
        return target.GetType().InvokeMember(
            methodName,
            System.Reflection.BindingFlags.InvokeMethod,
            binder: null,
            target,
            args,
            CultureInfo.InvariantCulture);
    }

    private static void TrySetExtendedProperty(
        IMsRdpExtendedSettings extendedSettings,
        string propertyName,
        object value)
    {
        try
        {
            extendedSettings.set_Property(propertyName, ref value);
        }
        catch (COMException)
        {
            // 旧版 MsTscAx 可能不支持个别扩展属性,忽略后沿用默认显示行为。
        }
    }

    private static void RunComStep(string stepName, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            if (exception.GetBaseException() is COMException comException)
            {
                throw new COMException($"{stepName}失败:{comException.Message}", comException.ErrorCode);
            }

            throw;
        }
    }

    #endregion

    #region COM 接口声明(源内 interop,免依赖)

    /// <summary>MsTscAx 事件源接口(RDP 客户端 ActiveX 自带)。</summary>
    [ComImport]
    [Guid("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    private interface IMsTscAxEvents
    {
        [DispId(1)]
        void OnConnecting();

        [DispId(2)]
        void OnConnected();

        [DispId(3)]
        void OnLoginComplete();

        [DispId(4)]
        void OnDisconnected([In] int disconnectReason);

        [DispId(5)]
        void OnEnterFullScreenMode();

        [DispId(6)]
        void OnLeaveFullScreenMode();

        [DispId(7)]
        void OnChannelReceivedData(
            [In, MarshalAs(UnmanagedType.BStr)] string channelName,
            [In, MarshalAs(UnmanagedType.BStr)] string data);

        [DispId(8)]
        void OnRequestGoFullScreen();

        [DispId(9)]
        void OnRequestLeaveFullScreen();

        [DispId(10)]
        void OnFatalError([In] int errorCode);

        [DispId(11)]
        void OnWarning([In] int warningCode);

        [DispId(12)]
        void OnRemoteDesktopSizeChange([In] int width, [In] int height);

        [DispId(13)]
        void OnIdleTimeoutNotification();

        [DispId(14)]
        void OnRequestContainerMinimize();

        [DispId(15)]
        void OnConfirmClose([Out] out bool allowClose);

        [DispId(16)]
        void OnReceivedTSPublicKey(
            [In, MarshalAs(UnmanagedType.BStr)] string publicKey,
            [Out] out bool continueLogon);

        [DispId(17)]
        void OnAutoReconnecting(
            [In] int disconnectReason,
            [In] int attemptCount,
            [Out] out AutoReconnectState continueStatus);

        [DispId(18)]
        void OnAuthenticationWarningDisplayed();

        [DispId(19)]
        void OnAuthenticationWarningDismissed();

        [DispId(22)]
        void OnLogonError([In] int errorCode);

        [DispId(23)]
        void OnFocusReleased([In] int direction);

        [DispId(24)]
        void OnUserNameAcquired([In, MarshalAs(UnmanagedType.BStr)] string userName);

        [DispId(26)]
        void OnMouseInputModeChanged([In] bool isRelativeMouseMode);

        [DispId(28)]
        void OnServiceMessageReceived([In, MarshalAs(UnmanagedType.BStr)] string serviceMessage);

        [DispId(30)]
        void OnConnectionBarPullDown();

        [DispId(32)]
        void OnNetworkStatusChanged(
            [In] uint qualityLevel,
            [In] int bandwidth,
            [In] int roundTripTime);

        [DispId(33)]
        void OnAutoReconnected();

        [DispId(34)]
        void OnAutoReconnecting2(
            [In] int disconnectReason,
            [In] bool networkAvailable,
            [In] int attemptCount,
            [In] int maxAttemptCount);

        [DispId(35)]
        void OnDevicesButtonPressed();
    }

    [ComVisible(true)]
    private sealed class RdpEventSink(RdpClientHost owner) : IMsTscAxEvents
    {
        public void OnConnecting() { }

        public void OnConnected() { }

        public void OnLoginComplete() => owner.OnLoginComplete();

        public void OnDisconnected(int disconnectReason) => owner.OnDisconnected(disconnectReason);

        public void OnEnterFullScreenMode() { }

        public void OnLeaveFullScreenMode() { }

        public void OnChannelReceivedData(string channelName, string data) { }

        public void OnRequestGoFullScreen() { }

        public void OnRequestLeaveFullScreen() { }

        public void OnFatalError(int errorCode) => owner.OnFatalError(errorCode);

        public void OnWarning(int warningCode) { }

        public void OnRemoteDesktopSizeChange(int width, int height) { }

        public void OnIdleTimeoutNotification() { }

        public void OnRequestContainerMinimize() { }

        public void OnConfirmClose(out bool allowClose) => allowClose = true;

        public void OnReceivedTSPublicKey(string publicKey, out bool continueLogon) =>
            continueLogon = true;

        public void OnAutoReconnecting(int disconnectReason, int attemptCount, out AutoReconnectState continueStatus) =>
            continueStatus = AutoReconnectState.Automatic;

        public void OnAuthenticationWarningDisplayed() { }

        public void OnAuthenticationWarningDismissed() { }

        public void OnLogonError(int errorCode) => owner.OnLogonError(errorCode);

        public void OnFocusReleased(int direction) { }

        public void OnUserNameAcquired(string userName) { }

        public void OnMouseInputModeChanged(bool isRelativeMouseMode) { }

        public void OnServiceMessageReceived(string serviceMessage) { }

        public void OnConnectionBarPullDown() { }

        public void OnNetworkStatusChanged(uint qualityLevel, int bandwidth, int roundTripTime) { }

        public void OnAutoReconnected() => owner.OnLoginComplete();

        public void OnAutoReconnecting2(int disconnectReason, bool networkAvailable, int attemptCount, int maxAttemptCount) { }

        public void OnDevicesButtonPressed() { }
    }

    private enum AutoReconnectState
    {
        Automatic = 0,
        Stop = 1,
        Manual = 2
    }

    /// <summary>RDP ActiveX 的扩展设置接口,承载 ConnectToChildSession 开关。</summary>
    [ComImport]
    [Guid("302D8188-0052-4807-806A-362B628F9AC5")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMsRdpExtendedSettings
    {
        void set_Property(
            [In, MarshalAs(UnmanagedType.BStr)] string propertyName,
            [In, MarshalAs(UnmanagedType.Struct)] ref object value);

        [return: MarshalAs(UnmanagedType.Struct)]
        object get_Property([In, MarshalAs(UnmanagedType.BStr)] string propertyName);
    }

    /// <summary>非脚本化接口,用于向远程会话注入扫描码。</summary>
    [ComImport]
    [Guid("2F079C4C-87B2-4AFD-97AB-20CDB43038AE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMsRdpClientNonScriptable
    {
        void put_ClearTextPassword([In, MarshalAs(UnmanagedType.BStr)] string value);

        void put_PortablePassword([In, MarshalAs(UnmanagedType.BStr)] string value);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_PortablePassword();

        void put_PortableSalt([In, MarshalAs(UnmanagedType.BStr)] string value);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_PortableSalt();

        void put_BinaryPassword([In, MarshalAs(UnmanagedType.BStr)] string value);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_BinaryPassword();

        void put_BinarySalt([In, MarshalAs(UnmanagedType.BStr)] string value);

        [return: MarshalAs(UnmanagedType.BStr)]
        string get_BinarySalt();

        void ResetPassword();

        void NotifyRedirectDeviceChange(nuint wParam, nint lParam);

        void SendKeys(
            int numKeys,
            [In] ref short keyUpStates,
            [In] ref int keyData);
    }

    private readonly record struct RemoteKey(int ScanCode, bool IsExtended);

    private readonly record struct KeyStroke(RemoteKey Key, bool IsKeyUp);

    #endregion
}
