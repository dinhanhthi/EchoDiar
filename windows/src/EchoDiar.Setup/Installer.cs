using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Management.Deployment;

namespace EchoDiar.Setup;

/// <summary>How an install ended: the window's message and, after a success, the package family to open.</summary>
internal sealed class InstallOutcome
{
    private InstallOutcome(bool succeeded, string message, string? packageFamilyName)
    {
        Succeeded = succeeded;
        Message = message;
        PackageFamilyName = packageFamilyName;
    }

    public bool Succeeded { get; }

    public string Message { get; }

    /// <summary>The installed EchoDiar's package family; null after a failure, or when Windows did not say.</summary>
    public string? PackageFamilyName { get; }

    public static InstallOutcome Installed(string message, string? packageFamilyName) =>
        new(true, message, packageFamilyName);

    /// <summary>A failure; its message ends with the docs link (manual install steps, what Setup changes).</summary>
    public static InstallOutcome Failed(string message) =>
        new(false, $"{message}\n\nHelp: {Installer.DocsUri}", null);
}

/// <summary>
/// Setup's work, run off the UI thread by <see cref="Run"/>: trust the embedded certificate (elevating only for the
/// import, through the <c>--trust-certificate</c> relaunch), install VCLibs when missing, then install EchoDiar from the
/// App Installer feed as the invoking user, so App Installer keeps it updated. Every WinRT call sits in its own
/// NoInlining method that takes and returns only CLR types: on .NET Framework a WinRT type that cannot load fails when
/// the method using it is JIT-compiled, so it is thrown by the call and caught here, never before a caller's try.
/// Setup writes no files of its own.
/// </summary>
internal sealed class Installer
{
    /// <summary>Why Windows warns, what Setup changes and the manual (PowerShell) install.</summary>
    public const string DocsUri = "https://ed.dinhanhthi.com/docs.html#windows-warnings";

    private const string PackageName = "dinh.thi.EchoDiar";
    private const string DevSubject = "CN=EchoDiar Dev";
    private const string VCLibsFamily = "Microsoft.VCLibs.140.00.UWPDesktop_8wekyb3d8bbwe";
    private static readonly Version MinimumVCLibs = new(14, 0, 33728, 0);

    /// <summary>ERROR_CANCELLED: the user answered No at UAC.</summary>
    private const int UacDeclined = 1223;

    private const int AlreadyExists = unchecked((int)0x80073CFB);
    private const int UntrustedRoot = unchecked((int)0x800B0109);
    private const int DependencyFailed = unchecked((int)0x80073CF3);
    private const int EFail = unchecked((int)0x80004005);

    private const ushort ImageFileMachineArm64 = 0xAA64;

    private readonly Uri _feed;
    private readonly X509Certificate2? _certificate;

    /// <param name="certificate">The certificate built into Setup; null in a build without one (no install).</param>
    public Installer(Uri feed, X509Certificate2? certificate)
    {
        _feed = feed;
        _certificate = certificate;
    }

    /// <summary>Setup carries the dev certificate (a QA build): the window says so.</summary>
    public bool IsDev => _certificate?.Subject == DevSubject;

    /// <summary>The certificate built into Setup (the resource <c>EchoDiar.cer</c>), or null in a build without one.</summary>
    public static X509Certificate2? EmbeddedCertificate()
    {
        using var stream = typeof(Installer).Assembly.GetManifestResourceStream("EchoDiar.cer");
        if (stream is null) return null;
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new X509Certificate2(memory.ToArray());
    }

