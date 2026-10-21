using System.Globalization;
using TimeSheetHelper.Core.Timesheets;

namespace TimeSheetHelper.Forecast.Submission;

/// <summary>Turns the draft and the result of a simulation (or fill) into the gaps the page lets the user resolve.</summary>
public interface ISubmissionGapService
{
    /// <summary>
    /// The gaps of the week and their summary. <paramref name="report"/> is null before any simulation (only the local gaps are
    /// then known); <paramref name="context"/> carries the daily objective and the unclassified work (null: no objective checks).
    /// </summary>
    Task<SubmissionAnalysis> AnalyzeAsync(
        TimesheetDraft draft, ForecastFillReport? report, SubmissionDraftContext? context = null, CancellationToken cancellationToken = default);

    /// <summary>The blocking gap of an operation that threw instead of producing a report (not signed in, Forecast unreachable...).</summary>
    SubmissionGap CreateFailureGap(Exception exception, DateOnly weekStart);
}

public sealed class SubmissionGapService : ISubmissionGapService
{
    /// <summary>A day or a week this close to its objective (in hours) is not a gap.</summary>
    public const decimal ObjectiveToleranceHours = 0.25m;

    private const string AdjustmentsLater = "Disponible avec les ajustements de brouillon (phase suivante).";

    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-CA");

