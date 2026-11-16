using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using DesktopClone.Interop;

namespace DesktopClone.Services;

/// <summary>需要重连才能生效的连接期设置。</summary>
[Flags]
internal enum SettingChange
{
    None = 0,
    Shortcuts = 1,
    Audio = 2,
    Resolution = 4
}

/// <summary>
/// 桌面分身的编排核心:预检 → 启用子会话 → RDP 连接(超时/重试)→
/// 向分身启动程序 / 断开(保留会话)/ 注销会话,并以 500ms 周期刷新状态。
/// 所有公开方法都应在 UI 线程调用(RDP ActiveX 与 WinForms Timer 均绑定 STA)。
/// </summary>
internal sealed class ChildSessionManager : IDisposable
{
    private static readonly Size FallbackDesktopSize = new(1920, 1080);
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(60);
    private const int InitialRetryCount = 3;
    private const int SettingsReconnectRetryCount = 2;
    private const int TimeoutDisconnectCode = 1460;

    private readonly RdpClientHost _rdpHost;
    private readonly AppSettings _settings;
    private readonly System.Windows.Forms.Timer _statusTimer;

    private TaskCompletionSource<bool>? _startCompletion;
    private RdpConnectionFailureEventArgs? _lastFailure;
    private int _initialRetriesRemaining;
    private bool _initialRetryScheduled;
    private SettingChange _pendingChanges = SettingChange.None;
    private int _settingsRetriesRemaining;
    private bool _settingsRetryScheduled;
    private bool _statusTickBusy;
    private bool _disposed;
    private string _lastOperation = "尚未启动桌面分身";
    private IReadOnlyList<EnvironmentIssue> _lastIssues = [];

    public event EventHandler? StateChanged;

    public ChildSessionManager(RdpClientHost rdpHost, AppSettings settings)
    {
        _rdpHost = rdpHost;
        _settings = settings;
        _rdpHost.LoginCompleted += OnLoginCompleted;
        _rdpHost.ConnectionFailed += OnConnectionFailed;
        _statusTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _statusTimer.Tick += OnStatusTimerTick;
        _statusTimer.Start();
    }

    public RdpClientHost RdpHost => _rdpHost;

    public uint? ChildSessionId { get; private set; }

    public bool IsConnected => _rdpHost.ConnectedState == 1;

    /// <summary>「原始分辨率」:本机主显示器的当前分辨率。</summary>
    public static Size OriginalDesktopSize
    {
        get
        {
            var bounds = Screen.PrimaryScreen?.Bounds
                         ?? new Rectangle(0, 0, FallbackDesktopSize.Width, FallbackDesktopSize.Height);
            return new Size(bounds.Width, bounds.Height);
        }
    }

    /// <summary>当前设置下实际使用的分身桌面分辨率。</summary>
    private Size ResolveDesktopSize()
    {
        if (_settings.DesktopWidth > 0 && _settings.DesktopHeight > 0)
        {
            return new Size(_settings.DesktopWidth, _settings.DesktopHeight);
        }

        return OriginalDesktopSize;
    }

    public bool IsFeatureEnabled { get; private set; }

    public bool WatchMode { get; private set; }

    public string StatusText { get; private set; } = string.Empty;

    /// <summary>最近一次启动失败的完整诊断(含环境预检结论)。</summary>
    public string? LastFailureMessage => _lastFailure?.Message;

    /// <summary>执行只读环境预检并缓存结论,供界面展示与失败诊断使用。</summary>
    public IReadOnlyList<EnvironmentIssue> RunPrecheck()
    {
        _lastIssues = EnvironmentCheck.Collect();
        return _lastIssues;
    }

