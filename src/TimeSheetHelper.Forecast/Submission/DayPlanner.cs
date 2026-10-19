using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;

namespace TimeSheetHelper.Forecast.Submission;

/// <summary>
/// State of one (task, day) cell. D = hours of the draft, F = hours read in Forecast, S = <see cref="DaySubmissionCell.SentHours"/>.
/// Equal = within <see cref="ForecastFillPlanner.Tolerance"/>. Evaluated in this order:
/// Ignored &gt; Locked &gt; NotAttempted &gt; Deleted &gt; Unchanged &gt; New &gt; Modified &gt; Conflict.
/// </summary>
public enum CellState
{
    /// <summary>D and no F: to write.</summary>
    New,

    /// <summary>D == F: already in Forecast, not written again (no duplicate).</summary>
    Unchanged,

    /// <summary>F == S and D != F: written by the application, the draft changed since.</summary>
    Modified,

    /// <summary>No D but S: sent, then removed from the draft.</summary>
    Deleted,

    /// <summary>F != S (or F without S) and D != F: Forecast was changed elsewhere.</summary>
    Conflict,

    /// <summary>Task left out of the submission (flag, choice of the week, or ignored by the caller).</summary>
    Ignored,

    /// <summary>The day holds registrations Forecast approved or invoiced.</summary>
    Locked,

    /// <summary>The send stopped before this cell, or Forecast could not be read whole (G4).</summary>
    NotAttempted,
}

/// <summary>Operation planned for a cell.</summary>
public enum CellOperation { None, Create, Update, Delete, Complement, AcceptForecast }

public sealed record CellPlan(
    int ForecastTaskId,
    string TaskDisplayName,
    decimal DraftHours,
    decimal? ForecastHours,
    decimal? SentHours,
    string? ForecastEntryId,
    CellState State,
    CellOperation Operation,
    IReadOnlyList<ConflictResolution> AllowedResolutions,
    string? BlockedReason,
    int? AmbiguousGroup = null);

/// <summary>What the send button of a day offers (labels: contracts §3.2).</summary>
public enum DayGate { NothingToSend, NothingNew, ReadForecastFirst, ResolveConflicts, Send, SendChanges, Resume, InProgress, Locked }

public sealed record DayPlan(
    DateOnly WeekStart,
    DateOnly Date,
    DayStatus Status,
    string? CurrentFingerprint,
    string? SentFingerprint,
    IReadOnlyList<CellPlan> Cells,
    DayGate Gate,
    bool ForecastReadComplete,
    ForecastCapabilities Capabilities)
{
    public bool HasWork => Cells.Any(c => c.Operation != CellOperation.None);

    public bool IsReady => Gate is DayGate.Send or DayGate.SendChanges or DayGate.Resume;
}

/// <summary>The registrations of the signed-in person on one day, and whether the list is whole.</summary>
public sealed record ForecastDayRead(DateOnly Date, IReadOnlyList<ForecastRemoteEntry> Entries, bool IsComplete, string? Detail)
{
    public static ForecastDayRead From(DateOnly date, ForecastEntryRead read) =>
        new(date, read.Entries.Where(e => e.Date == date).ToList(), read.IsComplete, read.Detail);

    public static ForecastDayRead Unreadable(DateOnly date, string detail) => new(date, [], false, detail);
}

