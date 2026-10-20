using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.App.Services;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests.App;

/// <summary>
/// The day submission engine wired as in the application, on an in-memory database and <see cref="FakeForecastWriteApi"/>.
/// Each <see cref="Service"/> has its own <see cref="DayLock"/>, like another window or process sharing the database.
/// </summary>
internal sealed class DaySubmissionHost : IDisposable
{
    public static readonly DateOnly Monday = new(2026, 10, 5);

    public static readonly IReadOnlyDictionary<int, ConflictResolution> NoResolutions = new Dictionary<int, ConflictResolution>();

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public FakeForecastWriteApi Server { get; } = new();

    public TimeProvider Time { get; } = new FixedTime(new DateTimeOffset(2026, 10, 9, 15, 0, 0, TimeSpan.Zero));

    public ForecastTask Dev { get; } = new() { ExternalId = "T3382", ProjectName = "Portail", TaskName = "Développement" };

    public ForecastTask Meetings { get; } = new() { ExternalId = "T200", ProjectName = "Interne", TaskName = "Réunions" };

    public ForecastTask Support { get; } = new() { ExternalId = "T5100", ProjectName = "Soutien", TaskName = "Soutien" };

    /// <summary>Flagged "left out of the submission" (Forecast's MCP cannot register it).</summary>
    public ForecastTask Training { get; } = new() { ExternalId = "T6100", ProjectName = "Formation", TaskName = "Formation", ExcludeFromSubmission = true };

    /// <summary>The special "ignored" task: hours classified as not work.</summary>
    public ForecastTask Ignored { get; } = new() { ExternalId = ForecastTask.IgnoredExternalId, ProjectName = "—", TaskName = "Ignoré" };

    public DaySubmissionHost()
    {
        _connection.Open();
        using var db = Db();
        db.Database.EnsureCreated();
        db.ForecastTasks.AddRange(Dev, Meetings, Support, Training, Ignored);
        db.TimesheetDrafts.Add(new TimesheetDraft { WeekStart = Monday });
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    public TimeSheetDbContext Db() => new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite(_connection).Options);

    public DaySubmissionService Service() => Service(new DayLock(new Factory(Db), Time));

    public DaySubmissionService Service(DayLock dayLock)
    {
        var factory = new Factory(Db);
        var writer = new ForecastTimesheetWriter(
            new ForecastApi(new FakeForecastConnector(Server), Options.Create(new ForecastOptions()), NullLogger<ForecastApi>.Instance),
            factory, NullLogger<ForecastTimesheetWriter>.Instance);
        return new DaySubmissionService(factory, writer, dayLock, Time, NullLogger<DaySubmissionService>.Instance);
    }

    public DayLock NewLock() => new(new Factory(Db), Time);

    /// <summary>Replaces the draft cells of <paramref name="date"/>.</summary>
    public async Task SetDraftAsync(DateOnly date, params (ForecastTask Task, decimal Hours)[] cells)
    {
        await using var db = Db();
        var draft = await db.TimesheetDrafts.Include(d => d.Entries).SingleAsync();
        draft.Entries.RemoveAll(e => e.Date == date);
        draft.Entries.AddRange(cells.Select(c => new TimesheetEntry { Date = date, ForecastTaskId = c.Task.Id, Hours = c.Hours }));
        await db.SaveChangesAsync();
    }

    public async Task<decimal> DraftHoursAsync(DateOnly date, ForecastTask task)
    {
        await using var db = Db();
        return await db.TimesheetEntries.Where(e => e.Date == date && e.ForecastTaskId == task.Id).SumAsync(e => e.Hours);
    }

    public async Task SeedDayAsync(DateOnly date, DayStatus status, DateTime? inProgressSinceUtc = null, string? fingerprint = null,
        params (ForecastTask Task, decimal Hours, string? EntryId)[] cells)
    {
        await using var db = Db();
        db.DaySubmissions.Add(new DaySubmission
        {
            WeekStart = DaySubmissionService.WeekStartOf(date),
            Date = date,
            Status = status,
            InProgressSinceUtc = inProgressSinceUtc,
            Fingerprint = fingerprint,
            Cells = cells.Select(c => new DaySubmissionCell { ForecastTaskId = c.Task.Id, SentHours = c.Hours, ForecastEntryId = c.EntryId }).ToList(),
        });
        await db.SaveChangesAsync();
    }

    public async Task IgnoreForTheWeekAsync(ForecastTask task, bool ignore)
    {
        await using var db = Db();
        db.WeekTaskChoices.Add(new WeekTaskChoice { WeekStart = Monday, ForecastTaskId = task.Id, Ignore = ignore });
        await db.SaveChangesAsync();
    }

    public async Task<DaySubmission?> DayAsync(DateOnly date)
    {
        await using var db = Db();
        return await db.DaySubmissions.AsNoTracking().Include(d => d.Cells).SingleOrDefaultAsync(d => d.Date == date);
    }

