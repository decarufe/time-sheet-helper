using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TimeSheetHelper.App.Plugins;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Storage;
using TimeSheetHelper.Tests.DevOps;
using TimeSheetHelper.TestPlugin;

namespace TimeSheetHelper.Tests.App;

public sealed class PluginHostTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tsh-plugins-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SourceCatalog _catalog = SourceCatalog.CreateDefault();

    public PluginHostTests()
    {
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
        Directory.CreateDirectory(Bundled);
        Directory.CreateDirectory(User);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A just-unloaded plugin may still be mapped; the temp folder is cleaned by the OS later.
        }
    }

    private string Bundled => Path.Combine(_root, "bundled");
    private string User => Path.Combine(_root, "user");

    private TimeSheetDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite(_connection).Options);

    private PluginHost CreateHost(Dictionary<string, string?>? settings = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        return new PluginHost(
            new PluginRoots(Bundled, User),
            new SharedFactory(CreateDb),
            configuration,
            new RecordingSink(),
            new FakeActivityHistory(),
            new FakeSecretStore(),
            NullLoggerFactory.Instance,
            TimeProvider.System,
            _catalog,
            NullLogger<PluginHost>.Instance);
    }

    /// <summary>Writes a plugin package (the test plugin assembly plus a manifest) under <paramref name="parent"/>.</summary>
    private static string WritePackage(string parent, string id = "test-pack", int apiVersion = 1, string? entry = null, string? folder = null)
    {
        var directory = Path.Combine(parent, folder ?? id);
        Directory.CreateDirectory(directory);
        var assembly = typeof(EchoSourcePlugin).Assembly.Location;
        File.Copy(assembly, Path.Combine(directory, Path.GetFileName(assembly)), overwrite: true);
        File.WriteAllText(Path.Combine(directory, "plugin.json"), $$"""
            { "id": "{{id}}", "name": "Paquet de test", "version": "1.2.3", "apiVersion": {{apiVersion}},
              "entryAssembly": "{{entry ?? Path.GetFileName(assembly)}}" }
            """);
        return directory;
    }

    [Fact]
    public async Task Startup_runs_the_enabled_sources_and_isolates_the_failing_and_foreign_ones()
    {
        WritePackage(Bundled);
        var host = CreateHost();

        await host.StartAsync(default);

        Assert.Equal(["echo", "elsewhere", "failing", "pusher"], host.Plugins.Select(p => p.Id).Order());
        Assert.Equal(PluginRunState.Running, host.Find("echo")!.State);
        Assert.Equal(PluginRunState.Failed, host.Find("failing")!.State);
        Assert.Equal("boom", host.Find("failing")!.Error);
        Assert.Equal(PluginRunState.Unsupported, host.Find("elsewhere")!.State);
        Assert.Equal(PluginRunState.Disabled, host.Find("pusher")!.State);
        Assert.Equal(["echo"], host.Collectors.Select(c => c.Name));
        Assert.Equal("echo", host.OwnerOf("ECHO")!.Id);
        Assert.Equal("test-pack", host.Packages.Single().Manifest.Id);
        Assert.True(host.Packages.Single().IsBundled);
        await host.StopAsync(default);
    }

    [Fact]
    public async Task Sources_declared_by_a_plugin_feed_the_catalog_with_their_traits()
    {
        WritePackage(Bundled);
        var host = CreateHost();

        await host.StartAsync(default);

        var traits = _catalog.Get(EchoSourcePlugin.Echo);
        Assert.Equal(("Écho", SourceBehavior.Presence, 2.5), (traits.DisplayName, traits.Behavior, traits.DefaultWeight));
        await host.StopAsync(default);
    }

    [Fact]
    public async Task Switching_a_source_off_stops_it_and_the_choice_survives_a_restart()
    {
        WritePackage(Bundled);
        var host = CreateHost();
        await host.StartAsync(default);

        await host.SetEnabledAsync("echo", false);

        Assert.Equal(PluginRunState.Disabled, host.Find("echo")!.State);
        Assert.Empty(host.Collectors);
        await host.StopAsync(default);

        var restarted = CreateHost();
        await restarted.StartAsync(default);
        Assert.False(restarted.Find("echo")!.Enabled);
        Assert.Empty(restarted.Collectors);
        await restarted.StopAsync(default);
    }

    [Fact]
    public async Task Switching_a_source_on_starts_it_without_restarting_the_host()
    {
        WritePackage(Bundled);
        var host = CreateHost();
        await host.StartAsync(default);
        Assert.Null(host.FindPushReceiver("pusher"));

        await host.SetEnabledAsync("pusher", true);

        var receiver = host.FindPushReceiver("pusher");
        Assert.NotNull(receiver);
        Assert.True(await receiver.ReceiveAsync("{}"u8.ToArray(), default));
        Assert.False(await receiver.ReceiveAsync("  "u8.ToArray(), default));

        await host.SetEnabledAsync("pusher", false);
        Assert.Null(host.FindPushReceiver("pusher"));
        await host.StopAsync(default);
    }

    [Fact]
    public async Task Settings_default_to_appsettings_and_saved_values_override_them_without_inheriting_a_longer_list()
    {
        WritePackage(Bundled);
        var host = CreateHost(new()
        {
            ["Echo:Greeting"] = "hello",
            ["Echo:Items:0"] = "a",
            ["Echo:Items:1"] = "b",
        });
        await host.StartAsync(default);

        Assert.Equal("hello|a,b|", await SyncWarningAsync(host));
        Assert.Equal("a\nb", host.GetConfigValues("echo")["Items"]);

        await host.SaveConfigAsync("echo", new Dictionary<string, string?> { ["Items"] = "z", ["Retries"] = "3" });

        Assert.Equal("hello|z|3", await SyncWarningAsync(host));
        await host.StopAsync(default);
    }

    [Fact]
    public async Task Saved_settings_restart_the_running_source_and_are_validated()
    {
        WritePackage(Bundled);
        var host = CreateHost(new() { ["Echo:Greeting"] = "hello" });
        await host.StartAsync(default);

        await host.SaveConfigAsync("echo", new Dictionary<string, string?> { ["Greeting"] = "salut" });

        var status = await host.GetStatusAsync("echo");
        Assert.Equal("salut", status!.Details!.Single().Value);
        await Assert.ThrowsAsync<ArgumentException>(() => host.SaveConfigAsync("echo", new Dictionary<string, string?> { ["Greeting"] = " " }));
        await Assert.ThrowsAsync<ArgumentException>(() => host.SaveConfigAsync("echo", new Dictionary<string, string?> { ["Retries"] = "many" }));
        await Assert.ThrowsAsync<ArgumentException>(() => host.SaveConfigAsync("nobody", new Dictionary<string, string?>()));
        Assert.Equal("salut", (await host.GetStatusAsync("echo"))!.Details!.Single().Value);
        await host.StopAsync(default);
    }

    [Fact]
    public async Task Actions_run_on_the_running_instance_only()
    {
        WritePackage(Bundled);
        var host = CreateHost();
        await host.StartAsync(default);

        Assert.Equal(new(true, "pong"), await host.ExecuteActionAsync("echo", "ping"));
        Assert.False((await host.ExecuteActionAsync("echo", "unknown")).Ok);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ExecuteActionAsync("pusher", "ping"));
        await host.StopAsync(default);
    }

    [Theory]
    [InlineData("future-api", 2, null, "version 2")]
    [InlineData("Bad_Id", 1, null, "Identifiant")]
    [InlineData("missing-dll", 1, "nothing.dll", "introuvable")]
    public async Task Unusable_packages_are_reported_and_ignored(string id, int apiVersion, string? entry, string expectedMessage)
    {
        WritePackage(Bundled, id, apiVersion, entry, folder: "broken");
        Directory.CreateDirectory(Path.Combine(Bundled, "no-manifest"));
        var host = CreateHost();

        await host.StartAsync(default);

        Assert.Empty(host.Plugins);
        Assert.Contains(host.Issues, i => i.Directory == "broken" && i.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(host.Issues, i => i.Directory == "no-manifest");
        await host.StopAsync(default);
    }

    [Fact]
    public async Task A_user_package_replaces_a_bundled_package_of_the_same_id()
    {
        WritePackage(Bundled);
        WritePackage(User);
        var host = CreateHost();

        await host.StartAsync(default);

        var package = Assert.Single(host.Packages);
        Assert.False(package.IsBundled);
        await host.StopAsync(default);
    }

    [Fact]
    public async Task A_package_installs_from_a_folder_or_a_zip_and_uninstalls()
    {
        var host = CreateHost();
        await host.StartAsync(default);
        var source = WritePackage(Path.Combine(_root, "source"));

        var result = await host.InstallAsync(source);

        Assert.Equal("test-pack", result.PackageId);
        Assert.Equal(PluginRunState.Running, host.Find("echo")!.State);
        var installed = Path.Combine(User, "test-pack");
        Assert.True(File.Exists(Path.Combine(installed, "plugin.json")));

        await host.UninstallAsync("test-pack");
        Assert.Empty(host.Plugins);
        Assert.Empty(host.Packages);
        Assert.True(!Directory.Exists(installed) || File.Exists(Path.Combine(installed, PluginLoader.RemovedMarker)));

        // The same package as an archive, with its files inside a folder, replaces nothing and starts running.
        var zip = Path.Combine(_root, "plugin.zip");
        ZipFile.CreateFromDirectory(Path.Combine(_root, "source"), zip, CompressionLevel.Fastest, includeBaseDirectory: false);
        await host.InstallAsync(zip);
        Assert.Equal(PluginRunState.Running, host.Find("echo")!.State);

        // Installing it again replaces the loaded package.
        await host.InstallAsync(source);
        Assert.Single(host.Packages);
        Assert.Equal(PluginRunState.Running, host.Find("echo")!.State);
        await host.StopAsync(default);
    }

    [Fact]
    public async Task Bundled_packages_cannot_be_uninstalled()
    {
        WritePackage(Bundled);
        var host = CreateHost();
        await host.StartAsync(default);

        await Assert.ThrowsAsync<PluginException>(() => host.UninstallAsync("test-pack"));
        await Assert.ThrowsAsync<PluginException>(() => host.UninstallAsync("unknown"));
        Assert.Equal(PluginRunState.Running, host.Find("echo")!.State);
        await host.StopAsync(default);
    }

    [Fact]
    public async Task A_bad_installation_changes_nothing()
    {
        WritePackage(Bundled);
        var host = CreateHost();
        await host.StartAsync(default);
        var future = WritePackage(Path.Combine(_root, "future"), apiVersion: 2);
        var empty = Path.Combine(_root, "empty.zip");
        using (var archive = ZipFile.Open(empty, ZipArchiveMode.Create))
            archive.CreateEntry("readme.txt");

        await Assert.ThrowsAsync<PluginException>(() => host.InstallAsync(future));
        await Assert.ThrowsAsync<PluginException>(() => host.InstallAsync(empty));
        await Assert.ThrowsAsync<PluginException>(() => host.InstallAsync(Path.Combine(_root, "does-not-exist")));

        Assert.Single(host.Packages);
        Assert.True(host.Packages.Single().IsBundled);
        Assert.Equal(PluginRunState.Running, host.Find("echo")!.State);
        await host.StopAsync(default);
    }

    [Fact]
    public async Task Command_line_start_only_runs_pull_sources()
    {
        WritePackage(Bundled);
        var first = CreateHost();
        await first.StartAsync(default);
        await first.SetEnabledAsync("pusher", true);
        await first.StopAsync(default);

        var host = CreateHost();
        await host.StartForCommandLineAsync(default);

        Assert.Equal(PluginRunState.Running, host.Find("echo")!.State);
        Assert.Equal(PluginRunState.Disabled, host.Find("pusher")!.State);
        await host.StopAsync(default);
    }

    private static async Task<string> SyncWarningAsync(PluginHost host)
    {
        var result = await host.Collectors.Single().SyncAsync(DateTime.UtcNow.AddDays(-1), DateTime.UtcNow);
        return result.Warnings.Single();
    }

    private sealed class SharedFactory(Func<TimeSheetDbContext> create) : IDbContextFactory<TimeSheetDbContext>
    {
        public TimeSheetDbContext CreateDbContext() => create();
    }
}