/// <summary>
/// Pure plan of one day: the draft, what Forecast holds and what the application sent before give the state and the operation of every
/// cell, the status of the day and its send button. No database, no network.
/// <para>
/// Forecast's registrations carry an internal task id that matches no task number, so they are attributed to the tasks of the day in this
/// order: the registration the application wrote (its id was kept), the task number when Forecast gives it, then the project. When the hours
/// of one project cannot be split between several tasks of the day, those tasks are a <see cref="CellState.Conflict"/>: never a creation
/// that could duplicate them.
/// </para>
/// </summary>
public static class DayPlanner
{
    public static DayPlan Plan(
        DateOnly weekStart, DateOnly date,
        IReadOnlyList<TimesheetEntry> draftEntries,
        ForecastDayRead forecast,
        DaySubmission? previous,
        IReadOnlySet<int> ignoredTaskIds,
        IReadOnlyDictionary<int, ForecastTask> tasks,
        ForecastCapabilities capabilities)
    {
        var draft = draftEntries
            .Where(e => e.Date == date)
            .GroupBy(e => e.ForecastTaskId)
            .Select(g => (TaskId: g.Key, Hours: ForecastHours.RoundToQuarter(g.Sum(e => e.Hours))))
            .Where(c => c.Hours > 0 && !(tasks.TryGetValue(c.TaskId, out var t) && t.IsIgnored))
            .ToDictionary(c => c.TaskId, c => c.Hours);
        var sent = (previous?.Cells ?? []).GroupBy(c => c.ForecastTaskId).ToDictionary(g => g.Key, g => g.First());
        var ids = draft.Keys.Union(sent.Keys).ToList();

        var dayEntries = forecast.Entries.Where(e => e.Date == date).ToList();
        var complete = forecast.IsComplete;
        var locked = complete ? dayEntries.FirstOrDefault(e => e.IsLocked) : null;
        var attribution = complete
            ? Attribute(dayEntries, ids.Where(id => !ignoredTaskIds.Contains(id)).ToList(), draft, sent.Values, tasks)
            : Attribution.Empty;

        var cells = ids
            .Select(id => PlanCell(id, date, draft, sent, ignoredTaskIds, tasks, capabilities, forecast, locked, attribution))
            .OrderBy(c => c.TaskDisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.ForecastTaskId)
            .ToList();

        var currentFingerprint = DayFingerprint.Compute(date,
            draft.Where(c => !ignoredTaskIds.Contains(c.Key)).Select(c => (c.Key, c.Value)));
        var previousStatus = previous?.Status ?? DayStatus.NotAttempted;
        var status = StatusOf(previousStatus, complete, locked is not null, cells);
        var gate = GateOf(previousStatus, complete, locked is not null, cells);

        return new DayPlan(weekStart, date, status, currentFingerprint, previous?.Fingerprint, cells, gate, complete, capabilities);
    }

