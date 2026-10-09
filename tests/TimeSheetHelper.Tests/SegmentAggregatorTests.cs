using TimeSheetHelper.Collectors.Windows;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Tests;

public class SegmentAggregatorTests
{
    private static readonly DateTime T0 = new(2026, 10, 5, 13, 0, 0, DateTimeKind.Utc);
    private static readonly FocusSnapshot Word = new(FocusState.Active, "WINWORD", "Plan.docx - Word", @"C:\docs\Plan.docx", @"C:\docs");
    private static readonly FocusSnapshot Teams = new(FocusState.Active, "ms-teams", "Chat | Microsoft Teams");

    private readonly SegmentAggregator _sut = new(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30));

    [Fact]
    public void Switching_window_closes_previous_segment()
    {
        _sut.Observe(Word, T0);
        var written = _sut.Observe(Teams, T0.AddMinutes(5));

        var segment = Assert.Single(written);
        Assert.Equal(ActivitySource.Focus, segment.Source);
        Assert.Equal(@"C:\docs\Plan.docx", segment.FilePath);
        Assert.Equal(TimeSpan.FromMinutes(5), segment.Duration);
    }

    [Fact]
    public void Same_snapshot_is_ignored()
    {
        _sut.Observe(Word, T0);
        Assert.Empty(_sut.Observe(Word with { }, T0.AddMinutes(1)));
    }

    [Fact]
    public void Unsaved_marker_in_title_does_not_split_segment()
    {
        _sut.Observe(Word, T0);
        Assert.Empty(_sut.Observe(Word with { Title = "*Plan.docx - Word" }, T0.AddMinutes(1)));
    }

    [Fact]
    public void Short_interruption_is_dropped_and_activity_merged()
    {
        _sut.Observe(Word, T0);
        var first = Assert.Single(_sut.Observe(Teams, T0.AddMinutes(5)));
        Assert.Empty(_sut.Observe(Word, T0.AddMinutes(5).AddSeconds(5)));
        var merged = Assert.Single(_sut.Observe(Teams, T0.AddMinutes(10)));

        Assert.Same(first, merged);
        Assert.Equal(TimeSpan.FromMinutes(10), merged.Duration);
    }

    [Fact]
    public void Idle_detected_late_ends_active_segment_at_last_input()
    {
        _sut.Observe(Word, T0);
        var written = _sut.Observe(FocusSnapshot.Idle, T0.AddMinutes(3));

        Assert.Equal(T0.AddMinutes(3), Assert.Single(written).EndUtc);
        var idle = Assert.Single(_sut.Observe(Word, T0.AddMinutes(20)));
        Assert.Equal(ActivitySource.Idle, idle.Source);
        Assert.Equal(TimeSpan.FromMinutes(17), idle.Duration);
    }

    [Fact]
    public void Checkpoint_persists_open_segment_and_keeps_extending_it()
    {
        _sut.Observe(Word, T0);
        var first = Assert.Single(_sut.Checkpoint(T0.AddMinutes(1)));
        var second = Assert.Single(_sut.Checkpoint(T0.AddMinutes(2)));
        var final = Assert.Single(_sut.Flush(T0.AddMinutes(2).AddSeconds(30)));

        Assert.Same(first, second);
        Assert.Same(first, final);
        Assert.Equal(TimeSpan.FromSeconds(150), final.Duration);
    }

    [Fact]
    public void Pause_closes_segment_and_records_nothing()
    {
        _sut.Observe(Word, T0);
        Assert.Single(_sut.Observe(FocusSnapshot.Paused, T0.AddMinutes(1)));
        Assert.Null(_sut.Current);
        Assert.Empty(_sut.Flush(T0.AddMinutes(30)));
    }
}
