using System;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace EchoDiar.Setup;

/// <summary>
/// EchoDiar-Setup's entry point. Without arguments (or with <c>--feed</c>) it shows <see cref="SetupForm"/>. The
/// elevated relaunch (<c>--trust-certificate</c>) only imports the embedded certificate and exits: 0, or the error's
/// HRESULT after an error box. A refused command line shows the usage and exits with 1. No crash dialog: errors end
/// in a message box.
/// </summary>
internal static class Program
{
    private const string Title = "EchoDiar Setup";

    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            var arguments = SetupArguments.Parse(args, DefaultFeed());
            switch (arguments.Mode)
            {
                case SetupMode.TrustCertificate:
                    return TrustCertificate();
                case SetupMode.Error:
                    MessageBox.Show(arguments.Error, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                default:
                    // An exception in an event handler would otherwise show WinForms' crash dialog.
                    Application.ThreadException += (_, e) => ShowError(e.Exception);
                    Application.Run(new SetupForm(new Installer(arguments.Feed!, Installer.EmbeddedCertificate())));
                    return 0;
            }
        }
        catch (Exception e)
        {
            ShowError(e);
            return 1;
        }
    }

    /// <summary>The feed built into Setup: the <c>SetupFeedUri</c> assembly metadata (an MSBuild property).</summary>
    private static Uri DefaultFeed() =>
        new(typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "SetupFeedUri").Value);

    private static int TrustCertificate()
    {
        try
        {
            Installer.TrustCertificate();
            return 0;
        }
        catch (Exception e)
        {
            MessageBox.Show($"Windows could not trust the EchoDiar certificate: {e.Message}", Title,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return e.HResult != 0 ? e.HResult : 1;
        }
    }

    private static void ShowError(Exception e) =>
        MessageBox.Show($"Setup stopped: {e.Message}", Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
}