    private static CellPlan PlanCell(
        int id, DateOnly date, Dictionary<int, decimal> draft, Dictionary<int, DaySubmissionCell> sent, IReadOnlySet<int> ignored,
        IReadOnlyDictionary<int, ForecastTask> tasks, ForecastCapabilities capabilities, ForecastDayRead forecast,
        ForecastRemoteEntry? locked, Attribution attribution)
    {
        var d = draft.GetValueOrDefault(id);
        decimal? s = sent.TryGetValue(id, out var sentCell) ? sentCell.SentHours : null;
        var known = tasks.TryGetValue(id, out var task);
        var name = known ? task!.DisplayName : $"Tâche #{id}";
        attribution.Cells.TryGetValue(id, out var found);
        decimal? f = found?.Hours;
        var entryId = found?.EntryId;

        CellPlan Cell(CellState state, CellOperation operation, string? reason = null, IReadOnlyList<ConflictResolution>? allowed = null, decimal? forecastHours = null, int? group = null) =>
            new(id, name, d, forecastHours ?? f, s, entryId, state, operation, allowed ?? [], reason, group);

        if (ignored.Contains(id))
            return Cell(CellState.Ignored, CellOperation.None);

        if (!forecast.IsComplete)
            return Cell(CellState.NotAttempted, CellOperation.None,
                $"Lecture incomplète des saisies de Forecast ({forecast.Detail ?? "liste tronquée"}) : aucune comparaison, rien n'est écrit.");

        if (locked is not null)
            return Cell(CellState.Locked, CellOperation.None,
                $"Le {date:yyyy-MM-dd} contient des saisies {(locked.Invoiced ? "facturées" : "approuvées")} dans Forecast : rien n'est écrit ni modifié.");

        if (attribution.Ambiguous.TryGetValue(id, out var ambiguous))
            return Cell(CellState.Conflict, CellOperation.None,
                $"Forecast contient {ambiguous.Hours:0.##} h sur ce projet le {date:yyyy-MM-dd} sans qu'on puisse les répartir entre les tâches du jour : "
                + "rien n'est écrit, pour ne pas créer de doublon. Vérifiez dans Forecast.",
                [ConflictResolution.Keep], ambiguous.Hours, ambiguous.Group);

        var canDelete = capabilities.Delete == ForecastCapability.Available && entryId is not null;
        var canUpdate = capabilities.Update == ForecastCapability.Available && entryId is not null;

        if (d == 0 && s is not null)
        {
            if (f is null)
                return Cell(CellState.Unchanged, CellOperation.None);
            if (Equal(f.Value, s.Value))
                return canDelete
                    ? Cell(CellState.Deleted, CellOperation.Delete)
                    : Cell(CellState.Deleted, CellOperation.None, Unavailable(capabilities.Delete, entryId, "la suppression", "Supprimez-la dans Forecast."));
            return Cell(CellState.Conflict, CellOperation.None, null, Resolutions(d, f.Value, canDelete, canUpdate));
        }

        if (f is not null && Equal(d, f.Value))
            return Cell(CellState.Unchanged, CellOperation.None);

        if (f is null)
        {
            if (!known)
                return Cell(CellState.New, CellOperation.None,
                    $"La tâche locale #{id} est inconnue (supprimée ?) : ses heures ne peuvent pas être saisies. Classez-les sur une autre tâche.");
            return capabilities.Create == ForecastCapability.Available
                ? Cell(CellState.New, CellOperation.Create)
                : Cell(CellState.New, CellOperation.None, capabilities.UnavailableReason ?? "Le serveur Forecast ne propose pas la création d'une saisie.");
        }

        if (s is not null && Equal(f.Value, s.Value))
            return canUpdate
                ? Cell(CellState.Modified, CellOperation.Update)
                : Cell(CellState.Modified, CellOperation.None, Unavailable(capabilities.Update, entryId, "la modification", "Modifiez-la dans Forecast."));

        return Cell(CellState.Conflict, CellOperation.None, null, Resolutions(d, f.Value, canDelete, canUpdate));
    }

    /// <summary>
    /// Keep and accept are always possible; complement when the draft holds more (it raises the registration by the delta, which needs the
    /// update tool); remove when the registration can be deleted.
    /// </summary>
    private static IReadOnlyList<ConflictResolution> Resolutions(decimal d, decimal f, bool canDelete, bool canUpdate)
    {
        var allowed = new List<ConflictResolution> { ConflictResolution.Keep, ConflictResolution.AcceptForecast };
        if (d - f > ForecastFillPlanner.Tolerance && canUpdate)
            allowed.Add(ConflictResolution.Complement);
        if (canDelete)
            allowed.Add(ConflictResolution.Remove);
        return allowed;
    }

    private static string Unavailable(ForecastCapability capability, string? entryId, string what, string advice) =>
        capability != ForecastCapability.Available
            ? $"Le serveur Forecast ne propose pas {what} d'une saisie : rien n'est écrit, jamais de saisie de remplacement. {advice}"
            : entryId is null
                ? $"La saisie de Forecast n'est pas identifiable seule (plusieurs saisies ou tâche reconnue par son projet) : rien n'est écrit. {advice}"
                : $"Rien n'est écrit. {advice}";

    private static DayStatus StatusOf(DayStatus previous, bool complete, bool locked, IReadOnlyList<CellPlan> cells)
    {
        if (previous == DayStatus.InProgress)
            return DayStatus.InProgress;
        if (!complete)
            return previous;
        if (locked)
            return DayStatus.Locked;
        if (cells.Any(c => c.State == CellState.Conflict))
            return DayStatus.Conflict;

        var settled = !cells.Any(c => c.Operation != CellOperation.None || (c.BlockedReason is not null && c.State != CellState.Ignored));
        return previous switch
        {
            DayStatus.NotAttempted => DayStatus.NotAttempted,
            DayStatus.Partial or DayStatus.Error => settled ? DayStatus.Sent : previous,
            _ => settled ? DayStatus.Sent : DayStatus.Modified,
        };
    }

