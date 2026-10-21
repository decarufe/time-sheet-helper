using TimeSheetHelper.App.Updates;

namespace TimeSheetHelper.Tests.App;

public sealed class UpdateTests
{
    private static AppVersion V(string text) => AppVersion.TryParse(text, out var v) ? v : throw new FormatException(text);

    [Theory]
    [InlineData("0.1.0-rc.1", "0.1.0")]
    [InlineData("0.1.0-rc.1", "0.1.0-rc.2")]
    [InlineData("0.1.0-rc.2", "0.1.0-rc.10")]
    [InlineData("0.1.0", "0.1.1")]
    [InlineData("0.9.9", "v0.10.0")]
    public void Older_version_sorts_before_newer(string older, string newer)
    {
        Assert.True(V(older) < V(newer));
        Assert.True(V(newer) > V(older));
    }

    [Fact]
    public void Build_metadata_is_ignored()
    {
        Assert.Equal(V("1.2.3+abc123"), V("1.2.3"));
        Assert.False(V("1.2.3+abc") > V("1.2.3+def"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.x.0")]
    [InlineData("1.0.0-")]
    public void Invalid_versions_are_rejected(string text) => Assert.False(AppVersion.TryParse(text, out _));

    private const string Feed = """
        [
          { "tag_name": "v0.3.0", "draft": true, "prerelease": false, "html_url": "https://x/0.3.0",
            "assets": [ { "name": "TimeSheetHelper-0.3.0.msi", "browser_download_url": "https://x/0.3.0.msi" } ] },
          { "tag_name": "v0.2.0-rc.1", "draft": false, "prerelease": true, "html_url": "https://x/0.2.0-rc.1",
            "assets": [ { "name": "TimeSheetHelper-0.2.0-rc.1.msi", "browser_download_url": "https://x/rc.msi" } ] },
          { "tag_name": "v0.1.5", "draft": false, "prerelease": false, "html_url": "https://x/0.1.5",
            "assets": [ { "name": "TimeSheetHelper-0.1.5.zip", "browser_download_url": "https://x/0.1.5.zip" } ] },
          { "tag_name": "v0.1.2", "draft": false, "prerelease": false, "html_url": "https://x/0.1.2",
            "assets": [
              { "name": "TimeSheetHelper-0.1.2.msi", "browser_download_url": "https://x/0.1.2.msi" },
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://x/sums" } ] },
          { "tag_name": "v0.1.0", "draft": false, "prerelease": false, "html_url": "https://x/0.1.0",
            "assets": [ { "name": "TimeSheetHelper-0.1.0.msi", "browser_download_url": "https://x/0.1.0.msi" } ] }
        ]
        """;

    [Fact]
    public void Stable_user_gets_newest_stable_release_with_an_installer()
    {
        var update = ReleaseFeed.FindUpdate(Feed, V("0.1.0"));

        Assert.NotNull(update);
        Assert.Equal(V("0.1.2"), update.Version);
        Assert.Equal("TimeSheetHelper-0.1.2.msi", update.MsiName);
        Assert.Equal("https://x/0.1.2.msi", update.MsiUrl);
        Assert.Equal("https://x/sums", update.ChecksumsUrl);
        Assert.Equal("https://x/0.1.2", update.PageUrl);
    }

    [Fact]
    public void Prerelease_user_also_sees_prereleases_but_never_drafts()
    {
        var update = ReleaseFeed.FindUpdate(Feed, V("0.1.0-rc.1"));

        Assert.Equal(V("0.2.0-rc.1"), update?.Version);
    }

    [Fact]
    public void Nothing_is_offered_when_up_to_date()
    {
        Assert.Null(ReleaseFeed.FindUpdate(Feed, V("0.1.2")));
        Assert.Null(ReleaseFeed.FindUpdate("[]", V("0.1.0")));
    }

    [Fact]
    public void Non_https_installer_is_ignored()
    {
        const string json = """[ { "tag_name": "v9.0.0", "assets": [ { "name": "a.msi", "browser_download_url": "http://x/a.msi" } ] } ]""";

        Assert.Null(ReleaseFeed.FindUpdate(json, V("0.1.0")));
    }

    [Fact]
    public void Checksum_is_found_in_sha256sum_output()
    {
        const string sums = "AAA111  TimeSheetHelper-1.0.0.zip\r\nBBB222 *TimeSheetHelper-1.0.0.msi\r\n";

        Assert.Equal("bbb222", ReleaseFeed.FindChecksum(sums, "TimeSheetHelper-1.0.0.msi"));
        Assert.Null(ReleaseFeed.FindChecksum(sums, "other.msi"));
    }

    [Fact]
    public void Installer_command_waits_for_the_app_and_quotes_paths()
    {
        var info = UpdateService.BuildInstallerStartInfo(@"C:\Users\O'Neil\x.msi", 1234, @"C:\App\TimeSheetHelper.exe");

        var script = info.ArgumentList[^1];
        Assert.Equal("powershell.exe", info.FileName);
        Assert.Contains("Wait-Process -Id 1234", script);
        Assert.Contains(@"'C:\Users\O''Neil\x.msi'", script);
        Assert.Contains(@"Start-Process 'C:\App\TimeSheetHelper.exe'", script);
        Assert.False(info.UseShellExecute);
    }
}
