using TimeSheetHelper.Core.Forecast;

namespace TimeSheetHelper.Forecast;

/// <summary>A planned cell whose task was found in Forecast.</summary>
internal sealed record ForecastResolvedCell(ForecastPlannedCell Cell, ForecastRemoteTask Remote)
{
    /// <summary>Cells of one project and day share the hours Forecast holds for them.</summary>
    public string GroupKey => $"{Remote.ProjectId ?? "task:" + Remote.Id}|{Cell.Date:yyyyMMdd}";
}

/// <summary>What to do with one cell, decided from what Forecast already holds for its project and day.</summary>
internal sealed record ForecastCellEvaluation(
    ForecastResolvedCell Resolved,
    ForecastCellAction Action,
    decimal Current,
    decimal? GroupExpected,
    ForecastComparisonLevel Comparison,
    IReadOnlyList<ForecastRemoteEntry> Existing,
    bool Locked,
    string? LockMessage,
    bool PendingApproval,
    string Message);

/// <summary>
/// Pure decision of the fill, per project and day. The registrations of Forecast carry an internal task id that matches no task
/// number, so two tasks of one project and day cannot be told apart: they are judged together. When what Forecast holds equals the sum
/// of some of the cells (a fill interrupted after the first of two tasks), those cells are done and the others are still to register.
/// </summary>
internal static class ForecastCellEvaluator
{
    private const int MaxSubsetCells = 16;

    public static IReadOnlyList<ForecastCellEvaluation> Evaluate(
        IReadOnlyList<ForecastResolvedCell> cells, IReadOnlyList<ForecastRemoteEntry> entries)
    {
        var results = new Dictionary<ForecastResolvedCell, ForecastCellEvaluation>();
        foreach (var group in cells.GroupBy(c => c.GroupKey))
        {
            var members = group.ToList();
            var date = members[0].Cell.Date;
            var dayEntries = entries.Where(e => e.Date == date).ToList();
            var matched = dayEntries.Where(e => members.Any(m => Matches(e, m.Remote))).ToList();
            var locked = dayEntries.FirstOrDefault(e => e.IsLocked);
            var pending = dayEntries.Any(e => e.IsSubmitted);

            var current = matched.Sum(e => e.Hours);
            var expected = members.Sum(m => m.Cell.Hours);
            var exact = matched.Count > 0 && members.Count == 1 && matched.All(e => MatchesExactly(e, members[0].Remote));
            var level = matched.Count == 0 ? ForecastComparisonLevel.None
                : exact ? ForecastComparisonLevel.TaskAndDay
                : ForecastComparisonLevel.ProjectAndDay;
            decimal? groupExpected = members.Count > 1 ? expected : null;

            var done = new HashSet<ForecastResolvedCell>();
            var conflict = false;
            if (current > 0)
            {
                if (Math.Abs(current - expected) <= ForecastFillPlanner.Tolerance)
                    done.UnionWith(members);
                else if (FindSubset(members, current) is { } subset)
                    done.UnionWith(subset);
                else
                    conflict = true;
            }

            foreach (var member in members)
            {
                var action = conflict ? ForecastCellAction.Conflict
                    : done.Contains(member) ? ForecastCellAction.AlreadyDone
                    : ForecastCellAction.Type;
                var message = Describe(action, member.Cell.Hours, current, groupExpected, level);
                string? lockMessage = null;
                if (action == ForecastCellAction.Type && locked is not null)
                {
                    lockMessage = $"Des saisies du {date:yyyy-MM-dd} sont déjà {(locked.Invoiced ? "facturées" : "approuvées")} dans Forecast"
                        + (locked.ProjectName is null ? "" : $" ({locked.ProjectName})")
                        + " : cette cellule n'est pas saisie.";
                }

                results[member] = new ForecastCellEvaluation(
                    member, action, current, groupExpected, level, matched, lockMessage is not null, lockMessage, pending, message);
            }
        }

        return cells.Select(c => results[c]).ToList();
    }

    /// <summary>
    /// Registrations of the project that day, or of the task itself when Forecast ever gives its code or id. A registration without
    /// a project (idle time) matches nothing.
    /// </summary>
    internal static bool Matches(ForecastRemoteEntry entry, ForecastRemoteTask task) =>
        MatchesExactly(entry, task) || (task.ProjectId is not null && entry.ProjectId == task.ProjectId);

    private static bool MatchesExactly(ForecastRemoteEntry entry, ForecastRemoteTask task) =>
        (entry.TaskId is not null && entry.TaskId == task.Id)
        || (entry.TaskCode is not null && string.Equals(entry.TaskCode, task.Code, StringComparison.OrdinalIgnoreCase));

    private static string Describe(ForecastCellAction action, decimal expected, decimal current, decimal? groupExpected, ForecastComparisonLevel level)
    {
        if (action == ForecastCellAction.Conflict)
        {
            var scope = groupExpected is { } g ? $"prévu {g:0.##} h pour ces tâches du même projet" : $"prévu {expected:0.##} h";
            return $"Forecast contient déjà {current:0.##} h ({scope}) : non modifié, car un nouvel enregistrement s'ajouterait au lieu de remplacer. Corrigez dans Forecast."
                + (level == ForecastComparisonLevel.ProjectAndDay ? " Comparaison approximative (projet et jour)." : "");
        }

        var (_, text) = ForecastFillPlanner.Decide(expected, action == ForecastCellAction.AlreadyDone ? expected : 0);
        return text;
    }

    /// <summary>Cells whose hours add up to what Forecast holds (smallest set first), or null.</summary>
    private static IReadOnlyList<ForecastResolvedCell>? FindSubset(IReadOnlyList<ForecastResolvedCell> members, decimal target)
    {
        if (members.Count < 2 || members.Count > MaxSubsetCells)
            return null;

        IReadOnlyList<ForecastResolvedCell>? best = null;
        for (var mask = 1; mask < (1 << members.Count) - 1; mask++)
        {
            var chosen = Enumerable.Range(0, members.Count).Where(i => (mask & (1 << i)) != 0).Select(i => members[i]).ToList();
            if (best is not null && chosen.Count >= best.Count)
                continue;
            if (Math.Abs(chosen.Sum(c => c.Cell.Hours) - target) <= ForecastFillPlanner.Tolerance)
                best = chosen;
        }
        return best;
    }
}
