using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Secrets;
using TimeSheetHelper.Engine;
using TimeSheetHelper.Engine.Classification;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests.Engine;

/// <summary>Fixed clock in America/Toronto.</summary>
internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");

    public override DateTimeOffset GetUtcNow() => now;
    public override TimeZoneInfo LocalTimeZone => Toronto;
}

internal sealed class FakeClassifier : ITaskClassifier
{
    public bool IsAvailable { get; set; } = true;
    public List<ActivityGroup> Calls { get; } = [];
    public Func<ActivityGroup, ClassificationResult?> Answer { get; set; } = _ => null;

    public Task<ClassificationResult?> ClassifyAsync(ActivityGroup group, IReadOnlyList<ForecastTask> tasks, IReadOnlyList<Rule> rules, CancellationToken cancellationToken = default)
    {
        Calls.Add(group);
        return Task.FromResult(Answer(group));
    }
}

internal sealed class MemorySecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _values = [];
    public string? Get(string name) => _values.GetValueOrDefault(name);
    public void Set(string name, string value) => _values[name] = value;
    public void Delete(string name) => _values.Remove(name);
}

/// <summary>Shared helpers: Toronto local times on the week of Monday 2026-10-05.</summary>
internal static class Fx
{
    public static readonly DateOnly Monday = new(2026, 10, 5);

    public static DateTime Local(DateOnly day, int hour, int minute = 0, int second = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(new TimeOnly(hour, minute, second)), FixedTimeProvider.Toronto);

    public static DateTime Mon(int hour, int minute = 0, int second = 0) => Local(Monday, hour, minute, second);

    public static readonly FixedTimeProvider Clock = new(new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.Zero));

    public static IOptions<EngineOptions> Options(Action<EngineOptions>? configure = null)
    {
        var options = new EngineOptions { TimeZoneId = "America/Toronto" };
        configure?.Invoke(options);
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    private static long _nextId = 1000;

    public static ActivityEvent Act(ActivitySource source, DateTime start, DateTime? end, Action<ActivityEvent>? configure = null)
    {
        var a = new ActivityEvent { Id = Interlocked.Increment(ref _nextId), Source = source, StartUtc = start, EndUtc = end };
        configure?.Invoke(a);
        return a;
    }
}

/// <summary>Full engine on an in-memory SQLite database, with a fake classifier and a fixed clock.</summary>
internal sealed class EngineTestHost : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    public ServiceProvider Services { get; }
    public FakeClassifier Classifier { get; } = new();

    public EngineTestHost(Dictionary<string, string?>? settings = null)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Engine:TimeZoneId"] = "America/Toronto" })
            .AddInMemoryCollection(settings ?? [])
            .Build();

        var services = new ServiceCollection();
        services.AddDbContextFactory<TimeSheetDbContext>(o => o.UseSqlite(_connection));
        services.AddSingleton<ActivityStore>();
        services.AddSingleton<TimeProvider>(Fx.Clock);
        services.AddSingleton<ITaskClassifier>(Classifier);
        services.AddTimesheetEngine(config);
        Services = services.BuildServiceProvider();

        using var db = Db();
        db.Database.EnsureCreated();
    }

    public TimeSheetDbContext Db() => Services.GetRequiredService<IDbContextFactory<TimeSheetDbContext>>().CreateDbContext();

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public async Task<ForecastTask> AddTaskAsync(string project, string task, string? client = null)
    {
        await using var db = Db();
        var t = new ForecastTask { ExternalId = $"{project}/{task}", ProjectName = project, TaskName = task, ClientName = client };
        db.ForecastTasks.Add(t);
        await db.SaveChangesAsync();
        return t;
    }

    public async Task<Rule> AddRuleAsync(RuleField field, RuleOperator op, string pattern, int taskId, int order = 10)
    {
        await using var db = Db();
        var r = new Rule { Name = pattern, Field = field, Operator = op, Pattern = pattern, ForecastTaskId = taskId, Order = order };
        db.Rules.Add(r);
        await db.SaveChangesAsync();
        return r;
    }

    public async Task AddActivitiesAsync(params ActivityEvent[] activities)
    {
        await using var db = Db();
        foreach (var a in activities)
        {
            a.Id = 0;
            db.Activities.Add(a);
        }
        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
