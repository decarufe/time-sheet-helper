using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Tests;

/// <summary>Changing and removing one registration (by its id) through <see cref="ForecastTimesheetWriter"/>.</summary>
public sealed class ForecastWriterUpdateDeleteTests : IDisposable
{
    private static readonly DateOnly Day = new(2026, 10, 5);

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Server _server = new();
    private readonly ForecastTask _dev = new() { ExternalId = "T3382", ProjectName = "Portail", TaskName = "Développement" };

    public ForecastWriterUpdateDeleteTests()
    {
        _connection.Open();
        using var db = CreateDb();
        db.Database.EnsureCreated();
        db.ForecastTasks.Add(_dev);
        db.SaveChanges();
        _server.Entries.Add(new Registration("501", Day, 120, "T3382"));
    }

    public void Dispose() => _connection.Dispose();

    private TimeSheetDbContext CreateDb() => new(new DbContextOptionsBuilder<TimeSheetDbContext>().UseSqlite(_connection).Options);

    private sealed class Factory(Func<TimeSheetDbContext> create) : IDbContextFactory<TimeSheetDbContext>
    {
        public TimeSheetDbContext CreateDbContext() => create();
    }

    private ForecastTimesheetWriter Writer(ForecastOptions? options = null) =>
        new(new ForecastApi(new FakeForecastConnector(_server), Options.Create(options ?? new ForecastOptions()), NullLogger<ForecastApi>.Instance),
            new Factory(CreateDb), NullLogger<ForecastTimesheetWriter>.Instance);

    private int Writes => _server.Calls.Count(c => c is "update_time_entry" or "delete_time_entry" or "confirm_time_entry_update" or "confirm_time_entry_delete");

    [Fact]
    public async Task The_real_catalogue_offers_creation_only()
    {
        _server.WithoutWriteTools();

        var capabilities = await Writer().GetCapabilitiesAsync();

        Assert.Equal(ForecastCapability.Available, capabilities.Create);
        Assert.Equal(ForecastCapability.Unavailable, capabilities.Update);
        Assert.Equal(ForecastCapability.Unavailable, capabilities.Delete);
        Assert.Contains("modification", capabilities.UnavailableReason);
    }

    [Fact]
    public async Task Without_an_update_tool_nothing_is_written()
    {
        _server.WithoutWriteTools();

        var cell = await Writer().UpdateCellAsync(Day, _dev.Id, "501", 2m, 3m);

        Assert.Equal(ForecastCellStatus.Error, cell.Status);
        Assert.Equal(ForecastCellIssue.WriteRefused, cell.Issue);
        Assert.Contains("modification", cell.Message);
        Assert.Equal(0, Writes);
        Assert.Equal(120, _server.Entries.Single().Minutes);
    }

    [Fact]
    public async Task A_truncated_read_writes_nothing()
    {
        _server.AlwaysHasMore = true;

        var cell = await Writer().DeleteCellAsync(Day, _dev.Id, "501", 2m);

        Assert.Equal(ForecastCellStatus.Unknown, cell.Status);
        Assert.Equal(ForecastCellIssue.ReadIncomplete, cell.Issue);
        Assert.Equal(0, Writes);
        Assert.Single(_server.Entries);
    }

