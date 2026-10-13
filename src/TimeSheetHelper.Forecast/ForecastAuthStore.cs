using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Authentication;

namespace TimeSheetHelper.Forecast;

/// <summary>
/// The client registered with Forecast's authorization server (dynamic registration) and the OAuth tokens,
/// kept in one file encrypted for the current Windows user (DPAPI). Registering once and reusing the client id
/// is required for the refresh token to keep working across runs.
/// </summary>
public sealed class ForecastAuthStore(IOptions<ForecastOptions> options) : ITokenCache
{
    private static readonly byte[] Entropy = "TimeSheetHelper.Forecast.Mcp"u8.ToArray();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private State? _state;

    public async ValueTask<TokenContainer?> GetTokensAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return Load().Tokens;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask StoreTokensAsync(TokenContainer tokens, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = Load();
            state.Tokens = tokens;
            Save(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? ClientId => Snapshot().ClientId;

    /// <summary>True when there is something to sign in with: an access token that may still work, or a refresh token.</summary>
    public bool HasCredentials => Snapshot().Tokens is { } t && (!string.IsNullOrEmpty(t.RefreshToken) || !string.IsNullOrEmpty(t.AccessToken));

    public async Task StoreClientAsync(string clientId, string? clientSecret, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var state = Load();
            state.ClientId = clientId;
            state.ClientSecret = clientSecret;
            state.RedirectUri = options.Value.RedirectUri.ToString();
            Save(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public string? ClientSecret => Snapshot().ClientSecret;

    /// <summary>Forgets the tokens and the registered client (sign out).</summary>
    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _state = new State();
            var file = options.Value.ResolvedAuthFile;
            if (File.Exists(file))
                File.Delete(file);
        }
        finally
        {
            _gate.Release();
        }
    }

    private State Snapshot()
    {
        _gate.Wait();
        try
        {
            var state = Load();
            // A client registered for another redirect URI cannot be reused.
            return state.RedirectUri is { } uri && uri != options.Value.RedirectUri.ToString() ? new State() : state;
        }
        finally
        {
            _gate.Release();
        }
    }

    private State Load()
    {
        if (_state is not null)
            return _state;

        var file = options.Value.ResolvedAuthFile;
        try
        {
            if (File.Exists(file))
            {
                var json = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
                _state = JsonSerializer.Deserialize<State>(json) ?? new State();
                return _state;
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable (other user, corrupted): sign in again.
        }
        return _state = new State();
    }

    private void Save(State state)
    {
        _state = state;
        var file = options.Value.ResolvedAuthFile;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(state), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(file, bytes);
    }

    private sealed class State
    {
        public string? ClientId { get; set; }
        public string? ClientSecret { get; set; }
        public string? RedirectUri { get; set; }
        public TokenContainer? Tokens { get; set; }
    }
}
