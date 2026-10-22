using System.Diagnostics;
using EchoDiar.Core;
using Microsoft.VisualBasic.FileIO;
using Windows.Win32;

namespace EchoDiar.Platform.SystemServices;

/// <summary>The Recycle Bin and File Explorer.</summary>
public sealed class ShellFiles : IFileTrash
{
    /// <summary>
    /// Moves a file or a folder to the Recycle Bin; only errors show a dialog. Throws like
    /// <see cref="FileSystem.DeleteFile(string, UIOption, RecycleOption)"/> (FileNotFoundException when nothing is there,
    /// IOException, UnauthorizedAccessException, OperationCanceledException when the user cancels an error dialog).
    /// </summary>
    public void MoveToTrash(string path)
    {
        if (Directory.Exists(path)) FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
    }

    /// <summary>
    /// Opens the folder that holds <paramref name="path"/> in File Explorer with the item selected
    /// (SHOpenFolderAndSelectItems; <c>explorer.exe /select,</c> when that fails, e.g. on a thread without COM).
    /// Throws Win32Exception when explorer.exe cannot start, ArgumentException for an invalid path.
    /// </summary>
    public static void Reveal(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!SelectInFolder(fullPath)) StartExplorer(fullPath);
    }

    private static unsafe bool SelectInFolder(string path)
    {
        var pidl = PInvoke.ILCreateFromPath(path);
        if (pidl is null) return false; // no such item
        try
        {
            // cidl 0: pidl is the single item to select in its parent folder.
            return PInvoke.SHOpenFolderAndSelectItems(pidl, 0, null, 0).Succeeded;
        }
        finally
        {
            PInvoke.ILFree(pidl);
        }
    }

    private static void StartExplorer(string path)
    {
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var info = new ProcessStartInfo(explorer) { UseShellExecute = false };
        info.ArgumentList.Add("/select,");
        info.ArgumentList.Add(path);
        using var process = Process.Start(info);
    }
}