    /// <summary>
    /// 启动桌面分身:预检 → 确保子会话功能开启 → 建立连接并等待登录完成。
    /// 已连接时立即返回 true;进行中时复用同一个等待任务。
    /// </summary>
    public async Task<bool> StartAsync()
    {
        ThrowIfDisposed();
        RunPrecheck();

        if (!ChildSessionApi.IsChildSessionsEnabled())
        {
            ChildSessionApi.EnableChildSessions();
        }

        RefreshState();
        if (IsConnected)
        {
            _lastOperation = "桌面分身已连接";
            RefreshState();
            return true;
        }

        if (_startCompletion is not { Task.IsCompleted: false })
        {
            _startCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _lastFailure = null;
            try
            {
                _lastOperation = "正在启动桌面分身";
                _initialRetriesRemaining = InitialRetryCount;
                _rdpHost.ConnectToChildSession(ResolveDesktopSize());
            }
            catch (Exception exception)
            {
                _initialRetriesRemaining = 0;
                var completion = _startCompletion;
                _startCompletion = null;
                completion.TrySetResult(false);
                _lastOperation = $"启动失败:{exception.Message}";
                RefreshState();
                return false;
            }
        }

        try
        {
            return await _startCompletion.Task.WaitAsync(ConnectionTimeout);
        }
        catch (TimeoutException)
        {
            if (_startCompletion is { Task.IsCompleted: false })
            {
                _initialRetriesRemaining = 0;
                var timeoutMessage =
                    $"桌面分身连接及登录初始化未能在 {ConnectionTimeout.TotalSeconds:0} 秒内完成。";
                if (_rdpHost.LastDiagnostic is { } diagnostic)
                {
                    timeoutMessage += $"\n\nRDP 最后报告:{diagnostic.Message}";
                }

                CompleteFailure(new RdpConnectionFailureEventArgs(
                    timeoutMessage, TimeoutDisconnectCode));
            }

            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    /// <summary>断开画面连接。分身会话保留在后台,重开工具后可再次启动并附着。</summary>
    public void Disconnect(string? operationMessage = null)
    {
        ThrowIfDisposed();
        _initialRetriesRemaining = 0;
        _pendingChanges = SettingChange.None;
        _startCompletion?.TrySetResult(false);
        _startCompletion = null;

        try
        {
            _rdpHost.DisconnectSession();
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException)
        {
            // ActiveX 正在自行断开时可能抛 COM 错误,不影响继续。
        }

        _lastOperation = operationMessage ?? "已断开连接,分身会话继续在后台运行";
        RefreshState();
    }

    /// <summary>断开连接并注销分身会话(其中所有程序都会被结束)。无会话时仅断开。</summary>
    public async Task<uint?> LogoffAsync(uint? expectedSessionId = null)
    {
        ThrowIfDisposed();
        var activeSessionId = ChildSessionApi.TryGetChildSessionId();
        if (activeSessionId is null)
        {
            Disconnect("当前没有分身会话");
            return null;
        }

        if (expectedSessionId.HasValue && activeSessionId.Value != expectedSessionId.Value)
        {
            throw new InvalidOperationException(
                $"分身会话已从 {expectedSessionId} 变为 {activeSessionId},已取消注销。");
        }

        Disconnect($"正在断开连接并注销分身会话 {activeSessionId}…");
        await Task.Run(() => ChildSessionApi.LogoffSession(activeSessionId.Value));
        _lastOperation = $"分身会话 {activeSessionId} 已注销";
        RefreshState();
        return activeSessionId;
    }

    /// <summary>以管理员权限把指定 exe 启动到分身会话中(临时计划任务实现)。</summary>
    public async Task LaunchProgramAsync(string executablePath)
    {
        ThrowIfDisposed();
        var sessionId = ChildSessionId
            ?? throw new InvalidOperationException("当前没有可用的桌面分身,请先启动。");

        var fullPath = Path.GetFullPath(executablePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("要启动的程序不存在。", fullPath);
        }

        _lastOperation = $"正在以管理员权限在分身中启动 {Path.GetFileName(fullPath)}…";
        RefreshState();
        await Task.Run(() => LaunchViaTemporaryTask(sessionId, fullPath));
        _lastOperation = $"已在分身会话 {sessionId} 中启动 {Path.GetFileName(fullPath)}";
        RefreshState();
    }

    public void SetWatchMode(bool enabled)
    {
        WatchMode = enabled;
        _settings.WatchMode = enabled;
        _rdpHost.SetInputBlocked(enabled);
        _lastOperation = enabled
            ? "已开启观看模式:本机键鼠不会进入分身"
            : "已关闭观看模式,可以操作分身";
        RefreshState();
    }

    public void SetSmartSizing(bool enabled)
    {
        _rdpHost.SetSmartSizing(enabled);
        _settings.SmartSizing = enabled;
        _lastOperation = enabled ? "自适应缩放已开启" : "自适应缩放已关闭(1:1 显示)";
        RefreshState();
    }

    /// <summary>设置分身桌面分辨率,null 表示跟随本机主显示器的原始分辨率。</summary>
    /// <returns>返回 true 表示已触发自动重连,新的分辨率在重连完成后生效。</returns>
    public bool SetDesktopSize(Size? size)
    {
        _settings.DesktopWidth = size?.Width ?? 0;
        _settings.DesktopHeight = size?.Height ?? 0;
        var effective = ResolveDesktopSize();
        return ReconnectForSettingChange(
            SettingChange.Resolution,
            $"分辨率已设为 {effective.Width}×{effective.Height}");
    }

    /// <summary>返回 true 表示已触发自动重连,设置在重连完成后生效。</summary>
    public bool SetAudioMuted(bool muted)
    {
        _settings.AudioMuted = muted;
        return ReconnectForSettingChange(
            SettingChange.Audio,
            muted ? "分身声音已关闭" : "分身声音已开启");
    }

    public bool SetShortcutsToRemote(bool toRemote)
    {
        _settings.ShortcutsToRemote = toRemote;
        return ReconnectForSettingChange(
            SettingChange.Shortcuts,
            toRemote ? "系统组合键改为作用于分身" : "系统组合键改为作用于本机");
    }

    public void SendWinD()
    {
        _rdpHost.SendShowDesktop();
        _lastOperation = "已向分身发送 Win+D";
        RefreshState();
    }

    public void SendWinTab()
    {
        _rdpHost.SendTaskView();
        _lastOperation = "已向分身发送 Win+Tab";
        RefreshState();
    }

    public void RefreshState()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            IsFeatureEnabled = ChildSessionApi.IsChildSessionsEnabled();
            ChildSessionId = ChildSessionApi.TryGetChildSessionId();
        }
        catch (Win32Exception)
        {
            // 读取失败时保留上一次的值。
        }

        var connectionText = _rdpHost.ConnectedState switch
        {
            0 => "未连接",
            1 => "已连接",
            2 => "连接中",
            var state => $"未知状态 {state}"
        };

        StatusText =
            $"{_lastOperation}    |    RDP:{connectionText}    |    分身会话:{ChildSessionId?.ToString() ?? "无"}"
            + $"    |    子会话功能:{(IsFeatureEnabled ? "已启用" : "未启用")}";
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _statusTimer.Stop();
        _statusTimer.Tick -= OnStatusTimerTick;
        _rdpHost.LoginCompleted -= OnLoginCompleted;
        _rdpHost.ConnectionFailed -= OnConnectionFailed;
        _startCompletion?.TrySetCanceled();
        _startCompletion = null;

        // 只断开画面;分身会话刻意保留后台运行,注销必须由用户显式发起。
        try
        {
            _rdpHost.DisconnectSession();
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException
                                              or InvalidOperationException)
        {
            // 退出时的清理失败不应阻止程序关闭。
        }
    }

