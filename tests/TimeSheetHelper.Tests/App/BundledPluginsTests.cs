using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TimeSheetHelper.App.Plugins;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Plugins;
using TimeSheetHelper.Storage;
using TimeSheetHelper.Tests.DevOps;

namespace TimeSheetHelper.Tests.App;

/// <summary>Loads the plugins staged next to the application, exactly as the installed application does.</summary>
public sealed class BundledPluginsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly SourceCatalog _catalog = SourceCatalog.CreateDefault();

    public BundledPluginsTests()
    {
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private static string BundledRoot => Path.Combine(Path.GetDirectoryName(typeof(PluginHost).Assembly.Location)!, "plugins");

    private TimeSheetDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite(_connection).Options);

    private PluginHost CreateHost(Dictionary<string, string?>? settings = null) => new(
        new PluginRoots(BundledRoot, Path.Combine(Path.GetTempPath(), "tsh-none-" + Guid.NewGuid().ToString("N"))),
        new Factory(CreateDb),
        new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build(),
        new RecordingSink(),
        new FakeActivityHistory(),
        new FakeSecretStore(),
        NullLoggerFactory.Instance,
        TimeProvider.System,
        _catalog,
        NullLogger<PluginHost>.Instance);

    [Fact]
    public async Task The_application_ships_every_source_as_a_plugin_that_loads_cleanly()
    {
        var host = CreateHost();

        await host.StartForCommandLineAsync(default);

        Assert.Empty(host.Issues);
        Assert.Equal(["devops", "microsoft-365", "windows-activity"], host.Packages.Select(p => p.Manifest.Id).Order());
        Assert.All(host.Packages, p => Assert.True(p.IsBundled));
        Assert.Equal(
            ["azure-devops", "claude-code", "focus-tracker", "github", "local-git", "microsoft-365", "vscode"],
            host.Plugins.Select(p => p.Id).Order());

        // Pull sources start; the window tracker and the editor receiver wait for the real application.
        Assert.All(host.Plugins.Where(p => p.Descriptor.Mode == SourceExecutionMode.Pull),
            p => Assert.True(p.State == PluginRunState.Running, $"{p.Id}: {p.State} {p.Error}"));
        Assert.Equal(
            ["azuredevops", "calendar", "claudecode", "email", "github", "localgit", "teams"],
            host.Collectors.Select(c => c.Name).Order());
        await host.StopAsync(default);
    }

    [Fact]
    public async Task Plugin_descriptors_agree_with_the_built_in_traits_the_engine_falls_back_on()
    {
        var host = CreateHost();
        await host.StartForCommandLineAsync(default);

        var emitted = host.Plugins.SelectMany(p => p.Descriptor.Emits).ToList();

        Assert.Equal(
            ["AzureDevOps", "Calendar", "ClaudeCode", "Email", "Focus", "GitHub", "Idle", "LocalGit", "Locked", "Teams", "VsCode"],
            emitted.Select(e => e.Source.Id).Order(StringComparer.Ordinal));
        Assert.All(emitted, e => Assert.Equal(SourceCatalog.Default.Get(e.Source), e.Traits));
        await host.StopAsync(default);
    }

    [Fact]
    public async Task Legacy_appsettings_sections_still_configure_the_sources()
    {
        var host = CreateHost(new()
        {
            ["AzureDevOps:Organizations:0"] = "contoso",
            ["Microsoft365:TenantId"] = "tenant",
            ["Microsoft365:ClientId"] = "client",
            ["LocalGit:Roots:0"] = "C:\\repos*",
        });
        await host.StartForCommandLineAsync(default);

        Assert.Equal("contoso", host.GetConfigValues("azure-devops")["Organizations"]);
        Assert.Equal(("tenant", "client"), (host.GetConfigValues("microsoft-365")["TenantId"], host.GetConfigValues("microsoft-365")["ClientId"]));
        Assert.Equal("C:\\repos*", host.GetConfigValues("local-git")["Roots"]);
        // The Azure DevOps source has an organization now, so the only thing missing is its token (the fake store is empty).
        Assert.Contains("PAT", (await host.GetStatusAsync("azure-devops"))!.Message);
        await host.StopAsync(default);
    }

    private sealed class Factory(Func<TimeSheetDbContext> create) : IDbContextFactory<TimeSheetDbContext>
    {
        public TimeSheetDbContext CreateDbContext() => create();
    }
}
