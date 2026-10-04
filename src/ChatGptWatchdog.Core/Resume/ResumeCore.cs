namespace ChatGptWatchdog.Core.Resume;

public sealed record ResumeResult(bool Success, string Detail)
{
    public static ResumeResult Ok(string detail) => new(true, detail);
    public static ResumeResult Fail(string detail) => new(false, detail);
}

public sealed class ResumeContext
{
    public required CodexThread Thread { get; init; }
    public required string Message { get; init; }
    public required ResumeSettings Settings { get; init; }
    public required Logger Log { get; init; }
    public required CodexSessions Sessions { get; init; }
    public required TargetApp App { get; init; }
}

public interface IResumeStrategy
{
    ResumeMethod Method { get; }
    string DisplayName { get; }
    Task<ResumeResult> ResumeAsync(ResumeContext ctx, CancellationToken ct);
}

public static class ResumeMethodInfo
{
    /// <summary>Turns Codex's "already has an active writer" error into an explanation.</summary>
    public static string ExplainError(string error) =>
        error.Contains("active writer", StringComparison.OrdinalIgnoreCase)
            ? "the ChatGPT app has this chat open (only one program can write to a chat at a time), so a background method can't continue it while the app runs. Use UI automation, or this method only while the app is closed."
            : error;

    public static string DisplayName(ResumeMethod m) => m switch
    {
        ResumeMethod.OpenChat => "Open chat only",
        ResumeMethod.UiAutomation => "Open chat + type message (UI automation)",
        ResumeMethod.CodexCli => "Codex CLI in background (codex exec resume)",
        ResumeMethod.AppServer => "Codex app-server (JSON-RPC)",
        _ => m.ToString(),
    };

    public static string Description(ResumeMethod m) => m switch
    {
        ResumeMethod.OpenChat =>
            "Opens the interrupted chat in the app (codex://threads/<id>) so you see it right away. Sends nothing. Always \"succeeds\", so put it last.",
        ResumeMethod.UiAutomation =>
            "Opens the chat, waits until you're idle, brings the app to the front, types the message and presses Enter. Runs inside the app with its normal permissions. Verified by checking that a new turn started.",
        ResumeMethod.CodexCli =>
            "Runs 'codex exec resume <id> \"<message>\"' hidden in the background. Only works while the app does NOT have the chat open (Codex allows one writer per chat), so it is mainly a fallback for when the app can't be restarted.",
        ResumeMethod.AppServer =>
            "Starts 'codex app-server' and sends thread/resume + turn/start (JSON-RPC). Headless like the CLI and with the same limit: only works while the app does NOT have the chat open.",
        _ => "",
    };
}

/// <summary>Opens the chat in the app via deep link. Cannot verify that work continues; always "succeeds".</summary>
public sealed class OpenChatStrategy : IResumeStrategy
{
    public ResumeMethod Method => ResumeMethod.OpenChat;
    public string DisplayName => ResumeMethodInfo.DisplayName(Method);

    public Task<ResumeResult> ResumeAsync(ResumeContext ctx, CancellationToken ct)
    {
        try
        {
            TargetApp.OpenUri($"codex://threads/{ctx.Thread.ThreadId}");
            return Task.FromResult(ResumeResult.Ok("chat opened in the app (nothing sent)"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ResumeResult.Fail("could not open deep link: " + ex.Message));
        }
    }
}
