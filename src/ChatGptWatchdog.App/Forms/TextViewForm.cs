using System;
using System.Drawing;
using System.Windows.Forms;

namespace ChatGptWatchdog.App.Forms;

/// <summary>Read-only text viewer (used for the diagnostics report).</summary>
internal sealed class TextViewForm : Form
{
    public TextViewForm(string title, string text, string? filePath)
    {
        Text = title + " — ChatGPT APP watchdog";
        Size = new Size(900, 640);
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill,
            Font = new Font("Cascadia Mono", 9f), Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
        };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(6) };
        var copy = new Button { Text = "Copy all", AutoSize = true };
        copy.Click += (_, _) => Clipboard.SetText(box.Text);
        bar.Controls.Add(copy);
        if (filePath != null) bar.Controls.Add(new Label { Text = "Saved: " + filePath, AutoSize = true, Margin = new Padding(8, 8, 3, 3) });
        Controls.Add(box);
        Controls.Add(bar);
        box.Select(0, 0);
        Load += (_, _) =>
        {
            // Center on the main window (CenterParent only works for modal dialogs).
            var area = Owner != null && Owner.Visible ? Owner.Bounds : Screen.PrimaryScreen!.WorkingArea;
            Location = new System.Drawing.Point(area.Left + (area.Width - Width) / 2, Math.Max(area.Top, area.Top + (area.Height - Height) / 2));
        };
    }
}