    public Task<SubmissionAnalysis> AnalyzeAsync(
        TimesheetDraft draft, ForecastFillReport? report, SubmissionDraftContext? context = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Analyze(draft, report, context));
    }

    public SubmissionAnalysis Analyze(TimesheetDraft draft, ForecastFillReport? report, SubmissionDraftContext? context)
    {
        var week = draft.WeekStart;
        var totals = Enumerable.Range(0, 7).Select(week.AddDays)
            .ToDictionary(d => d, d => draft.Entries.Where(e => e.Date == d).Sum(e => e.Hours));
        var weekTotal = totals.Values.Sum();
        var gaps = new List<SubmissionGap>();

        if (context is not null)
            gaps.AddRange(LocalGaps(context, totals, weekTotal));

        var cells = 0;
        var cellsWithoutGap = 0;
        if (report is not null)
        {
            var reportGaps = ReportGaps(report, totals, weekTotal, out cells, out cellsWithoutGap);
            gaps.AddRange(reportGaps);
        }

        var ordered = gaps
            .OrderBy(g => g.Severity)
            .ThenBy(g => g.Date ?? DateOnly.MaxValue)
            .ThenBy(g => g.Type)
            .ThenBy(g => g.Id, StringComparer.Ordinal)
            .ToList();

        decimal? weekTarget = context is null ? null : context.Days.Sum(d => d.TargetHours);
        decimal? weekDelta = weekTarget is { } target ? weekTotal - target : null;
        var blocking = ordered.Count(g => g.Severity == SubmissionGapSeverity.Blocking);
        var readComplete = report is not { ReadTruncated: true };
        var nonProject = report?.NonProjectEntries ?? [];
        var days = (context?.Days ?? []).OrderBy(d => d.Date).Select(d =>
        {
            var total = totals.GetValueOrDefault(d.Date);
            var diff = total - d.TargetHours;
            return new SubmissionDaySummary(
                d.Date, total, d.TargetHours, diff, Math.Abs(diff) <= ObjectiveToleranceHours, d.UnclassifiedHours, d.ExcludedHours,
                nonProject.Where(e => e.Date == d.Date).Sum(e => e.Hours));
        }).ToList();

        var summary = new SubmissionSummary(
            blocking,
            ordered.Count(g => g.Severity == SubmissionGapSeverity.ToVerify),
            ordered.Count(g => g.Severity == SubmissionGapSeverity.Info),
            cells,
            cellsWithoutGap,
            weekTotal,
            weekTarget,
            weekDelta,
            weekDelta is { } delta ? Math.Abs(delta) <= ObjectiveToleranceHours : null,
            context?.Days.Sum(d => d.UnclassifiedHours) ?? 0,
            context?.Days.Sum(d => d.ExcludedHours) ?? 0,
            ObjectiveToleranceHours,
            report is not null,
            readComplete,
            report is not null && readComplete && blocking == 0,
            days,
            nonProject.Sum(e => e.Hours));

        return new SubmissionAnalysis(week, ordered, summary);
    }

    public SubmissionGap CreateFailureGap(Exception exception, DateOnly weekStart)
    {
        if (exception is ForecastAuthRequiredException)
        {
            return new SubmissionGap
            {
                Id = IdOf(SubmissionGapType.AuthRequired, null, null),
                Type = SubmissionGapType.AuthRequired,
                Severity = SubmissionGapSeverity.Blocking,
                Title = "Connexion à Forecast requise",
                Message = "Vous n'êtes pas connecté à Forecast : connectez-vous (page Sources), puis relancez la simulation.",
                Actions = [new(SubmissionGapResolution.SignIn, IsRecommended: true), new(SubmissionGapResolution.Retry)],
            };
        }

        return new SubmissionGap
        {
            Id = IdOf(SubmissionGapType.ForecastError, null, null),
            Type = SubmissionGapType.ForecastError,
            Severity = SubmissionGapSeverity.Blocking,
            Title = "Forecast n'a pas pu être interrogé",
            Message = $"Forecast n'a pas pu être interrogé : {exception.Message}",
            Actions = [new(SubmissionGapResolution.Retry, IsRecommended: true), new(SubmissionGapResolution.OpenInForecast)],
        };
    }

    // ── Local gaps: unclassified work and days away from the objective ──────────────────────────────

    private static IEnumerable<SubmissionGap> LocalGaps(SubmissionDraftContext context, Dictionary<DateOnly, decimal> totals, decimal weekTotal)
    {
        foreach (var day in context.Days.OrderBy(d => d.Date))
        {
            var total = totals.GetValueOrDefault(day.Date);

            if (day.UnclassifiedHours > 0)
            {
                yield return new SubmissionGap
                {
                    Id = IdOf(SubmissionGapType.UnclassifiedRemaining, null, day.Date),
                    Type = SubmissionGapType.UnclassifiedRemaining,
                    Severity = SubmissionGapSeverity.ToVerify,
                    Date = day.Date,
                    Title = $"{Hours(day.UnclassifiedHours)} non classées",
                    Message = $"{Hours(day.UnclassifiedHours)} de travail du {DayName(day.Date)} n'ont pas de tâche : elles ne seront pas saisies dans Forecast.",
                    Actions =
                    [
                        new(SubmissionGapResolution.ReclassifySlots, IsRecommended: true),
                        new(SubmissionGapResolution.MarkNotWork),
                        new(SubmissionGapResolution.ViewDaySlots),
                    ],
                    Preview = new SubmissionGapPreview(
                        "Si ces heures reçoivent une tâche",
                        null, null, total, total + day.UnclassifiedHours, weekTotal, weekTotal + day.UnclassifiedHours),
                };
            }

            if (day.TargetHours <= 0 && total <= 0)
                continue;
            var diff = total - day.TargetHours;
            if (Math.Abs(diff) <= ObjectiveToleranceHours)
                continue;

            var below = diff < 0;
            yield return new SubmissionGap
            {
                Id = IdOf(below ? SubmissionGapType.DayBelowTarget : SubmissionGapType.DayAboveTarget, null, day.Date),
                Type = below ? SubmissionGapType.DayBelowTarget : SubmissionGapType.DayAboveTarget,
                Severity = below ? SubmissionGapSeverity.ToVerify : SubmissionGapSeverity.Info,
                Date = day.Date,
                Title = below ? $"{DayName(day.Date)} sous l'objectif" : $"{DayName(day.Date)} au-dessus de l'objectif",
                Message = below
                    ? $"Le {DayName(day.Date)}, {Hours(total)} sont prévues pour un objectif de {Hours(day.TargetHours)} (il manque {Hours(-diff)})"
                      + (day.UnclassifiedHours > 0 ? $", dont {Hours(day.UnclassifiedHours)} de travail non classé." : ".")
                    : day.TargetHours <= 0
                        ? $"Des heures ({Hours(total)}) sont prévues le {DayName(day.Date)}, jour sans objectif."
                        : $"Le {DayName(day.Date)}, {Hours(total)} sont prévues pour un objectif de {Hours(day.TargetHours)} (+{Hours(diff)}).",
                ExpectedHours = day.TargetHours,
                DeltaHours = diff,
                Actions = [new(SubmissionGapResolution.ViewDaySlots, IsRecommended: true)],
            };
        }
    }

    // ── Gaps found by the simulation or the fill ────────────────────────────────────────────────────

    private static List<SubmissionGap> ReportGaps(
        ForecastFillReport report, Dictionary<DateOnly, decimal> totals, decimal weekTotal, out int cellCount, out int cellsWithoutGap)
    {
        var gaps = new List<SubmissionGap>();
        cellCount = report.Cells.Count;

        if (report.GlobalError is { } error)
        {
            gaps.Add(new SubmissionGap
            {
                Id = IdOf(SubmissionGapType.ForecastError, null, null),
                Type = SubmissionGapType.ForecastError,
                Severity = SubmissionGapSeverity.Blocking,
                Title = "Forecast n'a pas pu être interrogé",
                Message = $"Forecast n'a pas pu être interrogé : {error}",
                Actions = [new(SubmissionGapResolution.Retry, IsRecommended: true), new(SubmissionGapResolution.OpenInForecast)],
            });
        }

        if (report.ReadTruncated)
        {
            gaps.Add(new SubmissionGap
            {
                Id = IdOf(SubmissionGapType.ReadTruncated, null, null),
                Type = SubmissionGapType.ReadTruncated,
                Severity = SubmissionGapSeverity.Blocking,
                Title = "Lecture de Forecast incomplète",
                Message = "Toutes les saisies de la semaine n'ont pas pu être lues dans Forecast"
                    + (report.ReadDetail is null ? "" : $" ({report.ReadDetail})")
                    + " : aucune comparaison n'est fiable et la saisie est refusée pour éviter des doublons.",
                Actions = [new(SubmissionGapResolution.ReadAgain, IsRecommended: true), new(SubmissionGapResolution.OpenInForecast)],
            });
        }

        if (report.Interrupted)
        {
            var entered = report.Cells.Count(c => c.EntryState == ForecastEntryState.Entered);
            gaps.Add(new SubmissionGap
            {
                Id = IdOf(SubmissionGapType.FillInterrupted, null, null),
                Type = SubmissionGapType.FillInterrupted,
                Severity = SubmissionGapSeverity.Blocking,
                Title = "Saisie interrompue",
                Message = $"La saisie s'est arrêtée avant la fin ({entered}/{report.Cells.Count} cellules dans Forecast). "
                    + (report.InterruptionMessage ?? "")
                    + " Seules les cellules manquantes seront saisies à la reprise.",
                Actions = [new(SubmissionGapResolution.ReadAgain, IsRecommended: true), new(SubmissionGapResolution.ResumeFill), new(SubmissionGapResolution.OpenInForecast)],
            });
        }

        if (report.PreflightLimits.Count > 0)
        {
            gaps.Add(new SubmissionGap
            {
                Id = IdOf(SubmissionGapType.PreflightUnavailable, null, null),
                Type = SubmissionGapType.PreflightUnavailable,
                Severity = SubmissionGapSeverity.Info,
                Title = "Vérifications préalables incomplètes",
                Message = "Non vérifiable : " + string.Join(" ", report.PreflightLimits.Distinct()),
            });
        }

        if (report.NonProjectEntries.Count > 0)
        {
            var hours = report.NonProjectEntries.Sum(e => e.Hours);
            gaps.Add(new SubmissionGap
            {
                Id = IdOf(SubmissionGapType.ForecastTimeWithoutProject, null, null),
                Type = SubmissionGapType.ForecastTimeWithoutProject,
                Severity = SubmissionGapSeverity.Info,
                Title = "Temps sans projet dans Forecast",
                Message = $"Forecast contient {Hours(hours)} sans projet cette semaine (temps interne ou d'inactivité) : l'application ne les saisit ni ne les compare, "
                    + "elles sont exclues de la soumission.",
                ForecastHours = hours,
                ExistingEntries = report.NonProjectEntries
                    .OrderBy(e => e.Date)
                    .Select(e => new SubmissionExistingEntry(e.EntryId, e.ProjectName, e.Notes, e.Hours, e.ApprovalStatus, e.Invoiced))
                    .ToList(),
                Actions = [new(SubmissionGapResolution.OpenInForecast, IsRecommended: true)],
            });
        }

        // Cells in conflict are judged by project and day: the cells of one group make one gap.
        var withGap = new HashSet<ForecastCellResult>();
        foreach (var group in report.Cells.Where(c => c.Status == ForecastCellStatus.Mismatch).GroupBy(c => (c.GroupKey, c.Date, c.GroupKey is null ? c.ForecastTaskId : 0)))
        {
            var members = group.ToList();
            gaps.Add(ConflictGap(members, totals, weekTotal));
            withGap.UnionWith(members);
        }

        foreach (var cell in report.Cells.Where(c => c.Status != ForecastCellStatus.Mismatch))
        {
            var before = gaps.Count;
            gaps.AddRange(CellGaps(cell, report, totals, weekTotal));
            if (gaps.Count > before)
                withGap.Add(cell);
        }

        // Findings (read-only checks, warnings) can sit on a cell that is otherwise fine: it still has something to say.
        foreach (var cell in report.Cells.Where(c => c.Status == ForecastCellStatus.Mismatch))
            gaps.AddRange(FindingGaps(cell));

        cellsWithoutGap = report.Cells.Count(c => !withGap.Contains(c) && HasNothingToReport(c));
        return gaps;
    }

    /// <summary>
    /// A cell with nothing to say: ready or already in Forecast, with no finding. A cell whose state is unknown, not attempted or in error
    /// is covered by a gap that names no cell (read incomplete, interrupted fill, Forecast error): it is not "without gap".
    /// </summary>
    public static bool HasNothingToReport(ForecastCellResult cell) => cell.IsOk && cell.Findings.Count == 0;

    private static IEnumerable<SubmissionGap> CellGaps(ForecastCellResult cell, ForecastFillReport report, Dictionary<DateOnly, decimal> totals, decimal weekTotal)
    {
        switch (cell.Status)
        {
            case ForecastCellStatus.Ok or ForecastCellStatus.Planned:
                break;

            case ForecastCellStatus.RowNotFound:
            {
                var ambiguous = cell.Issue == ForecastCellIssue.TaskAmbiguous;
                var type = ambiguous ? SubmissionGapType.TaskAmbiguous : SubmissionGapType.TaskNotFound;
                yield return CellGap(type, SubmissionGapSeverity.Blocking, cell,
                    ambiguous ? "Tâche ambiguë dans Forecast" : "Tâche introuvable dans Forecast",
                    cell.Message ?? "La tâche est introuvable dans Forecast.",
                    [
                        new(SubmissionGapResolution.LinkTask, IsRecommended: true),
                        new(SubmissionGapResolution.CreateTask),
                        new(SubmissionGapResolution.RefreshTasks),
                        new(SubmissionGapResolution.ReplaceTask, IsAvailable: false, UnavailableReason: AdjustmentsLater),
                        new(SubmissionGapResolution.MarkNotWork),
                        new(SubmissionGapResolution.IgnoreKnowingly, IsAvailable: false, UnavailableReason: AdjustmentsLater),
                    ],
                    removalPreview: Removal(cell, totals, weekTotal, "Si ces heures passent en « Pas du travail »"));
                break;
            }

            case ForecastCellStatus.Unknown when cell.Issue == ForecastCellIssue.ReadIncomplete:
                // Covered by the single "read incomplete" gap.
                break;

            case ForecastCellStatus.Unknown:
                yield return CellGap(SubmissionGapType.CellEntryUnknown, SubmissionGapSeverity.Blocking, cell,
                    "Saisie dont le résultat est inconnu",
                    cell.Message ?? "On ne sait pas si ces heures sont dans Forecast.",
                    [new(SubmissionGapResolution.ReadAgain, IsRecommended: true), new(SubmissionGapResolution.OpenInForecast)],
                    forecastHours: cell.Actual);
                break;

            case ForecastCellStatus.Error when cell.Issue == ForecastCellIssue.TaskUnknownLocally:
                yield return CellGap(SubmissionGapType.TaskUnknownLocally, SubmissionGapSeverity.Blocking, cell,
                    "Tâche inconnue localement",
                    cell.Message ?? "La tâche du brouillon n'existe plus dans le catalogue local.",
                    [
                        new(SubmissionGapResolution.RecalculateDraft, IsRecommended: true),
                        new(SubmissionGapResolution.ReplaceTask, IsAvailable: false, UnavailableReason: AdjustmentsLater),
                        new(SubmissionGapResolution.MarkNotWork),
                        new(SubmissionGapResolution.Remove),
                    ],
                    removalPreview: Removal(cell, totals, weekTotal, "Si ces heures sont retirées du brouillon"));
                break;

            case ForecastCellStatus.Error when cell.Issue == ForecastCellIssue.NotAttempted:
                // Covered by the "fill interrupted" gap.
                break;

            case ForecastCellStatus.Error when report.GlobalError is not null && cell.Message == report.GlobalError:
                // Covered by the single Forecast error gap.
                break;

            case ForecastCellStatus.Error:
                yield return CellGap(SubmissionGapType.ForecastError, SubmissionGapSeverity.Blocking, cell,
                    cell.Issue == ForecastCellIssue.WriteRefused ? "Saisie refusée par Forecast" : "Erreur Forecast sur la cellule",
                    cell.Message ?? "Forecast a répondu par une erreur.",
                    [new(SubmissionGapResolution.Retry, IsRecommended: true), new(SubmissionGapResolution.OpenInForecast)]);
                break;
        }

        foreach (var gap in FindingGaps(cell))
            yield return gap;
    }

    private static SubmissionGap ConflictGap(IReadOnlyList<ForecastCellResult> members, Dictionary<DateOnly, decimal> totals, decimal weekTotal)
    {
        var first = members[0];
        var expected = first.GroupExpected ?? first.Expected;
        var forecast = first.Actual ?? 0m;
        var higher = forecast > expected;
        var delta = forecast - expected;
        var type = higher ? SubmissionGapType.ForecastHigher : SubmissionGapType.ForecastLower;
        var approximate = first.Comparison == ForecastComparisonLevel.ProjectAndDay;
        var cellLabel = members.Count == 1
            ? $"« {first.TaskDisplayName} »"
            : $"{members.Count} tâches du même projet ({string.Join(", ", members.Select(m => "« " + m.TaskDisplayName + " »"))})";

        var message = higher
            ? $"Forecast contient {Hours(forecast)} le {DayName(first.Date)} pour {cellLabel}, {Hours(delta)} de plus que les {Hours(expected)} du brouillon."
            : $"Forecast contient {Hours(forecast)} le {DayName(first.Date)} pour {cellLabel}, {Hours(-delta)} de moins que les {Hours(expected)} du brouillon.";
        if (approximate)
            message += " Comparaison approximative : faite par projet et par jour, les heures d'une autre tâche du même projet peuvent s'y mêler.";

        SubmissionGapPreview preview;
        List<SubmissionGapAction> actions;
        var day = totals.GetValueOrDefault(first.Date);
        if (higher)
        {
            // Aligning the draft on Forecast: the draft cells take the hours Forecast holds.
            preview = new SubmissionGapPreview(
                "Si le brouillon est aligné sur Forecast", expected, forecast, day, day + delta, weekTotal, weekTotal + delta);
            actions =
            [
                new(SubmissionGapResolution.AlignDraftOnForecast, IsRecommended: true, IsAvailable: false, UnavailableReason: AdjustmentsLater),
                new(SubmissionGapResolution.OpenInForecast),
                new(SubmissionGapResolution.IgnoreKnowingly, IsAvailable: false, UnavailableReason: AdjustmentsLater),
                new(SubmissionGapResolution.ReadAgain),
            ];
        }
        else
        {
            // Adding the complement registers an entry in Forecast: the draft totals do not change.
            preview = new SubmissionGapPreview(
                $"Si un complément de {Hours(-delta)} est ajouté dans Forecast", forecast, expected, day, day, weekTotal, weekTotal);
            actions =
            [
                new(SubmissionGapResolution.AddComplement, IsRecommended: true, IsAvailable: false, UnavailableReason: AdjustmentsLater),
                new(SubmissionGapResolution.OpenInForecast),
                new(SubmissionGapResolution.IgnoreKnowingly, IsAvailable: false, UnavailableReason: AdjustmentsLater),
                new(SubmissionGapResolution.ReadAgain),
            ];
        }

        return new SubmissionGap
        {
            Id = IdOf(type, first.ForecastTaskId, first.Date),
            Type = type,
            Severity = SubmissionGapSeverity.Blocking,
            Date = first.Date,
            Cell = CellOf(first),
            RelatedCells = members.Skip(1).Select(CellOf).ToList(),
            Title = higher ? "Forecast contient plus d'heures que prévu" : "Forecast contient moins d'heures que prévu",
            Message = message,
            ExpectedHours = expected,
            ForecastHours = forecast,
            DeltaHours = delta,
            Comparison = first.Comparison,
            ExistingEntries = ExistingOf(first),
            Actions = actions,
            Preview = preview,
        };
    }

    private static IEnumerable<SubmissionGap> FindingGaps(ForecastCellResult cell)
    {
        foreach (var finding in cell.Findings.GroupBy(f => f.Kind))
        {
            var message = string.Join(" ", finding.Select(f => f.Message).Distinct());
            var (type, severity, title, actions) = finding.Key switch
            {
                ForecastFindingKind.TaskClosed => (SubmissionGapType.TaskClosed, SubmissionGapSeverity.ToVerify, "Tâche terminée dans Forecast",
                    Of(SubmissionGapResolution.OpenInForecast, SubmissionGapResolution.MarkNotWork)),
                ForecastFindingKind.TaskBlocked => (SubmissionGapType.TaskBlocked, SubmissionGapSeverity.ToVerify, "Tâche bloquée dans Forecast",
                    Of(SubmissionGapResolution.OpenInForecast, SubmissionGapResolution.MarkNotWork)),
                ForecastFindingKind.ProjectClosed => (SubmissionGapType.ProjectClosed, SubmissionGapSeverity.ToVerify, "Projet clos ou en pause dans Forecast",
                    Of(SubmissionGapResolution.OpenInForecast, SubmissionGapResolution.MarkNotWork)),
                ForecastFindingKind.EstimateExceeded => (SubmissionGapType.EstimateExceeded, SubmissionGapSeverity.ToVerify, "Estimation dépassée",
                    Of(SubmissionGapResolution.OpenInForecast)),
                ForecastFindingKind.TaskInactive => (SubmissionGapType.TaskInactive, SubmissionGapSeverity.ToVerify, "Tâche désactivée localement",
                    Of(SubmissionGapResolution.ReactivateTask, SubmissionGapResolution.MarkNotWork)),
                ForecastFindingKind.PendingApproval => (SubmissionGapType.CellPendingApproval, SubmissionGapSeverity.ToVerify, "Jour soumis pour approbation",
                    Of(SubmissionGapResolution.OpenInForecast)),
                _ => (SubmissionGapType.ForecastWarning, SubmissionGapSeverity.ToVerify, "Avertissement de Forecast",
                    Of(SubmissionGapResolution.OpenInForecast)),
            };

            yield return CellGap(type, severity, cell, title, message, actions);
        }

        static List<SubmissionGapAction> Of(params SubmissionGapResolution[] resolutions) =>
            resolutions.Select((r, i) => new SubmissionGapAction(r, IsRecommended: i == 0)).ToList();
    }

    private static SubmissionGap CellGap(
        SubmissionGapType type, SubmissionGapSeverity severity, ForecastCellResult cell, string title, string message,
        IReadOnlyList<SubmissionGapAction> actions, decimal? forecastHours = null, SubmissionGapPreview? removalPreview = null) =>
        new()
        {
            Id = IdOf(type, cell.ForecastTaskId, cell.Date),
            Type = type,
            Severity = severity,
            Date = cell.Date,
            Cell = CellOf(cell),
            Title = title,
            Message = message,
            ExpectedHours = cell.Expected,
            ForecastHours = forecastHours,
            DeltaHours = forecastHours is { } f ? f - cell.Expected : null,
            Comparison = cell.Comparison,
            ExistingEntries = ExistingOf(cell),
            Actions = actions,
            Preview = removalPreview,
        };

    private static SubmissionGapPreview Removal(ForecastCellResult cell, Dictionary<DateOnly, decimal> totals, decimal weekTotal, string description)
    {
        var day = totals.GetValueOrDefault(cell.Date);
        return new SubmissionGapPreview(description, cell.Expected, 0, day, day - cell.Expected, weekTotal, weekTotal - cell.Expected);
    }

    private static SubmissionGapCell CellOf(ForecastCellResult cell) => new(cell.ForecastTaskId, cell.TaskDisplayName, cell.Date);

    private static IReadOnlyList<SubmissionExistingEntry> ExistingOf(ForecastCellResult cell) =>
        cell.ExistingEntries.Select(e => new SubmissionExistingEntry(e.EntryId, e.ProjectName, e.Notes, e.Hours, e.ApprovalStatus, e.Invoiced)).ToList();

    private static string IdOf(SubmissionGapType type, int? taskId, DateOnly? date) =>
        $"{type}|{taskId?.ToString(CultureInfo.InvariantCulture)}|{date?.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}";

    private static string Hours(decimal hours) => hours.ToString("0.##", French) + " h";

    private static string DayName(DateOnly date) => date.ToString("ddd d MMM", French);
}
