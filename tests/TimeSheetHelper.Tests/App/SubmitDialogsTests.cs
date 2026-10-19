using TimeSheetHelper.App.Components;
using TimeSheetHelper.App.Components.Shared;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.Tests.App;

public sealed class SubmitDialogsTests
{
    [Fact]
    public void The_ignored_paragraph_states_the_hours_not_sent_names_the_tasks_and_says_the_draft_keeps_them()
    {
        var cells = new[]
        {
            new SubmissionIgnoredCell(1, "Interne – Réunions", GapFx.Day(0), 1m, true),
            new SubmissionIgnoredCell(1, "Interne – Réunions", GapFx.Day(1), 0.5m, true),
            new SubmissionIgnoredCell(2, "Portail – Revue", GapFx.Day(1), 2m, false),
        };

        var text = SubmitDialogs.IgnoredParagraph(cells);

        Assert.Contains("3,50 h", text);
        Assert.Contains("2 tâches ignorées", text);
        Assert.Contains("Interne – Réunions (exclue de la soumission)", text);
        Assert.Contains("Portail – Revue", text);
        Assert.DoesNotContain("Portail – Revue (", text);
        Assert.Contains("restent dans le brouillon", text);
    }
    private static IReadOnlyList<SubmissionGap> ToVerify(int count) =>
        Enumerable.Range(0, count)
            .Select(i => GapFx.Gap(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.ToVerify, taskId: null, day: i % 5, title: $"Point {i + 1}"))
            .ToList();

    private static IReadOnlyList<string> Fill(
        int toVerify = 0, int ignored = 0, SubmissionSummary? summary = null, decimal hours = 37.5m, int cells = 12) =>
        SubmitDialogs.FillParagraphs(hours, cells, GapFx.Monday, ToVerify(toVerify), ignored, summary);

    [Fact]
    public void A_clean_fill_says_what_is_typed_that_existing_hours_are_kept_and_that_nothing_is_sent_for_approval()
    {
        var paragraphs = Fill();

        Assert.Equal(3, paragraphs.Count);
        Assert.Contains("37,50 h", paragraphs[0]);
        Assert.Contains("12 cellules", paragraphs[0]);
        Assert.Contains("ne sont pas modifiées", paragraphs[1]);
        Assert.Contains("pas envoyée pour approbation", paragraphs[^1]);
    }

    [Fact]
    public void A_single_cell_is_written_in_the_singular()
    {
        Assert.Contains("1 cellule ", Fill(cells: 1)[0]);
    }

    [Fact]
    public void Open_points_to_verify_are_listed_and_said_not_to_block()
    {
        var paragraphs = Fill(toVerify: 2);

        var open = Assert.Single(paragraphs, p => p.Contains("à vérifier"));
        Assert.Contains("2 points à vérifier restent ouverts", open);
        Assert.Contains("ne bloquent pas la saisie", open);
        Assert.Contains("Point 1 ; Point 2", open);
    }

    [Fact]
    public void One_open_point_is_written_in_the_singular()
    {
        var open = Assert.Single(Fill(toVerify: 1), p => p.Contains("à vérifier"));

        Assert.Contains("1 point à vérifier reste ouvert", open);
        Assert.Contains("ne bloque pas la saisie", open);
    }

    [Fact]
    public void Only_five_open_points_are_named_and_the_others_are_counted()
    {
        var open = Assert.Single(Fill(toVerify: 7), p => p.Contains("à vérifier"));

        Assert.Contains("Point 5", open);
        Assert.DoesNotContain("Point 6", open);
        Assert.Contains("et 2 autre(s)", open);
    }

    [Fact]
    public void Ignored_gaps_are_recalled_with_the_fact_that_the_fill_does_not_change()
    {
        var one = Assert.Single(Fill(ignored: 1), p => p.Contains("ignoré"));
        var several = Assert.Single(Fill(ignored: 3), p => p.Contains("ignoré"));

        Assert.StartsWith("1 écart ignoré pour cette session", one);
        Assert.StartsWith("3 écarts ignorés pour cette session", several);
        Assert.Contains("la saisie reste la même", one);
    }

    [Fact]
    public void Time_without_a_project_is_said_to_stay_in_forecast_and_out_of_the_fill()
    {
        var withNonProject = Fill(summary: GapFx.Summary(nonProject: 2m));
        var without = Fill(summary: GapFx.Summary());

        Assert.Contains(withNonProject, p => p.Contains("2,00 h sans projet restent dans Forecast"));
        Assert.DoesNotContain(without, p => p.Contains("sans projet"));
    }

    [Fact]
    public void The_approval_reminder_always_comes_last()
    {
        var paragraphs = Fill(toVerify: 1, ignored: 1, summary: GapFx.Summary(nonProject: 1m));

        Assert.Contains("approbation", paragraphs[^1]);
        Assert.Equal(6, paragraphs.Count);
    }

    private static SubmitDialogs.MergeImpact Impact(int rules = 2, int cells = 3, decimal hours = 4.5m) =>
        new("Ancienne", "Intégration paie v2", "T3382", cells, hours, rules);

    [Fact]
    public void The_merge_title_names_both_tasks()
    {
        var title = SubmitDialogs.MergeTitle(Impact());

        Assert.Equal("Fusionner « Ancienne » avec T3382 ?", title);
    }

