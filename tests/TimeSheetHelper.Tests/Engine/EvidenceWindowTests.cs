using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Engine.Allocation;
using TimeSheetHelper.Engine.Grouping;
using TimeSheetHelper.Engine.Rules;

namespace TimeSheetHelper.Tests.Engine;

public class EvidenceWindowTests
{
    private static ActivityResolver Create(Action<EngineOptions>? configure = null)
    {
        var options = Fx.Options(configure);
        return new ActivityResolver(new RuleEvaluator(options), new GroupKeyBuilder(options), options);
    }

    private static ActivityEvent Commit(DateTime at, string repo, ActivitySource source = ActivitySource.GitHub) =>
        Fx.Act(source, at, null, a => { a.Kind = "commit"; a.Repo = repo; });

    private static Interval WindowOf(ActivityResolver sut, ActivityEvent target, params ActivityEvent[] all) =>
        sut.ComputeEvidenceWindows(all.OrderBy(a => a.StartUtc).ToList())[target];

    [Fact]
    public void Commit_gets_fifteen_minutes()
    {
        var c = Commit(Fx.Mon(11), "contoso/a");
        Assert.Equal(new Interval(Fx.Mon(10, 45), Fx.Mon(11)), WindowOf(Create(), c, c));
    }

    [Fact]
    public void Commit_window_stops_at_previous_commit_of_same_repo()
    {
        var first = Commit(Fx.Mon(10, 55), "contoso/a");
        var second = Commit(Fx.Mon(11), "Contoso/A", ActivitySource.LocalGit);
        Assert.Equal(new Interval(Fx.Mon(10, 55), Fx.Mon(11)), WindowOf(Create(), second, first, second));
    }

    [Fact]
    public void Commit_of_another_repo_does_not_bound_the_window()
    {
        var other = Commit(Fx.Mon(10, 55), "contoso/b");
        var c = Commit(Fx.Mon(11), "contoso/a");
        Assert.Equal(Fx.Mon(10, 45), WindowOf(Create(), c, other, c).Start);
    }

    [Fact]
    public void Same_commit_reported_twice_keeps_its_window()
    {
        var local = Commit(Fx.Mon(11), "contoso/a", ActivitySource.LocalGit);
        var remote = Commit(Fx.Mon(11), "contoso/a");
        var sut = Create();
        Assert.Equal(Fx.Mon(10, 45), WindowOf(sut, remote, local, remote).Start);
        Assert.Equal(Fx.Mon(10, 45), WindowOf(sut, local, local, remote).Start);
    }

    [Theory]
    [InlineData(ActivitySource.Email, "sent", 10)]
    [InlineData(ActivitySource.Teams, "chat-message", 5)]
    [InlineData(ActivitySource.GitHub, "pr-review", 20)]
    [InlineData(ActivitySource.AzureDevOps, "workitem-update", 10)]
    [InlineData(ActivitySource.AzureDevOps, "unknown-kind", 5)]
    public void Default_windows_per_source_and_kind(ActivitySource source, string kind, int minutes)
    {
        var e = Fx.Act(source, Fx.Mon(11), Fx.Mon(11), a => a.Kind = kind); // End == Start is a point event too
        Assert.Equal(TimeSpan.FromMinutes(minutes), WindowOf(Create(), e, e).Length);
    }

    [Fact]
    public void Windows_are_configurable_by_source_and_kind()
    {
        var sut = Create(o =>
        {
            o.EvidenceWindows["Email"] = TimeSpan.FromMinutes(3);
            o.EvidenceWindows["Teams.chat-message"] = TimeSpan.FromMinutes(7);
        });
        var mail = Fx.Act(ActivitySource.Email, Fx.Mon(11), null);
        var chat = Fx.Act(ActivitySource.Teams, Fx.Mon(11), null, a => a.Kind = "chat-message");
        Assert.Equal(TimeSpan.FromMinutes(3), WindowOf(sut, mail, mail).Length);
        Assert.Equal(TimeSpan.FromMinutes(7), WindowOf(sut, chat, chat).Length);
    }

    [Fact]
    public void Resolver_assigns_roles()
    {
        var sut = Create();
        Assert.Equal(ActivityRole.Active, sut.RoleOf(Fx.Act(ActivitySource.VsCode, Fx.Mon(9), Fx.Mon(10))));
        Assert.Equal(ActivityRole.Mask, sut.RoleOf(Fx.Act(ActivitySource.Idle, Fx.Mon(9), Fx.Mon(10))));
        Assert.Equal(ActivityRole.Meeting, sut.RoleOf(Fx.Act(ActivitySource.Calendar, Fx.Mon(9), Fx.Mon(10))));
        Assert.Equal(ActivityRole.Evidence, sut.RoleOf(Fx.Act(ActivitySource.Email, Fx.Mon(9), null)));
        Assert.Null(sut.RoleOf(Fx.Act(ActivitySource.Calendar, Fx.Mon(9), Fx.Mon(10), a => a.Kind = "declined")));
    }
}
