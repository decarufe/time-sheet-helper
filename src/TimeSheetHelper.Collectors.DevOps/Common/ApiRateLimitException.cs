namespace TimeSheetHelper.Collectors.DevOps.Common;

/// <summary>Raised when a remote API refuses further calls for now; collectors stop gracefully and report a warning.</summary>
public sealed class ApiRateLimitException(string message, DateTime? resetUtc = null) : Exception(message)
{
    public DateTime? ResetUtc { get; } = resetUtc;
}