    #region 连接流程

    private void OnLoginCompleted(object? sender, EventArgs e)
    {
        _lastFailure = null;
        _initialRetriesRemaining = 0;
        var completion = _startCompletion;
        _startCompletion = null;
        completion?.TrySetResult(true);

        // 输入窗口每次连接都是新建的,观看模式需要重新施加。
        _rdpHost.SetInputBlocked(WatchMode);

        if (_pendingChanges != SettingChange.None)
        {
            _pendingChanges = SettingChange.None;
            _settingsRetriesRemaining = 0;
            _lastOperation = "已重新连接,设置更改已生效";
        }
        else
        {
            _lastOperation = "桌面分身已登录";
        }

        RefreshState();
    }

    private void OnConnectionFailed(object? sender, RdpConnectionFailureEventArgs e)
    {
        _lastFailure = e;

        if (_pendingChanges != SettingChange.None)
        {
            if (_settingsRetryScheduled)
            {
                return;
            }

            if (_settingsRetriesRemaining > 0)
            {
                ScheduleSettingsReconnect();
                return;
            }

            _pendingChanges = SettingChange.None;
        }

        if (_initialRetriesRemaining > 0 && !_initialRetryScheduled)
        {
            ScheduleInitialRetry();
            return;
        }

        CompleteFailure(e);
    }

