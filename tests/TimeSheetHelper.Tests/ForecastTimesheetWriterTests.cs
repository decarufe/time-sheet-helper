using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests;

/// <summary>An in-memory Forecast MCP server with the tools of the real one: two tasks, the registrations of two people.</summary>
internal sealed class FakeForecastClient : IForecastToolClient
{
    public const string MyId = "9";
    public const string DevProject = "740";
    public const string MeetingsProject = "12";

    public List<ForecastToolInfo> Tools { get; } =
    [
        Tool("get_person_tasks", """{"type":"object","properties":{"person":{"type":"string"},"date_from":{"type":"string"},"date_to":{"type":"string"},"project":{"type":"string"}},"required":["person"]}"""),
        Tool("get_time_analysis", """{"type":"object","properties":{"start_date":{"type":"string"},"end_date":{"type":"string"},"person_id":{"type":"string"},"max_results":{"type":"integer"}},"required":["start_date","end_date"]}"""),
        Tool("plan_time_registration_create", """{"type":"object","properties":{"entries":{"type":"array","items":{"type":"object"}}},"required":["entries"]}"""),
        Tool("confirm_time_registration_create", """{"type":"object","properties":{"action_id":{"type":"string"}},"required":["action_id"]}"""),
        Tool("get_current_person", """{"type":"object","properties":{}}"""),
    ];

    /// <summary>The registrations (project id, day, minutes, person id).</summary>
    public List<(string ProjectId, DateOnly Date, int Minutes, string PersonId)> Entries { get; } = [];

    public List<(string Tool, IReadOnlyDictionary<string, object?> Args)> Calls { get; } = [];

    public bool ConfirmFails { get; set; }

    /// <summary>The registrations staged with the "plan" tool.</summary>
    public IEnumerable<JsonElement> Creates =>
        Calls.Where(c => c.Tool == "plan_time_registration_create").Select(c => ((JsonElement)c.Args["entries"]!)[0]);

    public int Confirms => Calls.Count(c => c.Tool == "confirm_time_registration_create");

    private readonly Dictionary<string, JsonElement> _staged = [];

    private static ForecastToolInfo Tool(string name, string schema)
    {
        using var doc = JsonDocument.Parse(schema);
        return new ForecastToolInfo(name, null, doc.RootElement.Clone());
    }

    private static ForecastToolResult Ok(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return new ForecastToolResult(false, json, doc.RootElement.Clone());
    }

    private static string ProjectOf(long taskId) => taskId == 200 ? MeetingsProject : DevProject;

    public Task<IReadOnlyList<ForecastToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ForecastToolInfo>>(Tools);

