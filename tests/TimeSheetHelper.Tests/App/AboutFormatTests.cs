using TimeSheetHelper.App.About;
using TimeSheetHelper.App.Updates;

namespace TimeSheetHelper.Tests.App;

public sealed class AboutFormatTests
{
    [Theory]
    [InlineData(0, "0 o")]
    [InlineData(1023, "1023 o")]
    [InlineData(1024, "1 Ko")]
    [InlineData(1536, "1,5 Ko")]
    [InlineData(5 * 1024 * 1024, "5 Mo")]
    [InlineData(300L * 1024 * 1024, "300 Mo")]
    [InlineData(3L * 1024 * 1024 * 1024, "3 Go")]
    public void Sizes_use_the_largest_fitting_unit_with_a_french_decimal_comma(long bytes, string expected) =>
        Assert.Equal(expected, AboutFormat.Size(bytes));

    [Fact]
    public void A_negative_size_is_shown_as_zero() => Assert.Equal("0 o", AboutFormat.Size(-5));

    [Theory]
    [InlineData(0, "Aucune activité")]
    [InlineData(1, "1 activité")]
    [InlineData(2, "2 activités")]
    public void Activity_counts_agree_in_number(long count, string expected) =>
        Assert.Equal(expected, AboutFormat.ActivityCount(count));

    [Theory]
    [InlineData("decarufe/time-sheet-helper", "https://github.com/decarufe/time-sheet-helper")]
    [InlineData(" decarufe/time-sheet-helper/ ", "https://github.com/decarufe/time-sheet-helper")]
    public void The_repository_url_is_built_from_the_update_repository(string repository, string expected) =>
        Assert.Equal(expected, AboutFormat.RepositoryUrl(repository));

    [Fact]
    public void An_available_release_is_announced_with_its_version() =>
        Assert.Equal("La version 1.4.0 est disponible.",
            AboutFormat.UpdateStatus(UpdatePhase.Idle, new AppVersion(1, 4, 0), null));

    [Fact]
    public void Without_a_release_the_user_is_told_they_are_up_to_date() =>
        Assert.Equal("Vous utilisez la dernière version.", AboutFormat.UpdateStatus(UpdatePhase.Idle, null, null));

    [Fact]
    public void A_failed_check_shows_the_error_but_a_failed_install_keeps_the_available_release()
    {
        Assert.Equal("Hors ligne", AboutFormat.UpdateStatus(UpdatePhase.Failed, null, "Hors ligne"));
        Assert.Equal("La version 1.4.0 est disponible.", AboutFormat.UpdateStatus(UpdatePhase.Failed, new AppVersion(1, 4, 0), "échec"));
    }

    [Fact]
    public void The_cleanup_offers_ninety_days_thirty_days_and_everything() =>
        Assert.Equal([90, 30, null], AboutFormat.ClearChoices.Select(c => c.Days));

    [Fact]
    public void The_confirmation_names_the_retention_and_warns_it_cannot_be_undone()
    {
        Assert.Contains("90 jours", AboutFormat.ClearConfirmation(90));
        Assert.Contains("irréversible", AboutFormat.ClearConfirmation(90));
        Assert.StartsWith("Toutes les activités", AboutFormat.ClearConfirmation(null));
    }

    [Fact]
    public void The_summary_lists_every_requested_fact()
    {
        var info = new AboutInfo("1.2.3", ".NET 10.0.0", "https://github.com/decarufe/time-sheet-helper", "MIT",
            @"C:\Data\TimeSheetHelper", @"C:\Data\TimeSheetHelper\data.db", 2048, 12, 4096);

        var text = AboutFormat.Summary(info, "Vous utilisez la dernière version.");

        Assert.Contains("Version : 1.2.3", text);
        Assert.Contains(".NET : .NET 10.0.0", text);
        Assert.Contains("Licence : MIT", text);
        Assert.Contains("https://github.com/decarufe/time-sheet-helper", text);
        Assert.Contains(@"C:\Data\TimeSheetHelper", text);
        Assert.Contains("12 activités (4 Ko)", text);
        Assert.Contains("Base de données : 2 Ko", text);
        Assert.Contains("Vous utilisez la dernière version.", text);
    }
}
