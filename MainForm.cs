using System.Runtime.InteropServices;
using DesktopClone.Interop;
using DesktopClone.Services;

namespace DesktopClone;

/// <summary>
/// 主界面:工具栏 + RDP 画面区 + 状态栏。
/// </summary>
internal sealed class MainForm : Form
{
    private const int MaxRecentPrograms = 5;
    private const int DefaultDisplayWidth = 1280;
    private const int DefaultDisplayHeight = 720;
    private const int DefaultWindowMargin = 32;
    private const int WmHotkey = 0x0312;
    private const int TopmostHotkeyId = 0x4D01;
    private const int BottomHotkeyId = 0x4D02;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private static readonly Size[] PresetResolutions =
    [
        new(1280, 720),
        new(1600, 900),
        new(1920, 1080),
        new(2560, 1440),
        new(3840, 2160)
    ];

    private readonly AppSettings _settings;
    private readonly RdpClientHost _rdpHost = null!;
    private readonly ChildSessionManager _manager = null!;
    private readonly string? _hostCreationError;

    private readonly NotifyIcon _trayIcon;
    private readonly ContextMenuStrip _trayMenu;
    private readonly Icon _trayIconImage;
    private bool _inTrayMode;
    private bool _handlingMinimize;
    private bool _trayHintShown;
    private bool _bottomMost;
    private bool _hotkeysInitialized;
    private bool _formShown;
    private string? _pendingHotkeyWarning;
    private Rectangle _trayRestoreBounds;
    private FormWindowState _trayRestoreState = FormWindowState.Normal;
    private FormWindowState _lastNonMinimizedState = FormWindowState.Normal;

    private readonly ToolStrip _toolStrip = new()
    {
        GripStyle = ToolStripGripStyle.Hidden,
        Padding = new Padding(6, 2, 6, 2),
        ImageScalingSize = new Size(16, 16)
    };

    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _statusLabel = new()
    {
        Spring = true,
        TextAlign = ContentAlignment.MiddleLeft
    };

    private readonly ToolStripButton _startButton = new("启动分身")
    {
        ToolTipText = "创建/附着分身会话并连接画面",
        Enabled = false
    };

    private readonly ToolStripButton _disconnectButton = new("断开画面")
    {
        ToolTipText = "断开画面连接,分身会话保留在后台运行",
        Enabled = false
    };

    private readonly ToolStripButton _logoffButton = new("注销分身")
    {
        ToolTipText = "断开连接并注销分身会话,其中所有程序都会被结束",
        Enabled = false
    };

    private readonly ToolStripSplitButton _recentButton = new("启动程序…")
    {
        ToolTipText = "把 exe 以管理员权限启动到分身会话。\n"
                      + "注意:分身内直接双击需要管理员权限的程序会失败(Windows 子会话不支持 UAC 提权),"
                      + "这类程序请统一用本按钮启动。",
        Enabled = false
    };

    private readonly ToolStripButton _watchButton = new("观看模式")
    {
        CheckOnClick = true,
        ToolTipText = "屏蔽本机键鼠进入分身(画面照常渲染)"
    };

    private readonly ToolStripButton _muteButton = new("分身静音")
    {
        CheckOnClick = true,
        ToolTipText = "关闭分身侧的音频(切换会触发自动重连)"
    };

    private readonly ToolStripButton _shortcutsButton = new("组合键→分身")
    {
        CheckOnClick = true,
        ToolTipText = "Win 系组合键作用于分身而非本机(切换会触发自动重连)"
    };

    private readonly ToolStripButton _winDButton = new("Win+D")
    {
        ToolTipText = "向分身发送「显示桌面」",
        Enabled = false
    };

    private readonly ToolStripButton _winTabButton = new("Win+Tab")
    {
        ToolTipText = "向分身发送「任务视图」",
        Enabled = false
    };

    private readonly ToolStripButton _topmostButton = new("窗口置顶")
    {
        CheckOnClick = true
    };

    private readonly ToolStripButton _bottomButton = new("窗口置底")
    {
        CheckOnClick = true,
        ToolTipText = "让窗口保持在其他窗口下方,不会被弹到最前(与窗口置顶互斥)"
    };

    private readonly ToolStripButton _sizingButton = new("自适应缩放")
    {
        CheckOnClick = true,
        ToolTipText = "画面缩放到窗口大小;关闭则 1:1 显示"
    };