    /// <summary>The registrations of <paramref name="date"/> as (task number, minutes), by task number.</summary>
    public IReadOnlyList<(int Task, int Minutes)> ForecastOn(DateOnly date) =>
        Server.Entries.Where(e => e.Date == date).Select(e => (e.TaskNumber, e.Minutes)).Order().ToList();

    private sealed class Factory(Func<TimeSheetDbContext> create) : IDbContextFactory<TimeSheetDbContext>
    {
        public TimeSheetDbContext CreateDbContext() => create();
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

/// <summary>
/// An in-memory Forecast MCP server for the anti-duplicate battery (WP12): registrations with stable ids and their task number,
/// a counter of the write calls, a truncated or failing read, a timeout at the n-th write, approved or invoiced registrations,
/// missing update/delete tools, and a barrier that holds a write until the test releases it.
/// </summary>
internal sealed class FakeForecastWriteApi : IForecastToolClient
{
    public const string MyId = "9";

    /// <summary>A task of Forecast: its number ("T3382" locally), its project.</summary>
    public sealed record RemoteTask(int Number, string Name, int ProjectId, string ProjectName);

    /// <summary>One registration of the signed-in person.</summary>
    public sealed record Registration(string Id, DateOnly Date, int Minutes, int TaskNumber, string? ApprovalStatus = null, bool Invoiced = false);

    public List<RemoteTask> Tasks { get; } =
    [
        new(3382, "Développement", 740, "Portail"),
        new(200, "Réunions", 12, "Interne"),
        new(5100, "Soutien", 51, "Soutien"),
        new(6100, "Formation", 61, "Formation"),
    ];

    public List<ForecastToolInfo> Tools { get; } =
    [
        Tool("get_person_tasks", """{"type":"object","properties":{"person":{"type":"string"},"date_from":{"type":"string"},"date_to":{"type":"string"}},"required":["person"]}"""),
        Tool("get_time_analysis", """{"type":"object","properties":{"start_date":{"type":"string"},"end_date":{"type":"string"},"person_id":{"type":"string"},"max_results":{"type":"integer"}},"required":["start_date","end_date"]}"""),
        Tool("plan_time_registration_create", """{"type":"object","properties":{"entries":{"type":"array","items":{"type":"object"}}},"required":["entries"]}"""),
        Tool("confirm_time_registration_create", """{"type":"object","properties":{"action_id":{"type":"string"}},"required":["action_id"]}"""),
        Tool("get_current_person", """{"type":"object","properties":{}}"""),
        Tool("update_time_entry", """{"type":"object","properties":{"time_entry_id":{"type":"integer"},"minutes":{"type":"integer"}},"required":["time_entry_id","minutes"]}"""),
        Tool("delete_time_entry", """{"type":"object","properties":{"time_entry_id":{"type":"integer"}},"required":["time_entry_id"]}"""),
    ];

    public List<Registration> Entries { get; } = [];

    /// <summary>Every write call that reached the server, applied or not: "create", "update" or "delete".</summary>
    public List<string> Writes { get; } = [];

    public int WriteCount => Writes.Count;

    public int Creates => Writes.Count(w => w == "create");

    public int Updates => Writes.Count(w => w == "update");

    public int Deletes => Writes.Count(w => w == "delete");

    /// <summary>"has_more" stays true: no read of a day is ever whole.</summary>
    public bool TruncatedRead { get; set; }

    /// <summary>"get_time_analysis" times out.</summary>
    public bool ReadFails { get; set; }

    private int _nextId = 7001;
    private readonly Dictionary<string, JsonElement> _staged = [];
    private (int Number, bool Applied)? _timeout;
    private (int Number, TaskCompletionSource Reached, TaskCompletionSource Release)? _hold;

    /// <summary>Adds a registration with a new stable id and returns it.</summary>
    public Registration Add(DateOnly date, decimal hours, int taskNumber, string? approvalStatus = null, bool invoiced = false)
    {
        var entry = new Registration((_nextId++).ToString(CultureInfo.InvariantCulture), date, (int)(hours * 60), taskNumber, approvalStatus, invoiced);
        Entries.Add(entry);
        return entry;
    }

    /// <summary>Forecast is changed outside the application (another tool, the web interface).</summary>
    public void ChangeElsewhere(string id, decimal hours)
    {
        var index = Entries.FindIndex(e => e.Id == id);
        Entries[index] = Entries[index] with { Minutes = (int)(hours * 60) };
    }

    /// <summary>The tools of the real forecast-mcp: creation only, nothing changes hours or removes a registration.</summary>
    public FakeForecastWriteApi WithoutEditTools()
    {
        Tools.RemoveAll(t => t.Name is "update_time_entry" or "delete_time_entry");
        return this;
    }

    /// <summary>The <paramref name="number"/>-th write call (1-based) times out; <paramref name="applied"/> says whether Forecast kept it anyway.</summary>
    public void TimeoutOnWrite(int number, bool applied) => _timeout = (number, applied);

