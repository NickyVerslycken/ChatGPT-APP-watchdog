using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChatGptWatchdog.Core;

public enum RestartCondition
{
    /// <summary>Restart whenever the app is not running (crash or normal close).</summary>
    Always,
    /// <summary>Restart only when Windows logged an application crash for the app.</summary>
    OnlyAfterCrash,
}

public enum ResumeMethod
{
    /// <summary>Open the interrupted chat in the app (codex://threads/&lt;id&gt;). Does not send anything.</summary>
    OpenChat,
    /// <summary>Open the chat and type the resume message into the composer, then press Enter.</summary>
    UiAutomation,
    /// <summary>Run "codex exec resume &lt;id&gt; &lt;message&gt;" in the background with the Codex CLI.</summary>
    CodexCli,
    /// <summary>Talk JSON-RPC to a Codex app-server (spawned, or an endpoint you configure) and start a new turn.</summary>
    AppServer,
}

public enum UiInputMode
{
    /// <summary>Types the message character by character (does not touch the clipboard).</summary>
    Type,
    /// <summary>Puts the message on the clipboard and pastes it (clipboard text is restored afterwards).</summary>
    Paste,
}

/// <summary>All user settings. Serialized to settings.json.</summary>
public sealed class WatchdogSettings
{
    // ----- Monitoring -----
    [Category("1. Monitoring"), DisplayName("Check interval (seconds)"),
     Description("How often the watchdog checks whether the ChatGPT app is running. E.g. 60 = every minute, 300 = every 5 minutes.")]
    public int CheckIntervalSeconds { get; set; } = 60;

    [Category("1. Monitoring"), DisplayName("Instant crash detection"),
     Description("Also react the moment the app's main process exits, instead of waiting for the next interval check.")]
    public bool InstantDetection { get; set; } = true;

    [Category("1. Monitoring"), DisplayName("Restart delay (seconds)"),
     Description("Wait this long after the app disappears before restarting it. Gives the app's own updater/restart a chance and avoids double launches.")]
    public int RestartDelaySeconds { get; set; } = 10;

    [Category("1. Monitoring"), DisplayName("Restart when"),
     Description("Always: restart whenever the app is not running (also after you close it yourself - use Pause for that). OnlyAfterCrash: only restart when Windows logged a crash (Application Error event) for the app.")]
    public RestartCondition RestartWhen { get; set; } = RestartCondition.Always;

    [Category("1. Monitoring"), DisplayName("Crash-loop: max restarts"),
     Description("If the app had to be restarted this many times within the crash-loop window, the watchdog stops restarting and notifies you.")]
    public int CrashLoopMaxRestarts { get; set; } = 5;

    [Category("1. Monitoring"), DisplayName("Crash-loop: window (minutes)"),
     Description("Time window for the crash-loop protection.")]
    public int CrashLoopWindowMinutes { get; set; } = 10;

    [Category("1. Monitoring"), DisplayName("Hang detection"),
     Description("If the app's window stops responding (Windows 'Not responding') for longer than the hang timeout, kill and restart it.")]
    public bool HangDetection { get; set; } = false;

    [Category("1. Monitoring"), DisplayName("Hang timeout (seconds)"),
     Description("How long the window must be unresponsive before it is treated as hung.")]
    public int HangTimeoutSeconds { get; set; } = 180;

    [Category("1. Monitoring"), DisplayName("Log crash details"),
     Description("Read the Windows Application event log after a crash and write the exception code/faulting module to the log.")]
    public bool LogCrashDetails { get; set; } = true;

    // ----- Target app -----
    [Category("2. Target app"), DisplayName("Process name"),
     Description("Executable name of the app without .exe. The Codex/ChatGPT desktop app on Windows is ChatGPT.")]
    public string ProcessName { get; set; } = "ChatGPT";

    [Category("2. Target app"), DisplayName("Process path filter"),
     Description("Only processes whose full path contains this text count (so other apps called ChatGPT.exe are ignored). Leave empty to match any path.")]
    public string ProcessPathFilter { get; set; } = @"\WindowsApps\OpenAI.Codex_";

    [Category("2. Target app"), DisplayName("App User Model ID"),
     Description("Store app ID used to start the app. Leave empty for auto-detect (default OpenAI.Codex_2p2nqsd0c76g0!App).")]
    public string AppUserModelId { get; set; } = "";

    [Category("2. Target app"), DisplayName("Custom launch command"),
     Description("Optional. If set, this executable/command is started instead of the Store app (e.g. a portable install).")]
    public string CustomLaunchCommand { get; set; } = "";

    [Category("2. Target app"), DisplayName("Custom launch arguments"),
     Description("Arguments for the custom launch command.")]
    public string CustomLaunchArguments { get; set; } = "";

    [Category("2. Target app"), DisplayName("Wait for window (seconds)"),
     Description("After starting the app, how long to wait for its main window to appear.")]
    public int LaunchTimeoutSeconds { get; set; } = 90;

