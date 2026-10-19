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

    /// <summary>Warnings the plan step of a registration returns.</summary>
    public List<string> PlanWarnings { get; } = [];

    /// <summary>The answer of the confirm tool (e.g. an "ok": false outcome) and whether the registration is applied anyway.</summary>
    public (string Json, bool Applies)? ConfirmReply { get; set; }

    /// <summary>Days whose registrations are approved ("approval_status": "APPROVED"), by project and day.</summary>
    public HashSet<(string ProjectId, DateOnly Date)> ApprovedDays { get; } = [];

    /// <summary>Same for registrations submitted for approval.</summary>
    public HashSet<(string ProjectId, DateOnly Date)> SubmittedDays { get; } = [];

    /// <summary>Same for invoiced registrations.</summary>
    public HashSet<(string ProjectId, DateOnly Date)> InvoicedDays { get; } = [];

    /// <summary>Notes of the registrations of a project and day.</summary>
    public Dictionary<(string ProjectId, DateOnly Date), string> Notes { get; } = [];

    /// <summary>When set, "get_time_analysis" lists at most this many registrations and says "has_more".</summary>
    public int? PageSize { get; set; }

    /// <summary>"has_more" stays true even for one day: the read can never be completed.</summary>
    public bool AlwaysHasMore { get; set; }

    /// <summary>Called first for every tool call: a non-null result replaces the normal answer (it may also throw).</summary>
    public Func<string, IReadOnlyDictionary<string, object?>, ForecastToolResult?>? Intercept { get; set; }

    /// <summary>Tasks of "list_project_tasks" (see <see cref="AddPreflightTools"/>).</summary>
    public List<(int Id, string Name, bool IsDone, bool Blocked, string Unit, double Estimate, double Actual)> ProjectTasks { get; } = [];

    /// <summary>Offers "list_project_tasks" (and "search_projects", for the project stage).</summary>
    public FakeForecastClient AddPreflightTools()
    {
        AddLookupTools();
        Tools.Add(Tool("list_project_tasks", """{"type":"object","properties":{"project_id":{"type":"integer"}},"required":["project_id"]}"""));
        return this;
    }

    /// <summary>Projects known to "search_projects" (see <see cref="AddLookupTools"/>).</summary>
    public List<(int Id, string Name, string Client, string Status)> Projects { get; } =
    [
        (1078, "M365 - Équipe Laurence - Intranet Centre de Marque", "Équipe Laurence Inc.", "RUNNING"),
        (1057, "M365 - ITAQ - Intranet", "Institut de technologie", "RUNNING"),
        (1020, "M365 - BCF - Refonte Intranet", "Corporation BCF inc.", "RUNNING"),
        (12, "Interne", "Nexus", "RUNNING"),
    ];

    /// <summary>Tasks known to "find_task" (see <see cref="AddLookupTools"/>).</summary>
    public List<(int Id, string Name, int ProjectId)> LookupTasks { get; } =
    [
        (35647, "Automatisation - Nethris", 1078),
        (35648, "Automatisation - Tests", 1057),
        (35700, "Revue de code", 1078),
        (35701, "Réunion d'équipe", 12),
    ];

    /// <summary>Thrown by "search_projects" and "find_task" instead of answering.</summary>
    public Exception? LookupFailure { get; set; }

    public int LookupCalls => Calls.Count(c => c.Tool is "search_projects" or "find_task");

    /// <summary>Offers the two lookup tools of the real server (kept out of the default catalogue: the writer tests do not use them).</summary>
    public FakeForecastClient AddLookupTools()
    {
        Tools.Add(Tool("search_projects", """{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}"""));
        Tools.Add(Tool("find_task", """{"type":"object","properties":{"name":{"type":"string"},"person_id":{"type":"string"},"project_id":{"type":"integer"}},"required":["name"]}"""));
        return this;
    }

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
        if (Intercept?.Invoke(toolName, arguments) is { } intercepted)
            return Task.FromResult(intercepted);
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
                        entry_id = 54000000 + i,
                        date = e.Date.ToString("yyyy-MM-dd"),
                        person = new { id = long.Parse(e.PersonId), name = "x" },
                        project = string.IsNullOrEmpty(e.ProjectId)
                            ? (object)new { id = (long?)null, name = "Non-project time" }
                            : new { id = (long?)long.Parse(e.ProjectId), name = "p" + e.ProjectId },
                        task_id = 15992000 + i,
                        total_hours = e.Minutes / 60.0,
                        approval_status = ApprovedDays.Contains((e.ProjectId, e.Date)) ? "APPROVED" : SubmittedDays.Contains((e.ProjectId, e.Date)) ? "SUBMITTED" : "NOT_SUBMITTED",
                        invoiced = InvoicedDays.Contains((e.ProjectId, e.Date)),
                        notes = Notes.GetValueOrDefault((e.ProjectId, e.Date)),
                    })
                    .ToList();
                var listed = PageSize is { } page ? entries.Take(page).ToList() : entries;
                return Task.FromResult(Ok(JsonSerializer.Serialize(new
                {
                    entry_count = entries.Count,
                    entries = listed,
                    has_more = AlwaysHasMore || listed.Count < entries.Count,
                })));
            case "list_project_tasks":
                return Task.FromResult(Ok(JsonSerializer.Serialize(new
                {
                    tool = "list_project_tasks",
                    found = true,
                    count = ProjectTasks.Count,
                    project_id = Convert.ToInt32(arguments["project_id"]),
                    tasks = ProjectTasks.Select(t => new
                    {
                        task_id = t.Id,
                        task_name = t.Name,
                        status_column_name = t.IsDone ? "Done" : "To-do",
                        status_category = t.IsDone ? "DONE" : "TODO",
                        is_done = t.IsDone,
                        blocked = t.Blocked,
                        estimation_unit = t.Unit,
                        estimate = t.Estimate,
                        remaining = 0,
                        actual_hours = t.Actual,
                    }),
                })));
            case "plan_time_registration_create":
                var staged = ((JsonElement)arguments["entries"]!)[0];
                var actionId = $"a{_staged.Count + 1}";
                _staged[actionId] = staged;
                return Task.FromResult(Ok(JsonSerializer.Serialize(new { action_id = actionId, tier = "low", affected_count = 1, warnings = PlanWarnings })));
            case "confirm_time_registration_create":
                if (ConfirmFails)
                    return Task.FromResult(new ForecastToolResult(true, "boom", null));
                var entry = _staged[(string)arguments["action_id"]!];
                if (ConfirmReply is not { Applies: false })
                    Entries.Add((
                        ProjectOf(entry.GetProperty("task_id").GetInt64()),
                        DateOnly.Parse(entry.GetProperty("date").GetString()!),
                        entry.GetProperty("minutes").GetInt32(),
                        MyId));
                return Task.FromResult(Ok(ConfirmReply?.Json ?? """{"status":"done"}"""));
            case "search_projects":
                cancellationToken.ThrowIfCancellationRequested();
                if (LookupFailure is not null)
                    throw LookupFailure;
                return Task.FromResult(SearchProjects((string)arguments["name"]!));
            case "find_task":
                cancellationToken.ThrowIfCancellationRequested();
                if (LookupFailure is not null)
                    throw LookupFailure;
                return Task.FromResult(FindTask(arguments));
            default:
                throw new InvalidOperationException($"Outil inattendu : {toolName}");
        }
    }

    private ForecastToolResult SearchProjects(string query)
    {
        var wanted = Normalize(query);
        object Row(int id, string name, string client, string status) => new
        {
            id = "UHJvamVjdFR5cGU6" + id, project_id = id, custom_project_id = "P" + id, name = name + "  ", client, status,
        };

        var number = wanted.TrimStart('p', '-');
        var exact = number.Length > 0 && number.All(char.IsDigit) ? Projects.Where(p => p.Id.ToString() == number).ToList() : [];
        if (exact.Count == 1)
        {
            var p = exact[0];
            return Ok(JsonSerializer.Serialize(new
            {
                found = true,
                exact_match = true,
                id = "UHJvamVjdFR5cGU6" + p.Id,
                project_id = p.Id,
                custom_project_id = "P" + p.Id,
                name = p.Name + "  ",
                client = p.Client,
                status = p.Status,
            }));
        }

        var matches = Projects.Where(p => Normalize(p.Name).Contains(wanted) || Normalize(p.Client).Contains(wanted)).ToList();
        if (matches.Count == 0)
            return Ok(JsonSerializer.Serialize(new
            {
                found = false,
                message = "No project matching",
                total_count = Projects.Count,
                all_projects = Projects.Select(p => Row(p.Id, p.Name, p.Client, p.Status)),
            }));
        return Ok(JsonSerializer.Serialize(new
        {
            found = true,
            exact_match = false,
            needs_disambiguation = matches.Count > 1,
            matches = matches.Select(p => Row(p.Id, p.Name, p.Client, p.Status)),
        }));
    }

    private ForecastToolResult FindTask(IReadOnlyDictionary<string, object?> arguments)
    {
        var query = (string)arguments["name"]!;
        var wanted = Normalize(query);
        var number = wanted.TrimStart('t');
        object Row((int Id, string Name, int ProjectId) t) => new
        {
            task_id = t.Id, task_name = t.Name, project_id = t.ProjectId,
            project_name = Projects.First(p => p.Id == t.ProjectId).Name, assignee_ids = Array.Empty<int>(), estimation_unit = "hours",
        };

        List<(int Id, string Name, int ProjectId)> found;
        if (number.Length > 0 && number.All(char.IsDigit))
            found = LookupTasks.Where(t => t.Id.ToString() == number).ToList(); // a task number is global: the project scope is ignored
        else
        {
            found = LookupTasks.Where(t => Normalize(t.Name).Contains(wanted)).ToList();
            if (arguments.TryGetValue("project_id", out var scope) && scope is not null)
                found = found.Where(t => t.ProjectId == Convert.ToInt32(scope)).ToList();
        }

        return found.Count == 1
            ? Ok(JsonSerializer.Serialize(new { tool = "find_task", query, found = true, count = 1, task = Row(found[0]) }))
            : Ok(JsonSerializer.Serialize(new { tool = "find_task", query, found = found.Count > 0, count = found.Count, tasks = found.Select(Row) }));
    }

    private static string Normalize(string text) => ForecastCodes.Normalize(text);

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
    public async Task A_day_scoped_fill_only_registers_and_reports_the_selected_day_leaving_the_rest_of_the_draft_untouched()
    {
        var report = await Writer().FillWeekAsync(
            Draft(Entry(_dev, 0, 2m), Entry(_dev, 1, 1.5m), Entry(_meetings, 2, 1m)), ForecastSubmitMode.Fill, days: [Monday.AddDays(1)]);

        var cell = Assert.Single(report.Cells);
        Assert.Equal(Monday.AddDays(1), cell.Date);
        Assert.Equal(ForecastCellStatus.Ok, cell.Status);
        var created = Assert.Single(_server.Creates.ToList());
        Assert.Equal("2026-10-06", created.GetProperty("date").GetString());
        Assert.Single(_server.Entries);
    }

    [Fact]
    public async Task A_dry_run_scoped_to_one_day_compares_only_that_days_cells_and_registers_nothing()
    {
        var report = await Writer().FillWeekAsync(
            Draft(Entry(_dev, 0, 2m), Entry(_meetings, 2, 1m)), ForecastSubmitMode.DryRun, days: [Monday]);

        var cell = Assert.Single(report.Cells);
        Assert.Equal(Monday, cell.Date);
        Assert.Empty(_server.Entries);
    }

    [Fact]
    public async Task A_rejected_entry_outside_the_selected_days_is_left_out_of_the_report()
    {
        var unknown = new ForecastTask { ExternalId = "T9999", ProjectName = "Fantôme", TaskName = "Inconnue" };
        await using (var db = CreateDb())
        {
            db.ForecastTasks.Add(unknown);
            await db.SaveChangesAsync();
        }

        var report = await Writer().FillWeekAsync(
            Draft(Entry(unknown, 0, 1m), Entry(_dev, 1, 1m)), ForecastSubmitMode.Fill, days: [Monday.AddDays(1)]);

        var cell = Assert.Single(report.Cells);
        Assert.Equal(_dev.Id, cell.ForecastTaskId);
    }

    [Fact]
    public async Task A_null_or_empty_day_selection_fills_the_whole_week_as_before()
    {
        var whole = Draft(Entry(_dev, 0, 1m), Entry(_meetings, 2, 1m));

        var nullDays = await Writer().FillWeekAsync(whole, ForecastSubmitMode.DryRun, days: null);
        var emptyDays = await Writer().FillWeekAsync(whole, ForecastSubmitMode.DryRun, days: []);

        Assert.Equal(2, nullDays.Cells.Count);
        Assert.Equal(2, emptyDays.Cells.Count);
    }

    [Fact]
    public async Task A_flagged_task_is_left_out_of_the_fill_by_default_and_listed_as_ignored()
    {
        await using (var db = CreateDb())
        {
            (await db.ForecastTasks.FindAsync(_meetings.Id))!.ExcludeFromSubmission = true;
            await db.SaveChangesAsync();
        }

        var report = await Writer().FillWeekAsync(
            Draft(Entry(_dev, 0, 2m), Entry(_meetings, 0, 1m)), ForecastSubmitMode.Fill);

        Assert.Equal(_dev.Id, Assert.Single(report.Cells).ForecastTaskId);
        var ignored = Assert.Single(report.IgnoredCells);
        Assert.Equal((_meetings.Id, 1m, true), (ignored.ForecastTaskId, ignored.Hours, ignored.Automatic));
        Assert.Single(_server.Creates.ToList());
    }

    [Fact]
    public async Task A_flagged_task_can_be_reincluded_and_any_task_can_be_ignored_explicitly()
    {
        await using (var db = CreateDb())
        {
            (await db.ForecastTasks.FindAsync(_meetings.Id))!.ExcludeFromSubmission = true;
            await db.SaveChangesAsync();
        }
        var draft = Draft(Entry(_dev, 0, 2m), Entry(_meetings, 0, 1m));

        var reincluded = await Writer().FillWeekAsync(draft, ForecastSubmitMode.DryRun, ignoredTaskIds: []);
        var manual = await Writer().FillWeekAsync(draft, ForecastSubmitMode.DryRun, ignoredTaskIds: [_dev.Id]);

        Assert.Equal(2, reincluded.Cells.Count);
        Assert.Empty(reincluded.IgnoredCells);
        Assert.Equal(_meetings.Id, Assert.Single(manual.Cells).ForecastTaskId);
        Assert.False(Assert.Single(manual.IgnoredCells).Automatic);
    }

    [Fact]
    public async Task A_draft_made_only_of_ignored_tasks_says_so_instead_of_claiming_no_hours()
    {
        var report = await Writer().FillWeekAsync(
            Draft(Entry(_dev, 0, 2m)), ForecastSubmitMode.Fill, ignoredTaskIds: [_dev.Id]);

        Assert.Empty(report.Cells);
        Assert.Contains(report.Warnings, w => w.Contains("ignorées"));
        Assert.DoesNotContain(report.Warnings, w => w.Contains("Aucune heure"));
        Assert.Empty(_server.Creates);
    }

    [Fact]
    public async Task Only_the_requested_tasks_are_written_and_the_others_serve_as_context()
    {
        _server.Entries.Add((FakeForecastClient.DevProject, Monday, 120, FakeForecastClient.MyId));

        var report = await Writer().FillWeekAsync(
            Draft(Entry(_dev, 0, 2m), Entry(_review, 0, 1m)), ForecastSubmitMode.Fill, onlyTaskIds: [_review.Id]);

        Assert.True(report.Success, report.Summary);
        Assert.Equal(ForecastCellStatus.Ok, Assert.Single(report.Cells).Status);
        Assert.Equal(_review.Id, report.Cells[0].ForecastTaskId);
        Assert.Equal(3383, Assert.Single(_server.Creates).GetProperty("task_id").GetInt64());
        Assert.Equal(180, _server.Entries.Sum(e => e.Minutes));
    }

    [Fact]
    public async Task A_context_cell_that_differs_from_Forecast_blocks_the_requested_cell_and_is_not_reported()
    {
        _server.Entries.Add((FakeForecastClient.DevProject, Monday, 120, FakeForecastClient.MyId));

        var report = await Writer().FillWeekAsync(
            Draft(Entry(_dev, 0, 3m), Entry(_review, 0, 1m)), ForecastSubmitMode.Fill, onlyTaskIds: [_review.Id]);

        Assert.Equal(ForecastCellStatus.Mismatch, Assert.Single(report.Cells).Status);
        Assert.Equal(_review.Id, report.Cells[0].ForecastTaskId);
        Assert.Empty(_server.Creates);
    }

    [Fact]
    public async Task Approval_submission_is_not_automated() =>
        await Assert.ThrowsAsync<NotImplementedException>(() => Writer().SubmitForApprovalAsync(Monday));
}
