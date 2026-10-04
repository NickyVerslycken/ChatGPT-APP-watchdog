using System;
using Microsoft.Win32;
using ChatGptWatchdog.Core;

namespace ChatGptWatchdog.App;

/// <summary>"Start with Windows" via HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ChatGPT-APP-watchdog";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    public static void Apply(bool enable, Logger log)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enable)
            {
                var exe = Environment.ProcessPath ?? System.Windows.Forms.Application.ExecutablePath;
                key.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, false);
            }
        }
        catch (Exception ex)
        {
            log.Warn("Could not update 'Start with Windows': " + ex.Message);
        }
    }
}