    /// <summary>The elevated instance's whole job: add the embedded certificate to LocalMachine\TrustedPeople.</summary>
    public static void TrustCertificate()
    {
        using var certificate = EmbeddedCertificate()
            ?? throw new InvalidOperationException("This build of Setup has no EchoDiar certificate.");
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadWrite);
        store.Add(certificate);
    }

    /// <summary>The window's message for an error <see cref="Run"/> did not handle.</summary>
    public static InstallOutcome Failed(Exception error) => InstallOutcome.Failed(Explain(error.HResult, 0, error.Message));

    /// <summary>Runs every step and says how it ended; blocks, so the window calls it in <c>Task.Run</c>.</summary>
    /// <param name="progress">The current step and, when Windows reports it, how far (0–100).</param>
    public InstallOutcome Run(IProgress<(string Status, int? Percent)> progress)
    {
        if (_certificate is null) return InstallOutcome.Failed("This build of Setup has no EchoDiar certificate.");

        progress.Report(("Checking the EchoDiar certificate…", null));
        if (!IsTrusted(_certificate))
        {
            progress.Report(("Waiting for permission to trust the EchoDiar certificate…", null));
            if (TrustElevated() is { } refused) return InstallOutcome.Failed(refused);
            if (!IsTrusted(_certificate))
                return InstallOutcome.Failed("EchoDiar was not installed: Windows did not trust the EchoDiar certificate.");
        }

        progress.Report(("Checking the Microsoft Visual C++ Runtime…", null));
        var arm64 = IsArm64();
        if (VCLibsMissing(arm64))
        {
            const string installing = "Installing the Microsoft Visual C++ Runtime…";
            if (AddVCLibs(arm64, percent => progress.Report((installing, percent))) is { } failed)
                return InstallOutcome.Failed(Explain(failed.Code, failed.ExtendedCode, failed.Text));
        }

        const string installingEchoDiar = "Installing EchoDiar…";
        var before = InstalledEchoDiar(_certificate.Subject);
        progress.Report((installingEchoDiar, null));
        DeploymentFailure? failure;
        try
        {
            failure = AddFromFeed(_feed, percent => progress.Report((installingEchoDiar, percent)));
        }
        catch (Exception e) when (Known(e.HResult) is null)
        {
            // PackageManager cannot be used from here (a type-load or activation failure): Add-AppxPackage installs.
            Debug.WriteLine($"EchoDiar Setup: PackageManager failed ({e.GetType().Name} 0x{e.HResult:X8}), using PowerShell.");
            progress.Report((installingEchoDiar, null));
            failure = AddWithPowerShell(_feed);
        }
        if (failure is not null) return InstallOutcome.Failed(Explain(failure.Code, failure.ExtendedCode, failure.Text));

        var after = InstalledEchoDiar(_certificate.Subject);
        if (after is not { } installed) return InstallOutcome.Installed("EchoDiar is installed.", null);
        return before?.Version == installed.Version
            ? InstallOutcome.Installed("EchoDiar is up to date.", installed.FamilyName)
            : InstallOutcome.Installed($"EchoDiar {installed.Version.ToString(3)} is installed.", installed.FamilyName);
    }

    /// <summary>The certificate is in LocalMachine\TrustedPeople; reading that store needs no elevation.</summary>
    private static bool IsTrusted(X509Certificate2 certificate)
    {
        using var store = new X509Store(StoreName.TrustedPeople, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        // validOnly: false, as a self-signed certificate never chains to a root.
        return store.Certificates.Find(X509FindType.FindByThumbprint, certificate.Thumbprint, false).Count > 0;
    }

    /// <summary>Relaunches Setup elevated with only <c>--trust-certificate</c> and waits; null, or why it failed.</summary>
    private static string? TrustElevated()
    {
        var start = new ProcessStartInfo(typeof(Installer).Assembly.Location, SetupArguments.TrustCertificateArgument)
        {
            UseShellExecute = true,
            Verb = "runas",
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null) return "EchoDiar was not installed: Setup could not ask Windows to trust the EchoDiar certificate.";
            process.WaitForExit();
            return process.ExitCode == 0
                ? null
                : $"EchoDiar was not installed: Windows could not trust the EchoDiar certificate (0x{process.ExitCode:X8}).";
        }
        catch (Win32Exception e) when (e.NativeErrorCode == UacDeclined)
        {
            return "EchoDiar was not installed: Windows needs your permission to trust the EchoDiar certificate.";
        }
    }

    /// <summary>ARM64 Windows (also when this process runs x64-emulated there); x64 otherwise, and where
    /// IsWow64Process2 is missing (Windows 10 before 1709).</summary>
    private static bool IsArm64()
    {
        try
        {
            return IsWow64Process2(GetCurrentProcess(), out _, out var native) && native == ImageFileMachineArm64;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>VCLibs for this architecture is missing for the user, or older than <see cref="MinimumVCLibs"/>.
    /// When PackageManager cannot tell, the install goes on: a missing VCLibs then fails it with 0x80073CF3.</summary>
    private static bool VCLibsMissing(bool arm64)
    {
        try
        {
            return !HasVCLibs(arm64);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar Setup: VCLibs check failed ({e.GetType().Name} 0x{e.HResult:X8}).");
            return false;
        }
    }

    /// <summary>The installed EchoDiar (newest version and its package family), or null: not installed, or
    /// PackageManager cannot be used from here.</summary>
    private static (Version Version, string FamilyName)? InstalledEchoDiar(string publisher)
    {
        try
        {
            return FindInstalled(publisher);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"EchoDiar Setup: package query failed ({e.GetType().Name} 0x{e.HResult:X8}).");
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool HasVCLibs(bool arm64)
    {
        var architecture = arm64 ? Windows.System.ProcessorArchitecture.Arm64 : Windows.System.ProcessorArchitecture.X64;
        var packages = new PackageManager().FindPackagesForUserWithPackageTypes(string.Empty, VCLibsFamily, PackageTypes.Framework);
        foreach (var package in packages)
        {
            var id = package.Id;
            var version = new Version(id.Version.Major, id.Version.Minor, id.Version.Build, id.Version.Revision);
            if (id.Architecture == architecture && version >= MinimumVCLibs) return true;
        }
        return false;
    }

    /// <param name="publisher">The embedded certificate's subject, as Windows writes it (the package's Publisher).</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (Version Version, string FamilyName)? FindInstalled(string publisher)
    {
        (Version Version, string FamilyName)? newest = null;
        foreach (var package in new PackageManager().FindPackagesForUser(string.Empty, PackageName, publisher))
        {
            var id = package.Id;
            var version = new Version(id.Version.Major, id.Version.Minor, id.Version.Build, id.Version.Revision);
            if (newest is null || version > newest.Value.Version) newest = (version, id.FamilyName);
        }
        return newest;
    }

    /// <summary>VCLibs from Microsoft's download link (AddPackageByUriAsync takes http/https; AddPackageAsync only
    /// files, which would need a temporary copy).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DeploymentFailure? AddVCLibs(bool arm64, Action<int> percent)
    {
        var uri = new Uri($"https://aka.ms/Microsoft.VCLibs.{(arm64 ? "arm64" : "x64")}.14.00.Desktop.appx");
        return Wait(new PackageManager().AddPackageByUriAsync(uri, new AddPackageOptions()), percent);
    }

    /// <summary>Installs from the feed, so the installed app knows its .appinstaller and updates; a running
    /// EchoDiar is closed for it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DeploymentFailure? AddFromFeed(Uri feed, Action<int> percent)
    {
        var manager = new PackageManager();
        return Wait(manager.AddPackageByAppInstallerFileAsync(
            feed, AddPackageByAppInstallerOptions.ForceTargetAppShutdown, manager.GetDefaultPackageVolume()), percent);
    }

    /// <summary>Waits for a deployment and reads its result even when it failed (AsTask would throw it away with its
    /// extended code and text); null on success.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static DeploymentFailure? Wait(
        IAsyncOperationWithProgress<DeploymentResult, DeploymentProgress> operation, Action<int> percent)
    {
        var completed = new TaskCompletionSource<bool>();
        operation.Progress = (_, p) => percent((int)Math.Min(p.percentage, 100u));
        // Set after the operation has ended, Completed still runs (once).
        operation.Completed = (_, _) => completed.TrySetResult(true);
        completed.Task.Wait();

        DeploymentResult? result = null;
        try
        {
            result = operation.GetResults();
        }
        catch (Exception)
        {
            // A failed deployment may have no result: its ErrorCode below says what happened.
        }
        var code = operation.ErrorCode?.HResult ?? 0;
        var extendedCode = result?.ExtendedErrorCode?.HResult ?? 0;
        if (operation.Status == AsyncStatus.Completed && code == 0 && extendedCode == 0) return null;
        if (code == 0 && extendedCode == 0) code = EFail;
        return new DeploymentFailure(code, extendedCode, result?.ErrorText ?? operation.ErrorCode?.Message ?? "");
    }

    /// <summary>The fallback when PackageManager cannot be used from here: Add-AppxPackage as the user. The feed was
    /// checked by <see cref="SetupArguments"/>; its single quotes are doubled for PowerShell's literal string.</summary>
    private static DeploymentFailure? AddWithPowerShell(Uri feed)
    {
        var command = "Add-AppxPackage -AppInstallerFile '" + feed.AbsoluteUri.Replace("'", "''") + "'";
        // The full path: never a powershell.exe next to Setup (its Downloads folder).
        var start = new ProcessStartInfo(
            Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
            "-NoProfile -NonInteractive -Command \"" + command + "\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(start);
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode == 0) return null;

        // "Add-AppxPackage : Deployment failed with HRESULT: 0x80073CF3, …": its code and its first line.
        var hresult = Regex.Match(error, "0x8[0-9A-Fa-f]{7}");
        var code = hresult.Success ? Convert.ToInt32(hresult.Value, 16) : EFail;
        var text = error.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        return new DeploymentFailure(code, 0, text.Length > 0 ? text[0].Trim() : $"PowerShell exited with {process.ExitCode}.");
    }

    /// <summary>Windows' error as one message: a known code (either one) gets its own, anything else its code, its
    /// text and the docs link.</summary>
    private static string Explain(int code, int extendedCode, string text)
    {
        if ((Known(code) ?? Known(extendedCode)) is { } known) return known;
        var shown = code != 0 ? code : extendedCode;
        text = text.Trim();
        return text.Length > 0
            ? $"EchoDiar was not installed (0x{shown:X8}): {text}"
            : $"EchoDiar was not installed (0x{shown:X8}).";
    }

    private static string? Known(int code) => code switch
    {
        AlreadyExists => "A different build of this EchoDiar version is installed. Uninstall EchoDiar, then run Setup again.",
        UntrustedRoot => "Windows does not trust the certificate this EchoDiar version is signed with. "
            + "Download the latest EchoDiar-Setup.exe and run it again.",
        DependencyFailed => "EchoDiar needs the Microsoft Visual C++ Runtime (Microsoft.VCLibs.140.00.UWPDesktop), "
            + "which Windows could not install. Check your connection, then run Setup again.",
        _ when IsNetworkError(code) => "Could not download EchoDiar. Check your connection, or try again in a few minutes.",
        _ => null,
    };

    /// <summary>A download failure: an HTTP status (the feed's 404 is 0x80190194), URL Moniker, BITS, Delivery
    /// Optimization, or a Winsock, WinINet or WinHTTP error (Win32 10000–12999).</summary>
    private static bool IsNetworkError(int code)
    {
        if (code >= 0) return false;
        var facility = (code >> 16) & 0x1FFF;
        return facility is 25 or 12 or 32 or 208 || (facility == 7 && (code & 0xFFFF) is >= 10000 and < 13000);
    }

    /// <summary>A failed deployment as CLR values: Windows' error code, its extended code and its text.</summary>
    private sealed class DeploymentFailure
    {
        public DeploymentFailure(int code, int extendedCode, string text)
        {
            Code = code;
            ExtendedCode = extendedCode;
            Text = text;
        }

        public int Code { get; }

        public int ExtendedCode { get; }

        public string Text { get; }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWow64Process2(IntPtr process, out ushort processMachine, out ushort nativeMachine);
}
