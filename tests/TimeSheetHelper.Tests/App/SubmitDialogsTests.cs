using TimeSheetHelper.App.Components.Shared;

namespace TimeSheetHelper.Tests.App;

public sealed class SubmitDialogsTests
{
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

}