    [Fact]
    public async Task A_cancelled_call_writes_nothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Writer().UpdateCellAsync(Day, _dev.Id, "501", 2m, 3m, cts.Token));
        Assert.Empty(_server.Calls);
    }

    [Fact]
    public async Task A_registration_that_changed_since_it_was_shown_is_left_alone()
    {
        var update = await Writer().UpdateCellAsync(Day, _dev.Id, "501", 1.5m, 3m);
        var delete = await Writer().DeleteCellAsync(Day, _dev.Id, "501", 1.5m);

        Assert.Equal(ForecastCellStatus.Mismatch, update.Status);
        Assert.Equal(2m, update.Actual);
        Assert.Equal(ForecastCellStatus.Mismatch, delete.Status);
        Assert.Equal(0, Writes);
        Assert.Equal(120, _server.Entries.Single().Minutes);
    }

    [Fact]
    public async Task Update_changes_the_minutes_and_a_replay_is_skipped()
    {
        var first = await Writer().UpdateCellAsync(Day, _dev.Id, "501", 2m, 3m);
        var replay = await Writer().UpdateCellAsync(Day, _dev.Id, "501", 2m, 3m);

        Assert.Equal(ForecastCellStatus.Updated, first.Status);
        Assert.Equal(ForecastEntryState.Entered, first.EntryState);
        Assert.Equal(3m, first.Actual);
        Assert.Equal(ForecastCellStatus.Skipped, replay.Status);
        Assert.Equal(1, Writes);
        Assert.Equal(180, _server.Entries.Single().Minutes);
        Assert.Equal(501L, _server.LastWrite!["time_entry_id"]);
        Assert.Equal(180L, _server.LastWrite!["minutes"]);
    }

    [Fact]
    public async Task Delete_removes_the_registration_and_a_replay_is_skipped()
    {
        var first = await Writer().DeleteCellAsync(Day, _dev.Id, "501", 2m);
        var replay = await Writer().DeleteCellAsync(Day, _dev.Id, "501", 2m);

        Assert.Equal(ForecastCellStatus.Deleted, first.Status);
        Assert.Equal(ForecastEntryState.NotEntered, first.EntryState);
        Assert.Equal(ForecastCellStatus.Skipped, replay.Status);
        Assert.Equal(1, Writes);
        Assert.Empty(_server.Entries);
    }

    [Fact]
    public async Task A_change_whose_answer_is_lost_is_settled_by_reading_the_day_back()
    {
        _server.TimeoutAfterWrite = true;

        var cell = await Writer().UpdateCellAsync(Day, _dev.Id, "501", 2m, 3m);

        Assert.Equal(ForecastCellStatus.Updated, cell.Status);
        Assert.Equal(1, Writes);
    }

    [Fact]
    public async Task A_registration_of_another_task_is_left_alone()
    {
        _server.Entries[0] = _server.Entries[0] with { TaskCode = "T9999" };

        var cell = await Writer().DeleteCellAsync(Day, _dev.Id, "501", 2m);

        Assert.Equal(ForecastCellStatus.Mismatch, cell.Status);
        Assert.Equal(0, Writes);
    }

    [Fact]
    public async Task A_day_holding_an_approved_registration_is_still_updated()
    {
        _server.Entries.Add(new Registration("502", Day, 60, "T3382", "APPROVED"));

        var update = await Writer().UpdateCellAsync(Day, _dev.Id, "501", 2m, 3m);

        Assert.Equal(ForecastCellStatus.Updated, update.Status);
        Assert.Equal(1, Writes);
        Assert.Equal(180, _server.Entries[0].Minutes);
    }

    [Theory]
    [InlineData(true, ForecastCellStatus.Updated)]
    [InlineData(false, ForecastCellStatus.Unknown)]
    public async Task A_cancellation_during_the_write_is_settled_by_reading_the_day_back(bool applied, ForecastCellStatus expected)
    {
        using var cts = new CancellationTokenSource();
        _server.CancelDuringWrite = (cts, applied);

        var cell = await Writer().UpdateCellAsync(Day, _dev.Id, "501", 2m, 3m, cts.Token);

        Assert.Equal(expected, cell.Status);
        Assert.Equal(1, Writes);
        Assert.Equal("get_time_analysis", _server.Calls[^1]);
        if (!applied)
            Assert.Equal(ForecastCellIssue.OutcomeUnknown, cell.Issue);
    }

    [Fact]
    public async Task A_plan_tool_is_staged_then_confirmed_with_one_registration()
    {
        _server.WithPlanTools();
        var options = new ForecastOptions { Tools = { UpdateTimeEntry = "plan_time_entry_update", DeleteTimeEntry = "plan_time_entry_delete" } };

        var update = await Writer(options).UpdateCellAsync(Day, _dev.Id, "501", 2m, 3m);
        var staged = _server.LastWrite!;
        var delete = await Writer(options).DeleteCellAsync(Day, _dev.Id, "501", 3m);

        Assert.Equal(ForecastCellStatus.Updated, update.Status);
        Assert.Equal(ForecastCellStatus.Deleted, delete.Status);
        var item = Assert.Single(((JsonElement)staged["entries"]!).EnumerateArray());
        Assert.Equal(501, item.GetProperty("time_entry_id").GetInt64());
        Assert.Equal(180, item.GetProperty("minutes").GetInt32());
        Assert.Equal(["plan_time_entry_update", "confirm_time_entry_update", "plan_time_entry_delete", "confirm_time_entry_delete"],
            _server.Calls.Where(c => c.StartsWith("plan_") || c.StartsWith("confirm_")));
        Assert.Empty(_server.Entries);
    }

    [Fact]
    public async Task A_refused_plan_names_the_change_and_writes_nothing()
    {
        _server.WithPlanTools();
        _server.RefusePlan = true;
        var options = new ForecastOptions { Tools = { DeleteTimeEntry = "plan_time_entry_delete" } };

        var cell = await Writer(options).DeleteCellAsync(Day, _dev.Id, "501", 2m);

        Assert.Equal(ForecastCellStatus.Error, cell.Status);
        Assert.Equal(ForecastCellIssue.WriteRefused, cell.Issue);
        Assert.Contains("a refusé la suppression", cell.Message);
        Assert.Equal(0, Writes);
        Assert.Single(_server.Entries);
    }

    private sealed record Registration(string Id, DateOnly Date, int Minutes, string TaskCode, string? ApprovalStatus = null);

    /// <summary>A Forecast MCP server whose registrations have stable ids, with update and delete tools unless removed.</summary>
    private sealed class Server : IForecastToolClient
    {
        public List<ForecastToolInfo> Tools { get; } =
        [
            Tool("get_time_analysis", """{"type":"object","properties":{"start_date":{"type":"string"},"end_date":{"type":"string"}},"required":["start_date","end_date"]}"""),
            Tool("get_current_person", """{"type":"object","properties":{}}"""),
            Tool("plan_time_registration_create", """{"type":"object","properties":{"entries":{"type":"array","items":{"type":"object"}}},"required":["entries"]}"""),
            Tool("confirm_time_registration_create", """{"type":"object","properties":{"action_id":{"type":"string"}},"required":["action_id"]}"""),
            Tool("update_time_entry", """{"type":"object","properties":{"time_entry_id":{"type":"integer"},"minutes":{"type":"integer"}},"required":["time_entry_id","minutes"]}"""),
            Tool("delete_time_entry", """{"type":"object","properties":{"time_entry_id":{"type":"integer"}},"required":["time_entry_id"]}"""),
        ];

        public List<Registration> Entries { get; } = [];

        public List<string> Calls { get; } = [];

        public IReadOnlyDictionary<string, object?>? LastWrite { get; private set; }

        public bool AlwaysHasMore { get; set; }

        /// <summary>The write is applied, then its answer is lost (timeout).</summary>
        public bool TimeoutAfterWrite { get; set; }

        /// <summary>The caller cancels while the write is in flight; the write is applied or not.</summary>
        public (CancellationTokenSource Source, bool Applied)? CancelDuringWrite { get; set; }

        /// <summary>The "plan_…" tools refuse to stage the change.</summary>
        public bool RefusePlan { get; set; }

        private (string Tool, IReadOnlyDictionary<string, object?> Args)? _staged;

        /// <summary>Update and delete as forecast-mcp-style "plan_…"/"confirm_…" pairs taking a list of registrations.</summary>
        public void WithPlanTools()
        {
            Tools.RemoveAll(t => t.Name is "update_time_entry" or "delete_time_entry");
            Tools.Add(Tool("plan_time_entry_update",
                """{"type":"object","properties":{"entries":{"type":"array","items":{"type":"object","properties":{"time_entry_id":{"type":"integer"},"minutes":{"type":"integer"}},"required":["time_entry_id","minutes"]}}},"required":["entries"]}"""));
            Tools.Add(Tool("confirm_time_entry_update", """{"type":"object","properties":{"action_id":{"type":"string"}},"required":["action_id"]}"""));
            Tools.Add(Tool("plan_time_entry_delete",
                """{"type":"object","properties":{"entries":{"type":"array","items":{"type":"object","properties":{"time_entry_id":{"type":"integer"}},"required":["time_entry_id"]}}},"required":["entries"]}"""));
            Tools.Add(Tool("confirm_time_entry_delete", """{"type":"object","properties":{"action_id":{"type":"string"}},"required":["action_id"]}"""));
        }

        /// <summary>The tools of the real forecast-mcp: its "edit" tool shifts dates and notes, nothing changes hours or removes.</summary>
        public void WithoutWriteTools()
        {
            Tools.RemoveAll(t => t.Name is "update_time_entry" or "delete_time_entry");
            Tools.Add(Tool("plan_time_registration_edit",
                """{"type":"object","properties":{"entries":{"type":"array","items":{"type":"object","properties":{"time_registration_id":{"type":"integer"},"offset_days":{"type":"integer"},"notes":{"type":"string"}}}}}}"""));
            Tools.Add(Tool("confirm_time_registration_edit", """{"type":"object","properties":{"action_id":{"type":"string"}},"required":["action_id"]}"""));
        }

        public Task<IReadOnlyList<ForecastToolInfo>> ListToolsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ForecastToolInfo>>(Tools);

        public Task<ForecastToolResult> CallToolAsync(string toolName, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(toolName);
            switch (toolName)
            {
                case "get_current_person":
                    return Reply(new { id = FakeForecastClient.MyId });
                case "get_time_analysis":
                    var from = DateOnly.Parse((string)arguments["start_date"]!);
                    var to = DateOnly.Parse((string)arguments["end_date"]!);
                    var items = Entries.Where(e => e.Date >= from && e.Date <= to).Select(e => new
                    {
                        entry_id = e.Id,
                        date = e.Date.ToString("yyyy-MM-dd"),
                        minutes = e.Minutes,
                        person_id = FakeForecastClient.MyId,
                        approval_status = e.ApprovalStatus,
                        task = new { company_task_id = e.TaskCode },
                    });
                    return Reply(new { entries = items, has_more = AlwaysHasMore });
                case "update_time_entry":
                case "delete_time_entry":
                    LastWrite = arguments;
                    if (CancelDuringWrite is { } cancel)
                    {
                        if (cancel.Applied)
                            Apply(toolName == "delete_time_entry", arguments);
                        cancel.Source.Cancel();
                        throw new OperationCanceledException(cancel.Source.Token);
                    }
                    if (!Apply(toolName == "delete_time_entry", arguments))
                        return Reply(new { ok = false, outcome = "failed", message = "not found" });
                    if (TimeoutAfterWrite)
                        throw new TimeoutException("délai dépassé");
                    return Reply(new { ok = true });
                case "plan_time_entry_update":
                case "plan_time_entry_delete":
                    LastWrite = arguments;
                    if (RefusePlan)
                        return Reply(new { ok = false, outcome = "refused", message = "non autorisé" });
                    _staged = (toolName, arguments);
                    return Reply(new { ok = true, action_id = "a1" });
                case "confirm_time_entry_update":
                case "confirm_time_entry_delete":
                    if (_staged is not { } staged || (string?)arguments["action_id"] != "a1")
                        return Reply(new { ok = false, outcome = "failed", message = "no such action" });
                    _staged = null;
                    var item = ((JsonElement)staged.Args["entries"]!).EnumerateArray().Single();
                    var values = new Dictionary<string, object?> { ["time_entry_id"] = item.GetProperty("time_entry_id").GetInt64() };
                    if (item.TryGetProperty("minutes", out var minutes))
                        values["minutes"] = minutes.GetInt32();
                    return Apply(staged.Tool.EndsWith("_delete"), values)
                        ? Reply(new { ok = true })
                        : Reply(new { ok = false, outcome = "failed", message = "not found" });
                default:
                    return Task.FromResult(new ForecastToolResult(true, $"unknown tool {toolName}", null));
            }
        }

        private bool Apply(bool delete, IReadOnlyDictionary<string, object?> arguments)
        {
            var id = Convert.ToString(arguments["time_entry_id"], System.Globalization.CultureInfo.InvariantCulture);
            var index = Entries.FindIndex(e => e.Id == id);
            if (index < 0)
                return false;
            if (delete)
                Entries.RemoveAt(index);
            else
                Entries[index] = Entries[index] with { Minutes = Convert.ToInt32(arguments["minutes"], System.Globalization.CultureInfo.InvariantCulture) };
            return true;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static Task<ForecastToolResult> Reply(object value)
        {
            var json = JsonSerializer.SerializeToElement(value);
            return Task.FromResult(new ForecastToolResult(false, json.GetRawText(), json));
        }

        private static ForecastToolInfo Tool(string name, string schema)
        {
            using var doc = JsonDocument.Parse(schema);
            return new ForecastToolInfo(name, null, doc.RootElement.Clone());
        }
    }
}
