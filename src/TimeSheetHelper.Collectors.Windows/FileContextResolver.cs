using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TimeSheetHelper.Collectors.Windows;

public sealed record FileContext(string? FilePath, string? Folder, string? Url = null);

/// <summary>Finds the file/folder behind the foreground window: Office via COM, Explorer via Shell, otherwise title patterns.</summary>
public sealed class FileContextResolver(IOptionsMonitor<FocusTrackerOptions> options, ILogger<FileContextResolver> logger)
{
    private static readonly TimeSpan ComTimeout = TimeSpan.FromSeconds(2);

    public async Task<FileContext> ResolveAsync(string processName, IntPtr hwnd, string title)
    {
        try
        {
            switch (processName.ToUpperInvariant())
            {
                case "WINWORD":
                    return FromPathOrTitle(await RunComAsync(() => GetOfficeDocument("Word.Application", app => app.ActiveDocument)), processName, title, hwnd);
                case "EXCEL":
                    return FromPathOrTitle(await RunComAsync(() => GetOfficeDocument("Excel.Application", app => app.ActiveWorkbook)), processName, title, hwnd);
                case "POWERPNT":
                    return FromPathOrTitle(await RunComAsync(() => GetOfficeDocument("PowerPoint.Application", app => app.ActivePresentation)), processName, title, hwnd);
                case "EXPLORER":
                    var folder = await RunComAsync(() => GetExplorerFolder(hwnd, title));
                    if (folder is not null) return new FileContext(null, folder);
                    break;
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or TimeoutException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            logger.LogDebug(ex, "COM resolution failed for {Process}", processName);
        }

        return FromTitle(processName, title);
    }

    internal FileContext FromTitle(string processName, string title)
    {
        if (!options.CurrentValue.TitlePatterns.TryGetValue(processName, out var pattern))
            return new FileContext(null, null);

        var match = Regex.Match(title, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
        if (!match.Success)
            return new FileContext(null, null);

        return new FileContext(
            match.Groups["file"] is { Success: true } f ? f.Value : null,
            match.Groups["folder"] is { Success: true } d ? d.Value : null);
    }

    internal static FileContext FromPath(string path)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
        {
            var lastSlash = path.LastIndexOf('/');
            return new FileContext(path, lastSlash > 0 ? path[..lastSlash] : null, path);
        }
        return new FileContext(path, Path.GetDirectoryName(path));
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Process, IntPtr Hwnd, string Title), FileContext> lastKnown = new();

    private FileContext FromPathOrTitle(string? path, string processName, string title, IntPtr hwnd)
    {
        var key = (processName, hwnd, title);
        if (path is not null)
            return lastKnown[key] = FromPath(path);

        // Office was busy or has no document: keep what we knew for this very window and title.
        if (lastKnown.TryGetValue(key, out var known))
            return known;
        return FromTitle(processName, title);
    }

    private static async Task<string?> RunComAsync(Func<string?> action)
    {
        // Office can block COM calls while a modal dialog is open; never let that stall the tracker.
        var task = Task.Factory.StartNew(() => RetryWhenBusy(action), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        return await task.WaitAsync(ComTimeout);
    }

    private const int ServerCallRetryLater = unchecked((int)0x8001010A);
    private const int CallRejected = unchecked((int)0x80010001);
    private const int BusyAttempts = 4;

    /// <summary>
    /// Office answers "busy" (RPC_E_SERVERCALL_RETRYLATER) while a cell is being edited or a dialog is open.
    /// Retries briefly, then returns null so the caller falls back to the last known context or the title.
    /// </summary>
    private static string? RetryWhenBusy(Func<string?> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (COMException ex) when (ex.HResult is ServerCallRetryLater or CallRejected)
            {
                if (attempt >= BusyAttempts)
                    return null;
                Thread.Sleep(150);
            }
        }
    }

    private static string? GetOfficeDocument(string progId, Func<dynamic, dynamic?> getDocument)
    {
        var app = NativeMethods.GetActiveComObject(progId);
        if (app is null) return null;
        try
        {
            dynamic? document = getDocument(app);
            return document is null ? null : (string)document.FullName;
        }
        finally
        {
            Marshal.ReleaseComObject(app);
        }
    }

    private static string? GetExplorerFolder(IntPtr hwnd, string title)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType is null) return null;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            string? fallback = null;
            foreach (dynamic window in shell.Windows())
            {
                if ((long)window.HWND != hwnd.ToInt64()) continue;

                string? path = window.Document?.Folder?.Self?.Path;
                // Windows 11 tabs share one HWND: prefer the tab whose name matches the window title.
                if (title.StartsWith((string)window.LocationName, StringComparison.OrdinalIgnoreCase))
                    return path;
                fallback ??= path;
            }
            return fallback;
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }
}
