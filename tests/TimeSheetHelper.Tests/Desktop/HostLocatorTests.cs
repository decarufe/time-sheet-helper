using System.Net;
using System.Net.Sockets;
using TimeSheetHelper.Desktop;

namespace TimeSheetHelper.Tests.Desktop;

public sealed class HostLocatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tsh-desktop-" + Guid.NewGuid().ToString("N"));

    public HostLocatorTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private void Write(string file, string json) => File.WriteAllText(Path.Combine(_directory, file), json);

    [Fact]
    public void Without_any_setting_the_default_address_is_used()
    {
        Assert.Equal(new Uri(HostLocator.DefaultUrl), HostLocator.ResolveUrl(_directory, []));
    }

    [Fact]
    public void The_command_line_wins_over_local_settings_which_win_over_the_shipped_ones()
    {
        Write("appsettings.json", """{ "Urls": "http://localhost:6001" }""");
        Assert.Equal(new Uri("http://localhost:6001"), HostLocator.ResolveUrl(_directory, []));

        Write("appsettings.Local.json", """{ "Urls": "http://localhost:6002;http://localhost:6003" }""");
        Assert.Equal(new Uri("http://localhost:6002"), HostLocator.ResolveUrl(_directory, []));

        Assert.Equal(new Uri("http://127.0.0.1:6004"), HostLocator.ResolveUrl(_directory, ["--url", "http://127.0.0.1:6004"]));
    }

    [Fact]
    public void Unusable_settings_fall_back_to_the_next_source()
    {
        Write("appsettings.json", """{ "Urls": "http://localhost:6001" }""");
        Write("appsettings.Local.json", "{ not json");

        Assert.Equal(new Uri("http://localhost:6001"), HostLocator.ResolveUrl(_directory, []));
        Assert.Equal(new Uri("http://localhost:6001"), HostLocator.ResolveUrl(_directory, ["--url", "ftp://nope"]));
    }

    [Fact]
    public void A_wildcard_binding_is_reached_through_localhost()
    {
        Write("appsettings.json", """{ "Urls": "http://*:5178" }""");

        Assert.Equal(new Uri("http://localhost:5178"), HostLocator.ResolveUrl(_directory, []));
    }

    [Theory]
    [InlineData("http://localhost:5178/taches", true)]
    [InlineData("HTTP://LOCALHOST:5178/", true)]
    [InlineData("http://localhost:5179/", false)]
    [InlineData("https://localhost:5178/", false)]
    [InlineData("http://example.com:5178/", false)]
    public void Only_the_applications_own_origin_stays_in_the_window(string candidate, bool expected)
    {
        Assert.Equal(expected, HostLocator.IsSameOrigin(new Uri(candidate), new Uri(HostLocator.DefaultUrl)));
    }

    [Theory]
    [InlineData("https://github.com/x", true)]
    [InlineData("http://example.com", true)]
    [InlineData("file:///C:/Windows/System32/cmd.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("mailto:someone@example.com", false)]
    public void Only_web_links_are_handed_to_the_default_browser(string uri, bool expected)
    {
        Assert.Equal(expected, HostLocator.IsOpenableExternally(new Uri(uri)));
    }

    [Fact]
    public async Task Listening_is_detected_and_a_running_application_is_not_started_again()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var url = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        try
        {
            Assert.True(await HostLocator.IsListeningAsync(url, default));
            await HostLocator.EnsureRunningAsync(url, Path.Combine(_directory, "missing.exe"), TimeSpan.FromSeconds(1), default);
        }
        finally
        {
            listener.Stop();
        }

        Assert.False(await HostLocator.IsListeningAsync(url, default));
    }

    [Fact]
    public async Task A_missing_executable_is_reported_when_nothing_listens()
    {
        var free = new TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            HostLocator.EnsureRunningAsync(new Uri($"http://127.0.0.1:{port}"), Path.Combine(_directory, "missing.exe"), TimeSpan.FromSeconds(1), default));
    }
}
