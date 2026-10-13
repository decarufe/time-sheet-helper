using System.Text.Json;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests;

public class ForecastJsonTests
{
    private static JsonElement Json(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    [Theory]
    [InlineData("""[{"id":1},{"id":2},3]""", 2)]
    [InlineData("""{"items":[{"id":1},{"id":2}]}""", 2)]
    [InlineData("""{"data":{"tasks":[{"id":1},{"id":2},{"id":3}]}}""", 3)]
    [InlineData("""{"total":2,"somethingElse":[{"id":1},{"id":2}]}""", 2)]
    [InlineData("""{"id":7,"title":"Seule"}""", 1)]
    [InlineData("""{"items":[],"message":"rien"}""", 0)]
    [InlineData("\"texte\"", 0)]
    public void Items_are_found_wherever_the_list_is(string json, int count) =>
        Assert.Equal(count, ForecastJson.FindItems(Json(json)).Count);

    [Fact]
    public void Tasks_with_numeric_company_ids_get_forecast_codes()
    {
        var tasks = ForecastJson.ParseTasks(Json("""
            {"items":[{"id":1234,"companyTaskId":3382,"title":"Développement",
                       "project":{"name":"Portail","companyProjectId":225},"client":{"name":"Acme"}}]}
            """));

        var task = Assert.Single(tasks);
        Assert.Equal(new ForecastRemoteTask("1234", "T3382", "Développement", "Portail", "P225", "Acme"), task);
    }

    [Fact]
    public void The_tasks_of_get_person_tasks_get_their_t_number_and_project()
    {
        var task = Assert.Single(ForecastJson.ParseTasks(Json("""
            {"person_id":707510,"tasks":[{"task_id":3382,"task_name":"Développement","project_id":740,"project_name":"Portail","actual_hours":3.5}],"truncated":false}
            """)));

        Assert.Equal("3382", task.Id);
        Assert.Equal("T3382", task.Code);
        Assert.Equal("Développement", task.Title);
        Assert.Equal("Portail", task.ProjectName);
        Assert.Equal("740", task.ProjectId);
    }

    [Fact]
    public void The_entries_of_get_time_analysis_carry_their_project_and_total_hours()
    {
        var entries = ForecastJson.ParseTimeEntries(Json("""
            {"entries":[{"entry_id":1,"date":"2026-10-05","person":{"id":707510,"name":"Moi"},
                         "project":{"id":740,"name":"Portail","client":"Acme"},"task_id":15992091,"total_hours":1.5,"notes":"x"}],"has_more":false}
            """));

        var entry = Assert.Single(entries);
        Assert.Equal(new DateOnly(2026, 10, 5), entry.Date);
        Assert.Equal(1.5m, entry.Hours);
        Assert.Equal("707510", entry.PersonId);
        Assert.Equal("740", entry.ProjectId);
    }

    [Fact]
    public void Codes_written_in_the_names_are_extracted_and_stripped()
    {
        var tasks = ForecastJson.ParseTasks(Json("""[{"id":"x7","title":"T55 - Revue de code","project":{"name":"P12 - Interne"}}]"""));

        var task = Assert.Single(tasks);
        Assert.Equal("T55", task.Code);
        Assert.Equal("Revue de code", task.Title);
        Assert.Equal("P12", task.ProjectCode);
        Assert.Equal("Interne", task.ProjectName);
        Assert.Null(task.ClientName);
    }

    [Fact]
    public void Flat_project_and_client_names_are_understood()
    {
        var task = Assert.Single(ForecastJson.ParseTasks(Json("""[{"id":1,"name":"Support","projectName":"Maintenance","clientName":"Globex"}]""")));
        Assert.Equal("Support", task.Title);
        Assert.Equal("Maintenance", task.ProjectName);
        Assert.Equal("Globex", task.ClientName);
        Assert.Null(task.Code);
    }

    [Fact]
    public void Tasks_without_id_or_title_are_skipped() =>
        Assert.Single(ForecastJson.ParseTasks(Json("""[{"title":"Sans id"},{"id":1},{"id":2,"title":"Ok"}]""")));

    [Fact]
    public void Time_entries_in_minutes_become_hours()
    {
        var entries = ForecastJson.ParseTimeEntries(Json("""
            {"data":[{"date":"2026-10-05T00:00:00Z","timeRegistered":90,"task":{"id":1234,"companyTaskId":3382},"person":{"id":9}},
                     {"date":"2026-10-06","hours":2.5,"taskId":77,"personId":"p1"},
                     {"day":"2026-10-07","minutes":"1:30","cardId":5}]}
            """));

        Assert.Equal(3, entries.Count);
        Assert.Equal(new ForecastRemoteEntry("1234", "T3382", new DateOnly(2026, 10, 5), 1.5m, "9"), entries[0]);
        Assert.Equal(new ForecastRemoteEntry("77", null, new DateOnly(2026, 10, 6), 2.5m, "p1"), entries[1]);
        Assert.Equal(new DateOnly(2026, 10, 7), entries[2].Date);
        Assert.Equal(1.5m, entries[2].Hours);
        Assert.Equal("5", entries[2].TaskId);
    }

    [Fact]
    public void Entries_without_a_readable_date_or_duration_are_skipped() =>
        Assert.Empty(ForecastJson.ParseTimeEntries(Json("""[{"timeRegistered":60},{"date":"bientôt","timeRegistered":60},{"date":"2026-10-05"}]""")));

    [Theory]
    [InlineData("""{"id":12,"name":"Moi"}""", "12")]
    [InlineData("""{"personId":"p-3"}""", "p-3")]
    [InlineData("""{"data":{"person":{"id":"p1"}}}""", "p1")]
    [InlineData("""{"user":{"userId":8}}""", "8")]
    [InlineData("""{"name":"Moi"}""", null)]
    [InlineData("""[1,2]""", null)]
    public void The_person_id_is_found_in_the_current_user_answer(string json, string? expected) =>
        Assert.Equal(expected, ForecastJson.ParsePersonId(Json(json)));
}