    [Category("2. Target app"), DisplayName("Codex home folder"),
     Description("Where Codex keeps its sessions. Leave empty for %CODEX_HOME% or %USERPROFILE%\\.codex.")]
    public string CodexHome { get; set; } = "";

    // ----- Watchdog app -----
    [Category("3. Watchdog"), DisplayName("Start watching on launch"),
     Description("Start monitoring immediately when the watchdog starts.")]
    public bool StartWatchingOnLaunch { get; set; } = true;

    [Category("3. Watchdog"), DisplayName("Start minimized to tray"),
     Description("Start hidden in the notification area.")]
    public bool StartMinimized { get; set; } = false;

    [Category("3. Watchdog"), DisplayName("Start with Windows"),
     Description("Start the watchdog automatically when you sign in to Windows.")]
    public bool StartWithWindows { get; set; } = false;

    [Category("3. Watchdog"), DisplayName("Close button minimizes to tray"),
     Description("Closing the window keeps the watchdog running in the tray. Use Exit in the tray menu to quit.")]
    public bool CloseToTray { get; set; } = true;

    [Category("3. Watchdog"), DisplayName("Tray notifications"),
     Description("Show a notification when the app is restarted or a chat is resumed.")]
    public bool ShowNotifications { get; set; } = true;

    [Category("3. Watchdog"), DisplayName("Keep log files (days)"),
     Description("Log files older than this are deleted.")]
    public int KeepLogDays { get; set; } = 14;

    [Browsable(false)]
    public ResumeSettings Resume { get; set; } = new();

    // ----- Persistence -----
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public static WatchdogSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<WatchdogSettings>(File.ReadAllText(path), JsonOptions);
                if (s != null) { s.Normalize(); return s; }
            }
        }
        catch
        {
            // Corrupt file: keep a copy and fall back to defaults.
            try { File.Copy(path, path + ".broken", true); } catch { }
        }
        var d = new WatchdogSettings();
        d.Normalize();
        return d;
    }

    public void Save(string path)
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, true);
    }

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public WatchdogSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        return JsonSerializer.Deserialize<WatchdogSettings>(json, JsonOptions)!;
    }

    /// <summary>Clamp values to sane ranges.</summary>
    public void Normalize()
    {
        CheckIntervalSeconds = Math.Clamp(CheckIntervalSeconds, 5, 24 * 3600);
        RestartDelaySeconds = Math.Clamp(RestartDelaySeconds, 0, 600);
        CrashLoopMaxRestarts = Math.Clamp(CrashLoopMaxRestarts, 1, 1000);
        CrashLoopWindowMinutes = Math.Clamp(CrashLoopWindowMinutes, 1, 24 * 60);
        HangTimeoutSeconds = Math.Clamp(HangTimeoutSeconds, 15, 3600);
        LaunchTimeoutSeconds = Math.Clamp(LaunchTimeoutSeconds, 10, 600);
        KeepLogDays = Math.Clamp(KeepLogDays, 1, 3650);
        ProcessName = (ProcessName ?? "ChatGPT").Trim();
        if (ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ProcessName = ProcessName[..^4];
        if (ProcessName.Length == 0) ProcessName = "ChatGPT";
        ProcessPathFilter ??= "";
        AppUserModelId ??= "";
        CustomLaunchCommand ??= "";
        CustomLaunchArguments ??= "";
        CodexHome ??= "";
        Resume ??= new ResumeSettings();
        Resume.Normalize();
    }
}

/// <summary>Settings for resuming chats that were interrupted by a crash.</summary>
public sealed class ResumeSettings
{
    public const string DefaultMessage =
        "The ChatGPT/Codex desktop app crashed and this session was interrupted mid-task. " +
        "First check the current state (git status, the files you were editing, anything half-done or still running), " +
        "then continue from where you left off. Don't redo steps that already completed.";

    [Browsable(false)]
    public bool Enabled { get; set; } = false;

    /// <summary>Methods to try, in order. The first one that succeeds wins; the rest are fallbacks.</summary>
    [Browsable(false)]
    public List<ResumeMethod> Methods { get; set; } = new() { ResumeMethod.UiAutomation, ResumeMethod.CodexCli, ResumeMethod.OpenChat };

    [Browsable(false)]
    public string Message { get; set; } = DefaultMessage;

    [Category("Which chats"), DisplayName("Look-back (minutes)"),
     Description("Only chats that were busy (a turn started but not finished) and active within this many minutes before the crash are resumed.")]
    public int LookbackMinutes { get; set; } = 30;

    [Category("Which chats"), DisplayName("Max chats per crash"),
     Description("At most this many chats are resumed after one crash (most recent first).")]
    public int MaxChatsPerCrash { get; set; } = 3;