    private readonly ToolStripDropDownButton _resolutionButton = new("分辨率")
    {
        ToolTipText = "分身桌面分辨率,默认跟随本机主显示器的原始分辨率;改动会触发自动重连"
    };

    private readonly ToolStripButton _aboutButton = new("关于")
    {
        ToolTipText = "查看版本、运行环境与项目信息"
    };

    private readonly ToolStripDropDownButton _hotkeyButton = new("快捷键")
    {
        ToolTipText = "设置置顶/置底全局快捷键"
    };

    public MainForm()
    {
        _settings = AppSettings.Load();

        _trayIconImage = (Icon)SystemIcons.Application.Clone();
        _trayMenu = new ContextMenuStrip();
        var showFromTrayItem = new ToolStripMenuItem("显示主窗口");
        showFromTrayItem.Click += (_, _) => ExitTrayMode();
        _trayMenu.Items.Add(showFromTrayItem);
        _trayMenu.Items.Add(new ToolStripSeparator());
        var exitFromTrayItem = new ToolStripMenuItem("退出");
        exitFromTrayItem.Click += (_, _) => ExitFromTray();
        _trayMenu.Items.Add(exitFromTrayItem);
        _trayIcon = new NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "桌面分身 DesktopClone",
            ContextMenuStrip = _trayMenu,
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) => ExitTrayMode();

        Text = "桌面分身 DesktopClone";
        StartPosition = FormStartPosition.Manual;
        Size = new Size(_settings.WindowWidth, _settings.WindowHeight);
        MinimumSize = new Size(480, 320);
        Font = new Font("Microsoft YaHei UI", 9f);

        // RDP ActiveX 宿主:构造失败(缺控件等)时降级为提示,程序其余部分可用。
        try
        {
            _rdpHost = new RdpClientHost();
        }
        catch (Exception exception)
        {
            _hostCreationError = exception.Message;
            _rdpHost = null!;
        }

