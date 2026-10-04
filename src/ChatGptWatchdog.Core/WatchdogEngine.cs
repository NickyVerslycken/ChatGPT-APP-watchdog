using System.Diagnostics;
using ChatGptWatchdog.Core.Resume;

namespace ChatGptWatchdog.Core;

public enum WatchdogStatus
{
    Stopped,
    Running,
    Restarting,
    Resuming,
    Paused,
    ClosedByUser,
    CrashLoop,
}

public sealed record StatusSnapshot(
    WatchdogStatus Status,
    string Detail,
    int? MainPid,
    DateTime? AppStartTime,
    DateTime? LastCheck,
    DateTime? NextCheck,
    int RestartsToday,
    int TotalRestarts,
    DateTime? LastCrashUtc,
    DateTime? PausedUntil);

/// <summary>The monitoring loop: checks the app, restarts it, and triggers chat resume.</summary>
public sealed class WatchdogEngine : IDisposable
{
    private readonly Func<WatchdogSettings> _settings;
    private readonly Logger _log;
    private readonly TargetApp _app;
    private readonly CodexSessions _sessions;
    private readonly WatchdogState _state;
    private readonly ResumeCoordinator _resume;

    private readonly SemaphoreSlim _wake = new(0, int.MaxValue);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    private WatchdogStatus _status = WatchdogStatus.Stopped;
    private string _detail = "Not watching";
    private ProcInfo? _main;
    private Process? _exitWatch;
    private int _exitWatchPid;
    private DateTime? _lastSeenRunningUtc;
    private DateTime? _lastExitUtc;
    private DateTime? _hungSinceUtc;
    private DateTime? _pausedUntil;
    private DateTime? _lastCheck, _nextCheck;
    private bool _closedByUser;
    private bool _crashLoopHalted;
    private bool _busy;

    public event Action<StatusSnapshot>? StatusChanged;
    /// <summary>(title, text) for tray notifications.</summary>
    public event Action<string, string>? Notify;

    public WatchdogEngine(Func<WatchdogSettings> settings, Logger log, TargetApp app, CodexSessions sessions,
        WatchdogState state, ResumeCoordinator resume)
    {
        _settings = settings;
        _log = log;
        _app = app;
        _sessions = sessions;
        _state = state;
        _resume = resume;
    }

    public bool IsWatching => _loop != null && !_loop.IsCompleted;
    public bool IsBusy => _busy;

    public StatusSnapshot Snapshot() => new(_status, _detail, _main?.Pid, _main?.StartTime, _lastCheck, _nextCheck,
        _state.RestartsToday(), _state.TotalRestarts, _state.LastCrashUtc, _pausedUntil);

    private void SetStatus(WatchdogStatus status, string detail)
    {
        _status = status;
        _detail = detail;
        try { StatusChanged?.Invoke(Snapshot()); } catch { }
    }

    // ------------------------------------------------------------------ control

    public void Start()
    {
        if (IsWatching) return;
        _crashLoopHalted = false;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _log.Info($"Watching started (check every {_settings().CheckIntervalSeconds}s).");
        _loop = Task.Run(() => LoopAsync(token));
    }

    public void Stop()
    {
        if (!IsWatching) return;
        _cts?.Cancel();
        try { _loop?.Wait(3000); } catch { }
        _loop = null;
        DetachExitWatch();
        _nextCheck = null;
        _log.Info("Watching stopped.");
        SetStatus(WatchdogStatus.Stopped, "Not watching");
    }

    public void Pause(TimeSpan duration)
    {
        _pausedUntil = DateTime.Now + duration;
        _log.Info($"Paused until {_pausedUntil:HH:mm} — the app will not be restarted until then.");
        SetStatus(WatchdogStatus.Paused, $"Paused until {_pausedUntil:HH:mm}");
    }

    /// <summary>Clears pause / crash-loop / closed-by-user and checks immediately.</summary>
    public void ResumeWatching()
    {
        _pausedUntil = null;
        _crashLoopHalted = false;
        _closedByUser = false;
        _log.Info("Watching resumed.");
        if (!IsWatching) Start(); else CheckNow();
    }

    public void CheckNow() => _wake.Release();

    public void SettingsChanged()
    {
        DetachExitWatch();
        CheckNow();
    }

    /// <summary>Kills the app to test that restart (and resume) works.</summary>
    public int SimulateCrash()
    {
        _log.Warn("Test: killing the ChatGPT app to simulate a crash.");
        _lastExitUtc = DateTime.UtcNow;
        var n = _app.KillAll();
        CheckNow();
        return n;
    }

    // ------------------------------------------------------------------ loop

    private async Task LoopAsync(CancellationToken ct)
    {
        // First check right away.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _busy = true;
                await CheckAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _log.Error("Check failed: " + ex.Message);
            }
            finally { _busy = false; }