    /// <summary>
    /// The <paramref name="number"/>-th write call waits at a barrier: the returned task completes when it is reached, the write goes on
    /// (or stops if its caller cancelled meanwhile) once <c>release()</c> is called.
    /// </summary>
    public (Task Reached, Action Release) HoldWrite(int number)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _hold = (number, reached, release);
        return (reached.Task, () => release.TrySetResult());
    }

    public Task<IReadOnlyList<ForecastToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ForecastToolInfo>>(Tools);

    public async Task<ForecastToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (toolName)
        {
            case "get_current_person":
                return Reply(new { found = true, person_id = long.Parse(MyId, CultureInfo.InvariantCulture), name = "Moi" });

            case "get_person_tasks":
                return Reply(new
                {
                    person_id = long.Parse(MyId, CultureInfo.InvariantCulture),
                    tasks = Tasks.Select(t => new { task_id = t.Number, task_name = t.Name, project_id = t.ProjectId, project_name = t.ProjectName }),
                    truncated = false,
                });

            case "get_time_analysis":
            {
                if (ReadFails)
                    throw new TimeoutException("Forecast ne répond pas.");
                var from = DateOnly.Parse((string)arguments["start_date"]!, CultureInfo.InvariantCulture);
                var to = DateOnly.Parse((string)arguments["end_date"]!, CultureInfo.InvariantCulture);
                var items = Entries.Where(e => e.Date >= from && e.Date <= to).Select(e =>
                {
                    var task = Tasks.Single(t => t.Number == e.TaskNumber);
                    return new
                    {
                        entry_id = long.Parse(e.Id, CultureInfo.InvariantCulture),
                        date = e.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        minutes = e.Minutes,
                        person_id = long.Parse(MyId, CultureInfo.InvariantCulture),
                        project = new { id = task.ProjectId, name = task.ProjectName },
                        task = new { id = 15990000 + task.Number, company_task_id = "T" + task.Number.ToString(CultureInfo.InvariantCulture) },
                        approval_status = e.ApprovalStatus ?? "NOT_SUBMITTED",
                        invoiced = e.Invoiced,
                    };
                }).ToList();
                return Reply(new { entry_count = items.Count, entries = items, has_more = TruncatedRead });
            }

            case "plan_time_registration_create":
            {
                var actionId = $"a{_staged.Count + 1}";
                _staged[actionId] = ((JsonElement)arguments["entries"]!)[0];
                return Reply(new { action_id = actionId, tier = "low", affected_count = 1, warnings = Array.Empty<string>() });
            }

            case "confirm_time_registration_create":
            {
                var staged = _staged[(string)arguments["action_id"]!];
                await WriteAsync("create", cancellationToken, () =>
                {
                    Add(DateOnly.Parse(staged.GetProperty("date").GetString()!, CultureInfo.InvariantCulture),
                        staged.GetProperty("minutes").GetInt32() / 60m, (int)staged.GetProperty("task_id").GetInt64());
                    return true;
                });
                return Reply(new { status = "done" });
            }

            case "update_time_entry":
            case "delete_time_entry":
            {
                var delete = toolName == "delete_time_entry";
                var id = Convert.ToString(arguments["time_entry_id"], CultureInfo.InvariantCulture);
                var applied = await WriteAsync(delete ? "delete" : "update", cancellationToken, () =>
                {
                    var index = Entries.FindIndex(e => e.Id == id);
                    if (index < 0)
                        return false;
                    if (delete)
                        Entries.RemoveAt(index);
                    else
                        Entries[index] = Entries[index] with { Minutes = Convert.ToInt32(arguments["minutes"], CultureInfo.InvariantCulture) };
                    return true;
                });
                return applied ? Reply(new { ok = true }) : Reply(new { ok = false, outcome = "failed", message = "not found" });
            }

            default:
                return new ForecastToolResult(true, $"unknown tool {toolName}", null);
        }
    }

    private async Task<bool> WriteAsync(string kind, CancellationToken cancellationToken, Func<bool> apply)
    {
        Writes.Add(kind);
        var number = Writes.Count;
        if (_hold is { } hold && hold.Number == number)
        {
            hold.Reached.TrySetResult();
            await hold.Release.Task;
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (_timeout is { } timeout && timeout.Number == number)
        {
            if (timeout.Applied)
                apply();
            throw new TimeoutException("Délai dépassé : la réponse de Forecast est perdue.");
        }

        return apply();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static ForecastToolResult Reply(object value)
    {
        var json = JsonSerializer.SerializeToElement(value);
        return new ForecastToolResult(false, json.GetRawText(), json);
    }

    private static ForecastToolInfo Tool(string name, string schema)
    {
        using var doc = JsonDocument.Parse(schema);
        return new ForecastToolInfo(name, null, doc.RootElement.Clone());
    }
}
