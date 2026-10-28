using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace EchoDiar.Setup;

/// <summary>
/// Setup's window: what Setup does, the link to why Windows warns, Install and Cancel. Install switches to the progress
/// state in the same click, then <see cref="Installer.Run"/> works off the UI thread; its steps come back through a
/// <see cref="Progress{T}"/>. The end shows the result, with Open EchoDiar after a success. System font and colors
/// only; laid out at 96 DPI and scaled by <see cref="AutoScaleMode.Dpi"/> (the manifest makes Setup per-monitor aware).
/// </summary>
internal sealed class SetupForm : Form
{
    /// <summary>The width text wraps at, at 96 DPI.</summary>
    private const int TextWidth = 380;

    private readonly Installer _installer;
    private readonly LinkLabel _status;
    private readonly ProgressBar _progress;
    private readonly Button _open;
    private readonly Button _install;
    private readonly Button _cancel;

    /// <summary>An install is under way: the window stays open.</summary>
    private bool _busy;

    private string? _familyName;

    public SetupForm(Installer installer)
    {
        _installer = installer;
        SuspendLayout();
        Font = SystemFonts.MessageBoxFont;
        Text = installer.IsDev ? "EchoDiar Setup (DEV)" : "EchoDiar Setup";
        Icon = OwnIcon();
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var icon = new PictureBox
        {
            Image = Icon.ToBitmap(),
            SizeMode = PictureBoxSizeMode.AutoSize,
            Margin = new Padding(0, 0, 12, 0),
        };
        var title = new Label
        {
            Text = "EchoDiar",
            AutoSize = true,
            Font = new Font(Font.FontFamily, Font.SizeInPoints * 1.5f, FontStyle.Bold, GraphicsUnit.Point),
            Margin = new Padding(0, 0, 0, 6),
        };
        var summary = new Label
        {
            Text = "Setup trusts the EchoDiar certificate on this PC, then installs EchoDiar from ed.dinhanhthi.com "
                + "and keeps it updated.",
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            Margin = new Padding(0, 0, 0, 6),
        };
        var why = new LinkLabel { Text = "Why does Windows warn?", AutoSize = true };
        why.LinkClicked += (_, _) => OpenLink(Installer.DocsUri);
        _status = new LinkLabel
        {
            AutoSize = true,
            MaximumSize = new Size(TextWidth, 0),
            Margin = new Padding(0, 12, 0, 6),
            Visible = false,
        };
        _status.LinkClicked += (_, e) => OpenLink((string)e.Link.LinkData);
        _progress = new ProgressBar
        {
            Size = new Size(TextWidth, 16),
            Style = ProgressBarStyle.Marquee,
            Visible = false,
        };

        _open = NewButton("Open EchoDiar");
        _open.Visible = false;
        _open.Click += OnOpen;
        _install = NewButton("Install");
        _install.Click += OnInstall;
        _cancel = NewButton("Cancel");
        _cancel.Click += (_, _) => Close();
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 12, 0, 0),
        };
        buttons.Controls.AddRange(new Control[] { _cancel, _install, _open });

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };
        layout.Controls.Add(icon, 0, 0);
        layout.SetRowSpan(icon, 5);
        layout.Controls.Add(title, 1, 0);
        layout.Controls.Add(summary, 1, 1);
        layout.Controls.Add(why, 1, 2);
        layout.Controls.Add(_status, 1, 3);
        layout.Controls.Add(_progress, 1, 4);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 2);

        AcceptButton = _install;
        CancelButton = _cancel;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Controls.Add(layout);
        ResumeLayout(false);
        PerformLayout();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Windows goes on with a deployment under way; its result would go unseen.
        if (_busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
        base.OnFormClosing(e);
    }

    /// <summary>Setup's own icon; Windows' default app icon where it cannot be read (.NET Framework refuses a UNC
    /// path, such as Setup run from a network share).</summary>
    private static Icon OwnIcon()
    {
        try
        {
            return Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
        }
        catch (ArgumentException)
        {
            return SystemIcons.Application;
        }
    }

    private static Button NewButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MinimumSize = new Size(88, 26),
        UseVisualStyleBackColor = true,
    };

    /// <summary>The progress state at once, in this click; the work runs off the UI thread.</summary>
    private async void OnInstall(object? sender, EventArgs e)
    {
        _busy = true;
        _install.Enabled = false;
        _cancel.Enabled = false;
        _status.Visible = true;
        _progress.Visible = true;
        ShowStep(("Starting…", null));

        InstallOutcome outcome;
        try
        {
            // Created on the UI thread, so its reports come back to it.
            var progress = new Progress<(string Status, int? Percent)>(ShowStep);
            outcome = await Task.Run(() => _installer.Run(progress));
        }
        catch (Exception error)
        {
            outcome = Installer.Failed(error);
        }
        _busy = false;
        ShowOutcome(outcome);
    }

    private void ShowStep((string Status, int? Percent) step)
    {
        // A report posted before the end may arrive after it.
        if (!_busy) return;
        ShowText(step.Status);
        if (step.Percent is { } percent)
        {
            _progress.Style = ProgressBarStyle.Continuous;
            _progress.Value = Math.Max(0, Math.Min(percent, 100));
        }
        else
        {
            _progress.Style = ProgressBarStyle.Marquee;
        }
    }

    private void ShowOutcome(InstallOutcome outcome)
    {
        ShowText(outcome.Message);
        _progress.Visible = false;
        _cancel.Text = "Close";
        _cancel.Enabled = true;
        if (outcome.Succeeded)
        {
            _install.Visible = false;
            _familyName = outcome.PackageFamilyName;
            _open.Visible = _familyName is not null;
            AcceptButton = _open.Visible ? _open : _cancel;
            (_open.Visible ? _open : _cancel).Focus();
        }
        else
        {
            _install.Text = "Try again";
            _install.Enabled = true;
            _install.Focus();
        }
    }

    /// <summary>The status line; the docs link in a message is clickable.</summary>
    private void ShowText(string text)
    {
        _status.Text = text;
        _status.Links.Clear();
        var at = text.IndexOf(Installer.DocsUri, StringComparison.Ordinal);
        if (at >= 0) _status.Links.Add(at, Installer.DocsUri.Length, Installer.DocsUri);
    }

    private void OnOpen(object? sender, EventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo($@"shell:AppsFolder\{_familyName}!App") { UseShellExecute = true });
            Close();
        }
        catch (Exception error)
        {
            ShowText($"Could not open EchoDiar: {error.Message}");
        }
    }

    private static void OpenLink(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Debug.WriteLine($"EchoDiar Setup: could not open {uri}: {error.GetType().Name}");
        }
    }
}
