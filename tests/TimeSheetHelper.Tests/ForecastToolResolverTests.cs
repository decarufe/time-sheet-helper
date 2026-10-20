using System.Text.Json;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests;

public class ForecastToolResolverTests
{
    private static ForecastToolInfo Tool(string name) => new(name, null, default);

    private static readonly IReadOnlyList<ForecastToolInfo> Catalogue =
    [
        Tool("list_tasks"), Tool("create_task"), Tool("delete_task"),
        Tool("get_time_entries"), Tool("create_time_entry"), Tool("update_time_entry"),
        Tool("get_current_user"), Tool("list_projects"),
    ];

    [Theory]
    [InlineData("list_my_Tasks", "list,my,task")]
    [InlineData("listMyTasks", "list,my,task")]
    [InlineData("list-my-tasks", "list,my,task")]
    [InlineData("get_time_entries", "get,time,entry")]
    [InlineData("GetTimeEntries", "get,time,entry")]
    [InlineData("address", "address")]
    public void Tokens_split_names_and_drop_plurals(string name, string expected) =>
        Assert.Equal(expected, string.Join(",", ForecastToolResolver.Tokens(name)));

    [Theory]
    [InlineData(ForecastToolRole.ListTasks, "list_tasks")]
    [InlineData(ForecastToolRole.ListTimeEntries, "get_time_entries")]
    [InlineData(ForecastToolRole.CreateTimeEntry, "create_time_entry")]
    [InlineData(ForecastToolRole.CurrentUser, "get_current_user")]
    public void Resolve_recognises_each_role_by_the_words_of_the_name(ForecastToolRole role, string expected) =>
        Assert.Equal(expected, ForecastToolResolver.Resolve(Catalogue, role, new ForecastToolNames())?.Name);

    [Fact]
    public void Resolve_prefers_assigned_tasks_then_shorter_names()
    {
        var tools = new[] { Tool("list_all_tasks"), Tool("list_tasks"), Tool("list_my_tasks") };
        Assert.Equal("list_my_tasks", ForecastToolResolver.Resolve(tools, ForecastToolRole.ListTasks, new ForecastToolNames())?.Name);
        Assert.Equal("list_tasks", ForecastToolResolver.Resolve(tools[..2], ForecastToolRole.ListTasks, new ForecastToolNames())?.Name);
    }

    [Fact]
    public void Resolve_never_picks_a_tool_that_writes_or_edits_for_a_read_role()
    {
        var tools = new[] { Tool("create_time_entry"), Tool("update_time_entry"), Tool("delete_task"), Tool("create_task") };
        var none = new ForecastToolNames();
        Assert.Null(ForecastToolResolver.Resolve(tools, ForecastToolRole.ListTimeEntries, none));
        Assert.Null(ForecastToolResolver.Resolve(tools, ForecastToolRole.ListTasks, none));
        Assert.Null(ForecastToolResolver.Resolve([Tool("update_time_entry")], ForecastToolRole.CreateTimeEntry, none));
    }

    [Fact]
    public void Resolve_returns_null_when_nothing_matches() =>
        Assert.Null(ForecastToolResolver.Resolve([Tool("list_projects")], ForecastToolRole.CreateTimeEntry, new ForecastToolNames()));

    [Fact]
    public void Configured_name_wins_and_is_matched_ignoring_case()
    {
        var overrides = new ForecastToolNames { ListTasks = "LIST_PROJECTS" };
        Assert.Equal("list_projects", ForecastToolResolver.Resolve(Catalogue, ForecastToolRole.ListTasks, overrides)?.Name);
    }

    [Fact]
    public void Configured_name_that_the_server_does_not_offer_is_not_replaced_by_a_guess()
    {
        var overrides = new ForecastToolNames { CreateTimeEntry = "log_hours" };
        Assert.Null(ForecastToolResolver.Resolve(Catalogue, ForecastToolRole.CreateTimeEntry, overrides));
    }

