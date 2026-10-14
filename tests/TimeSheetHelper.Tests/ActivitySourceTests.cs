using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests;

public sealed class ActivitySourceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public ActivitySourceTests()
    {
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private TimeSheetDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite(_connection).Options);

    private ActivityStore Store() => new(new Factory(CreateDb));

    private static ActivityEvent Event(ActivitySource source, string externalId, string title) => new()
    {
        Source = source,
        StartUtc = new DateTime(2026, 10, 5, 14, 0, 0, DateTimeKind.Utc),
        ExternalId = externalId,
        Title = title,
    };

    [Fact]
    public async Task A_source_a_plugin_invented_is_stored_and_read_back_as_its_identifier()
    {
        var store = Store();
        await store.WriteAsync(Event(new ActivitySource("Jira"), "J-1", "ticket"));
        await store.WriteAsync(Event(ActivitySource.GitHub, "J-1", "same external id, another source"));

        await using var db = CreateDb();
        Assert.Equal(["GitHub", "Jira"], db.Database.SqlQuery<string>($"SELECT Source AS Value FROM Activities ORDER BY Source").ToList());
        var read = await store.GetRangeAsync(new DateTime(2026, 10, 5, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal([ActivitySource.GitHub, new ActivitySource("Jira")], read.Select(a => a.Source).Order());
    }

    [Fact]
    public async Task The_same_external_id_from_the_same_source_updates_instead_of_duplicating()
    {
        var store = Store();
        await store.WriteAsync(Event(ActivitySource.Calendar, "evt", "first"));
        await store.WriteAsync(Event(ActivitySource.Calendar, "evt", "second"));

        await using var db = CreateDb();
        Assert.Equal("second", (await db.Activities.SingleAsync()).Title);
    }

    [Fact]
    public void Unknown_sources_are_plain_evidence_labelled_with_their_identifier_and_known_ones_keep_their_traits()
    {
        var catalog = SourceCatalog.CreateDefault();

        Assert.Equal(new SourceTraits("Jira"), catalog.Get(new ActivitySource("Jira")));
        Assert.Equal(SourceBehavior.Presence, catalog.Get(ActivitySource.VsCode).Behavior);
        Assert.True(catalog.Get(ActivitySource.VsCode).IsEditing);
        Assert.Equal(SourceBehavior.NonWork, catalog.Get(ActivitySource.Idle).Behavior);
        Assert.Equal(SourceBehavior.Meeting, catalog.Get(ActivitySource.Calendar).Behavior);

        catalog.Register(new ActivitySource("Jira"), new SourceTraits("Jira Cloud", SourceBehavior.Presence, DefaultWeight: 0.5));
        Assert.Equal("Jira Cloud", catalog.Get(new ActivitySource("Jira")).DisplayName);
        Assert.Contains(new ActivitySource("Jira"), catalog.Known);
        Assert.Equal(new SourceTraits("Jira"), SourceCatalog.Default.Get(new ActivitySource("Jira")));
    }

    private sealed class Factory(Func<TimeSheetDbContext> create) : IDbContextFactory<TimeSheetDbContext>
    {
        public TimeSheetDbContext CreateDbContext() => create();
    }
}
