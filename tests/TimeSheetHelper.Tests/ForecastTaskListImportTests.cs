using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Forecast;

namespace TimeSheetHelper.Tests;

public class ForecastTaskListImportTests
{
    private const string UserTable = "Column 1\tColumn 2\n" +
        "Projet\tTask\n" +
        "-Internal Time\t-Sport / Développement professionnel / Bénévolat\n" +
        "P1078-M365 - Équipe Laurence - Intranet Centre de Marque\tT35647-Automatisation - Nethris\n" +
        "P1134-GL - Durovac OSEv3 (Réalisation)\tT35043-Scrum ceremonies and meetings\n" +
        "P1141-GL - Gestion RFL - Refonte SLIM - Module Achats  (Réalisation)\tT35196-Kick off\n" +
        "P1141-GL - Gestion RFL - Refonte SLIM - Module Achats  (Réalisation)\tT35198-Plateforme / Fondation / Donnée\n" +
        "P740-Nexus - Génie Logiciel\tT33326-TeamLead/Gestion\n" +
        "P877-Nexus - Ingénerie\tT28393-Rencontre N1\n";

    [Fact]
    public void Parses_the_table_given_by_the_user()
    {
        var result = ForecastTaskListImport.Parse(UserTable);

        Assert.Empty(result.Warnings);
        Assert.Equal(7, result.Tasks.Count);

        var internalTime = result.Tasks[0];
        Assert.Equal("Internal Time/Sport / Développement professionnel / Bénévolat", internalTime.ExternalId);
        Assert.Equal("Internal Time", internalTime.ProjectName);
        Assert.Null(internalTime.ProjectCode);

        var coded = result.Tasks[1];
        Assert.Equal("T35647", coded.ExternalId);
        Assert.Equal("P1078", coded.ProjectCode);
        Assert.Equal("M365 - Équipe Laurence - Intranet Centre de Marque", coded.ProjectName);
        Assert.Equal("Automatisation - Nethris", coded.TaskName);

        // The double space of the Forecast project name is collapsed, as in the page's own text.
        Assert.Equal("GL - Gestion RFL - Refonte SLIM - Module Achats (Réalisation)", result.Tasks[3].ProjectName);
        Assert.Equal("TeamLead/Gestion", result.Tasks[5].TaskName);
    }

    [Fact]
    public void Reports_malformed_and_duplicate_lines_without_failing()
    {
        var result = ForecastTaskListImport.Parse("sans tabulation\nP1-Acme\tT1-Dev\nP1-Acme\tT1-Dev\n\t\nP2-Beta\t\n");

        Assert.Single(result.Tasks);
        Assert.Equal(3, result.Warnings.Count);
    }

    [Fact]
    public void Format_can_be_parsed_back()
    {
        var parsed = ForecastTaskListImport.Parse(UserTable).Tasks;
        var tasks = parsed.Select(p => new ForecastTask
        {
            ExternalId = p.ExternalId, ProjectCode = p.ProjectCode, ProjectName = p.ProjectName, TaskName = p.TaskName, IsActive = true,
        }).Append(new ForecastTask { ExternalId = "T9", ProjectName = "Inactive", TaskName = "Hidden", IsActive = false });

        var again = ForecastTaskListImport.Parse(ForecastTaskListImport.Format(tasks)).Tasks;

        Assert.Equal(parsed.Select(p => p.ExternalId).Order(), again.Select(p => p.ExternalId).Order());
        Assert.Equal(parsed.Select(p => p.ProjectCode).Order(), again.Select(p => p.ProjectCode).Order());
    }

    [Fact]
    public void Empty_text_gives_nothing() => Assert.Empty(ForecastTaskListImport.Parse(null).Tasks);
}
