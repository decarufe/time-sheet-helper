using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;

namespace TimeSheetHelper.Forecast;

public enum ForecastLoginState
{
    LoggedIn,
    LoginRequired,
    Unknown,
}

/// <summary>
/// Owns the Playwright driver and a persistent Edge profile (so the Forecast/SSO session survives between runs).
/// Only one process can use the profile at a time: the session is a singleton and must be disposed.
/// </summary>
public sealed class ForecastSession(IOptions<ForecastOptions> options, ILogger<ForecastSession> logger) : IAsyncDisposable
{
    private readonly ForecastOptions _options = options.Value;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPlaywright? _playwright;
    private IBrowserContext? _context;
    private bool _headless;
    private bool _disposed;

    public ForecastOptions Options => _options;

    /// <summary>The single page used for automation (opened on demand).</summary>
    public async Task<IPage> GetPageAsync(CancellationToken cancellationToken = default)
    {
        await OpenAsync(_options.Headless, cancellationToken);
        return _context!.Pages.FirstOrDefault() ?? await _context.NewPageAsync();
    }

    /// <summary>
    /// Navigates to the timesheets page and makes sure the user is logged in. When not logged in, (re)opens a
    /// visible window and waits up to <see cref="ForecastOptions.LoginTimeout"/> for the user to complete the login.
    /// </summary>
    public async Task<IPage> EnsureLoggedInAsync(CancellationToken cancellationToken = default)
    {
        var page = await GetPageAsync(cancellationToken);
        await GotoAsync(page, _options.TimesheetsPath);

        var state = await DetectLoginStateAsync(page, TimeSpan.FromSeconds(20), cancellationToken);
        if (state == ForecastLoginState.LoggedIn)
            return page;

        if (_headless)
        {
            logger.LogInformation("Session Forecast expirée : ouverture d'une fenêtre visible pour la connexion.");
            await OpenAsync(headless: false, cancellationToken, forceReopen: true);
            page = await GetPageAsync(cancellationToken);
            await GotoAsync(page, _options.TimesheetsPath);
        }

        logger.LogWarning(
            "Connectez-vous à Forecast dans la fenêtre Edge ouverte (délai : {Minutes} min).",
            (int)_options.LoginTimeout.TotalMinutes);
        await page.BringToFrontAsync();

        var deadline = DateTime.UtcNow + _options.LoginTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await DetectLoginStateAsync(page, TimeSpan.FromSeconds(2), cancellationToken) == ForecastLoginState.LoggedIn)
            {
                // After SSO the app may land elsewhere (home, my-work): go back to the timesheets.
                if (!page.Url.Contains(_options.TimesheetsPath, StringComparison.OrdinalIgnoreCase))
                    await GotoAsync(page, _options.TimesheetsPath);
                logger.LogInformation("Connecté à Forecast.");
                return page;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        throw new TimeoutException(
            $"Connexion à Forecast non détectée après {(int)_options.LoginTimeout.TotalMinutes} minutes.");
    }

    /// <summary>
    /// Logged in when the URL is on the Forecast host (not a login/SSO page) and an app-only element is visible.
    /// UNVERIFIED heuristic: see <see cref="ForecastSelectors.LoggedInMarker"/>.
    /// </summary>
    public async Task<ForecastLoginState> DetectLoginStateAsync(IPage page, TimeSpan wait, CancellationToken cancellationToken = default)
    {
        var appHost = new Uri(_options.BaseUrl).Host;
        var deadline = DateTime.UtcNow + wait;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = page.Url;
            var onAppHost = Uri.TryCreate(url, UriKind.Absolute, out var uri)
                            && uri.Host.Equals(appHost, StringComparison.OrdinalIgnoreCase);

            if (!onAppHost || ForecastSelectors.LoginUrl.IsMatch(url))
                return ForecastLoginState.LoginRequired;

            if (await IsVisibleAsync(ForecastSelectors.LoginForm(page)))
                return ForecastLoginState.LoginRequired;

            if (await IsVisibleAsync(ForecastSelectors.LoggedInMarker(page)))
                return ForecastLoginState.LoggedIn;

            await Task.Delay(500, cancellationToken);
        } while (DateTime.UtcNow < deadline);

        return ForecastLoginState.Unknown;
    }

    public async Task GotoAsync(IPage page, string path)
    {
        await page.GotoAsync(_options.Url(path), new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await WaitForIdleAsync(page);
    }

    /// <summary>Forecast is a single-page app talking GraphQL: wait for the network to calm down, but never fail on it.</summary>
    public static async Task WaitForIdleAsync(IPage page, float timeoutMs = 10_000)
    {
        try
        {
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = timeoutMs });
        }
        catch (TimeoutException)
        {
            // Long-polling/websockets can keep the network busy forever.
        }
    }

    internal static async Task<bool> IsVisibleAsync(ILocator locator)
    {
        try
        {
            return await locator.First.IsVisibleAsync();
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private async Task OpenAsync(bool headless, CancellationToken cancellationToken, bool forceReopen = false)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_context is not null && !forceReopen)
                return;

            if (_context is not null)
            {
                await _context.CloseAsync();
                _context = null;
            }

            _playwright ??= await Playwright.CreateAsync();
            Directory.CreateDirectory(_options.ResolvedUserDataDir);
            logger.LogDebug("Ouverture d'Edge (profil {Dir}, headless={Headless}).", _options.ResolvedUserDataDir, headless);

            _context = await _playwright.Chromium.LaunchPersistentContextAsync(_options.ResolvedUserDataDir, new()
            {
                Channel = _options.Channel,
                Headless = headless,
                SlowMo = _options.SlowMo,
                ViewportSize = new() { Width = 1600, Height = 1000 },
            });
            _context.SetDefaultTimeout((float)_options.ActionTimeout.TotalMilliseconds);
            _headless = headless;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the browser (releases the profile lock). The session can be reopened afterwards.</summary>
    public async Task CloseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_context is not null)
            {
                try
                {
                    await _context.CloseAsync();
                }
                catch (PlaywrightException)
                {
                    // Browser already closed by the user.
                }

                _context = null;
            }

            _playwright?.Dispose();
            _playwright = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await CloseAsync();
        _gate.Dispose();
    }
}
