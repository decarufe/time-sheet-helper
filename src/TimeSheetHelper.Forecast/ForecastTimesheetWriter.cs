using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Forecast;

/// <summary>
/// Registers a <see cref="TimesheetDraft"/> in Forecast through the MCP server and reads it back.
/// Only empty (task, day) cells receive a registration; a cell that already holds other hours is reported and left
/// alone (see <see cref="ForecastFillPlanner.Decide"/>), because creating a registration adds to what exists.
/// Sending the week for manager approval is never done here.
/// </summary>
public sealed class ForecastTimesheetWriter(
    ForecastApi api,
    IDbContextFactory<TimeSheetDbContext> dbFactory,
    ILogger<ForecastTimesheetWriter> logger)
{
    public async Task<ForecastFillReport> FillWeekAsync(
        TimesheetDraft draft, ForecastSubmitMode mode, CancellationToken cancellationToken = default)
    {
        var report = new ForecastFillReport { WeekStart = draft.WeekStart, Mode = mode };

        var plan = await PlanAsync(draft, report.Warnings, cancellationToken);
        if (plan.Count == 0)
        {
            report.Warnings.Add("Aucune heure à saisir dans ce brouillon.");
            return report;
        }

        var weekEnd = draft.WeekStart.AddDays(6);
        await using var session = await api.OpenAsync(cancellationToken);

        IReadOnlyList<ForecastRemoteTask> remoteTasks;
        IReadOnlyList<ForecastRemoteEntry> existing;
        try
        {
            remoteTasks = await session.ListTasksAsync(cancellationToken);
            existing = await session.ListTimeEntriesAsync(draft.WeekStart, weekEnd, cancellationToken);
            if (mode == ForecastSubmitMode.Fill)
                session.Require(ForecastToolRole.CreateTimeEntry);
        }
        catch (ForecastToolException ex)
        {
            foreach (var cell in plan)
                report.Cells.Add(Result(cell, ForecastCellStatus.Error, null, ex.Message));
            return report;
        }

        if (remoteTasks.Count == 0)
            report.Warnings.Add("Forecast n'a renvoyé aucune tâche : impossible d'y retrouver celles du brouillon.");

        var written = new List<(ForecastPlannedCell Cell, ForecastRemoteTask Remote, decimal Before)>();
        var looked = new Dictionary<string, ForecastRemoteTask?>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((FindRemote(cell.Task, remoteTasks) ?? await LookUpByCodeAsync(session, cell.Task, looked, cancellationToken)) is not { } remote)
            {
                report.Cells.Add(Result(cell, ForecastCellStatus.RowNotFound, null,
                    $"« {cell.Task.DisplayName} » est introuvable dans Forecast (code {cell.Task.ExternalId}) : "
                    + "cherchez la tâche dans Forecast et reliez-la (bouton « Rechercher et relier »)."));
                continue;
            }

            var current = HoursOf(existing, remote, cell.Date);
            var (action, message) = ForecastFillPlanner.Decide(cell.Hours, current);
            if (mode == ForecastSubmitMode.DryRun || action != ForecastCellAction.Type)
            {
                var status = action switch
                {
                    ForecastCellAction.AlreadyDone => ForecastCellStatus.Ok,
                    ForecastCellAction.Conflict => ForecastCellStatus.Mismatch,
                    _ => ForecastCellStatus.Planned,
                };
                report.Cells.Add(Result(cell, status, current, message));
                continue;
            }

            try
            {
                await session.CreateTimeEntryAsync(remote.Id, cell.Date, cell.Hours, cell.Notes, cancellationToken);
                written.Add((cell, remote, current));
            }
            catch (Exception ex) when (ex is ForecastToolException or TimeoutException or HttpRequestException)
            {
                logger.LogWarning(ex, "Échec de la saisie de {Task} le {Date}", cell.Task.DisplayName, cell.Date);
                report.Cells.Add(Result(cell, ForecastCellStatus.Error, null, ex.Message));
            }
        }

        if (written.Count > 0)
            await VerifyAsync(session, draft.WeekStart, weekEnd, written, report, cancellationToken);

        logger.LogInformation("{Summary}", report.Summary);
        return report;
    }

    /// <summary>
    /// Sends the week for manager approval. Deliberately not implemented: nothing in the Forecast tools has been
    /// checked for it, and submitting locks the week.
    /// </summary>
    public Task SubmitForApprovalAsync(DateOnly weekStart, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException(
            "TODO: soumission pour approbation non implémentée : à faire à la main dans Forecast.");

    /// <summary>
    /// Time can be logged against any task, not only the ones assigned to the person (the only ones listed): a task
    /// known by its T-number but missing from that list is asked for by number.
    /// </summary>
    private async Task<ForecastRemoteTask?> LookUpByCodeAsync(
        ForecastMcpSession session, ForecastTask task, Dictionary<string, ForecastRemoteTask?> looked, CancellationToken cancellationToken)
    {
        if (ForecastCodes.TaskCode.Match(task.ExternalId) is not { Success: true } code)
            return null;
        if (looked.TryGetValue(code.Value, out var known))
            return known;

        ForecastRemoteTask? found = null;
        try
        {
            found = (await session.FindTasksAsync(code.Value, cancellationToken))
                .FirstOrDefault(r => string.Equals(r.Code, code.Value, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is ForecastToolException or TimeoutException or HttpRequestException)
        {
            logger.LogWarning(ex, "Recherche de la tâche {Code} impossible.", code.Value);
        }

        looked[code.Value] = found;
        return found;
    }

    internal static ForecastRemoteTask? FindRemote(ForecastTask task, IReadOnlyList<ForecastRemoteTask> remote)
    {
        if (ForecastCodes.TaskCode.Match(task.ExternalId) is { Success: true } code)
        {
            var byCode = remote.FirstOrDefault(r => string.Equals(r.Code, code.Value, StringComparison.OrdinalIgnoreCase));
            if (byCode is not null)
                return byCode;
        }

        var title = Key(task.TaskName);
        var project = Key(task.ProjectName);
        var byNames = remote
            .Where(r => Key(r.Title) == title)
            .Where(r => Key(r.ProjectName) == project || Key(r.ClientName) == project)
            .Take(2)
            .ToList();
        return byNames.Count == 1 ? byNames[0] : null;
    }

    private async Task VerifyAsync(
        ForecastMcpSession session, DateOnly from, DateOnly to,
        IReadOnlyList<(ForecastPlannedCell Cell, ForecastRemoteTask Remote, decimal Before)> written,
        ForecastFillReport report, CancellationToken cancellationToken)
    {
        IReadOnlyList<ForecastRemoteEntry> after;
        try
        {
            after = await session.ListTimeEntriesAsync(from, to, cancellationToken);
        }
        catch (Exception ex) when (ex is ForecastToolException or TimeoutException or HttpRequestException)
        {
            report.Warnings.Add($"Relecture impossible : {ex.Message}");
            foreach (var (cell, _, _) in written)
                report.Cells.Add(Result(cell, ForecastCellStatus.Error, null, "Saisi, mais non relu dans Forecast."));
            return;
        }

        // The registrations of one project and day cannot be told apart (see HoursOf): what was added is checked for the group.
        foreach (var group in written.GroupBy(w => (Key: w.Remote.ProjectId ?? w.Remote.Id, w.Cell.Date)))
        {
            var members = group.ToList();
            var added = HoursOf(after, members[0].Remote, group.Key.Date) - members[0].Before;
            var (status, actual, message) = ForecastFillPlanner.Compare(members.Sum(m => m.Cell.Hours), added);
            foreach (var (cell, _, _) in members)
                report.Cells.Add(status == ForecastCellStatus.Ok
                    ? Result(cell, status, cell.Hours, message)
                    : Result(cell, status, actual, message));
        }
    }

    private async Task<IReadOnlyList<ForecastPlannedCell>> PlanAsync(
        TimesheetDraft draft, ICollection<string> warnings, CancellationToken cancellationToken)
    {
        var ids = draft.Entries.Select(e => e.ForecastTaskId).Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var tasks = await db.ForecastTasks.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
        return ForecastFillPlanner.Plan(draft, tasks, warnings);
    }

    /// <summary>
    /// The hours registered that day on the task. The registrations of "get_time_analysis" carry an internal task id that
    /// matches nothing else, so they are matched by project too: two tasks of one project share their hours.
    /// </summary>
    private static decimal HoursOf(IReadOnlyList<ForecastRemoteEntry> entries, ForecastRemoteTask task, DateOnly date) =>
        entries
            .Where(e => e.Date == date)
            .Where(e => e.TaskId == task.Id
                || (e.TaskCode is not null && string.Equals(e.TaskCode, task.Code, StringComparison.OrdinalIgnoreCase))
                || (task.ProjectId is not null && e.ProjectId == task.ProjectId))
            .Sum(e => e.Hours);

    private static string Key(string? text) =>
        new((text ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static ForecastCellResult Result(ForecastPlannedCell cell, ForecastCellStatus status, decimal? actual, string? message) =>
        new(cell.Task.Id, cell.Task.DisplayName, cell.Date, cell.Hours, actual, status, message);
}