    private static DayGate GateOf(DayStatus previous, bool complete, bool locked, IReadOnlyList<CellPlan> cells)
    {
        if (previous == DayStatus.InProgress)
            return DayGate.InProgress;
        if (!complete)
            return DayGate.ReadForecastFirst;
        if (locked)
            return DayGate.Locked;
        if (cells.Any(c => c.State == CellState.Conflict))
            return DayGate.ResolveConflicts;

        if (cells.Any(c => c.Operation != CellOperation.None))
        {
            if (previous is DayStatus.Partial or DayStatus.Error)
                return DayGate.Resume;
            return previous != DayStatus.NotAttempted || cells.Any(c => c.Operation is CellOperation.Update or CellOperation.Delete)
                ? DayGate.SendChanges
                : DayGate.Send;
        }

        if (cells.Any(c => c.BlockedReason is not null && c.State != CellState.Ignored))
            return DayGate.ResolveConflicts;

        return cells.Any(c => c.State != CellState.Ignored) ? DayGate.NothingNew : DayGate.NothingToSend;
    }

    private static bool Equal(decimal a, decimal b) => Math.Abs(a - b) <= ForecastFillPlanner.Tolerance;

    private sealed record AttributedCell(decimal Hours, IReadOnlyList<ForecastRemoteEntry> Entries)
    {
        public string? EntryId => Entries.Count == 1 ? Entries[0].EntryId : null;
    }

    /// <summary>Ambiguous cells: the hours Forecast holds for their project, and the group (id of its first task) they share.</summary>
    private sealed record Attribution(IReadOnlyDictionary<int, AttributedCell> Cells, IReadOnlyDictionary<int, (decimal Hours, int Group)> Ambiguous)
    {
        public static Attribution Empty { get; } = new(new Dictionary<int, AttributedCell>(), new Dictionary<int, (decimal, int)>());
    }