    [Fact]
    public void The_merge_message_counts_what_moves_and_says_it_cannot_be_undone()
    {
        var message = SubmitDialogs.MergeMessage(Impact());

        Assert.Contains("« Ancienne »", message);
        Assert.Contains("« Intégration paie v2 »", message);
        Assert.Contains("2 règles", message);
        Assert.Contains("3 cellules du brouillon", message);
        Assert.Contains("4,50 h", message);
        Assert.Contains("18 créneaux de 15 minutes", message);
        Assert.Contains("irréversible", message);
    }

    [Fact]
    public void The_merge_message_uses_the_singular_for_one_rule_one_cell_and_one_slot()
    {
        var message = SubmitDialogs.MergeMessage(Impact(rules: 1, cells: 1, hours: 0.25m));

        Assert.Contains("1 règle ", message);
        Assert.Contains("1 cellule du brouillon", message);
        Assert.Contains("1 créneau de 15 minutes", message);
    }

    // ── Per-day scope: labels, titles and messages ───────────────────────────────────────────────

    [Fact]
    public void The_days_label_names_the_whole_week_when_every_day_is_selected_or_none_is_given()
    {
        var wholeWeek = Enumerable.Range(0, 7).Select(GapFx.Monday.AddDays).ToHashSet();

        Assert.Equal(UiLabels.WeekTitle(GapFx.Monday).ToLower(UiLabels.French), SubmitDialogs.DaysLabel(null, GapFx.Monday));
        Assert.Equal(UiLabels.WeekTitle(GapFx.Monday).ToLower(UiLabels.French), SubmitDialogs.DaysLabel(wholeWeek, GapFx.Monday));
    }

    [Fact]
    public void The_days_label_names_a_single_day_and_lists_several_days_by_name()
    {
        Assert.Equal($"le {UiLabels.Day(GapFx.Day(1))}", SubmitDialogs.DaysLabel([GapFx.Day(1)], GapFx.Monday));

        var several = SubmitDialogs.DaysLabel([GapFx.Day(0), GapFx.Day(2)], GapFx.Monday);
        Assert.Contains("2 journées", several);
        Assert.Contains(UiLabels.Day(GapFx.Day(0)), several);
        Assert.Contains(UiLabels.Day(GapFx.Day(2)), several);
    }

    [Fact]
    public void The_days_label_says_no_day_when_none_is_checked_and_the_fill_title_stays_generic()
    {
        Assert.Equal("aucune journée", SubmitDialogs.DaysLabel([], GapFx.Monday));
        Assert.Equal("Saisir les heures dans Forecast ?", SubmitDialogs.FillTitle([], GapFx.Monday));
        Assert.Equal("Saisir dans Forecast…", SubmitDialogs.FillButtonLabel([], GapFx.Monday));
    }

    [Fact]
    public void The_fill_title_and_button_label_are_generic_for_the_whole_week_and_name_the_day_otherwise()
    {
        Assert.Equal("Saisir les heures dans Forecast ?", SubmitDialogs.FillTitle(null, GapFx.Monday));
        Assert.Equal("Saisir dans Forecast…", SubmitDialogs.FillButtonLabel(null, GapFx.Monday));

        var day = GapFx.Day(3);
        Assert.Equal($"Saisir les heures du {UiLabels.Day(day)} dans Forecast ?", SubmitDialogs.FillTitle([day], GapFx.Monday));
        Assert.Equal($"Saisir {UiLabels.Day(day)} dans Forecast…", SubmitDialogs.FillButtonLabel([day], GapFx.Monday));
    }

    [Fact]
    public void Fill_paragraphs_scoped_to_a_day_drop_the_open_points_of_the_other_days_but_keep_those_without_a_date()
    {
        var sameDay = GapFx.Gap(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.ToVerify, taskId: null, day: 1, title: "Même jour");
        var otherDay = GapFx.Gap(SubmissionGapType.DayBelowTarget, SubmissionGapSeverity.ToVerify, taskId: null, day: 2, title: "Autre jour");
        var noDate = GapFx.Gap(SubmissionGapType.TaskNotFound, SubmissionGapSeverity.ToVerify, taskId: null, title: "Sans date") with { Date = null, Cell = null };

        var paragraphs = SubmitDialogs.FillParagraphs(
            4m, 1, GapFx.Monday, [sameDay, otherDay, noDate], 0, null, selectedDays: [GapFx.Day(1)]);

        var open = Assert.Single(paragraphs, p => p.Contains("à vérifier"));
        Assert.Contains("Même jour", open);
        Assert.Contains("Sans date", open);
        Assert.DoesNotContain("Autre jour", open);
    }

    [Fact]
    public void Scope_entered_and_not_entered_messages_name_the_day_and_capitalize_it_at_the_start_of_the_sentence()
    {
        var day = GapFx.Day(2);
        var label = SubmitDialogs.DaysLabel([day], GapFx.Monday);
        var capitalized = $"{char.ToUpper(label[0])}{label[1..]}";

        var entered = SubmitDialogs.ScopeEnteredMessage("Résumé", [day], GapFx.Monday);
        Assert.StartsWith("Résumé. ", entered);
        Assert.Contains($"{capitalized} est saisi dans Forecast", entered);
        Assert.Contains("restent à soumettre", entered);

        var notEntered = SubmitDialogs.ScopeNotEnteredMessage("Résumé", [day], GapFx.Monday);
        Assert.StartsWith("Résumé. ", notEntered);
        Assert.Contains($"{capitalized} n'est pas marqué(e) comme saisi(e)", notEntered);
    }
}