    [Fact]
    public void The_tools_of_the_real_server_win_over_look_alikes()
    {
        IReadOnlyList<ForecastToolInfo> real =
        [
            Tool("find_task"), Tool("get_person_tasks"), Tool("get_time_analysis"), Tool("get_current_person"),
            Tool("plan_time_registration_create"), Tool("confirm_time_registration_create"), Tool("plan_time_registration_update"),
        ];
        var none = new ForecastToolNames();
        Assert.Equal("get_person_tasks", ForecastToolResolver.Resolve(real, ForecastToolRole.ListTasks, none)?.Name);
        Assert.Equal("get_time_analysis", ForecastToolResolver.Resolve(real, ForecastToolRole.ListTimeEntries, none)?.Name);
        Assert.Equal("plan_time_registration_create", ForecastToolResolver.Resolve(real, ForecastToolRole.CreateTimeEntry, none)?.Name);
        Assert.Equal("get_current_person", ForecastToolResolver.Resolve(real, ForecastToolRole.CurrentUser, none)?.Name);
    }

    [Fact]
    public void A_confirm_tool_is_never_taken_for_the_create_tool() =>
        Assert.Null(ForecastToolResolver.Resolve([Tool("confirm_time_registration_create")], ForecastToolRole.CreateTimeEntry, new ForecastToolNames()));

    [Fact]
    public void Tool_info_lists_the_parameter_names_of_its_schema()
    {
        using var doc = JsonDocument.Parse("""{"type":"object","properties":{"a":{},"b":{}}}""");
        var tool = new ForecastToolInfo("x", null, doc.RootElement.Clone());
        Assert.Equal(["a", "b"], tool.ParameterNames.ToArray());
        Assert.Empty(Tool("y").ParameterNames);
    }

    private static ForecastToolInfo Tool(string name, string schema)
    {
        using var doc = JsonDocument.Parse(schema);
        return new ForecastToolInfo(name, null, doc.RootElement.Clone());
    }

    [Fact]
    public void The_edit_tool_of_forecast_mcp_is_never_taken_for_changing_or_removing_a_registration()
    {
        // Real forecast-mcp: it shifts the date and replaces the notes of registrations, it cannot change their hours.
        var tools = new[]
        {
            Tool("plan_time_registration_edit",
                """{"type":"object","properties":{"entries":{"type":"array","items":{"type":"object","properties":{"time_registration_id":{"type":"integer"},"offset_days":{"type":"integer"},"notes":{"type":"string"}}}}}}"""),
            Tool("confirm_time_registration_edit", """{"type":"object","properties":{"action_id":{"type":"string"}}}"""),
        };

        Assert.Null(ForecastToolResolver.Resolve(tools, ForecastToolRole.UpdateTimeEntry, new ForecastToolNames()));
        Assert.Null(ForecastToolResolver.Resolve(tools, ForecastToolRole.DeleteTimeEntry, new ForecastToolNames()));
    }

    [Fact]
    public void Update_and_delete_tools_are_guessed_only_when_their_schema_names_the_registration()
    {
        var update = Tool("update_time_entry", """{"type":"object","properties":{"time_entry_id":{"type":"integer"},"minutes":{"type":"integer"}}}""");
        var delete = Tool("delete_time_entry", """{"type":"object","properties":{"time_entry_id":{"type":"integer"}}}""");

        Assert.Equal("update_time_entry", ForecastToolResolver.Resolve([update, delete], ForecastToolRole.UpdateTimeEntry, new ForecastToolNames())?.Name);
        Assert.Equal("delete_time_entry", ForecastToolResolver.Resolve([update, delete], ForecastToolRole.DeleteTimeEntry, new ForecastToolNames())?.Name);
        // Same names without a schema: which registration they act on is unknown.
        Assert.Null(ForecastToolResolver.Resolve([Tool("update_time_entry")], ForecastToolRole.UpdateTimeEntry, new ForecastToolNames()));
        Assert.Null(ForecastToolResolver.Resolve([Tool("delete_time_entry")], ForecastToolRole.DeleteTimeEntry, new ForecastToolNames()));
        // A configured name wins.
        Assert.Equal("delete_time_entry",
            ForecastToolResolver.Resolve([Tool("delete_time_entry")], ForecastToolRole.DeleteTimeEntry, new ForecastToolNames { DeleteTimeEntry = "delete_time_entry" })?.Name);
    }
}
