using TimeSheetHelper.Collectors.DevOps.Common;
using TimeSheetHelper.Core.Secrets;

namespace TimeSheetHelper.Collectors.DevOps.GitHub;

/// <summary>Token from the secret store, falling back to the GitHub CLI session (<c>gh auth token</c>).</summary>
public sealed class GitHubTokenProvider(ISecretStore secrets, IProcessRunner processes)
{
    public async Task<string?> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        var stored = secrets.Get(SecretNames.GitHubToken);
        if (!string.IsNullOrWhiteSpace(stored))
        {
            return stored.Trim();
        }

        var result = await processes.RunAsync("gh", ["auth", "token"], cancellationToken: cancellationToken);
        var token = result is { Succeeded: true } ? result.StandardOutput.Trim() : null;
        return string.IsNullOrEmpty(token) ? null : token;
    }
}