    private void ScheduleInitialRetry()
    {
        _initialRetryScheduled = true;
        var attempt = InitialRetryCount - _initialRetriesRemaining + 1;
        _initialRetriesRemaining--;
        var delay = TimeSpan.FromSeconds(1 << (attempt - 1));

        _lastOperation =
            $"桌面分身首次连接失败,{delay.TotalSeconds:0} 秒后自动重试({attempt}/{InitialRetryCount})";
        RefreshState();

        _ = RetryConnectAsync(delay, attempt, InitialRetryCount, isInitial: true);
    }

    private void ScheduleSettingsReconnect()
    {
        _settingsRetryScheduled = true;
        var attempt = SettingsReconnectRetryCount - _settingsRetriesRemaining + 1;
        _settingsRetriesRemaining--;
        var delay = TimeSpan.FromSeconds(1 << attempt);

        _lastOperation =
            $"设置切换后自动重连失败,{delay.TotalSeconds:0} 秒后重试({attempt}/{SettingsReconnectRetryCount})";
        RefreshState();

        _ = RetryConnectAsync(delay, attempt, SettingsReconnectRetryCount, isInitial: false);
    }

    private async Task RetryConnectAsync(TimeSpan delay, int attempt, int total, bool isInitial)
    {
        try
        {
            await Task.Delay(delay);
        }
        finally
        {
            if (isInitial)
            {
                _initialRetryScheduled = false;
            }
            else
            {
                _settingsRetryScheduled = false;
            }
        }

        if (_disposed)
        {
            return;
        }

        _lastOperation = $"正在重试桌面分身连接({attempt}/{total})";
        RefreshState();
        try
        {
            _rdpHost.ReconnectToChildSession(ResolveDesktopSize());
        }
        catch (Exception exception)
        {
            // 交回统一失败处理,走剩余重试或最终收尾。
            OnConnectionFailed(this, new RdpConnectionFailureEventArgs(
                $"重试桌面分身连接失败:{exception.GetBaseException().Message}", 0));
        }
    }