        if (_rdpHost is not null)
        {
            _manager = new ChildSessionManager(_rdpHost, _settings);
            _manager.StateChanged += (_, _) => UpdateUi();

            var hostPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Black };
            _rdpHost.Dock = DockStyle.Fill;
            hostPanel.Controls.Add(_rdpHost);
            Controls.Add(hostPanel);
        }
        else
        {
            _manager = null!;
        }

        BuildToolBar();
        _statusStrip.Items.Add(_statusLabel);
        Controls.Add(_toolStrip);
        Controls.Add(_statusStrip);

        Load += OnLoad;
        Shown += OnShownHandler;
        FormClosing += OnFormClosingHandler;
        FormClosed += OnFormClosedHandler;
    }

    private void BuildToolBar()
    {
        _startButton.Click += OnStartClicked;
        _disconnectButton.Click += OnDisconnectClicked;
        _logoffButton.Click += OnLogoffClicked;
        _recentButton.ButtonClick += OnLaunchProgramClicked;
        _recentButton.DropDownItemClicked += OnRecentItemClicked;
        _watchButton.CheckedChanged += (_, _) => SafeManagerCall(() =>
            _manager.SetWatchMode(_watchButton.Checked));
        _muteButton.CheckedChanged += (_, _) => SafeManagerCall(() =>
            _manager.SetAudioMuted(_muteButton.Checked));
        _shortcutsButton.CheckedChanged += (_, _) => SafeManagerCall(() =>
            _manager.SetShortcutsToRemote(_shortcutsButton.Checked));
        _winDButton.Click += (_, _) => SafeManagerCall(() => _manager.SendWinD());
        _winTabButton.Click += (_, _) => SafeManagerCall(() => _manager.SendWinTab());
        _topmostButton.CheckedChanged += OnTopmostChanged;
        _bottomButton.CheckedChanged += OnBottomMostChanged;
        _sizingButton.CheckedChanged += (_, _) => SafeManagerCall(() =>
            _manager.SetSmartSizing(_sizingButton.Checked));
        _resolutionButton.DropDownOpening += (_, _) => RebuildResolutionMenu();
        _hotkeyButton.DropDownOpening += (_, _) => RebuildHotkeyMenu();
        _aboutButton.Click += OnAboutClicked;

        _toolStrip.Items.AddRange(
        [
            _startButton,
            _disconnectButton,
            _logoffButton,
            new ToolStripSeparator(),
            _recentButton,
            new ToolStripSeparator(),
            _watchButton,
            _muteButton,
            _shortcutsButton,
            new ToolStripSeparator(),
            _winDButton,
            _winTabButton,
            new ToolStripSeparator(),
            _topmostButton,
            _bottomButton,
            _sizingButton,
            new ToolStripSeparator(),
            _resolutionButton,
            new ToolStripSeparator(),
            _hotkeyButton,
            _aboutButton
        ]);
        RebuildRecentMenu();
        RebuildResolutionMenu();
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        RestoreWindowBounds();

        _settings.Topmost &= !_settings.BottomMost;
        _bottomButton.Checked = _settings.BottomMost;
        _topmostButton.Checked = _settings.Topmost;
        TopMost = _topmostButton.Checked;
        ApplyHotkeySettings();
        _hotkeysInitialized = true;

        if (_manager is null)
        {
            _statusLabel.Text = $"RDP ActiveX 初始化失败:{_hostCreationError}";
            return;
        }

        _watchButton.Checked = _settings.WatchMode;
        _muteButton.Checked = _settings.AudioMuted;
        _shortcutsButton.Checked = _settings.ShortcutsToRemote;
        _sizingButton.Checked = _settings.SmartSizing;

        _manager.RefreshState();
        UpdateUi();

        if (_hostCreationError is null)
        {
            var issues = _manager.RunPrecheck();
            if (issues.Count > 0)
            {
                _statusLabel.Text = "环境预检发现问题,详见启动时的提示。";
            }
        }
    }

    private void OnShownHandler(object? sender, EventArgs e)
    {
        _formShown = true;
        if (_pendingHotkeyWarning is { } warning)
        {
            _pendingHotkeyWarning = null;
            MessageBox.Show(this, warning, "全局快捷键", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    protected override void WndProc(ref Message m)
    {
        const int WmSysCommand = 0x0112;
        const int ScMinimize = 0xF020;

        if (m.Msg == WmHotkey)
        {
            HandleHotkey(m.WParam.ToInt32());
            return;
        }

        // 置底模式下持续拦截 Z 序变更:点击、显示、激活都不能把窗口拉回最前。
        if (_bottomMost)
        {
            WindowZOrder.ForceBottom(ref m);
        }

        // 不真正最小化宿主窗口:RDP 控件一旦被最小化,Windows 会抑制子会话的输入/显示。
        if (m.Msg == WmSysCommand && ((int)m.WParam & 0xFFF0) == ScMinimize)
        {
            EnterTrayMode();
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // 窗口句柄重建后系统会丢弃已注册的热键,需要重新注册。
        if (_hotkeysInitialized)
        {
            ApplyHotkeySettings();
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);

        if (WindowState != FormWindowState.Minimized)
        {
            _lastNonMinimizedState = WindowState;
            return;
        }

        if (_handlingMinimize || _inTrayMode)
        {
            return;
        }

        // 兜底:Win+D 等路径可能绕过 WM_SYSCOMMAND,先把窗口恢复到非最小化再隐藏。
        _handlingMinimize = true;
        if (!IsHandleCreated || IsDisposed)
        {
            _handlingMinimize = false;
            return;
        }

        try
        {
            BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!IsDisposed && WindowState == FormWindowState.Minimized)
                    {
                        EnterTrayMode();
                    }
                }
                finally
                {
                    _handlingMinimize = false;
                }
            }));
        }
        catch (InvalidOperationException)
        {
            _handlingMinimize = false;
        }
    }

    private void EnterTrayMode()
    {
        if (_inTrayMode || IsDisposed)
        {
            return;
        }

        _inTrayMode = true;
        var wasMinimized = WindowState == FormWindowState.Minimized;
        _trayRestoreState = wasMinimized ? _lastNonMinimizedState : WindowState;
        _trayRestoreBounds = wasMinimized || _trayRestoreState == FormWindowState.Maximized
            ? RestoreBounds
            : Bounds;

        if (wasMinimized)
        {
            // 先退出最小化状态,避免 RDP 控件继续停留在 SIZE_MINIMIZED。
            WindowState = _trayRestoreState;
        }

        SaveWindowBounds();
        Hide();
        _trayIcon.Visible = true;

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _trayIcon.ShowBalloonTip(
                3000,
                "桌面分身",
                "已最小化到托盘，分身继续在后台运行。双击图标可恢复窗口。",
                ToolTipIcon.Info);
        }
    }

    private void ExitTrayMode()
    {
        if (!_inTrayMode || IsDisposed)
        {
            return;
        }

        _inTrayMode = false;
        Show();
        WindowState = FormWindowState.Normal;
        Bounds = _trayRestoreBounds;
        if (_trayRestoreState == FormWindowState.Maximized)
        {
            WindowState = FormWindowState.Maximized;
        }

        _trayIcon.Visible = false;
        Activate();
        BringToFront();
    }

    private void ExitFromTray()
    {
        ExitTrayMode();
        Close();
    }

    private async void OnStartClicked(object? sender, EventArgs e)
    {
        var issues = _manager.RunPrecheck();
        if (issues.Count > 0)
        {
            var summary = EnvironmentCheck.FormatSummary(issues);
            var choice = MessageBox.Show(
                this,
                $"环境预检发现问题,可能导致启动失败:\n\n{summary}\n\n仍要继续启动吗?",
                "环境预检",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (choice != DialogResult.Yes)
            {
                return;
            }
        }

        _startButton.Enabled = false;
        try
        {
            var started = await _manager.StartAsync();
            if (!started)
            {
                MessageBox.Show(
                    this,
                    _manager.LastFailureMessage ?? "桌面分身启动失败,原因未知。",
                    "启动失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"启动失败:{exception.Message}",
                "启动失败",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        finally
        {
            UpdateUi();
        }
    }

    private void OnDisconnectClicked(object? sender, EventArgs e)
    {
        SafeManagerCall(() => _manager.Disconnect());
        UpdateUi();
    }

    private async void OnLogoffClicked(object? sender, EventArgs e)
    {
        var sessionId = _manager.ChildSessionId;
        var choice = MessageBox.Show(
            this,
            "注销分身会结束其中所有正在运行的程序,未保存的数据会丢失。是否继续?",
            "注销桌面分身",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (choice != DialogResult.Yes)
        {
            return;
        }

        try
        {
            await _manager.LogoffAsync(sessionId);
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"注销失败:{exception.Message}",
                "注销桌面分身",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        UpdateUi();
    }

    private async void OnLaunchProgramClicked(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择要在分身中启动的程序",
            Filter = "程序 (*.exe)|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await LaunchAndRecordAsync(dialog.FileName);
    }

    private async void OnRecentItemClicked(object? sender, ToolStripItemClickedEventArgs e)
    {
        if (e.ClickedItem?.Tag is not string path)
        {
            return;
        }

        await LaunchAndRecordAsync(path);
    }

    private async Task LaunchAndRecordAsync(string path)
    {
        try
        {
            await _manager.LaunchProgramAsync(path);
            _settings.RecentPrograms.Remove(path);
            _settings.RecentPrograms.Insert(0, path);
            if (_settings.RecentPrograms.Count > MaxRecentPrograms)
            {
                _settings.RecentPrograms.RemoveRange(MaxRecentPrograms, _settings.RecentPrograms.Count - MaxRecentPrograms);
            }

            _settings.Save();
            RebuildRecentMenu();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"启动失败:{exception.Message}",
                "启动程序",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }

        UpdateUi();
    }

    private void OnTopmostChanged(object? sender, EventArgs e)
    {
        if (_topmostButton.Checked && _bottomButton.Checked)
        {
            // 置顶与置底互斥:先解除置底,否则窗口仍会被压回最底层。
            _bottomButton.Checked = false;
        }

        TopMost = _topmostButton.Checked;
        _settings.Topmost = _topmostButton.Checked;
        _settings.Save();
    }

    private void OnAboutClicked(object? sender, EventArgs e)
    {
        // 置顶窗口的 owned dialog 必须同样置顶,否则会被主窗口盖住而无法操作。
        var topMost = TopMost;
        using var dialog = new AboutDialog { TopMost = topMost };
        dialog.ShowDialog(this);
    }

    private void OnBottomMostChanged(object? sender, EventArgs e)
    {
        var enabled = _bottomButton.Checked;
        if (enabled && _topmostButton.Checked)
        {
            // 置顶与置底互斥:先取消置顶,避免 TopMost 把窗口重新拉到最前。
            _topmostButton.Checked = false;
        }

        _bottomMost = enabled;
        _settings.BottomMost = enabled;
        _settings.Save();

        if (enabled)
        {
            WindowZOrder.MoveToBottom(Handle);
        }
        else
        {
            WindowZOrder.MoveToTop(Handle);
        }
    }

    private void HandleHotkey(int hotkeyId)
    {
        if (!_settings.GlobalHotkeysEnabled)
        {
            return;
        }

        switch (hotkeyId)
        {
            case TopmostHotkeyId:
                _topmostButton.Checked = !_topmostButton.Checked;
                break;
            case BottomHotkeyId:
                _bottomButton.Checked = !_bottomButton.Checked;
                break;
        }
    }

    private void RebuildHotkeyMenu()
    {
        _hotkeyButton.DropDownItems.Clear();

        var enabledItem = new ToolStripMenuItem("启用全局快捷键")
        {
            Checked = _settings.GlobalHotkeysEnabled
        };
        enabledItem.Click += OnToggleGlobalHotkeys;
        _hotkeyButton.DropDownItems.Add(enabledItem);
        _hotkeyButton.DropDownItems.Add(new ToolStripSeparator());

        _hotkeyButton.DropDownItems.Add(new ToolStripMenuItem("快捷键方案")
        {
            Enabled = false
        });

        var firstSchemeItem = new ToolStripMenuItem("Ctrl+Alt+T(置顶) / Ctrl+Alt+B(置底)")
        {
            Checked = !_settings.HotkeyUseShift
        };
        firstSchemeItem.Click += (_, _) => ApplyHotkeyScheme(useShift: false);
        _hotkeyButton.DropDownItems.Add(firstSchemeItem);

        var secondSchemeItem = new ToolStripMenuItem("Ctrl+Shift+Alt+T(置顶) / Ctrl+Shift+Alt+B(置底)")
        {
            Checked = _settings.HotkeyUseShift
        };
        secondSchemeItem.Click += (_, _) => ApplyHotkeyScheme(useShift: true);
        _hotkeyButton.DropDownItems.Add(secondSchemeItem);

        _hotkeyButton.DropDownItems.Add(new ToolStripSeparator());
        var retryItem = new ToolStripMenuItem("重新注册快捷键")
        {
            Enabled = _settings.GlobalHotkeysEnabled
        };
        retryItem.Click += (_, _) => ApplyHotkeySettings();
        _hotkeyButton.DropDownItems.Add(retryItem);
    }

    private void OnToggleGlobalHotkeys(object? sender, EventArgs e)
    {
        _settings.GlobalHotkeysEnabled = !_settings.GlobalHotkeysEnabled;
        _settings.Save();
        ApplyHotkeySettings();
    }

    private void ApplyHotkeyScheme(bool useShift)
    {
        if (_settings.HotkeyUseShift == useShift)
        {
            return;
        }

        _settings.HotkeyUseShift = useShift;
        _settings.Save();
        ApplyHotkeySettings();
    }

    /// <summary>按当前设置注册全局热键;重复调用会先注销旧注册。</summary>
    private void ApplyHotkeySettings()
    {
        UnregisterHotkeys();
        UpdateHotkeyToolTips();

        if (!_settings.GlobalHotkeysEnabled)
        {
            _pendingHotkeyWarning = null;
            return;
        }

        var scheme = GetHotkeyScheme(_settings.HotkeyUseShift);
        if (!GlobalHotkey.TryRegister(
                Handle,
                TopmostHotkeyId,
                scheme.Modifiers,
                scheme.TopmostVirtualKey,
                out var errorCode))
        {
            ReportHotkeyFailure("置顶", scheme.TopmostText, errorCode);
            return;
        }

        if (!GlobalHotkey.TryRegister(
                Handle,
                BottomHotkeyId,
                scheme.Modifiers,
                scheme.BottommostVirtualKey,
                out errorCode))
        {
            GlobalHotkey.Unregister(Handle, TopmostHotkeyId);
            ReportHotkeyFailure("置底", scheme.BottommostText, errorCode);
            return;
        }

        _pendingHotkeyWarning = null;
    }

    private void UnregisterHotkeys()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        GlobalHotkey.Unregister(Handle, TopmostHotkeyId);
        GlobalHotkey.Unregister(Handle, BottomHotkeyId);
    }

    private void UpdateHotkeyToolTips()
    {
        var scheme = GetHotkeyScheme(_settings.HotkeyUseShift);
        _topmostButton.ToolTipText = _settings.GlobalHotkeysEnabled
            ? $"窗口置顶(全局快捷键 {scheme.TopmostText})"
            : "窗口置顶";
        _bottomButton.ToolTipText = _settings.GlobalHotkeysEnabled
            ? $"让窗口保持在其他窗口下方,不会被弹到最前(与窗口置顶互斥;全局快捷键 {scheme.BottommostText})"
            : "让窗口保持在其他窗口下方,不会被弹到最前(与窗口置顶互斥)";
    }

    private void ReportHotkeyFailure(string action, string hotkeyText, int errorCode)
    {
        var reason = errorCode == ErrorHotkeyAlreadyRegistered
            ? "该组合键已被其他程序占用"
            : $"系统返回错误码 {errorCode}";
        var message = $"全局快捷键 {hotkeyText}({action})注册失败:{reason}。\n\n"
                      + "可在工具栏「快捷键」菜单中改用 Ctrl+Shift+Alt+T/B,或关闭全局快捷键。";
        if (_formShown)
        {
            MessageBox.Show(this, message, "全局快捷键", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else
        {
            _pendingHotkeyWarning ??= message;
        }
    }

    private static HotkeyScheme GetHotkeyScheme(bool useShift)
    {
        const uint virtualKeyT = 0x54;
        const uint virtualKeyB = 0x42;

        return useShift
            ? new HotkeyScheme(
                GlobalHotkey.ModControl | GlobalHotkey.ModAlt | GlobalHotkey.ModShift,
                virtualKeyT,
                virtualKeyB,
                "Ctrl+Shift+Alt+T",
                "Ctrl+Shift+Alt+B")
            : new HotkeyScheme(
                GlobalHotkey.ModControl | GlobalHotkey.ModAlt,
                virtualKeyT,
                virtualKeyB,
                "Ctrl+Alt+T",
                "Ctrl+Alt+B");
    }

    private void RebuildRecentMenu()
    {
        _recentButton.DropDownItems.Clear();
        foreach (var path in _settings.RecentPrograms)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            _recentButton.DropDownItems.Add(path).Tag = path;
        }
    }

    private void RebuildResolutionMenu()
    {
        _resolutionButton.DropDownItems.Clear();

        var originalSize = ChildSessionManager.OriginalDesktopSize;
        var hasSelectedSize = _settings.DesktopWidth > 0 && _settings.DesktopHeight > 0;
        var selected = hasSelectedSize
            ? new Size(_settings.DesktopWidth, _settings.DesktopHeight)
            : (Size?)null;

        _resolutionButton.ToolTipText = selected is { } current
            ? $"分身桌面分辨率:{current.Width}×{current.Height};改动会触发自动重连"
            : $"分身桌面分辨率:原始分辨率({originalSize.Width}×{originalSize.Height},跟随本机主显示器);"
              + "改动会触发自动重连";

        var originalItem = new ToolStripMenuItem(
            $"原始分辨率({originalSize.Width}×{originalSize.Height})")
        {
            Checked = selected is null,
            ToolTipText = "跟随本机主显示器的当前分辨率"
        };
        originalItem.Click += (_, _) => ApplyResolution(null);
        _resolutionButton.DropDownItems.Add(originalItem);
        _resolutionButton.DropDownItems.Add(new ToolStripSeparator());

        foreach (var resolution in PresetResolutions)
        {
            var preset = resolution;
            var presetItem = new ToolStripMenuItem($"{preset.Width}×{preset.Height}")
            {
                Checked = selected == preset
            };
            presetItem.Click += (_, _) => ApplyResolution(preset);
            _resolutionButton.DropDownItems.Add(presetItem);
        }

        _resolutionButton.DropDownItems.Add(new ToolStripSeparator());
        var customItem = new ToolStripMenuItem("自定义…")
        {
            Checked = selected is { } custom && Array.IndexOf(PresetResolutions, custom) < 0
        };
        customItem.Click += OnCustomResolutionClicked;
        _resolutionButton.DropDownItems.Add(customItem);
    }

    private void ApplyResolution(Size? resolution)
    {
        if (_manager is not null)
        {
            SafeManagerCall(() => _manager.SetDesktopSize(resolution));
        }
        else
        {
            _settings.DesktopWidth = resolution?.Width ?? 0;
            _settings.DesktopHeight = resolution?.Height ?? 0;
        }

        _settings.Save();
        RebuildResolutionMenu();
    }

    private void OnCustomResolutionClicked(object? sender, EventArgs e)
    {
        var initialSize = _settings.DesktopWidth > 0 && _settings.DesktopHeight > 0
            ? new Size(_settings.DesktopWidth, _settings.DesktopHeight)
            : ChildSessionManager.OriginalDesktopSize;

        var topMost = TopMost;
        using var dialog = new ResolutionDialog(initialSize) { TopMost = topMost };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            ApplyResolution(dialog.SelectedSize);
        }
    }

    private void UpdateUi()
    {
        if (_manager is null)
        {
            return;
        }

        _statusLabel.Text = _manager.StatusText;
        var hasSession = _manager.ChildSessionId is not null;
        var connected = _manager.IsConnected;

        _startButton.Enabled = !connected;
        _disconnectButton.Enabled = connected;
        _logoffButton.Enabled = hasSession;
        _recentButton.Enabled = hasSession;
        _winDButton.Enabled = connected;
        _winTabButton.Enabled = connected;
    }

    private void SafeManagerCall(Action action)
    {
        if (_manager is null)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                              or COMException)
        {
            MessageBox.Show(this, exception.Message, "桌面分身", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        UpdateUi();
    }

    private void RestoreWindowBounds()
    {
        if (_settings.WindowX == -1 && _settings.WindowY == -1)
        {
            // 尚未保存过窗口位置:默认显示区域固定为 1280×720,
            // 窗口外框还会自动加上标题栏,ClientSize 再加工具栏和状态栏。
            // 默认离屏幕工作区左上角留 32px,避免完全贴边。
            var workingArea = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(
                workingArea.Left + DefaultWindowMargin,
                workingArea.Top + DefaultWindowMargin);
            WindowState = FormWindowState.Normal;
            ClientSize = new Size(
                DefaultDisplayWidth,
                DefaultDisplayHeight + _toolStrip.Height + _statusStrip.Height);
            return;
        }

        var bounds = new Rectangle(
            _settings.WindowX,
            _settings.WindowY,
            _settings.WindowWidth,
            _settings.WindowHeight);
        var virtualScreen = SystemInformation.VirtualScreen;
        if (bounds.IntersectsWith(virtualScreen))
        {
            StartPosition = FormStartPosition.Manual;
            Location = bounds.Location;
            Size = bounds.Size;
            if (_settings.WindowMaximized)
            {
                WindowState = FormWindowState.Maximized;
            }
        }
        else
        {
            StartPosition = FormStartPosition.CenterScreen;
        }
    }

    private void SaveWindowBounds()
    {
        if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowX = Location.X;
            _settings.WindowY = Location.Y;
            _settings.WindowWidth = Size.Width;
            _settings.WindowHeight = Size.Height;
        }

        _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        _settings.Save();
    }

    private void OnFormClosingHandler(object? sender, FormClosingEventArgs e)
    {
        if (_inTrayMode)
        {
            // 关闭确认框必须挂在可见的窗口上,否则可能看不见也点不到。
            ExitTrayMode();
        }

        if (_manager is null || e.CloseReason == CloseReason.ApplicationExitCall)
        {
            SaveWindowBounds();
            return;
        }

        if (_manager.ChildSessionId is null)
        {
            SaveWindowBounds();
            return;
        }

        var choice = MessageBox.Show(
            this,
            "分身会话仍在运行。\n\n"
            + "「是」- 断开画面并保留分身,分身里的程序继续在后台运行\n"
            + "「否」- 注销分身(结束其中所有程序)并退出\n"
            + "「取消」- 返回",
            "关闭桌面分身",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        if (choice == DialogResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (choice == DialogResult.Yes)
        {
            SaveWindowBounds();
            return;
        }

        // 注销后再关闭:先取消本次关闭,注销完成后重新 Close。
        e.Cancel = true;
        _ = LogoffThenCloseAsync();
    }

    private async Task LogoffThenCloseAsync()
    {
        try
        {
            await _manager.LogoffAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"注销失败:{exception.Message}",
                "注销桌面分身",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        SaveWindowBounds();
        Close();
    }

    private void OnFormClosedHandler(object? sender, FormClosedEventArgs e)
    {
        UnregisterHotkeys();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayMenu.Dispose();
        _trayIconImage.Dispose();
        _manager?.Dispose();
        _settings.Save();
    }

    private readonly record struct HotkeyScheme(
        uint Modifiers,
        uint TopmostVirtualKey,
        uint BottommostVirtualKey,
        string TopmostText,
        string BottommostText);
}
