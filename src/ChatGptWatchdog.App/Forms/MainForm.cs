using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ChatGptWatchdog.Core;

namespace ChatGptWatchdog.App.Forms;

/// <summary>Main window: status, controls and live log. Lives in the tray when hidden.</summary>
internal sealed class MainForm : Form
{
    private readonly Services _svc;
    private readonly bool _startMinimized;
    private bool _reallyExit;

    private readonly Panel _dot = new() { Size = new Size(18, 18), Margin = new Padding(0, 6, 8, 0) };
    private readonly Label _statusLabel = new() { AutoSize = true, Font = new Font("Segoe UI", 14f, FontStyle.Bold) };
    private readonly Label _detailLabel = new() { AutoSize = true, Font = new Font("Segoe UI", 9.5f) };
    private readonly Label _statsLabel = new() { AutoSize = true, Font = new Font("Segoe UI", 9f) };
    private readonly ListView _log = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
        Font = new Font("Cascadia Mono", 9f), BorderStyle = BorderStyle.None, HideSelection = false,
    };
    private readonly Button _btnStartStop = MakeButton("Stop watching");
    private readonly Button _btnPause = MakeButton("Pause 30 min");
    private readonly Button _btnResumeWatching = MakeButton("Resume watching");
    private readonly Button _btnCheck = MakeButton("Check now");
    private readonly Button _btnSettings = MakeButton("Settings…");
    private readonly Button _btnTest = MakeButton("Test resume…");
    private readonly Button _btnDiag = MakeButton("Diagnostics");
    private readonly Button _btnLogs = MakeButton("Open log folder");
    private readonly Button _btnCrash = MakeButton("Simulate crash");
    private readonly CheckBox _chkDebug = new() { Text = "Show debug", AutoSize = true, Margin = new Padding(12, 9, 3, 3) };
    private readonly NotifyIcon _tray = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly ToolStripMenuItem _trayPause = new("Pause 30 minutes");
    private readonly ToolStripMenuItem _trayResume = new("Resume watching");
    private readonly EventWaitHandle _showEvent;
    private StatusSnapshot? _last;
    private WatchdogStatus? _lastTrayStatus;

    private readonly Icon _icoApp, _icoOk, _icoWarn, _icoError, _icoOff;

    public MainForm(Services svc, bool startMinimized)
    {
        _svc = svc;
        _startMinimized = startMinimized || svc.Settings.StartMinimized;

        _icoApp = LoadIcon("app.ico");
        _icoOk = LoadIcon("tray-ok.ico");
        _icoWarn = LoadIcon("tray-warn.ico");
        _icoError = LoadIcon("tray-error.ico");
        _icoOff = LoadIcon("tray-off.ico");

        Text = "ChatGPT APP watchdog";
        Icon = _icoApp;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(900, 560);
        MinimumSize = new Size(680, 380);
        Font = new Font("Segoe UI", 9f);

        BuildLayout();
        BuildTray();

        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) =>
        {
            Ui(ShowFromTray);
        }, null, Timeout.Infinite, false);

        foreach (var e in _svc.Log.Recent) AddLogRow(e);
        _svc.Log.EntryAdded += e => { if (IsHandleCreated) Ui(() => AddLogRow(e)); };
        _svc.Engine.StatusChanged += s => { if (IsHandleCreated) Ui(() => ShowStatus(s)); };
        _svc.Engine.Notify += (title, text) => { if (IsHandleCreated) Ui(() => Balloon(title, text)); };
        _svc.Resume.Notify += text => { if (IsHandleCreated && _svc.Settings.ShowNotifications) Ui(() => Balloon("ChatGPT APP watchdog", text)); };

        _tick.Tick += (_, _) => RefreshStats();
        _tick.Start();
    }

    /// <summary>Runs an action on the UI thread (no-op if the window is gone).</summary>
    private void Ui(Action a)
    {
        try { if (IsHandleCreated && !IsDisposed) BeginInvoke(a); } catch { }
    }

    private static Icon LoadIcon(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        return s != null ? new Icon(s) : SystemIcons.Application;
    }

    private static Button MakeButton(string text) => new()
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(6, 2, 6, 2),
        Margin = new Padding(3, 3, 3, 3), FlatStyle = FlatStyle.System,
    };

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(14, 12, 14, 6) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _dot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(StatusColor(_last?.Status ?? WatchdogStatus.Stopped));
            e.Graphics.FillEllipse(b, 1, 1, _dot.Width - 3, _dot.Height - 3);
        };
        var texts = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        texts.Controls.Add(_statusLabel);
        texts.Controls.Add(_detailLabel);
        texts.Controls.Add(_statsLabel);
        header.Controls.Add(_dot, 0, 0);
        header.Controls.Add(texts, 1, 0);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10, 2, 10, 6), WrapContents = true };
        buttons.Controls.AddRange(new Control[] { _btnStartStop, _btnPause, _btnResumeWatching, _btnCheck, _btnSettings, _btnTest, _btnDiag, _btnLogs, _btnCrash, _chkDebug });

        _log.Columns.Add("Time", 140);
        _log.Columns.Add("Level", 60);
        _log.Columns.Add("Message", 640);
        _log.Resize += (_, _) => _log.Columns[2].Width = Math.Max(200, _log.ClientSize.Width - 200 - SystemInformation.VerticalScrollBarWidth);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Copy selected", null, (_, _) => CopySelected());
        menu.Items.Add("Clear view", null, (_, _) => _log.Items.Clear());
        _log.ContextMenuStrip = menu;
        _log.KeyDown += (_, e) => { if (e.Control && e.KeyCode == Keys.C) CopySelected(); };

        var status = new StatusStrip();
        status.Items.Add(new ToolStripStatusLabel($"v{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}  ·  data: {AppPaths.DataDirectory}{(AppPaths.IsPortable ? " (portable)" : "")}")
            { Spring = true, TextAlign = ContentAlignment.MiddleLeft });

        var logHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 6) };
        logHost.Controls.Add(_log);
        Controls.Add(logHost);
        Controls.Add(buttons);
        Controls.Add(header);
        Controls.Add(status);

        _btnStartStop.Click += (_, _) =>
        {
            if (_svc.Engine.IsWatching) _svc.Engine.Stop(); else _svc.Engine.Start();
            UpdateButtons();
        };
        _btnPause.Click += (_, _) => _svc.Engine.Pause(TimeSpan.FromMinutes(30));
        _btnResumeWatching.Click += (_, _) => { _svc.Engine.ResumeWatching(); UpdateButtons(); };
        _btnCheck.Click += (_, _) => { if (_svc.Engine.IsWatching) _svc.Engine.CheckNow(); else _svc.Log.Info("Not watching — click Start watching first."); };
        _btnSettings.Click += (_, _) => OpenSettings();
        _btnTest.Click += (_, _) => new TestResumeForm(_svc).Show(this);
        _btnDiag.Click += async (_, _) => await RunDiagnosticsAsync();
        _btnLogs.Click += (_, _) => Process.Start(new ProcessStartInfo(_svc.Log.LogDirectory) { UseShellExecute = true });
        _btnCrash.Click += (_, _) => SimulateCrash();
        _chkDebug.CheckedChanged += (_, _) => { _log.Items.Clear(); foreach (var e in _svc.Log.Recent) AddLogRow(e); };
    }

    private void BuildTray()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show", null, (_, _) => ShowFromTray());
        menu.Items.Add("Check now", null, (_, _) => _svc.Engine.CheckNow());
        _trayPause.Click += (_, _) => _svc.Engine.Pause(TimeSpan.FromMinutes(30));
        _trayResume.Click += (_, _) => _svc.Engine.ResumeWatching();
        menu.Items.Add(_trayPause);
        menu.Items.Add(_trayResume);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { _reallyExit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.Icon = _icoOff;
        _tray.Text = "ChatGPT APP watchdog";
        _tray.Visible = true;
        _tray.DoubleClick += (_, _) => ShowFromTray();
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
    }

    // ------------------------------------------------------------------ lifecycle

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_startMinimized)
        {
            WindowState = FormWindowState.Minimized;
            ShowInTaskbar = false;
            Ui(Hide);
        }
        _svc.Log.Info($"ChatGPT APP watchdog started (data folder: {AppPaths.DataDirectory}).");
        if (_svc.Settings.Resume.Enabled)
            _svc.Log.Info("Resume after crash: ON — " + string.Join(" → ", _svc.Settings.Resume.Methods.Select(Core.Resume.ResumeMethodInfo.DisplayName)));
        if (_svc.Settings.StartWatchingOnLaunch) _svc.Engine.Start();
        else ShowStatus(_svc.Engine.Snapshot());
        UpdateButtons();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && _svc.Settings.CloseToTray)
        {
            Hide();
            ShowInTaskbar = false;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing && _svc.Settings.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            Balloon("Still watching", "The watchdog keeps running in the tray. Right-click the icon to exit.", once: true);
            return;
        }
        _tick.Stop();
        _svc.Log.Info("ChatGPT APP watchdog exiting.");
        _svc.Engine.Stop();
        _tray.Visible = false;
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _tick.Dispose();
            _showEvent.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private bool _balloonShownOnce;
    private void Balloon(string title, string text, bool once = false)
    {
        if (once && _balloonShownOnce) return;
        if (once) _balloonShownOnce = true;
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.ShowBalloonTip(5000);
    }

    // ------------------------------------------------------------------ status & log

    private static Color StatusColor(WatchdogStatus s) => s switch
    {
        WatchdogStatus.Running => Color.FromArgb(16, 163, 127),
        WatchdogStatus.Restarting or WatchdogStatus.Resuming or WatchdogStatus.Paused => Color.FromArgb(217, 140, 0),
        WatchdogStatus.CrashLoop => Color.FromArgb(205, 45, 45),
        WatchdogStatus.ClosedByUser => Color.FromArgb(120, 120, 220),
        _ => Color.Gray,
    };

    private static string StatusTitle(WatchdogStatus s) => s switch
    {
        WatchdogStatus.Running => "Watching — ChatGPT is running",
        WatchdogStatus.Restarting => "Restarting ChatGPT",
        WatchdogStatus.Resuming => "Resuming interrupted chats",
        WatchdogStatus.Paused => "Paused",
        WatchdogStatus.ClosedByUser => "ChatGPT closed (not restarted)",
        WatchdogStatus.CrashLoop => "Crash loop — restarts stopped",
        _ => "Not watching",
    };

    private void ShowStatus(StatusSnapshot s)
    {
        _last = s;
        _statusLabel.Text = StatusTitle(s.Status);
        _detailLabel.Text = s.Detail;
        _dot.Invalidate();
        RefreshStats();
        UpdateButtons();

        if (_lastTrayStatus != s.Status)
        {
            _lastTrayStatus = s.Status;
            _tray.Icon = s.Status switch
            {
                WatchdogStatus.Running => _icoOk,
                WatchdogStatus.CrashLoop => _icoError,
                WatchdogStatus.Stopped => _icoOff,
                _ => _icoWarn,
            };
        }
        var tip = $"ChatGPT watchdog: {StatusTitle(s.Status)}";
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    private void RefreshStats()
    {
        var s = _last ?? _svc.Engine.Snapshot();
        var parts = new System.Collections.Generic.List<string>
        {
            $"Restarts today: {s.RestartsToday}",
            $"total: {s.TotalRestarts}",
        };
        if (s.LastCrashUtc != null) parts.Add($"last stop: {s.LastCrashUtc.Value.ToLocalTime():dd/MM HH:mm}");
        if (s.AppStartTime != null && s.Status == WatchdogStatus.Running)
        {
            var up = DateTime.Now - s.AppStartTime.Value;
            parts.Add($"app uptime: {(up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes}m" : $"{up.Minutes}m {up.Seconds}s")}");
        }
        if (_svc.Engine.IsWatching && s.NextCheck != null && !_svc.Engine.IsBusy)
        {
            var left = s.NextCheck.Value - DateTime.Now;
            parts.Add($"next check in {Math.Max(0, (int)left.TotalSeconds)}s");
        }
        parts.Add($"resume: {(_svc.Settings.Resume.Enabled ? "on" : "off")}");
        _statsLabel.Text = string.Join("  ·  ", parts);
    }

    private void UpdateButtons()
    {
        var watching = _svc.Engine.IsWatching;
        _btnStartStop.Text = watching ? "Stop watching" : "Start watching";
        var st = _last?.Status;
        var canResume = st is WatchdogStatus.Paused or WatchdogStatus.CrashLoop or WatchdogStatus.ClosedByUser;
        _btnResumeWatching.Visible = canResume;
        _btnPause.Visible = watching && !canResume;
        _btnCheck.Enabled = watching;
        _trayPause.Enabled = watching && !canResume;
        _trayResume.Enabled = canResume || !watching;
    }

    private void AddLogRow(LogEntry e)
    {
        if (e.Level == LogLevel.Debug && !_chkDebug.Checked) return;
        var item = new ListViewItem(new[] { e.Time.ToString("dd/MM HH:mm:ss"), Logger.LevelTag(e.Level).Trim(), e.Message });
        item.ForeColor = e.Level switch
        {
            LogLevel.Error => Color.FromArgb(220, 50, 50),
            LogLevel.Warning => Color.FromArgb(205, 130, 0),
            LogLevel.Success => Color.FromArgb(16, 150, 110),
            LogLevel.Debug => Color.Gray,
            _ => _log.ForeColor,
        };
        _log.BeginUpdate();
        _log.Items.Add(item);
        while (_log.Items.Count > 2000) _log.Items.RemoveAt(0);
        _log.EndUpdate();
        item.EnsureVisible();
    }

    private void CopySelected()
    {
        var lines = _log.SelectedItems.Cast<ListViewItem>()
            .Select(i => $"{i.SubItems[0].Text} [{i.SubItems[1].Text}] {i.SubItems[2].Text}");
        var text = string.Join(Environment.NewLine, lines);
        if (text.Length > 0) Clipboard.SetText(text);
    }

    // ------------------------------------------------------------------ actions

    private void OpenSettings()
    {
        using var f = new SettingsForm(_svc.Settings.Clone(), AutoStart.IsEnabled());
        if (f.ShowDialog(this) == DialogResult.OK)
        {
            _svc.ApplySettings(f.Result);
            RefreshStats();
        }
    }

    private async Task RunDiagnosticsAsync()
    {
        _btnDiag.Enabled = false;
        _svc.Log.Info("Collecting diagnostics...");
        try
        {
            var report = await Task.Run(() => DiagnosticsReport.Build(_svc.Settings, _svc.App, _svc.Sessions));
            var file = Path.Combine(_svc.Log.LogDirectory, $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, report);
            _svc.Log.Info($"Diagnostics saved to {file}");
            new TextViewForm("Diagnostics", report, file).Show(this);
        }
        catch (Exception ex) { _svc.Log.Error("Diagnostics failed: " + ex.Message); }
        finally { _btnDiag.Enabled = true; }
    }

    private void SimulateCrash()
    {
        var r = MessageBox.Show(this,
            "This kills the ChatGPT app right now, exactly like a crash, so you can see the watchdog restart it (and resume chats if that is enabled).\n\nAny running chat in the app will be interrupted. Continue?",
            "Simulate crash", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) return;
        if (!_svc.Engine.IsWatching) _svc.Engine.Start();
        var n = _svc.Engine.SimulateCrash();
        _svc.Log.Info($"Killed {n} ChatGPT process(es).");
    }
}
