using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Collectors.Windows;

public enum FocusState
{
    Active,
    Idle,
    Locked,
    Paused,
}

/// <summary>What the user is doing at a given instant.</summary>
public sealed record FocusSnapshot(
    FocusState State,
    string? ProcessName = null,
    string? Title = null,
    string? FilePath = null,
    string? Folder = null,
    string? Url = null)
{
    public static readonly FocusSnapshot Idle = new(FocusState.Idle);
    public static readonly FocusSnapshot Locked = new(FocusState.Locked);
    public static readonly FocusSnapshot Paused = new(FocusState.Paused);

    public bool SameActivityAs(FocusSnapshot other) =>
        State == other.State
        && string.Equals(ProcessName, other.ProcessName, StringComparison.OrdinalIgnoreCase)
        && NormalizeTitle(Title) == NormalizeTitle(other.Title)
        && string.Equals(FilePath, other.FilePath, StringComparison.OrdinalIgnoreCase);

    public ActivityEvent ToActivity(DateTime startUtc) => new()
    {
        Source = State switch
        {
            FocusState.Idle => ActivitySource.Idle,
            FocusState.Locked => ActivitySource.Locked,
            _ => ActivitySource.Focus,
        },
        StartUtc = startUtc,
        EndUtc = startUtc,
        ProcessName = ProcessName,
        Title = Title,
        FilePath = FilePath,
        Folder = Folder,
        Url = Url,
    };

    /// <summary>Strips unsaved-changes markers editors add to window titles ("● file", "*file").</summary>
    internal static string? NormalizeTitle(string? title) => title?.TrimStart('●', '*', ' ').Trim();
}
