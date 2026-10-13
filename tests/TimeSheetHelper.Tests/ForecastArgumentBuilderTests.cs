using System.Text.Json;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests;

public class ForecastArgumentBuilderTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    private static ForecastToolInfo Tool(string schemaJson)
    {
        using var doc = JsonDocument.Parse(schemaJson);
        return new ForecastToolInfo("tool", null, doc.RootElement.Clone());
    }

    [Fact]
    public void Dates_and_person_are_placed_under_the_parameters_that_fit()
    {
        var tool = Tool("""
            {"type":"object","properties":{"startDate":{"type":"string"},"endDate":{"type":"string"},"personId":{"type":"integer"},"unknown":{"type":"string"}},
             "required":["startDate","endDate"]}
            """);

        var args = ForecastArgumentBuilder.Build(tool, new ForecastArgumentValues { From = Monday, To = Monday.AddDays(6), PersonId = "42" });

        Assert.True(args.IsComplete);
        Assert.Equal("2026-10-05", args.Values["startDate"]);
        Assert.Equal("2026-10-11", args.Values["endDate"]);
        Assert.Equal(42L, args.Values["personId"]);
        Assert.False(args.Values.ContainsKey("unknown"));
    }

    [Fact]
    public void A_non_numeric_id_stays_text_even_for_an_integer_parameter()
    {
        var tool = Tool("""{"type":"object","properties":{"personId":{"type":"integer"}}}""");
        var args = ForecastArgumentBuilder.Build(tool, new ForecastArgumentValues { PersonId = "abc" });
        Assert.Equal("abc", args.Values["personId"]);
    }

    [Fact]
    public void Required_parameters_that_cannot_be_filled_are_reported()
    {
        var tool = Tool("""{"type":"object","properties":{"projectId":{"type":"integer"},"date":{"type":"string"}},"required":["projectId","date"]}""");

        var args = ForecastArgumentBuilder.Build(tool, new ForecastArgumentValues { Date = Monday });

        Assert.False(args.IsComplete);
        Assert.Equal(["projectId"], args.MissingRequired.ToArray());
        Assert.Equal("2026-10-05", args.Values["date"]);
    }

    [Fact]
    public void A_time_registration_is_expressed_in_minutes_when_the_parameter_says_so()
    {
        var tool = Tool("""
            {"type":"object","properties":{"taskId":{"type":"integer"},"date":{"type":"string"},"minutes":{"type":"integer"},"notes":{"type":"string"}},
             "required":["taskId","date","minutes"]}
            """);

        var args = ForecastArgumentBuilder.Build(tool, new ForecastArgumentValues { TaskId = "1234", Date = Monday, Hours = 1.5m, Notes = "Revue" });

        Assert.True(args.IsComplete);
        Assert.Equal(1234L, args.Values["taskId"]);
        Assert.Equal(90L, args.Values["minutes"]);
        Assert.Equal("Revue", args.Values["notes"]);
    }

    [Theory]
    [InlineData("""{"hours":{"type":"number"}}""", "hours", 1.5)]
    [InlineData("""{"timeRegistered":{"type":"integer"}}""", "timeRegistered", 90)]
    [InlineData("""{"duration":{"type":"integer","description":"Duration in seconds"}}""", "duration", 5400)]
    [InlineData("""{"duration":{"type":"number","description":"Time in minutes"}}""", "duration", 90)]
    public void Duration_unit_follows_the_name_or_the_description(string properties, string name, double expected)
    {
        var tool = Tool($$"""{"type":"object","properties":{{properties}}}""");

        var args = ForecastArgumentBuilder.Build(tool, new ForecastArgumentValues { Hours = 1.5m });

        Assert.Equal((decimal)expected, Convert.ToDecimal(args.Values[name]));
    }

    [Fact]
    public void Duration_given_as_text_is_formatted_with_a_dot()
    {
        var tool = Tool("""{"type":"object","properties":{"duration":{"type":"string"}}}""");
        var args = ForecastArgumentBuilder.Build(tool, new ForecastArgumentValues { Hours = 1.5m });
        Assert.Equal("1.5", args.Values["duration"]);
    }

    [Fact]
    public void Parameters_without_a_value_are_left_out()
    {
        var tool = Tool("""{"type":"object","properties":{"date":{"type":"string"},"personId":{"type":"integer"},"minutes":{"type":"integer"}}}""");
        var args = ForecastArgumentBuilder.Build(tool, new ForecastArgumentValues());
        Assert.Empty(args.Values);
    }

    [Fact]
    public void A_date_parameter_typed_as_a_number_is_not_guessed()
    {
        var tool = Tool("""{"type":"object","properties":{"date":{"type":"integer"}}}""");
        var args = ForecastArgumentBuilder.Build(tool, new ForecastArgumentValues { Date = Monday });
        Assert.Empty(args.Values);
    }

    [Fact]
    public void A_schema_without_properties_gives_no_arguments()
    {
        var args = ForecastArgumentBuilder.Build(new ForecastToolInfo("t", null, default), new ForecastArgumentValues { From = Monday });
        Assert.True(args.IsComplete);
        Assert.Empty(args.Values);
    }

    [Theory]
    [InlineData("""{"startDate":{},"endDate":{}}""", true)]
    [InlineData("""{"from":{},"to":{}}""", true)]
    [InlineData("""{"date":{}}""", false)]
    [InlineData("""{"week_start":{}}""", true)]
    [InlineData("""{"personId":{}}""", false)]
    public void Date_range_is_detected_from_the_parameter_names(string properties, bool expected) =>
        Assert.Equal(expected, ForecastArgumentBuilder.AcceptsDateRange(Tool($$"""{"type":"object","properties":{{properties}}}""")));

    [Theory]
    [InlineData("start_date", "startdate")]
    [InlineData("Start-Date", "startdate")]
    [InlineData("timeRegistered", "timeregistered")]
    public void Names_are_compared_without_case_or_separators(string name, string expected) =>
        Assert.Equal(expected, ForecastArgumentBuilder.Normalize(name));
}