            var interval = TimeSpan.FromSeconds(_settings().CheckIntervalSeconds);
            _nextCheck = DateTime.Now + interval;
            try { StatusChanged?.Invoke(Snapshot()); } catch { }
            try
            {
                await _wake.WaitAsync(interval, ct).ConfigureAwait(false);
                // Drain extra wake-ups so several triggers cause one check.
                while (_wake.CurrentCount > 0) _wake.Wait(0);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        var s = _settings();
        _lastCheck = DateTime.Now;

        if (_pausedUntil != null)
        {
            if (DateTime.Now < _pausedUntil) { SetStatus(WatchdogStatus.Paused, $"Paused until {_pausedUntil:HH:mm}"); return; }
            _pausedUntil = null;
            _log.Info("Pause ended.");
        }
        if (_crashLoopHalted)
        {
            SetStatus(WatchdogStatus.CrashLoop, "Stopped restarting (crash loop). Click Resume watching to continue.");
            return;
        }

        var procs = _app.FindAppProcesses();
        var main = TargetApp.FindMainProcess(procs);
        bool hangKill = false;

        if (main != null)
        {
            if (_main == null || _main.Pid != main.Pid)
                _log.Info($"ChatGPT is running (pid {main.Pid}{(main.StartTime != null ? $", started {main.StartTime:HH:mm:ss}" : "")}, {procs.Count} processes).");
            _main = main;
            _lastSeenRunningUtc = DateTime.UtcNow;
            _lastExitUtc = null;
            _closedByUser = false;
            if (s.InstantDetection) AttachExitWatch(main.Pid); else DetachExitWatch();

            if (s.HangDetection)
            {
                var win = _app.FindMainWindow();
                if (win != null && TargetApp.IsHung(win.Handle))
                {
                    _hungSinceUtc ??= DateTime.UtcNow;
                    var hungFor = DateTime.UtcNow - _hungSinceUtc.Value;
                    _log.Warn($"ChatGPT window is not responding ({hungFor.TotalSeconds:0}s).");
                    if (hungFor.TotalSeconds >= s.HangTimeoutSeconds)
                    {
                        _log.Error($"ChatGPT has been hung for {hungFor.TotalSeconds:0}s — killing it so it can be restarted.");
                        _lastExitUtc = DateTime.UtcNow;
                        _app.KillAll();
                        _hungSinceUtc = null;
                        hangKill = true;
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                    }
                }
                else _hungSinceUtc = null;
            }
            if (!hangKill)
            {
                SetStatus(WatchdogStatus.Running, $"ChatGPT is running (pid {main.Pid})");
                return;
            }
        }

        // ---------------- not running ----------------
        if (_closedByUser)
        {
            SetStatus(WatchdogStatus.ClosedByUser, "ChatGPT was closed normally; not restarting (Restart when = OnlyAfterCrash).");
            return;
        }

        var goneUtc = _lastExitUtc ?? _lastSeenRunningUtc ?? DateTime.UtcNow;
        if (_main != null || _lastExitUtc != null)
            _log.Warn("ChatGPT is not running anymore.");
        else
            _log.Warn("ChatGPT is not running.");
        _main = null;
        DetachExitWatch();

        if (s.RestartDelaySeconds > 0 && !hangKill)
        {
            SetStatus(WatchdogStatus.Restarting, $"ChatGPT not running — restarting in {s.RestartDelaySeconds}s");
            await Task.Delay(TimeSpan.FromSeconds(s.RestartDelaySeconds), ct).ConfigureAwait(false);
            if (_app.IsRunning())
            {
                _log.Info("ChatGPT came back by itself (update or self-restart); no action needed.");
                _lastSeenRunningUtc = DateTime.UtcNow;
                return;
            }
            if (_pausedUntil != null) return;
        }

        // Crash details from the event log.
        string? crash = null;
        if (s.LogCrashDetails || s.RestartWhen == RestartCondition.OnlyAfterCrash)
        {
            var window = DateTime.UtcNow - goneUtc + TimeSpan.FromMinutes(2);
            if (window < TimeSpan.FromMinutes(3)) window = TimeSpan.FromMinutes(3);
            crash = await Task.Run(() => CrashInfo.FindRecentCrash(s.ProcessName + ".exe", window), ct).ConfigureAwait(false);
            if (crash != null) _log.Warn("Crash recorded by Windows: " + crash);
            else if (s.LogCrashDetails) _log.Info("No crash entry in the Windows event log (the app may have been closed or exited without an error report).");
        }

        if (s.RestartWhen == RestartCondition.OnlyAfterCrash && crash == null && !hangKill)
        {
            _closedByUser = true;
            _log.Info("No crash recorded, so it looks like ChatGPT was closed normally — not restarting.");
            SetStatus(WatchdogStatus.ClosedByUser, "ChatGPT was closed normally; not restarting.");
            return;
        }

        // Crash-loop protection.
        var loopWindow = TimeSpan.FromMinutes(s.CrashLoopWindowMinutes);
        if (_state.RestartsWithin(loopWindow) >= s.CrashLoopMaxRestarts)
        {
            _crashLoopHalted = true;
            _log.Error($"ChatGPT was restarted {s.CrashLoopMaxRestarts} times within {s.CrashLoopWindowMinutes} minutes — stopping automatic restarts.");
            Notify?.Invoke("Crash loop detected", "ChatGPT keeps crashing. Automatic restarts are paused; open the watchdog to continue.");
            SetStatus(WatchdogStatus.CrashLoop, "Stopped restarting (crash loop). Click Resume watching to continue.");
            return;
        }

        // Which chats were busy? (read before the app starts and touches the session files)
        List<CodexThread> interrupted = new();
        if (s.Resume.Enabled)
        {
            try
            {
                var lookback = TimeSpan.FromMinutes(s.Resume.LookbackMinutes) + (DateTime.UtcNow - goneUtc);
                interrupted = _sessions.FindInterruptedThreads(goneUtc, lookback);
                if (interrupted.Count == 0) _log.Info("Resume: no chats were busy when ChatGPT stopped.");
                foreach (var t in interrupted)
                    _log.Info($"Resume: interrupted chat {t}{(t.ExcludedReason != null ? $" (will skip: {t.ExcludedReason})" : "")}");
            }
            catch (Exception ex) { _log.Warn("Resume: could not read Codex sessions: " + ex.Message); }
        }

        // Restart.
        SetStatus(WatchdogStatus.Restarting, "Starting ChatGPT...");
        string how;
        try { how = _app.Launch(); }
        catch (Exception ex)
        {
            _log.Error("Could not start ChatGPT: " + ex.Message);
            SetStatus(WatchdogStatus.Restarting, "Start failed; will retry at the next check");
            return;
        }
        _state.LastCrashUtc = goneUtc;
        _state.RecordRestart();
        _log.Info($"Starting ChatGPT via {how}.");

        var deadline = DateTime.UtcNow.AddSeconds(s.LaunchTimeoutSeconds);
        WindowInfo? window2 = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(1000, ct).ConfigureAwait(false);
            window2 = _app.FindMainWindow();
            if (window2 != null) break;
        }
        var newMain = _app.FindMainProcess();
        if (newMain == null)
        {
            _log.Error($"ChatGPT did not start within {s.LaunchTimeoutSeconds}s; will retry at the next check.");
            SetStatus(WatchdogStatus.Restarting, "ChatGPT did not start; retrying at next check");
            return;
        }
        _main = newMain;
        _lastSeenRunningUtc = DateTime.UtcNow;
        if (s.InstantDetection) AttachExitWatch(newMain.Pid);
        _log.Success($"ChatGPT restarted (pid {newMain.Pid}){(window2 == null ? " — window not visible yet" : "")}.");
        if (s.ShowNotifications) Notify?.Invoke("ChatGPT restarted", crash != null ? "The app crashed and was restarted." : "The app was not running and was restarted.");

