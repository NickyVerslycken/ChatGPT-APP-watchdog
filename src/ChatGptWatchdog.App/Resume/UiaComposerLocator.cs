using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Automation;
using ChatGptWatchdog.Core;
using ChatGptWatchdog.Core.Resume;

namespace ChatGptWatchdog.App.Resume;

/// <summary>
/// Finds the chat message box in the ChatGPT window with Windows UI Automation and focuses it.
/// Chromium/Electron builds its accessibility tree lazily, so the search is retried for a few seconds.
/// </summary>
internal sealed class UiaComposerLocator : IComposerLocator
{
    private readonly Logger _log;
    public UiaComposerLocator(Logger log) => _log = log;

    public bool TryFocusComposer(IntPtr appWindow, out string detail)
    {
        detail = "";
        var sw = Stopwatch.StartNew();
        string last = "no editable field found";
        while (sw.Elapsed < TimeSpan.FromSeconds(8))
        {
            try
            {
                var root = AutomationElement.FromHandle(appWindow);
                var cond = new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.IsEnabledProperty, true),
                    new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true));
                var edits = root.FindAll(TreeScope.Descendants, cond).Cast<AutomationElement>()
                    .Select(e => new { E = e, R = SafeRect(e), Name = SafeName(e) })
                    .Where(x => !x.R.IsEmpty && x.R.Width > 150)
                    .ToList();

                if (edits.Count > 0)
                {
                    // Prefer a field that looks like the chat composer, otherwise the lowest one on screen.
                    var pick = edits.FirstOrDefault(x => LooksLikeComposer(x.Name))
                               ?? edits.OrderByDescending(x => x.R.Bottom).First();
                    pick.E.SetFocus();
                    Thread.Sleep(150);
                    detail = $"edit field '{Short(pick.Name)}' ({edits.Count} candidates)";
                    return true;
                }
                last = "no editable field in the accessibility tree yet";
            }
            catch (Exception ex)
            {
                last = ex.Message;
            }
            Thread.Sleep(700);
        }
        _log.Debug("UI automation: composer not found via accessibility (" + last + "); using fallback click.");
        return false;
    }

    private static bool LooksLikeComposer(string name) =>
        name.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Codex", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("message", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Ask", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("prompt", StringComparison.OrdinalIgnoreCase);

    private static System.Windows.Rect SafeRect(AutomationElement e)
    {
        try { return e.Current.BoundingRectangle; } catch { return System.Windows.Rect.Empty; }
    }

    private static string SafeName(AutomationElement e)
    {
        try { return e.Current.Name ?? ""; } catch { return ""; }
    }

    private static string Short(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
