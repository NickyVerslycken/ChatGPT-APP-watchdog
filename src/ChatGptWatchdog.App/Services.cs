using System;
using ChatGptWatchdog.App.Resume;
using ChatGptWatchdog.Core;
using ChatGptWatchdog.Core.Resume;

namespace ChatGptWatchdog.App;

/// <summary>Creates and holds the watchdog's building blocks.</summary>
internal sealed class Services : IDisposable
{
    public WatchdogSettings Settings { get; private set; }
    public Logger Log { get; }
    public WatchdogState State { get; }
    public TargetApp App { get; }
    public CodexSessions Sessions { get; }
    public ResumeCoordinator Resume { get; }
    public WatchdogEngine Engine { get; }

    public Services()
    {
        Settings = WatchdogSettings.Load(AppPaths.SettingsFile);
        if (!System.IO.File.Exists(AppPaths.SettingsFile)) Settings.Save(AppPaths.SettingsFile);
        Log = new Logger(AppPaths.LogDirectory);
        Log.CleanupOldLogs(Settings.KeepLogDays);
        State = WatchdogState.Load(AppPaths.StateFile);
        App = new TargetApp(() => Settings);
        Sessions = new CodexSessions(() => Settings, Log);
        Resume = new ResumeCoordinator(new IResumeStrategy[]
        {
            new OpenChatStrategy(),
            new UiAutomationStrategy(new UiaComposerLocator(Log)),
            new CodexCliStrategy(),
            new AppServerStrategy(),
        }, Log, Sessions, App, State);
        Engine = new WatchdogEngine(() => Settings, Log, App, Sessions, State, Resume);
    }

    public void ApplySettings(WatchdogSettings newSettings)
    {
        newSettings.Normalize();
        newSettings.Save(AppPaths.SettingsFile);
        Settings = newSettings;
        AutoStart.Apply(newSettings.StartWithWindows, Log);
        Log.Info("Settings saved.");
        Engine.SettingsChanged();
    }

    public void Dispose() => Engine.Dispose();
}
