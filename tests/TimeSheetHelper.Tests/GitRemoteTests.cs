using TimeSheetHelper.Collectors.Windows;

namespace TimeSheetHelper.Tests;

public class GitRemoteTests
{
    [Theory]
    [InlineData("https://github.com/contoso/billing.git", "contoso/billing")]
    [InlineData("https://github.com/contoso/billing", "contoso/billing")]
    [InlineData("git@github.com:contoso/billing.git", "contoso/billing")]
    [InlineData("ssh://git@github.com/contoso/billing.git", "contoso/billing")]
    [InlineData("https://dev.azure.com/org/Project%20X/_git/billing-api", "billing-api")]
    [InlineData("https://org.visualstudio.com/Project/_git/billing-api", "billing-api")]
    [InlineData("git@ssh.dev.azure.com:v3/org/Project/billing-api", "billing-api")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("not a url", null)]
    public void Normalize(string? url, string? expected) => Assert.Equal(expected, GitRemote.Normalize(url));

    [Fact]
    public void Parse_remote_prefers_origin()
    {
        string[] config =
        [
            "[core]",
            "\tbare = false",
            "[remote \"upstream\"]",
            "\turl = https://github.com/upstream/repo.git",
            "[remote \"origin\"]",
            "\turl = git@github.com:me/repo.git",
            "\tfetch = +refs/heads/*:refs/remotes/origin/*",
        ];
        Assert.Equal("git@github.com:me/repo.git", GitRemote.ParseRemoteUrl(config));
    }

    [Fact]
    public void Find_remote_walks_up_from_subfolder()
    {
        var root = Directory.CreateTempSubdirectory("tsh-git-");
        try
        {
            var git = Directory.CreateDirectory(Path.Combine(root.FullName, ".git"));
            File.WriteAllLines(Path.Combine(git.FullName, "config"), ["[remote \"origin\"]", "\turl = https://github.com/me/app.git"]);
            var sub = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "lib"));

            Assert.Equal("https://github.com/me/app.git", GitRemote.FindRemoteUrl(sub.FullName));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
