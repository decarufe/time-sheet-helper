using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;
using Microsoft.Identity.Client.Extensions.Msal;
using TimeSheetHelper.Core;

namespace TimeSheetHelper.Collectors.Microsoft365;

/// <summary>Supplies Graph access tokens to <see cref="GraphClient"/>. Abstracted so tests can run without MSAL.</summary>
internal interface IGraphAccessTokenProvider
{
    bool IsConfigured { get; }
    Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default);

    /// <summary>Silent acquisition only; throws <see cref="Microsoft365SignInRequiredException"/> when the user must sign in.</summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

public sealed class Microsoft365SignInRequiredException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Delegated sign-in to Microsoft Graph with MSAL (public client). Uses the Windows broker (WAM) when enabled,
/// otherwise the system browser with the http://localhost redirect. The token cache is persisted with DPAPI.
/// </summary>
public sealed class GraphAuthenticator(IOptionsMonitor<Microsoft365Options> options, ILogger<GraphAuthenticator> logger)
    : IGraphAccessTokenProvider
{
    public static readonly string[] Scopes = ["User.Read", "Calendars.Read", "Mail.ReadBasic", "Chat.ReadBasic", "ChatMessage.Read"];

    internal const string NotConfiguredMessage = "Inscription d'application Microsoft 365 manquante";
    internal const string SignInRequiredMessage = "Connexion Microsoft 365 requise";

    private const string CacheFileName = "msal-microsoft365.cache";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (string Key, IPublicClientApplication App)? _app;

    public bool IsConfigured => options.CurrentValue.IsConfigured;

    public async Task<bool> IsSignedInAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return false;
        try
        {
            await AcquireSilentAsync(cancellationToken);
            return true;
        }
        catch (Microsoft365SignInRequiredException)
        {
            return false;
        }
    }

    /// <summary>Signs in (silently when possible, otherwise interactively) and returns the account user name.</summary>
    public async Task<string?> SignInAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException(NotConfiguredMessage);

        try
        {
            return (await AcquireSilentAsync(cancellationToken)).Account?.Username;
        }
        catch (Microsoft365SignInRequiredException)
        {
            // Fall through to interactive sign-in.
        }

        var app = await GetAppAsync(cancellationToken);
        var builder = app.AcquireTokenInteractive(Scopes).WithPrompt(Prompt.SelectAccount);
        builder = options.CurrentValue.UseBroker
            ? builder.WithParentActivityOrWindow(GetParentWindow())
            : builder.WithUseEmbeddedWebView(false);

        var result = await builder.ExecuteAsync(cancellationToken);
        logger.LogInformation("Signed in to Microsoft 365 as {User}", result.Account?.Username);
        return result.Account?.Username;
    }

    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            return;
        var app = await GetAppAsync(cancellationToken);
        foreach (var account in await app.GetAccountsAsync())
            await app.RemoveAsync(account);
    }

    async Task<string> IGraphAccessTokenProvider.GetAccessTokenAsync(CancellationToken cancellationToken) =>
        (await AcquireSilentAsync(cancellationToken)).AccessToken;

    private async Task<AuthenticationResult> AcquireSilentAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured)
            throw new Microsoft365SignInRequiredException(NotConfiguredMessage);

        var app = await GetAppAsync(cancellationToken);
        var account = (await app.GetAccountsAsync()).FirstOrDefault()
            // With WAM, the Windows account of an Entra-joined machine gives SSO without any prompt.
            ?? (options.CurrentValue.UseBroker ? PublicClientApplication.OperatingSystemAccount : null);
        if (account is null)
            throw new Microsoft365SignInRequiredException(SignInRequiredMessage);

        try
        {
            return await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(cancellationToken);
        }
        catch (MsalUiRequiredException ex)
        {
            throw new Microsoft365SignInRequiredException(SignInRequiredMessage, ex);
        }
        catch (MsalClientException ex) when (ex.ErrorCode is "wam_runtime_init_failed" or "window_handle_required")
        {
            logger.LogWarning(ex, "Windows broker unavailable for silent sign-in");
            throw new Microsoft365SignInRequiredException(SignInRequiredMessage, ex);
        }
    }

    private async Task<IPublicClientApplication> GetAppAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        var key = $"{current.TenantId}|{current.ClientId}|{current.UseBroker}";

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_app is { } cached && cached.Key == key)
                return cached.App;

            var builder = PublicClientApplicationBuilder.Create(current.ClientId)
                .WithAuthority(AzureCloudInstance.AzurePublic, current.TenantId)
                .WithRedirectUri("http://localhost")
                .WithClientName("TimeSheetHelper");
            if (current.UseBroker)
            {
                builder = builder.WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows)
                {
                    Title = "Time Sheet Helper",
                });
            }

            var app = builder.Build();

            // DPAPI-protected file under %LOCALAPPDATA%\TimeSheetHelper (default on Windows).
            var storage = new StorageCreationPropertiesBuilder(CacheFileName, DataPaths.Root).Build();
            var cacheHelper = await MsalCacheHelper.CreateAsync(storage);
            cacheHelper.RegisterCache(app.UserTokenCache);

            _app = (key, app);
            return app;
        }
        finally
        {
            _gate.Release();
        }
    }

    // WAM needs a parent window. The app is a tray icon + browser UI, so the foreground window (the browser
    // that triggered the sign-in) is the best parent; the desktop window is the fallback.
    private static IntPtr GetParentWindow()
    {
        var handle = GetForegroundWindow();
        return handle != IntPtr.Zero ? handle : GetDesktopWindow();
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();
}
