using ChatGptWatchdog.Core.Interop;

namespace ChatGptWatchdog.Core.Resume;

/// <summary>
/// Finds and focuses the message box inside the app window. Implemented by the UI project with
/// Windows UI Automation; returns false when it can't find it (the strategy then clicks at a fallback position).
/// </summary>
public interface IComposerLocator
{
    bool TryFocusComposer(IntPtr appWindow, out string detail);
}

/// <summary>
/// Opens the chat with codex://threads/&lt;id&gt;, waits until the user is idle, brings the app to the front,
/// focuses the composer, types the message and presses Enter. Success is verified in the session file.
/// </summary>
public sealed class UiAutomationStrategy : IResumeStrategy
{
    private readonly IComposerLocator? _locator;
    private static readonly SemaphoreSlim InputLock = new(1, 1);

    public UiAutomationStrategy(IComposerLocator? locator) => _locator = locator;

    public ResumeMethod Method => ResumeMethod.UiAutomation;
    public string DisplayName => ResumeMethodInfo.DisplayName(Method);

    public async Task<ResumeResult> ResumeAsync(ResumeContext ctx, CancellationToken ct)
    {
        var s = ctx.Settings;
        var log = ctx.Log;

        // 1. Wait until the user is idle so we never type into their work.
        if (s.UiRequireIdleSeconds > 0)
        {
            var deadline = DateTime.UtcNow.AddSeconds(s.UiMaxWaitForIdleSeconds);
            bool told = false;
            while (InputSim.UserIdleMilliseconds() < s.UiRequireIdleSeconds * 1000)
            {
                if (DateTime.UtcNow > deadline)
                    return ResumeResult.Fail($"you were active for more than {s.UiMaxWaitForIdleSeconds}s; not taking over the keyboard");
                if (!told) { log.Info($"UI automation: waiting until keyboard/mouse are idle for {s.UiRequireIdleSeconds}s..."); told = true; }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }

        await InputLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var startedUtc = DateTime.UtcNow;

            // 2. Open the chat.
            TargetApp.OpenUri($"codex://threads/{ctx.Thread.ThreadId}");
            await Task.Delay(3500, ct).ConfigureAwait(false);

            // 3. Bring the app to the front.
            var win = ctx.App.FindMainWindow();
            if (win == null) return ResumeResult.Fail("app window not found");
            if (!InputSim.BringToFront(win.Handle))
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                if (!InputSim.BringToFront(win.Handle))
                    return ResumeResult.Fail("could not bring the app window to the front");
            }
            await Task.Delay(800, ct).ConfigureAwait(false);

            var appPids = ctx.App.AppPids();
            bool AppInFront() => appPids.Contains(InputSim.ForegroundPid());

            // 4. Focus the composer.
            string how;
            if (_locator != null && _locator.TryFocusComposer(win.Handle, out var detail))
            {
                how = "accessibility: " + detail;
            }
            else
            {
                var (l, t, r, b) = InputSim.WindowRect(win.Handle);
                int x = l + (int)((r - l) * s.UiFallbackClickX);
                int y = b - s.UiFallbackClickBottomOffset;
                if (!AppInFront()) return ResumeResult.Fail("app lost focus before clicking the message box");
                InputSim.LeftClickAt(x, y);
                how = $"fallback click at ({x},{y})";
            }
            await Task.Delay(400, ct).ConfigureAwait(false);
            if (!AppInFront()) return ResumeResult.Fail("app lost focus before typing");
            log.Debug("UI automation: composer focused via " + how);

            // 5. Enter the message.
            if (s.UiInputMode == UiInputMode.Paste)
            {
                var saved = InputSim.GetClipboardText();
                try
                {
                    if (!InputSim.SetClipboardText(ctx.Message)) return ResumeResult.Fail("could not use the clipboard");
                    InputSim.CtrlV();
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                finally
                {
                    if (saved != null) InputSim.SetClipboardText(saved);
                }
            }
            else
            {
                if (!InputSim.TypeText(ctx.Message, AppInFront))
                    return ResumeResult.Fail("typing interrupted: the app window lost focus");
                await Task.Delay(300, ct).ConfigureAwait(false);
            }

            if (!AppInFront()) return ResumeResult.Fail("app lost focus before sending (message may be left in the box)");
            InputSim.Enter();
            log.Debug("UI automation: message entered and sent, verifying...");

            // 6. Verify in the session file.
            var ok = await ctx.Sessions.WaitForTurnStartedAsync(ctx.Thread.ThreadId, startedUtc,
                TimeSpan.FromSeconds(s.VerifyTimeoutSeconds), ct).ConfigureAwait(false);
            return ok
                ? ResumeResult.Ok($"message sent in the app ({how}); new turn started")
                : ResumeResult.Fail($"message typed ({how}) but no new turn appeared within {s.VerifyTimeoutSeconds}s");
        }
        finally
        {
            InputLock.Release();
        }
    }
}
