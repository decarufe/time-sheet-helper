using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core;

namespace TimeSheetHelper.App.Updates;

public enum UpdatePhase { Idle, Checking, Downloading, Installing, Failed }

/// <summary>Polls GitHub releases (hourly by default) and installs the MSI of a newer release on request.</summary>
public sealed class UpdateService(
    HttpClient http,
    IOptions<UpdateOptions> options,
    IHostApplicationLifetime lifetime,
    ILogger<UpdateService> logger) : BackgroundService
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppVersion CurrentVersion { get; } = AppVersion.Current();
    public ReleaseInfo? Available { get; private set; }
    public UpdatePhase Phase { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Raised from a background thread whenever <see cref="Available"/>, <see cref="Phase"/> or <see cref="Error"/> changes.</summary>
    public event Action? Changed;

    public bool IsBusy => Phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = options.Value.Interval > TimeSpan.Zero ? options.Value.Interval : TimeSpan.FromHours(1);
        try
        {
            await Task.Delay(FirstCheckDelay, stoppingToken);
            using var timer = new PeriodicTimer(interval);
            do
            {
                if (options.Value.Enabled)
                    await CheckAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Checks GitHub for a newer release. Works even when automatic checks are disabled.</summary>
    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (!await _gate.WaitAsync(0, ct))
            return;
        try
        {
            SetPhase(UpdatePhase.Checking, null);
            var url = $"https://api.github.com/repos/{options.Value.Repository}/releases?per_page=20";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            AddHeaders(request);
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(ct);
            Available = ReleaseFeed.FindUpdate(json, CurrentVersion);
            SetPhase(UpdatePhase.Idle, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Update check failed");
            SetPhase(UpdatePhase.Failed, "Impossible de vérifier les mises à jour.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Downloads and verifies the MSI of <see cref="Available"/>, then restarts into the installer.</summary>
    public async Task InstallAsync(CancellationToken ct = default)
    {
        var release = Available;
        if (release is null || !await _gate.WaitAsync(0, ct))
            return;
        try
        {
            SetPhase(UpdatePhase.Downloading, null);
            var msiPath = await DownloadVerifiedAsync(release, ct);

            SetPhase(UpdatePhase.Installing, null);
            Process.Start(BuildInstallerStartInfo(msiPath, Environment.ProcessId, Environment.ProcessPath));
            lifetime.StopApplication();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Update to {Version} failed", release.Version);
            SetPhase(UpdatePhase.Failed, "La mise à jour a échoué : " + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> DownloadVerifiedAsync(ReleaseInfo release, CancellationToken ct)
    {
        if (release.ChecksumsUrl is null)
            throw new InvalidOperationException("la somme de contrôle SHA-256 est absente de la version.");

        var checksums = await GetStringAsync(release.ChecksumsUrl, ct);
        var expected = ReleaseFeed.FindChecksum(checksums, release.MsiName)
            ?? throw new InvalidOperationException("la somme de contrôle de l'installateur est introuvable.");

        var dir = Path.Combine(DataPaths.Root, "updates");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, Path.GetFileName(release.MsiName));
        var partial = target + ".part";

        using (var request = new HttpRequestMessage(HttpMethod.Get, release.MsiUrl))
        {
            AddHeaders(request);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(partial);
            await source.CopyToAsync(file, ct);
        }

        string actual;
        await using (var file = File.OpenRead(partial))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partial);
            throw new InvalidOperationException("la somme de contrôle de l'installateur ne correspond pas.");
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddHeaders(request);
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    private static void AddHeaders(HttpRequestMessage request)
    {
        request.Headers.UserAgent.ParseAdd("TimeSheetHelper-Updater");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>Waits for this process to exit, runs the per-user MSI (which relaunches the app), and restarts the old exe if it fails.</summary>
    internal static ProcessStartInfo BuildInstallerStartInfo(string msiPath, int processId, string? exePath)
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";

        var script = $"Wait-Process -Id {processId} -ErrorAction SilentlyContinue; "
            + $"$p = Start-Process msiexec.exe -ArgumentList ('/i', ('\"' + {Quote(msiPath)} + '\"'), '/passive') -Wait -PassThru; ";
        if (exePath is not null)
            script += $"if ($p.ExitCode -ne 0) {{ Start-Process {Quote(exePath)} }}";

        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var a in new[] { "-NoProfile", "-WindowStyle", "Hidden", "-Command", script })
            info.ArgumentList.Add(a);
        return info;
    }

    private void SetPhase(UpdatePhase phase, string? error)
    {
        Phase = phase;
        Error = error;
        Changed?.Invoke();
    }
}