    public Task<ForecastToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        Calls.Add((toolName, arguments));
        switch (toolName)
        {
            case "get_person_tasks":
                return Task.FromResult(Ok("""
                    {"person_id":9,"tasks":[
                      {"task_id":3382,"task_name":"Développement","project_id":740,"project_name":"Portail"},
                      {"task_id":3383,"task_name":"Revue","project_id":740,"project_name":"Portail"},
                      {"task_id":200,"task_name":"Réunions","project_id":12,"project_name":"Interne"}],"truncated":false}
                    """));
            case "get_current_person":
                return Task.FromResult(Ok($$"""{"found":true,"person_id":{{MyId}},"name":"Moi"}"""));
            case "get_time_analysis":
                var from = DateOnly.Parse((string)arguments["start_date"]!);
                var to = DateOnly.Parse((string)arguments["end_date"]!);
                var entries = Entries
                    .Where(e => e.Date >= from && e.Date <= to)
                    .Select((e, i) => new
                    {
                        entry_id = i + 1,
                        date = e.Date.ToString("yyyy-MM-dd"),
                        person = new { id = long.Parse(e.PersonId), name = "x" },
                        project = new { id = long.Parse(e.ProjectId), name = "p" },
                        task_id = 15992000 + i,
                        total_hours = e.Minutes / 60.0,
                    });
                return Task.FromResult(Ok(JsonSerializer.Serialize(new { entries, has_more = false })));
            case "plan_time_registration_create":
                var staged = ((JsonElement)arguments["entries"]!)[0];
                var actionId = $"a{_staged.Count + 1}";
                _staged[actionId] = staged;
                return Task.FromResult(Ok($$"""{"action_id":"{{actionId}}","tier":"low","affected_count":1,"warnings":[]}"""));
            case "confirm_time_registration_create":
                if (ConfirmFails)
                    return Task.FromResult(new ForecastToolResult(true, "boom", null));
                var entry = _staged[(string)arguments["action_id"]!];
                Entries.Add((
                    ProjectOf(entry.GetProperty("task_id").GetInt64()),
                    DateOnly.Parse(entry.GetProperty("date").GetString()!),
                    entry.GetProperty("minutes").GetInt32(),
                    MyId));
                return Task.FromResult(Ok("""{"status":"done"}"""));
            default:
                throw new InvalidOperationException($"Outil inattendu : {toolName}");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeForecastConnector(IForecastToolClient client) : IForecastConnector
{
    public int Connects { get; private set; }

    public bool HasCredentials => true;

    public Task<IForecastToolClient> ConnectAsync(bool interactive, Action<string>? progress = null, CancellationToken cancellationToken = default)
    {
        Connects++;
        return Task.FromResult(client);
    }

    public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class ForecastTimesheetWriterTests : IDisposable
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly FakeForecastClient _server = new();
    private readonly FakeForecastConnector _connector;
    private readonly ForecastTask _dev = new() { ExternalId = "T3382", ProjectName = "Portail", ClientName = "Acme", TaskName = "Développement" };
    private readonly ForecastTask _review = new() { ExternalId = "T3383", ProjectName = "Portail", TaskName = "Revue" };
    private readonly ForecastTask _meetings = new() { ExternalId = "T200", ProjectName = "Interne", TaskName = "Réunions" };

    public ForecastTimesheetWriterTests()
    {
        _connector = new FakeForecastConnector(_server);
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
        db.ForecastTasks.AddRange(_dev, _meetings, _review);
        db.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private TimeSheetDbContext CreateDb() =>
        new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite(_connection).Options);

    private sealed class Factory(Func<TimeSheetDbContext> create) : IDbContextFactory<TimeSheetDbContext>
    {
        public TimeSheetDbContext CreateDbContext() => create();
    }

    private ForecastTimesheetWriter Writer() =>
        new(new ForecastApi(_connector, Options.Create(new ForecastOptions()), NullLogger<ForecastApi>.Instance),
            new Factory(CreateDb), NullLogger<ForecastTimesheetWriter>.Instance);

    private static TimesheetEntry Entry(ForecastTask task, int day, decimal hours, string? notes = null) =>
        new() { ForecastTaskId = task.Id, Date = Monday.AddDays(day), Hours = hours, Notes = notes };

    private TimesheetDraft Draft(params TimesheetEntry[] entries) => new() { WeekStart = Monday, Entries = [.. entries] };

    private static ForecastCellResult Cell(ForecastFillReport report, ForecastTask task, int day) =>
        report.Cells.Single(c => c.ForecastTaskId == task.Id && c.Date == Monday.AddDays(day));

    [Fact]
    public async Task Dry_run_reads_but_registers_nothing()
    {
        var report = await Writer().FillWeekAsync(
            Draft(Entry(_dev, 0, 2m), Entry(_meetings, 0, 1m)), ForecastSubmitMode.DryRun);

        Assert.Equal(2, report.Cells.Count);
        Assert.All(report.Cells, c => Assert.Equal(ForecastCellStatus.Planned, c.Status));
        Assert.Empty(_server.Creates);
        Assert.Empty(_server.Entries);
    }

    [Fact]
    public async Task Fill_registers_empty_cells_in_minutes_and_reads_them_back()
    {
        var report = await Writer().FillWeekAsync(
            Draft(Entry(_dev, 0, 2m, "Revue de PR"), Entry(_dev, 1, 1.5m), Entry(_meetings, 0, 1m)), ForecastSubmitMode.Fill);

        Assert.True(report.Success, report.Summary);
        Assert.Equal(3, report.Cells.Count);
        Assert.All(report.Cells, c => Assert.Equal(ForecastCellStatus.Ok, c.Status));

        var creates = _server.Creates.ToList();
        Assert.Equal(3, creates.Count);
        Assert.Equal(3, _server.Confirms);
        var first = creates.Single(a => a.GetProperty("task_id").GetInt64() == 3382 && a.GetProperty("date").GetString() == "2026-10-05");
        Assert.Equal(120, first.GetProperty("minutes").GetInt32());
        Assert.Equal("Revue de PR", first.GetProperty("notes").GetString());
        Assert.Equal(90, creates.Single(a => a.GetProperty("date").GetString() == "2026-10-06").GetProperty("minutes").GetInt32());
        Assert.Equal(200, creates.Single(a => a.GetProperty("task_id").GetInt64() == 200).GetProperty("task_id").GetInt64());
    }

    [Fact]
    public async Task Fill_never_touches_cells_that_already_hold_hours()
    {
        _server.Entries.Add((FakeForecastClient.DevProject, Monday, 120, FakeForecastClient.MyId));          // same hours: already done
        _server.Entries.Add((FakeForecastClient.DevProject, Monday.AddDays(1), 30, FakeForecastClient.MyId)); // other hours: conflict
        _server.Entries.Add((FakeForecastClient.MeetingsProject, Monday, 300, "99"));                         // someone else's time: ignored

        var report = await Writer().FillWeekAsync(
            Draft(Entry(_dev, 0, 2m), Entry(_dev, 1, 1m), Entry(_meetings, 0, 1m), Entry(_meetings, 2, 1m)), ForecastSubmitMode.Fill);

        Assert.False(report.Success);
        Assert.Equal(ForecastCellStatus.Ok, Cell(report, _dev, 0).Status);
        var conflict = Cell(report, _dev, 1);
        Assert.Equal(ForecastCellStatus.Mismatch, conflict.Status);
        Assert.Equal(0.5m, conflict.Actual);
        Assert.NotNull(conflict.Message);
        Assert.Equal(ForecastCellStatus.Ok, Cell(report, _meetings, 0).Status);
        Assert.Equal(ForecastCellStatus.Ok, Cell(report, _meetings, 2).Status);

        var created = _server.Creates.ToList();
        Assert.Equal(2, created.Count);
        Assert.All(created, a => Assert.Equal(200, a.GetProperty("task_id").GetInt64()));
        Assert.Equal(30, _server.Entries.Single(e => e.ProjectId == FakeForecastClient.DevProject && e.Date == Monday.AddDays(1)).Minutes);
    }

    [Fact]
    public async Task A_task_missing_from_forecast_is_reported_and_the_others_continue()
    {
        var unknown = new ForecastTask { ExternalId = "T9999", ProjectName = "Fantôme", TaskName = "Inconnue" };
        await using (var db = CreateDb())
        {
            db.ForecastTasks.Add(unknown);
            await db.SaveChangesAsync();
        }

        var report = await Writer().FillWeekAsync(Draft(Entry(unknown, 0, 1m), Entry(_dev, 0, 1m)), ForecastSubmitMode.Fill);

        Assert.Equal(ForecastCellStatus.RowNotFound, Cell(report, unknown, 0).Status);
        Assert.Equal(ForecastCellStatus.Ok, Cell(report, _dev, 0).Status);
        Assert.Single(_server.Creates);
    }

    [Fact]
    public async Task An_error_answer_of_the_confirm_tool_marks_the_cell_without_stopping()
    {
        _server.ConfirmFails = true;

        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m), Entry(_dev, 1, 1m)), ForecastSubmitMode.Fill);

