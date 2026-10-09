namespace TimeSheetHelper.Collectors.Microsoft365;

public sealed class Microsoft365Options
{
    public const string SectionName = "Microsoft365";

    /// <summary>Entra ID tenant (GUID or domain). "organizations" also works for any work account.</summary>
    public string? TenantId { get; set; }

    /// <summary>Application (client) ID of the public client app registration.</summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Use the Windows broker (WAM) for single sign-on on Entra-joined machines. Requires the
    /// ms-appx-web://microsoft.aad.brokerplugin/{ClientId} redirect URI on the app registration.
    /// When false, the system browser is used with the http://localhost redirect URI.
    /// </summary>
    public bool UseBroker { get; set; } = true;

    /// <summary>Maximum number of chats scanned per Teams sync (most recently active first).</summary>
    public int MaxChats { get; set; } = 50;

    /// <summary>
    /// Extra domains treated as internal when picking the dominant external domain of a mail or meeting.
    /// The signed-in user's own domain is always internal.
    /// </summary>
    public List<string> InternalDomains { get; set; } = [];

    internal bool IsConfigured => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);
}