        if (s.Resume.Enabled && interrupted.Count > 0)
        {
            SetStatus(WatchdogStatus.Resuming, $"Waiting {s.Resume.AppReadyDelaySeconds}s for the app to load, then resuming chats");
            await Task.Delay(TimeSpan.FromSeconds(s.Resume.AppReadyDelaySeconds), ct).ConfigureAwait(false);
            SetStatus(WatchdogStatus.Resuming, "Resuming interrupted chats...");
            try
            {
                await _resume.ResumeAfterCrashAsync(interrupted, goneUtc, s.Resume, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { _log.Error("Resume failed: " + ex.Message); }
        }
        SetStatus(WatchdogStatus.Running, $"ChatGPT is running (pid {newMain.Pid})");
    }

    // ------------------------------------------------------------------ instant exit detection

    private void AttachExitWatch(int pid)
    {
        if (_exitWatch != null && _exitWatchPid == pid) return;
        DetachExitWatch();
        try
        {
            var p = Process.GetProcessById(pid);
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) =>
            {
                _lastExitUtc = DateTime.UtcNow;
                _log.Debug($"Main process {pid} exited.");
                _wake.Release();
            };
            _exitWatch = p;
            _exitWatchPid = pid;
        }
        catch (Exception ex)
        {
            _log.Debug($"Instant detection unavailable for pid {pid}: {ex.Message} (interval checks still work).");
        }
    }

    private void DetachExitWatch()
    {
        try { _exitWatch?.Dispose(); } catch { }
        _exitWatch = null;
        _exitWatchPid = 0;
    }

    public void Dispose()
    {
        Stop();
        _wake.Dispose();
    }
}
