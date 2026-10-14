using TimeSheetHelper.Forecast.Submission;

namespace TimeSheetHelper.App.Components.Shared;

public enum GapActionKind
{
    /// <summary>Done in the page (a button).</summary>
    Button,

    /// <summary>Done in another page (a link).</summary>
    Link,

    /// <summary>Not possible now: <see cref="GapActionView.Reason"/> says why.</summary>
    Disabled,
}

/// <summary>An action of a gap as the card shows it.</summary>
public sealed record GapActionView(
    SubmissionGapResolution Resolution, string Label, GapActionKind Kind, bool IsRecommended, string? Href = null, string? Reason = null);

/// <param name="WeekStart">Monday of the week of the draft.</param>
/// <param name="TaskExists">Whether the local task of the draft still exists (the actions that edit it need it).</param>
public sealed record GapActionContext(DateOnly WeekStart, Func<int, bool> TaskExists);

/// <summary>
/// Which actions a gap card offers, and how: a button when the page does it, a link when another page does it, and a disabled entry
/// with its reason when it cannot be done yet. Pure: the page only maps the resolution to its handler.
/// </summary>
public static class GapActionPlanner
{
    public const string ForecastUrlUnknown =
        "L'adresse de la feuille de temps Forecast n'est pas connue de l'application : ouvrez Forecast dans votre navigateur.";

    public const string BlockingCannotBeIgnored =
        "Un écart bloquant ne peut pas être ignoré pour le moment : il sera possible de l'accepter avec les ajustements du brouillon (étape suivante).";

    public const string LaterStep = "Pas encore disponible : prévu avec les ajustements du brouillon (étape suivante).";

    public const string TaskMissing = "La tâche n'existe plus dans le catalogue local : recalculez le brouillon.";

    /// <summary>The resolutions the page runs itself (a button); any other one the backend lists as available is shown disabled.</summary>
    private static readonly HashSet<SubmissionGapResolution> RunInPage =
    [
        SubmissionGapResolution.LinkTask,
        SubmissionGapResolution.CreateTask,
        SubmissionGapResolution.RefreshTasks,
        SubmissionGapResolution.ReactivateTask,
        SubmissionGapResolution.RecalculateDraft,
        SubmissionGapResolution.Retry,
        SubmissionGapResolution.ReadAgain,
        SubmissionGapResolution.ResumeFill,
        SubmissionGapResolution.IgnoreKnowingly,
    ];

    /// <summary>The actions in display order: recommended first, then the other available ones, then the disabled ones.</summary>
    public static IReadOnlyList<GapActionView> Plan(SubmissionGap gap, GapActionContext context)
    {
        var views = gap.Actions.Select((action, index) => (View: ViewOf(gap, action, context), Index: index)).ToList();

        // The backend lists "ignore knowingly" for the blocking gaps only (unavailable); a gap to verify can be ignored for the session.
        if (gap.Severity == SubmissionGapSeverity.ToVerify && views.All(v => v.View.Resolution != SubmissionGapResolution.IgnoreKnowingly))
        {
            views.Add((new GapActionView(
                SubmissionGapResolution.IgnoreKnowingly, GapLabels.Resolution(SubmissionGapResolution.IgnoreKnowingly), GapActionKind.Button, false),
                views.Count));
        }

        return views
            .OrderBy(v => v.View.Kind == GapActionKind.Disabled ? 2 : v.View.IsRecommended ? 0 : 1)
            .ThenBy(v => v.Index)
            .Select(v => v.View)
            .ToList();
    }

    public static string WeekHref(DateOnly weekStart, bool unclassified = false) =>
        $"./?semaine={TimesheetUi.IsoDate(weekStart)}" + (unclassified ? "#unclassified-title" : "");

    public static string DayHref(DateOnly day) => $"jour/{TimesheetUi.IsoDate(day)}";

    private static GapActionView ViewOf(SubmissionGap gap, SubmissionGapAction action, GapActionContext context)
    {
        var resolution = action.Resolution;
        var label = GapLabels.Resolution(resolution);
        var recommended = action.IsRecommended;

        // A blocking gap is never ignorable, whatever the backend says: the entry stays visible with its reason.
        if (resolution == SubmissionGapResolution.IgnoreKnowingly && gap.Severity == SubmissionGapSeverity.Blocking)
            return new GapActionView(resolution, label, GapActionKind.Disabled, recommended, Reason: BlockingCannotBeIgnored);

        // A gap to verify is ignorable for the session even though the backend lists the resolution as not yet available.
        var ignorableNow = resolution == SubmissionGapResolution.IgnoreKnowingly;
        if (!action.IsAvailable && !ignorableNow)
            return new GapActionView(resolution, label, GapActionKind.Disabled, recommended, Reason: action.UnavailableReason ?? LaterStep);

        var dayHref = gap.Date is { } day ? DayHref(day) : WeekHref(context.WeekStart);
        return resolution switch
        {
            SubmissionGapResolution.OpenInForecast =>
                new GapActionView(resolution, label, GapActionKind.Disabled, recommended, Reason: ForecastUrlUnknown),
            SubmissionGapResolution.SignIn =>
                new GapActionView(resolution, label, GapActionKind.Link, recommended, Href: "sources"),
            SubmissionGapResolution.ReclassifySlots =>
                new GapActionView(resolution, label, GapActionKind.Link, recommended, Href: WeekHref(context.WeekStart, unclassified: true)),
            SubmissionGapResolution.ViewDaySlots or SubmissionGapResolution.MarkNotWork or SubmissionGapResolution.Remove =>
                new GapActionView(resolution, label, GapActionKind.Link, recommended, Href: dayHref),
            SubmissionGapResolution.CreateTask or SubmissionGapResolution.ReactivateTask or SubmissionGapResolution.LinkTask
                when gap.Cell is { } cell && !context.TaskExists(cell.ForecastTaskId) =>
                new GapActionView(resolution, label, GapActionKind.Disabled, recommended, Reason: TaskMissing),
            _ when RunInPage.Contains(resolution) => new GapActionView(resolution, label, GapActionKind.Button, recommended),
            _ => new GapActionView(resolution, label, GapActionKind.Disabled, recommended, Reason: LaterStep),
        };
    }
}
