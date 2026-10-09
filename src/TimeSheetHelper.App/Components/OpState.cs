namespace TimeSheetHelper.App.Components;

/// <summary>State of a long operation shown by <see cref="OpStatus"/>: running (with a message), succeeded or failed.</summary>
public sealed record OpState(bool Busy, string? Message, string? Error)
{
    public static readonly OpState Idle = new(false, null, null);
}
