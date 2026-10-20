using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Forecast;

/// <summary>
/// Registers a <see cref="TimesheetDraft"/> in Forecast through the MCP server and reads it back.
/// Only empty (task, day) cells receive a registration; a cell that already holds other hours is reported and left
/// alone (see <see cref="ForecastCellEvaluator"/>), because creating a registration adds to what exists. A day holding registrations
/// that Forecast approved or invoiced is not written to. Whatever happens (timeout, network, refusal), the week is read back and a
/// report with the state of every cell is returned, so the fill can be resumed.
/// Sending the week for manager approval is never done here.
/// </summary>
public sealed class ForecastTimesheetWriter(
    ForecastApi api,
    IDbContextFactory<TimeSheetDbContext> dbFactory,
    ILogger<ForecastTimesheetWriter> logger)
{
    /// <param name="days">
    /// Only the planned cells of these days are compared with Forecast and (in <see cref="ForecastSubmitMode.Fill"/>) registered;
    /// the other days of the week are not read for comparison and never written to. Null or empty: the whole week, as before.
    /// </param>
    /// <param name="ignoredTaskIds">
    /// Tasks whose cells are left out of the comparison and the fill (their hours are listed in <see cref="ForecastFillReport.IgnoredCells"/>).
    /// Null: the tasks flagged <see cref="ForecastTask.ExcludeFromSubmission"/> — the safe default, so a caller that knows nothing of
    /// the exclusion never writes a task Forecast's MCP cannot register. Empty: nothing is left out.
    /// </param>
    /// <param name="onlyTaskIds">
    /// Null: every planned cell is reported and registered. Otherwise only the cells of these tasks are: the others are context, judged
    /// with the registrations of their project and day (so they count in what Forecast already holds) but never reported nor written.
    /// </param>
    public async Task<ForecastFillReport> FillWeekAsync(
        TimesheetDraft draft, ForecastSubmitMode mode, IReadOnlyCollection<DateOnly>? days = null,
        IReadOnlyCollection<int>? ignoredTaskIds = null, CancellationToken cancellationToken = default,
        IReadOnlyCollection<int>? onlyTaskIds = null)
    {
        var report = new ForecastFillReport { WeekStart = draft.WeekStart, Mode = mode };
        bool Reported(int taskId) => onlyTaskIds is null || onlyTaskIds.Contains(taskId);

        var (plan, taskById) = await PlanAsync(draft, report.Warnings, cancellationToken);
        var ignored = ignoredTaskIds is null
            ? SubmissionTaskScope.AutoExcluded(taskById.Values)
            : ignoredTaskIds.ToHashSet();
        var automatic = SubmissionTaskScope.AutoExcluded(taskById.Values);
        report.IgnoredCells.AddRange(SubmissionTaskScope.IgnoredCells(draft, taskById, ignored, automatic, days));
        if (ignored.Count > 0)
            plan = new ForecastFillPlan(
                plan.Cells.Where(c => !ignored.Contains(c.Task.Id)).ToList(),
                plan.Rejected.Where(r => !ignored.Contains(r.ForecastTaskId)).ToList());
        if (days is { Count: > 0 } dayScope)
            plan = new ForecastFillPlan(
                plan.Cells.Where(c => dayScope.Contains(c.Date)).ToList(),
                plan.Rejected.Where(r => dayScope.Contains(r.Date)).ToList());

        foreach (var rejected in plan.Rejected.Where(r => Reported(r.ForecastTaskId)))
        {
            // The cell would vanish from the plan, and the week would be taken for complete: it is an error cell instead.
            report.Cells.Add(new ForecastCellResult(
                rejected.ForecastTaskId, $"Tâche #{rejected.ForecastTaskId} inconnue", rejected.Date, rejected.Hours, null, ForecastCellStatus.Error,
                $"La tâche locale #{rejected.ForecastTaskId} est inconnue (supprimée ?) : {rejected.Hours:0.##} h du {rejected.Date:yyyy-MM-dd} ne peuvent pas être saisies. "
                + "Remplacez la tâche ou classez ces heures autrement.")
            {
                Issue = ForecastCellIssue.TaskUnknownLocally,
                EntryState = ForecastEntryState.NotEntered,
            });
        }

        var reportable = plan.Cells.Where(c => Reported(c.Task.Id)).ToList();
        if (reportable.Count == 0)
        {
            if (report.Cells.Count == 0)
                report.Warnings.Add(report.IgnoredCells.Count > 0
                    ? "Toutes les heures à saisir sont sur des tâches ignorées pour cette soumission : rien n'est envoyé à Forecast."
                    : "Aucune heure à saisir dans ce brouillon.");
            return report;
        }

        var weekEnd = draft.WeekStart.AddDays(6);
        ForecastMcpSession session;
        try
        {
            session = await api.OpenAsync(cancellationToken);
        }
        catch (Exception ex) when (ForecastErrors.IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Connexion à Forecast impossible.");
            FailAll(report, reportable, ex.Message);
            return report;
        }

        await using var scope = session;

        IReadOnlyList<ForecastRemoteTask> remoteTasks;
        ForecastEntryRead existing;
        try
        {
            remoteTasks = await session.ListTasksAsync(cancellationToken);
            existing = await session.ReadTimeEntriesAsync(draft.WeekStart, weekEnd, cancellationToken);
            if (mode == ForecastSubmitMode.Fill)
                session.Require(ForecastToolRole.CreateTimeEntry);
        }
        catch (Exception ex) when (ForecastErrors.IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Lecture de Forecast impossible.");
            FailAll(report, reportable, ex.Message);
            return report;
        }

        if (!existing.IsComplete)
        {
            // Without every registration of the week, "nothing there" could be a lie and the fill would duplicate hours.
            report.ReadTruncated = true;
            report.ReadDetail = existing.Detail;
            foreach (var cell in reportable)
                report.Cells.Add(Result(cell, ForecastCellStatus.Unknown, null,
                    $"Lecture incomplète des saisies de Forecast ({existing.Detail ?? "liste tronquée"}) : aucune comparaison, rien n'est saisi.")
                    with { Issue = ForecastCellIssue.ReadIncomplete, EntryState = ForecastEntryState.Unknown });
            return report;
        }

        if (remoteTasks.Count == 0)
            report.Warnings.Add("Forecast n'a renvoyé aucune tâche : impossible d'y retrouver celles du brouillon.");

        report.NonProjectEntries.AddRange(existing.Entries.Where(e => e.ProjectId is null));

        var resolved = new List<ForecastResolvedCell>();
        var looked = new Dictionary<string, (ForecastRemoteTask? Remote, string? Failure)>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in plan.Cells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (remote, ambiguous) = Resolve(cell.Task, remoteTasks);
            string? failure = null;
            if (remote is null)
                (remote, failure) = await LookUpByCodeAsync(session, cell.Task, looked, cancellationToken);

            if (remote is not null)
            {
                resolved.Add(new ForecastResolvedCell(cell, remote));
            }
            else if (!Reported(cell.Task.Id))
            {
                // Context cell the server does not know: nothing to judge it by, and it is not this call's to report.
            }
            else if (failure is not null)
            {
                report.Cells.Add(Result(cell, ForecastCellStatus.Error, null, failure) with { EntryState = ForecastEntryState.NotEntered });
            }
            else
            {
                var detail = ambiguous
                    ? $"Plusieurs tâches de Forecast portent le nom de « {cell.Task.DisplayName} » : impossible de choisir. Reliez-la à la bonne (bouton « Rechercher et relier »)."
                    : $"« {cell.Task.DisplayName} » est introuvable dans Forecast (code {cell.Task.ExternalId}) : "
                      + "cherchez la tâche dans Forecast et reliez-la (bouton « Rechercher et relier »).";
                report.Cells.Add(Result(cell, ForecastCellStatus.RowNotFound, null, detail)
                    with { Issue = ambiguous ? ForecastCellIssue.TaskAmbiguous : ForecastCellIssue.None, EntryState = ForecastEntryState.NotEntered });
            }
        }

        var evaluations = ForecastCellEvaluator.Evaluate(resolved, existing.Entries)
            .Where(e => Reported(e.Resolved.Cell.Task.Id)).ToList();
        var preflight = await PreflightAsync(session, evaluations, report, cancellationToken);

        var written = new List<WrittenCell>();
        var interrupted = false;
        foreach (var evaluation in evaluations)
        {
            var findings = FindingsOf(evaluation, preflight);

            if (evaluation.Action == ForecastCellAction.AlreadyDone)
            {
                report.Cells.Add(Build(evaluation, ForecastCellStatus.Ok, evaluation.Current, evaluation.Message, ForecastEntryState.Entered, findings));
                continue;
            }

            if (evaluation.Action == ForecastCellAction.Conflict)
            {
                report.Cells.Add(Build(evaluation, ForecastCellStatus.Mismatch, evaluation.Current, evaluation.Message, ForecastEntryState.NotEntered, findings));
                continue;
            }

            if (evaluation.Locked)
            {
                report.Cells.Add(Build(evaluation, ForecastCellStatus.Locked, evaluation.Current, evaluation.LockMessage, ForecastEntryState.NotEntered, findings));
                continue;
            }

            if (mode == ForecastSubmitMode.DryRun)
            {
                report.Cells.Add(Build(evaluation, ForecastCellStatus.Planned, evaluation.Current, evaluation.Message, ForecastEntryState.NotApplicable, findings));
                continue;
            }

            if (interrupted)
            {
                report.Cells.Add(Build(evaluation, ForecastCellStatus.Error, null,
                    "Non tentée : la saisie a été interrompue avant cette cellule. Relancez la simulation puis la saisie pour la reprendre.",
                    ForecastEntryState.NotEntered, findings, ForecastCellIssue.NotAttempted));
                continue;
            }

            var cell = evaluation.Resolved.Cell;
            try
            {
                var created = await session.CreateTimeEntryAsync(evaluation.Resolved.Remote.Id, cell.Date, cell.Hours, cell.Notes, cancellationToken);
                written.Add(new WrittenCell(evaluation, created.Warnings, null));
            }
            catch (ForecastWriteException ex) when (ex.Outcome == ForecastWriteOutcome.Unknown)
            {
                logger.LogWarning(ex, "Résultat inconnu de la saisie de {Task} le {Date}", cell.Task.DisplayName, cell.Date);
                written.Add(new WrittenCell(evaluation, [], ex.Message));
                if (ex.IsTransportFailure)
                {
                    // The request itself failed (timeout, network): the next ones would most likely fail the same way.
                    interrupted = true;
                    report.Interrupted = true;
                    report.InterruptionMessage = $"{ex.Message} Les cellules déjà saisies sont conservées ; relancez la simulation puis la saisie pour reprendre.";
                }
            }
            catch (ForecastToolException ex)
            {
                logger.LogWarning(ex, "Échec de la saisie de {Task} le {Date}", cell.Task.DisplayName, cell.Date);
                var refused = ex is ForecastWriteException { Outcome: ForecastWriteOutcome.Refused };
                report.Cells.Add(Build(evaluation, ForecastCellStatus.Error, null, ex.Message, ForecastEntryState.NotEntered, findings,
                    refused ? ForecastCellIssue.WriteRefused : ForecastCellIssue.WriteFailed));
            }
            catch (Exception ex) when (ForecastErrors.IsRecoverable(ex, cancellationToken) || ex is OperationCanceledException)
            {
                // A timeout, the network or a cancellation in the middle of a registration: whether it was applied is unknown,
                // the read-back below says. The following cells are not tried.
                logger.LogWarning(ex, "Saisie interrompue sur {Task} le {Date}", cell.Task.DisplayName, cell.Date);
                interrupted = true;
                report.Interrupted = true;
                report.InterruptionMessage = $"{ex.Message} Les cellules déjà saisies sont conservées ; relancez la simulation puis la saisie pour reprendre.";
                written.Add(new WrittenCell(evaluation, [], ex.Message));
            }
        }

        if (written.Count > 0)
        {
            foreach (var w in written)
            {
                foreach (var warning in w.Warnings)
                    report.Warnings.Add($"Avertissement de Forecast ({w.Evaluation.Resolved.Cell.Task.DisplayName}, {w.Evaluation.Resolved.Cell.Date:yyyy-MM-dd}) : {warning}");
            }

            // Even after a cancellation, what was done is read back: a report without it would hide registrations already made.
            var verifyToken = cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken;
            await VerifyAsync(session, draft.WeekStart, weekEnd, written, preflight, report, verifyToken);
        }

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
    /// The writes the server offers: creating a registration, changing its hours, removing it. A server that cannot be reached
    /// gives <see cref="ForecastCapabilities.Unknown"/> with the reason; an authentication to renew propagates.
    /// </summary>
    public async Task<ForecastCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var session = await api.OpenAsync(cancellationToken);
            return CapabilitiesOf(session);
        }
        catch (Exception ex) when (ForecastErrors.IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Capacités de Forecast illisibles.");
            return ForecastCapabilities.Unknown with { UnavailableReason = $"Capacités de Forecast inconnues : {ex.Message}" };
        }
    }

    internal static ForecastCapabilities CapabilitiesOf(ForecastMcpSession session)
    {
        var reasons = new List<string>();
        ForecastCapability Check(ForecastToolRole role, string what)
        {
            if (session.Has(role))
                return ForecastCapability.Available;
            reasons.Add($"Le serveur Forecast ne propose pas {what} d'une saisie.");
            return ForecastCapability.Unavailable;
        }

        var create = Check(ForecastToolRole.CreateTimeEntry, "la création");
        var update = Check(ForecastToolRole.UpdateTimeEntry, "la modification");
        var delete = Check(ForecastToolRole.DeleteTimeEntry, "la suppression");
        return new ForecastCapabilities(create, update, delete, reasons.Count == 0 ? null : string.Join(" ", reasons));
    }

    /// <summary>The registrations of the signed-in person on one day, and whether the list is whole.</summary>
    public async Task<ForecastEntryRead> ReadDayAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        await using var session = await api.OpenAsync(cancellationToken);
        return await session.ReadDayAsync(date, cancellationToken);
    }

    /// <summary>
    /// Sets the registration <paramref name="entryId"/> of <paramref name="date"/> to <paramref name="newHours"/>. The day is read again
    /// first: nothing is written when the read is incomplete, when the registration no longer holds <paramref name="expectedForecastHours"/>
    /// (it changed since it was shown), when the day is approved or invoiced, or when the server has no tool for it. A registration that
    /// already holds <paramref name="newHours"/> is <see cref="ForecastCellStatus.Skipped"/>, so replaying the change is harmless.
    /// A change whose outcome is unknown (timeout, cancellation during the call) is settled by reading the day back.
    /// </summary>
    public Task<ForecastCellResult> UpdateCellAsync(
        DateOnly date, int forecastTaskId, string entryId, decimal expectedForecastHours, decimal newHours, CancellationToken cancellationToken = default) =>
        ChangeEntryAsync(delete: false, date, forecastTaskId, entryId, expectedForecastHours, newHours, cancellationToken);

    /// <summary>
    /// Removes the registration <paramref name="entryId"/> of <paramref name="date"/>, on the same conditions as <see cref="UpdateCellAsync"/>.
    /// A registration that is already gone is <see cref="ForecastCellStatus.Skipped"/>.
    /// </summary>
    public Task<ForecastCellResult> DeleteCellAsync(
        DateOnly date, int forecastTaskId, string entryId, decimal expectedForecastHours, CancellationToken cancellationToken = default) =>
        ChangeEntryAsync(delete: true, date, forecastTaskId, entryId, expectedForecastHours, 0m, cancellationToken);

    private async Task<ForecastCellResult> ChangeEntryAsync(
        bool delete, DateOnly date, int forecastTaskId, string entryId, decimal expected, decimal newHours, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var role = delete ? ForecastToolRole.DeleteTimeEntry : ForecastToolRole.UpdateTimeEntry;
        var (displayName, localCode) = await LabelAsync(forecastTaskId, cancellationToken);

        ForecastCellResult Cell(ForecastCellStatus status, decimal? actual, string message, ForecastEntryState state, ForecastCellIssue issue = ForecastCellIssue.None) =>
            new(forecastTaskId, displayName, date, newHours, actual, status, message) { Issue = issue, EntryState = state };

        ForecastMcpSession session;
        try
        {
            session = await api.OpenAsync(cancellationToken);
        }
        catch (Exception ex) when (ForecastErrors.IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Connexion à Forecast impossible.");
            return Cell(ForecastCellStatus.Error, null, ex.Message, ForecastEntryState.NotApplicable);
        }

        await using var scope = session;

        if (!session.Has(role))
            return Cell(ForecastCellStatus.Error, null,
                delete
                    ? "Le serveur Forecast ne propose pas la suppression d'une saisie : rien n'a été supprimé. Supprimez-la dans Forecast."
                    : "Le serveur Forecast ne propose pas la modification d'une saisie : rien n'a été modifié. Corrigez-la dans Forecast.",
                ForecastEntryState.NotApplicable, ForecastCellIssue.WriteRefused);

        ForecastEntryRead before;
        try
        {
            before = await session.ReadDayAsync(date, cancellationToken);
        }
        catch (Exception ex) when (ForecastErrors.IsRecoverable(ex, cancellationToken))
        {
            return Cell(ForecastCellStatus.Unknown, null, $"Lecture de Forecast impossible ({ex.Message}) : rien n'est modifié.",
                ForecastEntryState.Unknown, ForecastCellIssue.ReadIncomplete);
        }

        if (!before.IsComplete)
            return Cell(ForecastCellStatus.Unknown, null,
                $"Lecture incomplète des saisies du {date:yyyy-MM-dd} ({before.Detail ?? "liste tronquée"}) : rien n'est modifié.",
                ForecastEntryState.Unknown, ForecastCellIssue.ReadIncomplete);

        var entry = Find(before.Entries, entryId);
        if (entry is null)
            return delete
                ? Cell(ForecastCellStatus.Skipped, 0m, "La saisie n'est plus dans Forecast : rien à supprimer.", ForecastEntryState.NotEntered)
                : Cell(ForecastCellStatus.Mismatch, null, $"La saisie {entryId} est introuvable dans Forecast le {date:yyyy-MM-dd} : rien n'est modifié.",
                    ForecastEntryState.NotApplicable);

        if (entry.TaskCode is { } remoteCode && localCode is not null && !string.Equals(remoteCode, localCode, StringComparison.OrdinalIgnoreCase))
            return Cell(ForecastCellStatus.Mismatch, entry.Hours,
                $"La saisie {entryId} est sur la tâche {remoteCode}, pas sur {localCode} : rien n'est modifié.", ForecastEntryState.NotApplicable);

        if (before.Entries.Any(e => e.IsLocked))
            return Cell(ForecastCellStatus.Locked, entry.Hours,
                $"Le {date:yyyy-MM-dd} contient des saisies approuvées ou facturées dans Forecast : rien n'est modifié.", ForecastEntryState.NotApplicable);

        if (!delete && Math.Abs(entry.Hours - newHours) <= ForecastFillPlanner.Tolerance)
            return Cell(ForecastCellStatus.Skipped, entry.Hours, $"Forecast contient déjà {entry.Hours:0.##} h : rien à modifier.", ForecastEntryState.Entered);

        if (Math.Abs(entry.Hours - expected) > ForecastFillPlanner.Tolerance)
            return Cell(ForecastCellStatus.Mismatch, entry.Hours,
                $"Forecast contient {entry.Hours:0.##} h, pas les {expected:0.##} h attendues : la saisie a changé, rien n'est modifié.",
                ForecastEntryState.NotApplicable);

        string? unknownOutcome = null;
        try
        {
            if (delete)
                await session.DeleteTimeEntryAsync(entryId, cancellationToken);
            else
                await session.UpdateTimeEntryAsync(entryId, newHours, cancellationToken);
        }
        catch (ForecastWriteException ex) when (ex.Outcome == ForecastWriteOutcome.Unknown)
        {
            unknownOutcome = ex.Message;
        }
        catch (ForecastToolException ex)
        {
            var refused = ex is ForecastWriteException { Outcome: ForecastWriteOutcome.Refused };
            return Cell(ForecastCellStatus.Error, entry.Hours, ex.Message, ForecastEntryState.NotApplicable,
                refused ? ForecastCellIssue.WriteRefused : ForecastCellIssue.WriteFailed);
        }
        catch (Exception ex) when (ex is OperationCanceledException || ForecastErrors.IsRecoverable(ex, cancellationToken))
        {
            // The request left: it may have been applied. Reading back settles it, even when the caller cancelled.
            unknownOutcome = ex.Message;
        }

        var verifyToken = cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken;
        ForecastEntryRead after;
        try
        {
            after = await session.ReadDayAsync(date, verifyToken);
        }
        catch (Exception ex) when (ForecastErrors.IsRecoverable(ex, verifyToken))
        {
            return Cell(ForecastCellStatus.Unknown, null, $"Relecture de Forecast impossible ({ex.Message}) : vérifiez la saisie dans Forecast.",
                ForecastEntryState.Unknown, ForecastCellIssue.OutcomeUnknown);
        }

        if (!after.IsComplete)
            return Cell(ForecastCellStatus.Unknown, null,
                $"Relecture incomplète des saisies du {date:yyyy-MM-dd} ({after.Detail ?? "liste tronquée"}) : vérifiez la saisie dans Forecast.",
                ForecastEntryState.Unknown, ForecastCellIssue.OutcomeUnknown);

        var now = Find(after.Entries, entryId);
        if (delete ? now is null : now is not null && Math.Abs(now.Hours - newHours) <= ForecastFillPlanner.Tolerance)
            return delete
                ? Cell(ForecastCellStatus.Deleted, 0m, "Saisie supprimée.", ForecastEntryState.NotEntered)
                : Cell(ForecastCellStatus.Updated, now!.Hours, $"Saisie modifiée : {now.Hours:0.##} h.", ForecastEntryState.Entered);

        return unknownOutcome is not null
            ? Cell(ForecastCellStatus.Unknown, now?.Hours, $"{unknownOutcome} La relecture ne montre pas le changement : vérifiez la saisie dans Forecast.",
                ForecastEntryState.Unknown, ForecastCellIssue.OutcomeUnknown)
            : Cell(ForecastCellStatus.Mismatch, now?.Hours,
                delete
                    ? $"La saisie {entryId} est toujours dans Forecast après la suppression."
                    : $"Forecast contient {(now is null ? "0" : now.Hours.ToString("0.##"))} h après la modification, pas {newHours:0.##} h.",
                ForecastEntryState.Unknown);
    }

    private static ForecastRemoteEntry? Find(IEnumerable<ForecastRemoteEntry> entries, string entryId) =>
        entries.FirstOrDefault(e => string.Equals(e.EntryId, entryId, StringComparison.OrdinalIgnoreCase));

    private async Task<(string DisplayName, string? Code)> LabelAsync(int forecastTaskId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var task = await db.ForecastTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == forecastTaskId, cancellationToken);
        return task is null
            ? ($"Tâche #{forecastTaskId}", null)
            : (task.DisplayName, ForecastCodes.TaskCode.Match(task.ExternalId) is { Success: true } code ? code.Value : null);
    }

    private sealed record WrittenCell(ForecastCellEvaluation Evaluation, IReadOnlyList<string> Warnings, string? UnknownOutcome);

    /// <summary>
    /// Time can be logged against any task, not only the ones assigned to the person (the only ones listed): a task
    /// known by its T-number but missing from that list is asked for by number. A search that fails is a failure, not "not found".
    /// </summary>
    private async Task<(ForecastRemoteTask? Remote, string? Failure)> LookUpByCodeAsync(
        ForecastMcpSession session, ForecastTask task, Dictionary<string, (ForecastRemoteTask? Remote, string? Failure)> looked, CancellationToken cancellationToken)
    {
        if (ForecastCodes.TaskCode.Match(task.ExternalId) is not { Success: true } code)
            return (null, null);
        if (looked.TryGetValue(code.Value, out var known))
            return known;
        if (!session.HasTool("find_task"))
            return (null, null); // No way to ask: the task stays "not found", as when the server does not know it.

        (ForecastRemoteTask?, string?) outcome;
        try
        {
            var found = (await session.FindTasksAsync(code.Value, cancellationToken))
                .FirstOrDefault(r => string.Equals(r.Code, code.Value, StringComparison.OrdinalIgnoreCase));
            outcome = (found, null);
        }
        catch (Exception ex) when (ForecastErrors.IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Recherche de la tâche {Code} impossible.", code.Value);
            outcome = (null, $"Recherche de la tâche {code.Value} dans Forecast impossible : {ex.Message}");
        }

        looked[code.Value] = outcome;
        return outcome;
    }

    internal static ForecastRemoteTask? FindRemote(ForecastTask task, IReadOnlyList<ForecastRemoteTask> remote) => Resolve(task, remote).Remote;

    /// <summary>The task in Forecast by code, else by name and project; <c>Ambiguous</c> when the name alone fits several.</summary>
    internal static (ForecastRemoteTask? Remote, bool Ambiguous) Resolve(ForecastTask task, IReadOnlyList<ForecastRemoteTask> remote)
    {
        if (ForecastCodes.TaskCode.Match(task.ExternalId) is { Success: true } code)
        {
            var byCode = remote.FirstOrDefault(r => string.Equals(r.Code, code.Value, StringComparison.OrdinalIgnoreCase));
            if (byCode is not null)
                return (byCode, false);
        }

        var title = Key(task.TaskName);
        var project = Key(task.ProjectName);
        var byNames = remote
            .Where(r => Key(r.Title) == title)
            .Where(r => Key(r.ProjectName) == project || Key(r.ClientName) == project)
            .Take(2)
            .ToList();
        return byNames.Count == 1 ? (byNames[0], false) : (null, byNames.Count > 1);
    }

    private async Task VerifyAsync(
        ForecastMcpSession session, DateOnly from, DateOnly to, IReadOnlyList<WrittenCell> written,
        IReadOnlyDictionary<ForecastResolvedCell, List<ForecastCellFinding>> preflight, ForecastFillReport report, CancellationToken cancellationToken)
    {
        ForecastEntryRead after;
        try
        {
            after = await session.ReadTimeEntriesAsync(from, to, cancellationToken);
        }
        catch (Exception ex) when (ForecastErrors.IsRecoverable(ex, cancellationToken))
        {
            report.Warnings.Add($"Relecture impossible : {ex.Message}");
            foreach (var w in written)
                report.Cells.Add(Build(w.Evaluation, ForecastCellStatus.Unknown, null,
                    "Saisie tentée, mais Forecast n'a pas pu être relu : on ne sait pas si ces heures y sont. Relancez la simulation avant toute nouvelle saisie.",
                    ForecastEntryState.Unknown, FindingsOf(w, preflight), ForecastCellIssue.OutcomeUnknown));
            return;
        }

        if (!after.IsComplete)
        {
            report.ReadTruncated = true;
            report.ReadDetail = after.Detail;
            foreach (var w in written)
                report.Cells.Add(Build(w.Evaluation, ForecastCellStatus.Unknown, null,
                    $"Relecture incomplète de Forecast ({after.Detail ?? "liste tronquée"}) : on ne sait pas si ces heures y sont.",
                    ForecastEntryState.Unknown, FindingsOf(w, preflight), ForecastCellIssue.ReadIncomplete));
            return;
        }

        // The registrations of one project and day cannot be told apart (see ForecastCellEvaluator): what was added is checked for the group.
        foreach (var group in written.GroupBy(w => w.Evaluation.Resolved.GroupKey))
        {
            var members = group.ToList();
            var first = members[0].Evaluation;
            var now = after.Entries.Where(e => e.Date == first.Resolved.Cell.Date && members.Any(m => ForecastCellEvaluator.Matches(e, m.Evaluation.Resolved.Remote))).ToList();
            var added = now.Sum(e => e.Hours) - first.Current;
            var (status, actual, message) = ForecastFillPlanner.Compare(members.Sum(m => m.Evaluation.Resolved.Cell.Hours), added);

            foreach (var w in members)
            {
                var cell = w.Evaluation.Resolved.Cell;
                var findings = FindingsOf(w, preflight);
                var withNow = w.Evaluation with { Existing = now };
                if (status == ForecastCellStatus.Ok)
                {
                    var note = w.UnknownOutcome is null ? message : "Résultat incertain de la saisie, confirmé par la relecture de Forecast.";
                    report.Cells.Add(Build(withNow, status, cell.Hours, note, ForecastEntryState.Entered, findings));
                }
                else if (w.UnknownOutcome is not null)
                {
                    report.Cells.Add(Build(withNow, ForecastCellStatus.Unknown, actual,
                        (added <= 0
                            ? "Aucune trace de cette saisie à la relecture, mais Forecast n'a pas confirmé l'échec"
                            : $"Forecast contient {added:0.##} h de plus, pas les {cell.Hours:0.##} h attendues")
                        + " : relancez la simulation avant de réessayer (risque de doublon). " + w.UnknownOutcome,
                        ForecastEntryState.Unknown, findings, ForecastCellIssue.OutcomeUnknown));
                }
                else
                {
                    report.Cells.Add(Build(withNow, status, actual, message, added <= 0 ? ForecastEntryState.NotEntered : ForecastEntryState.Unknown, findings));
                }
            }
        }
    }

    private async Task<(ForecastFillPlan Plan, Dictionary<int, ForecastTask> Tasks)> PlanAsync(
        TimesheetDraft draft, ICollection<string> warnings, CancellationToken cancellationToken)
    {
        var ids = draft.Entries.Select(e => e.ForecastTaskId).Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var tasks = await db.ForecastTasks.AsNoTracking().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, cancellationToken);
        return (ForecastFillPlanner.PlanDetailed(draft, tasks, warnings), tasks);
    }

    /// <summary>
    /// Read-only checks of what the fill is about to register: task finished or blocked, project closed or paused, estimate exceeded.
    /// What cannot be checked (tool missing, error, estimate in points) is listed in <see cref="ForecastFillReport.PreflightLimits"/>.
    /// Nothing here stops a fill: whether Forecast refuses such a registration has not been established.
    /// </summary>
    private async Task<IReadOnlyDictionary<ForecastResolvedCell, List<ForecastCellFinding>>> PreflightAsync(
        ForecastMcpSession session, IReadOnlyList<ForecastCellEvaluation> evaluations, ForecastFillReport report, CancellationToken cancellationToken)
    {
        var findings = new Dictionary<ForecastResolvedCell, List<ForecastCellFinding>>();
        var targets = evaluations.Where(e => e.Action == ForecastCellAction.Type).Select(e => e.Resolved).ToList();
        if (targets.Count == 0 || !session.PreflightEnabled)
            return findings;

        void Add(ForecastResolvedCell cell, ForecastFindingKind kind, string message)
        {
            if (!findings.TryGetValue(cell, out var list))
                findings[cell] = list = [];
            list.Add(new ForecastCellFinding(kind, message));
        }

        var limits = report.PreflightLimits;
        var canTasks = session.HasTool(ForecastMcpSession.ListProjectTasksTool);
        var canStage = session.HasTool(ForecastMcpSession.SearchProjectsTool);
        if (!canTasks)
            limits.Add("Outil « list_project_tasks » absent du serveur Forecast : tâche terminée ou bloquée et estimation non vérifiables.");
        if (!canStage)
            limits.Add("Outil « search_projects » absent du serveur Forecast : étape du projet (terminé, en pause) non vérifiable.");

        foreach (var cell in targets.Where(t => t.Remote.ProjectId is null))
            limits.Add($"Projet de « {cell.Cell.Task.DisplayName} » inconnu : état non vérifiable.");

        var pointsReported = false;
        foreach (var project in targets.Where(t => t.Remote.ProjectId is not null).GroupBy(t => t.Remote.ProjectId!))
        {
            var sample = project.First().Remote;
            var label = sample.ProjectName ?? $"#{project.Key}";

            IReadOnlyList<ForecastRemoteTaskState>? states = null;
            if (canTasks)
            {
                try
                {
                    states = await session.ListProjectTaskStatesAsync(project.Key, cancellationToken);
                }
                catch (Exception ex) when (ForecastErrors.IsRecoverable(ex, cancellationToken))
                {
                    limits.Add($"Projet {label} : état des tâches non vérifiable ({ex.Message}).");
                }
            }

            string? stage = null;
            if (canStage)
            {
                try
                {
                    var found = await session.SearchProjectsAsync(sample.ProjectCode ?? "P" + project.Key, cancellationToken);
                    stage = found.FirstOrDefault(p => p.Id == project.Key)?.Status;
                    if (stage is null)
                        limits.Add($"Projet {label} : étape non retrouvée dans Forecast, non vérifiable.");
                }
                catch (Exception ex) when (ForecastErrors.IsRecoverable(ex, cancellationToken))
                {
                    limits.Add($"Projet {label} : étape non vérifiable ({ex.Message}).");
                }
            }

            foreach (var cell in project)
            {
                if (stage is not null && StageLabel(stage) is { } closed)
                    Add(cell, ForecastFindingKind.ProjectClosed, $"Le projet {label} est {closed} dans Forecast.");

                if (states is null)
                    continue;
                var state = states.FirstOrDefault(s => string.Equals(s.Code, cell.Remote.Code, StringComparison.OrdinalIgnoreCase));
                if (state is null)
                {
                    limits.Add($"Tâche « {cell.Cell.Task.DisplayName} » absente de la liste du projet {label} : état non vérifiable.");
                    continue;
                }

                if (state.IsDone)
                    Add(cell, ForecastFindingKind.TaskClosed, $"La tâche est terminée dans Forecast ({state.StatusName ?? "statut terminé"}).");
                if (state.IsBlocked)
                    Add(cell, ForecastFindingKind.TaskBlocked, "La tâche est marquée comme bloquée dans Forecast.");

                if (state.Estimate is > 0 and var estimate && state.ActualHours is { } actual)
                {
                    if (string.Equals(state.EstimationUnit, "points", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!pointsReported)
                            limits.Add("Estimation en points sur certaines tâches : dépassement non vérifiable (le temps est saisi en heures).");
                        pointsReported = true;
                    }
                    else
                    {
                        var toAdd = targets.Where(t => string.Equals(t.Remote.Code, cell.Remote.Code, StringComparison.OrdinalIgnoreCase)).Sum(t => t.Cell.Hours);
                        if (actual + toAdd > estimate + ForecastFillPlanner.Tolerance)
                            Add(cell, ForecastFindingKind.EstimateExceeded,
                                $"Dépassement de l'estimation : {actual:0.##} h déjà saisies + {toAdd:0.##} h cette semaine pour {estimate:0.##} h estimées.");
                    }
                }
            }
        }

        return findings;
    }

    private static string? StageLabel(string stage) => stage.Trim().ToUpperInvariant() switch
    {
        "DONE" or "CLOSED" or "COMPLETED" => "terminé",
        "HALTED" or "ON_HOLD" or "PAUSED" => "en pause",
        _ => null,
    };

    private static IReadOnlyList<ForecastCellFinding> FindingsOf(
        ForecastCellEvaluation evaluation, IReadOnlyDictionary<ForecastResolvedCell, List<ForecastCellFinding>> preflight, IReadOnlyList<string>? planWarnings = null)
    {
        var findings = new List<ForecastCellFinding>();
        if (!evaluation.Resolved.Cell.Task.IsActive)
            findings.Add(new ForecastCellFinding(ForecastFindingKind.TaskInactive, "La tâche est désactivée dans le catalogue local : ses heures sont quand même proposées."));
        if (preflight.TryGetValue(evaluation.Resolved, out var checks))
            findings.AddRange(checks);
        if (evaluation.PendingApproval)
            findings.Add(new ForecastCellFinding(ForecastFindingKind.PendingApproval, "Des saisies de ce jour sont déjà soumises pour approbation dans Forecast."));
        foreach (var warning in planWarnings ?? [])
            findings.Add(new ForecastCellFinding(ForecastFindingKind.PlanWarning, warning));
        return findings;
    }

    private static IReadOnlyList<ForecastCellFinding> FindingsOf(
        WrittenCell written, IReadOnlyDictionary<ForecastResolvedCell, List<ForecastCellFinding>> preflight) =>
        FindingsOf(written.Evaluation, preflight, written.Warnings);

    private static void FailAll(ForecastFillReport report, IReadOnlyList<ForecastPlannedCell> cells, string message)
    {
        report.GlobalError = message;
        foreach (var cell in cells)
            report.Cells.Add(Result(cell, ForecastCellStatus.Error, null, message) with { EntryState = ForecastEntryState.NotEntered });
    }

    private static ForecastCellResult Build(
        ForecastCellEvaluation evaluation, ForecastCellStatus status, decimal? actual, string? message, ForecastEntryState state,
        IReadOnlyList<ForecastCellFinding> findings, ForecastCellIssue issue = ForecastCellIssue.None)
    {
        var cell = evaluation.Resolved.Cell;
        return Result(cell, status, actual, message) with
        {
            Issue = issue,
            EntryState = state,
            Comparison = evaluation.Comparison,
            GroupKey = evaluation.Resolved.GroupKey,
            GroupExpected = evaluation.GroupExpected,
            ExistingEntries = evaluation.Existing,
            Findings = findings,
        };
    }

    private static string Key(string? text) =>
        new((text ?? "").Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static ForecastCellResult Result(ForecastPlannedCell cell, ForecastCellStatus status, decimal? actual, string? message) =>
        new(cell.Task.Id, cell.Task.DisplayName, cell.Date, cell.Hours, actual, status, message);
}
