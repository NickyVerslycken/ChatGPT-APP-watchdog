namespace ChatGptWatchdog.Core.Resume;

/// <summary>Runs the configured resume methods in order (fallback chain) for each interrupted chat.</summary>
public sealed class ResumeCoordinator
{
    private readonly Dictionary<ResumeMethod, IResumeStrategy> _strategies;
    private readonly Logger _log;
    private readonly CodexSessions _sessions;
    private readonly TargetApp _app;
    private readonly WatchdogState _state;

    public event Action<string>? Notify;

    public ResumeCoordinator(IEnumerable<IResumeStrategy> strategies, Logger log, CodexSessions sessions, TargetApp app, WatchdogState state)
    {
        _strategies = strategies.ToDictionary(s => s.Method);
        _log = log;
        _sessions = sessions;
        _app = app;
        _state = state;
    }

    /// <summary>Resume the given chats after a crash (skips excluded and already-handled ones).</summary>
    public async Task ResumeAfterCrashAsync(IReadOnlyList<CodexThread> candidates, DateTime crashUtc, ResumeSettings s, CancellationToken ct)
    {
        var eligible = new List<CodexThread>();
        foreach (var t in candidates)
        {
            if (t.ExcludedReason != null) { _log.Info($"Resume: skipping {t} — {t.ExcludedReason}."); continue; }
            if (t.OpenTurnId != null && _state.WasResumed(t.OpenTurnId)) { _log.Info($"Resume: skipping {t} — already handled this interrupted turn."); continue; }
            eligible.Add(t);
        }
        if (eligible.Count == 0) { _log.Info("Resume: no interrupted chats to resume."); return; }

        var selected = eligible.OrderByDescending(t => t.LastWriteUtc).Take(s.MaxChatsPerCrash).ToList();
        if (eligible.Count > selected.Count)
            _log.Warn($"Resume: {eligible.Count} chats were interrupted; resuming the {selected.Count} most recent (max per crash).");

        for (int i = 0; i < selected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var t = selected[i];
            if (_sessions.HasTurnStartedSince(t.ThreadId, crashUtc))
            {
                _log.Info($"Resume: {t} already continued by itself since the crash; skipping.");
                if (t.OpenTurnId != null) _state.MarkResumed(t.OpenTurnId);
                continue;
            }
            await ResumeOneAsync(t, s.Message, s.Methods, s, ct).ConfigureAwait(false);
            if (t.OpenTurnId != null) _state.MarkResumed(t.OpenTurnId);
            if (i < selected.Count - 1 && s.DelayBetweenChatsSeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(s.DelayBetweenChatsSeconds), ct).ConfigureAwait(false);
        }
    }

    /// <summary>Resume one chat with the given method chain. Returns true if a method succeeded.</summary>
    public async Task<bool> ResumeOneAsync(CodexThread thread, string message, IReadOnlyList<ResumeMethod> chain, ResumeSettings s, CancellationToken ct)
    {
        _log.Info($"Resume: {thread} — trying {string.Join(" → ", chain.Select(ResumeMethodInfo.DisplayName))}");
        foreach (var method in chain)
        {
            ct.ThrowIfCancellationRequested();
            if (!_strategies.TryGetValue(method, out var strategy))
            {
                _log.Warn($"Resume: method {method} is not available.");
                continue;
            }
            var ctx = new ResumeContext
            {
                Thread = thread, Message = message, Settings = s, Log = _log, Sessions = _sessions, App = _app,
            };
            ResumeResult result;
            try
            {
                result = await strategy.ResumeAsync(ctx, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                result = ResumeResult.Fail("unexpected error: " + ex.Message);
            }

            if (result.Success)
            {
                _log.Success($"Resume: {thread} — {strategy.DisplayName}: {result.Detail}");
                Notify?.Invoke($"Resumed '{thread.DisplayTitle}' ({strategy.DisplayName})");
                return true;
            }
            _log.Warn($"Resume: {thread} — {strategy.DisplayName} failed: {result.Detail}");
        }
        _log.Error($"Resume: {thread} — all methods failed.");
        Notify?.Invoke($"Could not resume '{thread.DisplayTitle}'");
        return false;
    }
}