        Assert.Equal(2, report.Cells.Count);
        Assert.All(report.Cells, c =>
        {
            Assert.Equal(ForecastCellStatus.Error, c.Status);
            Assert.Contains("boom", c.Message);
        });
        Assert.Empty(_server.Entries);
    }

    [Fact]
    public async Task A_missing_create_tool_fails_every_cell_when_filling_but_not_in_dry_run()
    {
        _server.Tools.RemoveAll(t => t.Name == "plan_time_registration_create");

        var dry = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.DryRun);
        Assert.Equal(ForecastCellStatus.Planned, Assert.Single(dry.Cells).Status);

        var fill = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.Fill);
        var cell = Assert.Single(fill.Cells);
        Assert.Equal(ForecastCellStatus.Error, cell.Status);
        Assert.Contains("CreateTimeEntry", cell.Message);
    }

    [Fact]
    public async Task Two_tasks_of_one_project_on_one_day_are_both_registered_and_checked_together()
    {
        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 2m), Entry(_review, 0, 1m)), ForecastSubmitMode.Fill);

        Assert.True(report.Success, report.Summary);
        Assert.Equal(2, _server.Creates.Count());
        Assert.All(report.Cells, c => Assert.Equal(ForecastCellStatus.Ok, c.Status));
        Assert.Equal(180, _server.Entries.Sum(e => e.Minutes));
    }

    [Fact]
    public async Task A_missing_confirm_tool_registers_nothing()
    {
        _server.Tools.RemoveAll(t => t.Name == "confirm_time_registration_create");

        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 1m)), ForecastSubmitMode.Fill);

        var cell = Assert.Single(report.Cells);
        Assert.Equal(ForecastCellStatus.Error, cell.Status);
        Assert.Contains("confirm_time_registration_create", cell.Message);
        Assert.Empty(_server.Entries);
    }

    [Fact]
    public async Task A_draft_without_hours_does_not_even_connect()
    {
        var report = await Writer().FillWeekAsync(Draft(Entry(_dev, 0, 0m)), ForecastSubmitMode.Fill);

        Assert.Empty(report.Cells);
        Assert.NotEmpty(report.Warnings);
        Assert.Equal(0, _connector.Connects);
    }

    [Fact]
    public async Task Approval_submission_is_not_automated() =>
        await Assert.ThrowsAsync<NotImplementedException>(() => Writer().SubmitForApprovalAsync(Monday));
}