    private void CompleteFailure(RdpConnectionFailureEventArgs e)
    {
        var message = e.Message;

        if (ChildSessionApi.TryGetChildSessionId() is null)
        {
            message +=
                "\n\n诊断:当前没有分身会话 —— 系统未能创建会话与凭据未通过验证都会得到该结果,"
                + "请结合上方错误信息判断。";
        }

        var issueSummary = EnvironmentCheck.FormatSummary(_lastIssues);
        if (!string.IsNullOrEmpty(issueSummary))
        {
            message += "\n\n" + issueSummary;
        }

        _lastFailure = new RdpConnectionFailureEventArgs(message, e.ErrorCode, e.ExtendedErrorCode);
        _initialRetriesRemaining = 0;
        var completion = _startCompletion;
        _startCompletion = null;
        completion?.TrySetResult(false);
        _lastOperation = message.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0];
        RefreshState();
    }

    private bool ReconnectForSettingChange(SettingChange change, string operationMessage)
    {
        RefreshState();
        if (_rdpHost.ConnectedState == 0 && ChildSessionId is null)
        {
            _lastOperation = $"{operationMessage}(下次连接后生效)";
            RefreshState();
            return false;
        }

        _pendingChanges |= change;
        _settingsRetriesRemaining = SettingsReconnectRetryCount;
        _rdpHost.ReconnectToChildSession(ResolveDesktopSize());
        _lastOperation = $"{operationMessage},正在自动重连…";
        RefreshState();
        return true;
    }

    private void OnStatusTimerTick(object? sender, EventArgs e)
    {
        if (_statusTickBusy || _disposed)
        {
            return;
        }

        _statusTickBusy = true;
        try
        {
            RefreshState();
        }
        catch (Exception exception) when (exception is Win32Exception
                                              or COMException
                                              or InvalidOperationException)
        {
            // 个别状态读取失败不影响整体运行,跳过本轮。
        }
        finally
        {
            _statusTickBusy = false;
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    #endregion

    #region 向分身启动程序

    private static void LaunchViaTemporaryTask(uint sessionId, string executablePath)
    {
        var actualSessionId = ChildSessionApi.TryGetChildSessionId();
        if (actualSessionId != sessionId)
        {
            throw new InvalidOperationException(
                $"目标分身会话已变化:请求 {sessionId},当前 {actualSessionId?.ToString() ?? "无"}。");
        }

        var schedulerType = Type.GetTypeFromProgID("Schedule.Service")
            ?? throw new InvalidOperationException("系统未提供任务计划程序 COM 服务。");
        var taskName = $"DesktopClone-Launch-{Guid.NewGuid():N}";
        string accountName;
        using (var identity = WindowsIdentity.GetCurrent())
        {
            accountName = identity.Name;
        }

        object? schedulerObject = null;
        object? rootFolderObject = null;
        object? taskDefinitionObject = null;
        object? actionObject = null;
        object? registeredTaskObject = null;
        object? runningTaskObject = null;
        var taskRegistered = false;

        try
        {
            schedulerObject = Activator.CreateInstance(schedulerType)
                ?? throw new InvalidOperationException("无法创建任务计划程序 COM 对象。");
            dynamic scheduler = schedulerObject;
            scheduler.Connect();

            rootFolderObject = scheduler.GetFolder("\\");
            dynamic rootFolder = rootFolderObject;
            taskDefinitionObject = scheduler.NewTask(0);
            dynamic taskDefinition = taskDefinitionObject;

            taskDefinition.RegistrationInfo.Author = "DesktopClone";
            taskDefinition.RegistrationInfo.Description =
                $"临时在分身会话 {sessionId} 中启动 {Path.GetFileName(executablePath)}";
            taskDefinition.Settings.Enabled = true;
            taskDefinition.Settings.Hidden = true;
            taskDefinition.Settings.AllowDemandStart = true;
            taskDefinition.Settings.DisallowStartIfOnBatteries = false;
            taskDefinition.Settings.StopIfGoingOnBatteries = false;
            taskDefinition.Settings.ExecutionTimeLimit = "PT0S";
            taskDefinition.Principal.UserId = accountName;
            taskDefinition.Principal.LogonType = 3;   // TASK_LOGON_INTERACTIVE_TOKEN
            taskDefinition.Principal.RunLevel = 1;    // TASK_RUNLEVEL_HIGHEST

            actionObject = taskDefinition.Actions.Create(0);   // TASK_ACTION_EXEC
            dynamic action = actionObject;
            action.Path = executablePath;
            action.WorkingDirectory = Path.GetDirectoryName(executablePath)
                                      ?? Environment.CurrentDirectory;

            registeredTaskObject = rootFolder.RegisterTaskDefinition(
                taskName,
                taskDefinitionObject,
                2,            // TASK_CREATE
                accountName,
                null,
                3,            // TASK_LOGON_INTERACTIVE_TOKEN
                null);
            taskRegistered = true;

            dynamic registeredTask = registeredTaskObject;
            runningTaskObject = registeredTask.RunEx(
                null,
                0x4,      // TASK_RUN_USE_SESSION:在指定会话中启动
                unchecked((int)sessionId),
                null);
            if (runningTaskObject is null)
            {
                throw new InvalidOperationException("任务计划程序没有返回运行实例。");
            }
        }
        finally
        {
            if (taskRegistered && rootFolderObject is not null)
            {
                try
                {
                    dynamic rootFolder = rootFolderObject;
                    rootFolder.DeleteTask(taskName, 0);
                }
                catch (COMException)
                {
                    // 任务清理失败不应影响已经启动的目标程序。
                }
            }

            foreach (var comObject in new[]
                     {
                         runningTaskObject, registeredTaskObject, actionObject,
                         taskDefinitionObject, rootFolderObject, schedulerObject
                     })
            {
                if (comObject is not null && Marshal.IsComObject(comObject))
                {
                    Marshal.FinalReleaseComObject(comObject);
                }
            }
        }
    }

    #endregion
}