    [Category("Which chats"), DisplayName("Skip automation chats"),
     Description("Skip chats driven by Codex automations/scheduled tasks (they restart by themselves; resuming them would run work twice).")]
    public bool SkipAutomationChats { get; set; } = true;

    [Category("Which chats"), DisplayName("Skip sub-agent chats"),
     Description("Skip sub-agent threads (their parent chat is resumed instead).")]
    public bool SkipSubAgentChats { get; set; } = true;

    [Category("Timing"), DisplayName("Wait after restart (seconds)"),
     Description("After the app's window appears, wait this long before resuming so the app can finish loading.")]
    public int AppReadyDelaySeconds { get; set; } = 20;

    [Category("Timing"), DisplayName("Verify timeout (seconds)"),
     Description("How long to wait for proof (a new turn in the chat's session file) that a resume method worked before trying the next method.")]
    public int VerifyTimeoutSeconds { get; set; } = 60;

    [Category("Timing"), DisplayName("Pause between chats (seconds)"),
     Description("Pause between resuming two chats.")]
    public int DelayBetweenChatsSeconds { get; set; } = 5;

    [Category("UI automation"), DisplayName("Input mode"),
     Description("Type: types the message (clipboard untouched). Paste: pastes via clipboard (restored afterwards).")]
    public UiInputMode UiInputMode { get; set; } = UiInputMode.Type;

    [Category("UI automation"), DisplayName("Require idle user (seconds)"),
     Description("Only take over keyboard input when you haven't used keyboard/mouse for this many seconds, so it never types into your other work.")]
    public int UiRequireIdleSeconds { get; set; } = 20;

    [Category("UI automation"), DisplayName("Max wait for idle (seconds)"),
     Description("Give up on UI automation (and use the next method) if you stay active longer than this.")]
    public int UiMaxWaitForIdleSeconds { get; set; } = 300;

    [Category("UI automation"), DisplayName("Composer click fallback: X (fraction of width)"),
     Description("If the message box can't be found via accessibility, click at this horizontal position of the window (0.0-1.0).")]
    public double UiFallbackClickX { get; set; } = 0.62;

    [Category("UI automation"), DisplayName("Composer click fallback: Y offset from bottom (px)"),
     Description("If the message box can't be found via accessibility, click this many pixels above the bottom of the window.")]
    public int UiFallbackClickBottomOffset { get; set; } = 75;

    [Category("Codex CLI / app-server"), DisplayName("codex.exe path"),
     Description("Path to the Codex CLI. Leave empty to auto-detect (PATH, then the app's install folder).")]
    public string CodexCliPath { get; set; } = "";

    [Category("Codex CLI / app-server"), DisplayName("Extra CLI arguments"),
     Description("Extra arguments inserted after 'exec'. --skip-git-repo-check is needed for folders that are not git repositories. Add e.g. --sandbox workspace-write to let the agent edit files.")]
    public string CliExtraArguments { get; set; } = "--skip-git-repo-check";

    [Category("Codex CLI / app-server"), DisplayName("App-server endpoint"),
     Description("Optional ws://host:port of an already running Codex app-server. Empty = the watchdog starts its own 'codex app-server' process.")]
    public string AppServerEndpoint { get; set; } = "";

    [Category("Codex CLI / app-server"), DisplayName("Auto-approve in background"),
     Description("For the app-server method: approve command/file-change requests automatically. Off = they are declined (the agent continues without them).")]
    public bool AppServerAutoApprove { get; set; } = false;

    public void Normalize()
    {
        Methods ??= new();
        Methods = Methods.Distinct().ToList();
        if (Methods.Count == 0) Methods.Add(ResumeMethod.OpenChat);
        if (string.IsNullOrWhiteSpace(Message)) Message = DefaultMessage;
        LookbackMinutes = Math.Clamp(LookbackMinutes, 1, 7 * 24 * 60);
        MaxChatsPerCrash = Math.Clamp(MaxChatsPerCrash, 1, 50);
        AppReadyDelaySeconds = Math.Clamp(AppReadyDelaySeconds, 0, 600);
        VerifyTimeoutSeconds = Math.Clamp(VerifyTimeoutSeconds, 10, 900);
        DelayBetweenChatsSeconds = Math.Clamp(DelayBetweenChatsSeconds, 0, 300);
        UiRequireIdleSeconds = Math.Clamp(UiRequireIdleSeconds, 0, 3600);
        UiMaxWaitForIdleSeconds = Math.Clamp(UiMaxWaitForIdleSeconds, 0, 24 * 3600);
        UiFallbackClickX = Math.Clamp(UiFallbackClickX, 0.0, 1.0);
        UiFallbackClickBottomOffset = Math.Clamp(UiFallbackClickBottomOffset, 0, 2000);
        CodexCliPath ??= "";
        CliExtraArguments ??= "";
        AppServerEndpoint ??= "";
    }
}
