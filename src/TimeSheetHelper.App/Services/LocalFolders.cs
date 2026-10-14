using System.Diagnostics;
using TimeSheetHelper.Core;

namespace TimeSheetHelper.App.Services;

/// <summary>Opens app data folders in Explorer, never anything outside the data folder.</summary>
public static class LocalFolders
{
    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it (after normalization).</summary>
    public static bool IsUnder(string path, string root)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Opens a folder of the app data directory in Explorer (or selects a file in it).</summary>
    public static void OpenInExplorer(string path)
    {
        if (!IsUnder(path, DataPaths.Root))
            throw new InvalidOperationException("Seuls les dossiers de données de Time Sheet Helper peuvent être ouverts.");

        var full = Path.GetFullPath(path);
        var startInfo = File.Exists(full)
            ? new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select," + full } }
            : new ProcessStartInfo("explorer.exe") { ArgumentList = { full } };
        Process.Start(startInfo)?.Dispose();
    }
}
