using TimeSheetHelper.Collectors.Windows.VsCode;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Tests;

public class VsCodeSessionAggregatorTests
{
    private static readonly DateTime T0 = new(2026, 10, 5, 13, 0, 0, DateTimeKind.Utc);

    private static readonly VsCodeHeartbeat Editing = new()
    {
        SessionId = "s1",
        TimestampUtc = T0,
        WindowFocused = true,
        WorkspaceName = "billing",
        WorkspaceFolders = [@"C:\src\billing", @"C:\src\shared"],
        ActiveFile = @"C:\src\billing\Invoice.cs",
        LanguageId = "csharp",
        GitRemote = "git@github.com:contoso/billing.git",
        GitBranch = "feature/tax",
    };

    private readonly VsCodeSessionAggregator _sut = new();

    private static VsCodeHeartbeat At(VsCodeHeartbeat heartbeat, double seconds) =>
        heartbeat with { TimestampUtc = T0.AddSeconds(seconds) };

    [Fact]
    public void First_heartbeat_writes_nothing_until_segment_has_a_duration()
    {
        Assert.Empty(_sut.Observe(Editing));
        var segment = Assert.Single(_sut.Observe(At(Editing, 30)));

        Assert.Equal(ActivitySource.VsCode, segment.Source);
        Assert.Equal(VsCodeSessionAggregator.EditingKind, segment.Kind);
        Assert.Equal(@"C:\src\billing", segment.Folder);
        Assert.Equal(@"C:\src\billing\Invoice.cs", segment.FilePath);
        Assert.Equal("contoso/billing", segment.Repo);
        Assert.Equal("feature/tax", segment.Title);
        Assert.Equal(TimeSpan.FromSeconds(30), segment.Duration);
    }

    [Fact]
    public void Consecutive_heartbeats_extend_the_same_instance()
    {
        _sut.Observe(Editing);
        var first = Assert.Single(_sut.Observe(At(Editing, 30)));
        var second = Assert.Single(_sut.Observe(At(Editing, 60) with { EditsSinceLast = 4 }));

        Assert.Same(first, second);
        Assert.Equal(TimeSpan.FromSeconds(60), second.Duration);
    }

    [Fact]
    public void File_change_closes_segment_at_switch_and_starts_new_one()
    {
        _sut.Observe(Editing);
        var first = Assert.Single(_sut.Observe(At(Editing, 30)));
        var closed = Assert.Single(_sut.Observe(At(Editing, 45) with { ActiveFile = @"C:\src\billing\Tax.cs" }));

        Assert.Same(first, closed);
        Assert.Equal(T0.AddSeconds(45), closed.EndUtc);

        var next = Assert.Single(_sut.Observe(At(Editing, 75) with { ActiveFile = @"C:\src\billing\Tax.cs" }));
        Assert.NotSame(first, next);
        Assert.Equal(T0.AddSeconds(45), next.StartUtc);
        Assert.Equal(@"C:\src\billing\Tax.cs", next.FilePath);
    }

    [Fact]
    public void Ai_activity_is_a_separate_kind()
    {
        _sut.Observe(Editing);
        _sut.Observe(At(Editing, 30));
        _sut.Observe(At(Editing, 60) with { AiActivity = true });
        var ai = Assert.Single(_sut.Observe(At(Editing, 90) with { AiActivity = true }));

        Assert.Equal(VsCodeSessionAggregator.AiKind, ai.Kind);
        Assert.Equal(T0.AddSeconds(60), ai.StartUtc);
    }

    [Fact]
    public void Gap_longer_than_90_seconds_splits_segment()
    {
        _sut.Observe(Editing);
        var first = Assert.Single(_sut.Observe(At(Editing, 30)));

        Assert.Empty(_sut.Observe(At(Editing, 30 + 91)));
        var second = Assert.Single(_sut.Observe(At(Editing, 30 + 121)));

        Assert.NotSame(first, second);
        Assert.Equal(T0.AddSeconds(30), first.EndUtc);
        Assert.Equal(T0.AddSeconds(121), second.StartUtc);
    }

    [Fact]
    public void Gap_of_exactly_90_seconds_continues_segment()
    {
        _sut.Observe(Editing);
        var first = Assert.Single(_sut.Observe(At(Editing, 90)));
        Assert.Same(first, Assert.Single(_sut.Observe(At(Editing, 180))));
    }

    [Fact]
    public void Blur_ends_segment_and_next_focus_starts_fresh()
    {
        _sut.Observe(Editing);
        var first = Assert.Single(_sut.Observe(At(Editing, 30)));
        var blurred = Assert.Single(_sut.Observe(At(Editing, 40) with { WindowFocused = false }));

        Assert.Same(first, blurred);
        Assert.Equal(T0.AddSeconds(40), blurred.EndUtc);
        Assert.Null(_sut.Current);

        Assert.Empty(_sut.Observe(At(Editing, 50)));
        Assert.NotSame(first, Assert.Single(_sut.Observe(At(Editing, 80))));
    }

    [Fact]
    public void Same_file_in_another_window_continues_segment()
    {
        _sut.Observe(Editing);
        var first = Assert.Single(_sut.Observe(At(Editing, 30)));
        Assert.Same(first, Assert.Single(_sut.Observe(At(Editing, 60) with { SessionId = "s2" })));
    }

    [Fact]
    public void Out_of_order_heartbeat_is_ignored()
    {
        _sut.Observe(Editing);
        var first = Assert.Single(_sut.Observe(At(Editing, 60)));
        Assert.Empty(_sut.Observe(At(Editing, 30) with { ActiveFile = "other.cs" }));
        Assert.Same(first, Assert.Single(_sut.Observe(At(Editing, 90))));
    }

    [Fact]
    public void Missing_workspace_and_file_are_null()
    {
        var bare = Editing with { WorkspaceFolders = [], ActiveFile = "", GitRemote = null, GitBranch = null };
        _sut.Observe(bare);
        var segment = Assert.Single(_sut.Observe(At(bare, 30)));

        Assert.Null(segment.Folder);
        Assert.Null(segment.FilePath);
        Assert.Null(segment.Repo);
        Assert.Null(segment.Title);
    }
}