    private static Attribution Attribute(
        IReadOnlyList<ForecastRemoteEntry> entries, IReadOnlyList<int> candidates, IReadOnlyDictionary<int, decimal> draft,
        IEnumerable<DaySubmissionCell> sent, IReadOnlyDictionary<int, ForecastTask> tasks)
    {
        var remaining = entries.ToList();
        var byTask = new Dictionary<int, List<ForecastRemoteEntry>>();
        var ambiguous = new Dictionary<int, (decimal Hours, int Group)>();

        void Assign(int taskId, ForecastRemoteEntry entry)
        {
            if (!byTask.TryGetValue(taskId, out var list))
                byTask[taskId] = list = [];
            list.Add(entry);
            remaining.Remove(entry);
        }

        // 1. The registrations the application wrote: their id was kept.
        foreach (var cell in sent.Where(c => c.ForecastEntryId is not null && candidates.Contains(c.ForecastTaskId)))
        {
            foreach (var entry in remaining.Where(e => string.Equals(e.EntryId, cell.ForecastEntryId, StringComparison.Ordinal)).ToList())
                Assign(cell.ForecastTaskId, entry);
        }

        // 2. The task number, when Forecast gives it.
        foreach (var entry in remaining.Where(e => e.TaskCode is not null).ToList())
        {
            var matching = candidates.Where(id => tasks.TryGetValue(id, out var t) && TaskCodeOf(t) is { } code
                && string.Equals(code, entry.TaskCode, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matching.Count == 1)
                Assign(matching[0], entry);
        }

        // 3. The project: the hours of a project go to its only task of the day, or are split when the draft explains them.
        var open = candidates.Where(id => !byTask.ContainsKey(id) && tasks.ContainsKey(id)).ToList();
        // A registration whose task number is known but matches no task of the day belongs to another task of the project (or to nobody we
        // know): it is never attributed, so never deleted nor modified through another task.
        bool IsForeign(ForecastRemoteEntry entry) =>
            entry.TaskCode is not null
            && !candidates.Any(id => tasks.TryGetValue(id, out var t) && string.Equals(TaskCodeOf(t), entry.TaskCode, StringComparison.OrdinalIgnoreCase));

        foreach (var group in remaining.Where(e => (e.ProjectName is not null || e.ProjectId is not null) && !IsForeign(e))
                     .GroupBy(e => e.ProjectId ?? e.ProjectName!).ToList())
        {
            var groupEntries = group.ToList();
            var members = open.Where(id => groupEntries.Any(e => SameProject(tasks[id], e))).ToList();
            if (members.Count == 0)
                continue;
            open.RemoveAll(members.Contains);

            if (members.Count == 1)
            {
                foreach (var entry in groupEntries)
                    Assign(members[0], entry);
                continue;
            }

            var total = groupEntries.Sum(e => e.Hours);
            var explained = Explain(members, draft, total);
            if (explained is null)
            {
                foreach (var id in members)
                    ambiguous[id] = (total, members[0]);
                continue;
            }

            foreach (var entry in groupEntries)
                remaining.Remove(entry);
            foreach (var id in explained)
                byTask[id] = [];
            foreach (var id in explained)
            {
                var single = groupEntries.Count == explained.Count
                    ? groupEntries.Where(e => Equal(e.Hours, draft.GetValueOrDefault(id))).ToList()
                    : [];
                byTask[id] = single.Count == 1 && explained.Count(o => Equal(draft.GetValueOrDefault(o), draft.GetValueOrDefault(id))) == 1
                    ? single
                    : [];
            }
        }

        var cells = byTask.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Count > 0
                ? new AttributedCell(kv.Value.Sum(e => e.Hours), kv.Value)
                : new AttributedCell(draft.GetValueOrDefault(kv.Key), []));
        return new Attribution(cells, ambiguous);
    }

    /// <summary>The tasks whose draft hours add up to what Forecast holds for their project (all of them first, else the smallest set), or null.</summary>
    private static IReadOnlyList<int>? Explain(IReadOnlyList<int> members, IReadOnlyDictionary<int, decimal> draft, decimal total)
    {
        const int maxMembers = 16;
        if (Equal(members.Sum(id => draft.GetValueOrDefault(id)), total))
            return members;
        if (members.Count > maxMembers)
            return null;

        IReadOnlyList<int>? best = null;
        for (var mask = 1; mask < (1 << members.Count) - 1; mask++)
        {
            var chosen = Enumerable.Range(0, members.Count).Where(i => (mask & (1 << i)) != 0).Select(i => members[i]).ToList();
            if (best is not null && chosen.Count >= best.Count)
                continue;
            if (chosen.All(id => draft.GetValueOrDefault(id) > 0) && Equal(chosen.Sum(id => draft.GetValueOrDefault(id)), total))
                best = chosen;
        }
        return best;
    }

    private static string? TaskCodeOf(ForecastTask task) =>
        ForecastCodes.TaskCode.Match(task.ExternalId) is { Success: true } code ? code.Value : null;

    private static bool SameProject(ForecastTask task, ForecastRemoteEntry entry)
    {
        if (entry.ProjectName is null)
            return false;
        if (task.ProjectCode is { } localCode && ForecastCodes.ProjectCode.Match(entry.ProjectName) is { Success: true } remoteCode
            && string.Equals(Normalize(localCode), Normalize(remoteCode.Value), StringComparison.OrdinalIgnoreCase))
            return true;
        var name = Key(ForecastCodes.ProjectCode.Replace(entry.ProjectName, ""));
        return name.Length > 0 && (name == Key(task.ProjectName) || name == Key(task.ClientName));
    }

    private static string Normalize(string code) => code.Replace("-", "", StringComparison.Ordinal);

    private static string Key(string? text) =>
        text is null ? "" : new string(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
