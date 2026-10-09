using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Engine.Grouping;

namespace TimeSheetHelper.Tests.Engine;

public class GroupKeyBuilderTests
{
    private readonly GroupKeyBuilder _sut = new(Fx.Options(o => o.OwnDomains = ["contoso.com"]));

    private static ActivityEvent Make(ActivitySource source, Action<ActivityEvent> configure) =>
        Fx.Act(source, Fx.Mon(9), Fx.Mon(10), configure);

    [Fact]
    public void Repo_wins_over_folder() =>
        Assert.Equal("repo:contoso/api", _sut.Build(Make(ActivitySource.VsCode, a => { a.Repo = " Contoso/API "; a.Folder = @"C:\src\api"; })));

    [Fact]
    public void Folder_is_normalized() =>
        Assert.Equal(@"folder:c:\src\api", _sut.Build(Make(ActivitySource.Focus, a => { a.Folder = @"C:\Src\Api\"; a.ProcessName = "Code"; })));

    [Fact]
    public void Meeting_subject_is_normalized()
    {
        var a = _sut.Build(Make(ActivitySource.Calendar, a => { a.Subject = "RE: Sprint 42 Review!"; a.Participants = "x@fabrikam.com"; }));
        var b = _sut.Build(Make(ActivitySource.Calendar, a => a.Subject = "Sprint 43 review"));
        Assert.Equal("meeting:sprint review", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Email_groups_by_external_domains()
    {
        var key = _sut.Build(Make(ActivitySource.Email, a =>
        {
            a.Subject = "Proposal";
            a.Participants = "me@contoso.com; Bob <bob@Fabrikam.com>; carol@adatum.com; dave@fabrikam.com";
        }));
        Assert.Equal("domain:adatum.com,fabrikam.com", key);
    }

    [Fact]
    public void Internal_only_email_groups_by_people()
    {
        var key = _sut.Build(Make(ActivitySource.Teams, a => a.Participants = "Zoe@contoso.com;alice@contoso.com"));
        Assert.Equal("people:alice@contoso.com,zoe@contoso.com", key);
    }

    [Fact]
    public void Process_and_title_stem_is_the_last_resort()
    {
        var a = _sut.Build(Make(ActivitySource.Focus, a => { a.ProcessName = "WINWORD"; a.Title = "● Plan 2026.docx - Word"; }));
        var b = _sut.Build(Make(ActivitySource.Focus, a => { a.ProcessName = "winword"; a.Title = "Plan 2027.docx - Word"; }));
        Assert.Equal("app:winword:plan .docx", a);
        Assert.Equal(a, b);
    }

    [Theory]
    [InlineData("Chat | Microsoft Teams", "chat")]
    [InlineData("Inbox - alice@contoso.com - Outlook", "inbox")]
    [InlineData("", "")]
    public void Title_stem(string title, string expected) => Assert.Equal(expected, GroupKeyBuilder.TitleStem(title));

    [Fact]
    public void Group_key_round_trips()
    {
        Assert.True(GroupKey.TryParse(@"folder:c:\src\api", out var key));
        Assert.Equal(GroupKind.Folder, key.Kind);
        Assert.Equal(@"c:\src\api", key.Value);
        Assert.False(GroupKey.TryParse("nope", out _));
    }
}
