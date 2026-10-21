using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Forecast;
using TimeSheetHelper.Forecast.Submission;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.App.Services;

/// <summary>What one send of a day did. <see cref="Written"/> counts the creations, changes and removals actually made in Forecast.</summary>
public sealed record DaySendResult(
    DateOnly Date,
    DayStatus Status,
    int Written,
    int Skipped,
    IReadOnlyList<ForecastCellResult> Cells,
    string Message);

public sealed record SendReadyResult(IReadOnlyList<DaySendResult> Sent, IReadOnlyList<(DateOnly Date, string Reason)> SkippedDays);

/// <summary>Plans and sends one day at a time to Forecast (contracts §1.5).</summary>
public interface IDaySubmissionService
{
    Task<ForecastCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default);

    /// <summary>Reads the draft, Forecast (whole read) and what was sent; writes nothing.</summary>
    Task<DayPlan> PlanAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>
    /// Same plan, for display only: uses the last read of Forecast kept by the application (shared by every window) when it is recent,
    /// and reads Forecast otherwise. A send never relies on it.
    /// </summary>
    Task<DayPlan> PlanCachedAsync(DateOnly date, CancellationToken cancellationToken = default);

    /// <summary>When Forecast was last read successfully by the application, or null when it never was.</summary>
    DateTime? LastSyncedUtc { get; }

    /// <summary>Raised after <see cref="SyncWeekAsync"/> and after a send: the windows can plan again from the cache.</summary>
    event Action? Synced;

    /// <summary>Reads the working days of the week (and the cached weekend days) in Forecast and keeps them. False when no day could be read.</summary>
    Task<bool> SyncWeekAsync(DateOnly weekStart, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes the day's lock, plans again (Forecast is read before any write), applies the operations and the conflict resolutions, and
    /// keeps a journal. Nothing new or nothing to send: no write. Lock not obtained: <see cref="DayStatus.InProgress"/>, no write.
    /// </summary>
    Task<DaySendResult> SendAsync(DateOnly date, IReadOnlyDictionary<int, ConflictResolution> resolutions, CancellationToken cancellationToken = default);

    /// <summary>Sends only the days whose plan is ready; the others are reported in <see cref="SendReadyResult.SkippedDays"/> (G9).</summary>
    Task<SendReadyResult> SendReadyAsync(DateOnly weekStart, CancellationToken cancellationToken = default);

    /// <summary>Stops the send in progress for this day: the cells not reached stay not attempted.</summary>
    Task CancelAsync(DateOnly date);

    Task<IReadOnlyList<SubmissionJournalEntry>> GetJournalAsync(DateOnly date, CancellationToken cancellationToken = default);
}

/// <summary>
/// The day submission engine. Every write goes through <see cref="ForecastTimesheetWriter"/>, always after a whole read of the day:
/// a cell already in Forecast is never created again, a read that is not whole writes nothing.
/// </summary>
public sealed class DaySubmissionService(
    IDbContextFactory<TimeSheetDbContext> dbFactory,
    ForecastTimesheetWriter writer,
    DayLock dayLock,
    TimeProvider timeProvider,
    ILogger<DaySubmissionService> logger) : IDaySubmissionService
{
    private readonly ConcurrentDictionary<DateOnly, CancellationTokenSource> running = new();

    public static DateOnly WeekStartOf(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    /// <summary>A cached read older than this is read again.</summary>
    public static readonly TimeSpan MaxCacheAge = TimeSpan.FromMinutes(15);

    private const string UnknownCapabilitiesPrefix = "Capacités de Forecast inconnues";

    private readonly ConcurrentDictionary<DateOnly, (ForecastDayRead Read, DateTime AtUtc)> cachedReads = new();
    private readonly object cacheLock = new();
    private ForecastCapabilities? cachedCapabilities;
    private DateTime? lastSyncedUtc;

    public event Action? Synced;

    public DateTime? LastSyncedUtc
    {
        get
        {
            lock (cacheLock)
                return lastSyncedUtc;
        }
    }

    public Task<ForecastCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        FetchCapabilitiesAsync(cancellationToken);

    public async Task<DayPlan> PlanAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var capabilities = await FetchCapabilitiesAsync(cancellationToken);
        return await PlanAsync(date, capabilities, null, cancellationToken);
    }

    public async Task<DayPlan> PlanCachedAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        ForecastCapabilities? capabilities;
        lock (cacheLock)
            capabilities = cachedCapabilities;
        if (capabilities is not null && cachedReads.TryGetValue(date, out var hit)
            && timeProvider.GetUtcNow().UtcDateTime - hit.AtUtc <= MaxCacheAge)
            return await PlanAsync(date, capabilities, hit.Read, cancellationToken);
        return await PlanAsync(date, cancellationToken);
    }

    public async Task<bool> SyncWeekAsync(DateOnly weekStart, CancellationToken cancellationToken = default)
    {
        var monday = WeekStartOf(weekStart);
        await FetchCapabilitiesAsync(cancellationToken);
        var anyRead = false;
        for (var i = 0; i < 7; i++)
        {
            var date = monday.AddDays(i);
            if (i >= 5 && !cachedReads.ContainsKey(date))
                continue;
            anyRead |= (await ReadAsync(date, cancellationToken)).IsComplete;
        }
        Synced?.Invoke();
        return anyRead;
    }

    private async Task<ForecastCapabilities> FetchCapabilitiesAsync(CancellationToken cancellationToken)
    {
        var capabilities = await writer.GetCapabilitiesAsync(cancellationToken);
        if (capabilities.UnavailableReason?.StartsWith(UnknownCapabilitiesPrefix, StringComparison.Ordinal) != true)
            lock (cacheLock)
                cachedCapabilities = capabilities;
        return capabilities;
    }

    private async Task<DayPlan> PlanAsync(DateOnly date, ForecastCapabilities capabilities, ForecastDayRead? cached, CancellationToken cancellationToken)
    {
        var context = await LoadAsync(date, cancellationToken);
        var read = cached ?? await ReadAsync(date, cancellationToken);
        var previous = context.Previous;
        if (previous is { Status: DayStatus.InProgress, InProgressSinceUtc: { } since }
            && since <= timeProvider.GetUtcNow().UtcDateTime - DayLock.StaleAfter)
            previous = Copy(previous, DayStatus.Partial, previous.Cells);
        return DayPlanner.Plan(context.WeekStart, date, context.Entries, read, previous, context.Ignored, context.Tasks, capabilities);
    }

    public async Task<DaySendResult> SendAsync(
        DateOnly date, IReadOnlyDictionary<int, ConflictResolution> resolutions, CancellationToken cancellationToken = default)
    {
        var weekStart = WeekStartOf(date);
        if (await dayLock.TryAcquireAsync(weekStart, date, cancellationToken) is not DayLock.DayLease lease)
            return new DaySendResult(date, DayStatus.InProgress, 0, 0, [],
                $"Un envoi du {date:yyyy-MM-dd} est déjà en cours (autre onglet ou fenêtre) : rien n'est écrit.");

        await using (lease)
        {
            cachedReads.TryRemove(date, out _);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            running[date] = cts;
            try
            {
                return await SendLockedAsync(lease, weekStart, date, resolutions, cts.Token);
            }
            finally
            {
                running.TryRemove(KeyValuePair.Create(date, cts));
                Synced?.Invoke();
            }
        }
    }

    public Task CancelAsync(DateOnly date)
    {
        if (running.TryGetValue(date, out var cts))
        {
            try
            {
                cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The send ended meanwhile.
            }
        }
        return Task.CompletedTask;
    }

    public async Task<SendReadyResult> SendReadyAsync(DateOnly weekStart, CancellationToken cancellationToken = default)
    {
        var monday = WeekStartOf(weekStart);
        var capabilities = await FetchCapabilitiesAsync(cancellationToken);
        var sent = new List<DaySendResult>();
        var skipped = new List<(DateOnly Date, string Reason)>();
        for (var i = 0; i < 7; i++)
        {
            var date = monday.AddDays(i);
            try
            {
                var plan = await PlanAsync(date, capabilities, null, cancellationToken);
                if (plan.IsReady)
                    sent.Add(await SendAsync(date, new Dictionary<int, ConflictResolution>(), cancellationToken));
                else if (plan.Gate != DayGate.NothingToSend)
                    skipped.Add((date, ReasonOf(plan.Gate)));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // One day failing (read, database, busy) must not stop the days after it.
                logger.LogWarning(ex, "Envoi du {Date} impossible", date);
                skipped.Add((date, "L'envoi de ce jour a échoué : vérifiez son état puis relancez-le."));
            }
        }
        return new SendReadyResult(sent, skipped);
    }

    public async Task<IReadOnlyList<SubmissionJournalEntry>> GetJournalAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var weekStart = WeekStartOf(date);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var dayId = await db.DaySubmissions.Where(d => d.WeekStart == weekStart && d.Date == date).Select(d => (int?)d.Id).FirstOrDefaultAsync(cancellationToken);
        if (dayId is null)
            return [];
        return await db.SubmissionJournal.AsNoTracking().Where(j => j.DaySubmissionId == dayId)
            .OrderBy(j => j.TimestampUtc).ThenBy(j => j.Id).ToListAsync(cancellationToken);
    }

    private async Task<DaySendResult> SendLockedAsync(
        DayLock.DayLease lease, DateOnly weekStart, DateOnly date, IReadOnlyDictionary<int, ConflictResolution> resolutions, CancellationToken token)
    {
        var journal = new List<SubmissionJournalEntry>();
        var results = new List<ForecastCellResult>();
        var written = 0;
        var errors = 0;
        var resolved = 0;
        var interrupted = false;

        void Log(JournalKind kind, JournalOutcome outcome, string message, int? taskId = null, decimal? hours = null) =>
            journal.Add(new SubmissionJournalEntry
            {
                DaySubmissionId = lease.DaySubmissionId,
                TimestampUtc = timeProvider.GetUtcNow().UtcDateTime,
                Kind = kind,
                Outcome = outcome,
                ForecastTaskId = taskId,
                Hours = hours,
                Message = message.Length > SubmissionJournalEntry.MessageMaxLength ? message[..SubmissionJournalEntry.MessageMaxLength] : message,
            });

        DayContext context;
        DayPlan plan;
        Dictionary<int, (decimal Hours, string? EntryId)> sent;
        try
        {
            var capabilities = await FetchCapabilitiesAsync(token);
            context = await LoadAsync(date, token);
            var read = await ReadAsync(date, token);
            sent = (context.Previous?.Cells ?? []).GroupBy(c => c.ForecastTaskId).ToDictionary(g => g.Key, g => (g.First().SentHours, g.First().ForecastEntryId));
            plan = DayPlanner.Plan(weekStart, date, context.Entries, read,
                Copy(context.Previous, lease.PreviousStatus, context.Previous?.Cells ?? []), context.Ignored, context.Tasks, capabilities);
        }
        catch (Exception ex)
        {
            // Nothing was written: the day keeps its status.
            await RestoreAsync(lease, ex is OperationCanceledException
                ? $"Envoi du {date:yyyy-MM-dd} annulé avant toute écriture."
                : $"Envoi du {date:yyyy-MM-dd} arrêté avant toute écriture : Forecast ou la base locale n'a pas pu être lu.");
            throw;
        }

        var unresolved = plan.Cells.Where(c => c.State == CellState.Conflict
            && !(resolutions.TryGetValue(c.ForecastTaskId, out var r) && c.AllowedResolutions.Contains(r))).ToList();
        var proceed = plan.IsReady || (plan.Gate == DayGate.ResolveConflicts && plan.Cells.Any(c => c.State == CellState.Conflict) && unresolved.Count == 0);
        Log(JournalKind.Planned, JournalOutcome.Ok,
            $"Envoi du {date:yyyy-MM-dd} demandé : {plan.Cells.Count(c => c.Operation != CellOperation.None)} opération(s) prévue(s), "
            + $"{plan.Cells.Count(c => c.State == CellState.Conflict)} conflit(s).");

        if (!proceed)
        {
            var reason = plan.Gate == DayGate.ResolveConflicts && unresolved.Count > 0
                ? $"{unresolved.Count} conflit(s) sans résolution : rien n'est écrit."
                : ReasonOf(plan.Gate);
            Log(JournalKind.Skipped, JournalOutcome.Ok, reason);
            var settled = plan.Gate == DayGate.NothingNew && plan.Status == DayStatus.Sent;
            if (settled)
                Record(plan, sent);
            var skippedStatus = plan.ForecastReadComplete ? plan.Status : lease.PreviousStatus;
            await PersistAsync(lease, weekStart, date, skippedStatus, settled ? plan.CurrentFingerprint : null, settled, sent, journal);
            return new DaySendResult(date, skippedStatus, 0, plan.Cells.Count, [], reason);
        }

        if (plan.Gate == DayGate.Resume)
            Log(JournalKind.Resumed, JournalOutcome.Ok, $"Reprise de l'envoi du {date:yyyy-MM-dd} : Forecast relu, les cellules déjà présentes ne sont pas recréées.");

        var steps = new List<(CellPlan Cell, CellOperation Operation)>();
        steps.AddRange(plan.Cells.Where(c => c.Operation == CellOperation.Delete).Select(c => (c, CellOperation.Delete)));
        steps.AddRange(plan.Cells.Where(c => c.Operation == CellOperation.Update).Select(c => (c, CellOperation.Update)));
        foreach (var cell in plan.Cells.Where(c => c.State == CellState.Conflict))
        {
            var resolution = resolutions[cell.ForecastTaskId];
            steps.Add((cell, resolution switch
            {
                ConflictResolution.AcceptForecast => CellOperation.AcceptForecast,
                ConflictResolution.Complement => CellOperation.Complement,
                ConflictResolution.Remove => CellOperation.Delete,
                _ => CellOperation.None,
            }));
        }
        var creates = plan.Cells.Where(c => c.Operation == CellOperation.Create).ToList();
        var held = HeldHours(plan);
        var reached = new HashSet<int>();

        foreach (var (cell, operation) in steps)
        {
            if (token.IsCancellationRequested)
            {
                interrupted = true;
                break;
            }
            reached.Add(cell.ForecastTaskId);
            try
            {
                switch (operation)
                {
                    case CellOperation.None:
                        resolved++;
                        Log(JournalKind.Resolved, JournalOutcome.Ok, "Conflit : Forecast gardé tel quel, rien n'est écrit.", cell.ForecastTaskId, cell.ForecastHours);
                        break;

                    case CellOperation.AcceptForecast:
                        await AcceptForecastAsync(weekStart, date, cell.ForecastTaskId, cell.ForecastHours ?? 0m, token);
                        sent[cell.ForecastTaskId] = (cell.ForecastHours ?? 0m, cell.ForecastEntryId);
                        resolved++;
                        Log(JournalKind.Resolved, JournalOutcome.Ok,
                            $"Conflit : le brouillon prend les {cell.ForecastHours ?? 0m:0.##} h de Forecast, rien n'est écrit dans Forecast.",
                            cell.ForecastTaskId, cell.ForecastHours);
                        break;

                    case CellOperation.Delete:
                    {
                        var result = await writer.DeleteCellAsync(date, cell.ForecastTaskId, cell.ForecastEntryId!, cell.ForecastHours ?? 0m, token);
                        results.Add(result);
                        if (result.Status is ForecastCellStatus.Deleted or ForecastCellStatus.Skipped)
                        {
                            if (result.Status == ForecastCellStatus.Deleted)
                                written++;
                            sent.Remove(cell.ForecastTaskId);
                            held.Remove(cell.ForecastTaskId);
                            Log(JournalKind.Deleted, JournalOutcome.Ok,
                                result.Status == ForecastCellStatus.Deleted ? "Saisie retirée de Forecast." : "Saisie déjà absente de Forecast : rien à retirer.",
                                cell.ForecastTaskId, cell.ForecastHours);
                        }
                        else
                        {
                            errors++;
                            Log(JournalKind.Failed, OutcomeOf(result), $"Retrait non fait ({Label(result.Status)}).", cell.ForecastTaskId, cell.ForecastHours);
                        }
                        break;
                    }

                    case CellOperation.Update or CellOperation.Complement:
                    {
                        var result = await writer.UpdateCellAsync(date, cell.ForecastTaskId, cell.ForecastEntryId!, cell.ForecastHours ?? 0m, cell.DraftHours, token);
                        results.Add(result);
                        if (result.Status is ForecastCellStatus.Updated or ForecastCellStatus.Skipped)
                        {
                            if (result.Status == ForecastCellStatus.Updated)
                                written++;
                            sent[cell.ForecastTaskId] = (cell.DraftHours, cell.ForecastEntryId);
                            held[cell.ForecastTaskId] = cell.DraftHours;
                            Log(operation ==  CellOperation.Complement ? JournalKind.Resolved : JournalKind.Updated, JournalOutcome.Ok,
                                $"Saisie portée de {cell.ForecastHours ?? 0m:0.##} h à {cell.DraftHours:0.##} h.", cell.ForecastTaskId, cell.DraftHours);
                        }
                        else
                        {
                            errors++;
                            Log(JournalKind.Failed, OutcomeOf(result), $"Modification non faite ({Label(result.Status)}).", cell.ForecastTaskId, cell.DraftHours);
                        }
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                interrupted = true;
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Envoi du {Date} interrompu sur la tâche {TaskId}", date, cell.ForecastTaskId);
                errors++;
                interrupted = true;
                Log(JournalKind.Failed, JournalOutcome.Unknown, "Opération interrompue par une erreur : vérifiez la saisie dans Forecast.", cell.ForecastTaskId);
                break;
            }
        }

        if (creates.Count > 0 && !interrupted && !token.IsCancellationRequested)
        {
            foreach (var cell in creates)
                reached.Add(cell.ForecastTaskId);
            try
            {
                var report = await writer.FillWeekAsync(
                    CreationDraft(weekStart, date, held, creates, context.Entries), ForecastSubmitMode.Fill, [date], context.Ignored.ToList(), token,
                    creates.Select(c => c.ForecastTaskId).ToList());
                var created = creates.Select(c => c.ForecastTaskId).ToHashSet();
                foreach (var result in report.Cells.Where(c => c.Date == date && created.Contains(c.ForecastTaskId)))
                {
                    results.Add(result);
                    if (result.Issue == ForecastCellIssue.NotAttempted)
                        continue;
                    if (result.Status != ForecastCellStatus.Ok)
                    {
                        errors++;
                        Log(JournalKind.Failed, OutcomeOf(result), $"Création non faite ({Label(result.Status)}).", result.ForecastTaskId, result.Expected);
                    }
                }
                interrupted |= report.Interrupted;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                interrupted = true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Créations du {Date} interrompues", date);
                errors++;
                interrupted = true;
                Log(JournalKind.Failed, JournalOutcome.Unknown, $"Créations du {date:yyyy-MM-dd} interrompues par une erreur : relancez l'envoi pour reprendre.");
            }
        }
        interrupted |= token.IsCancellationRequested;

        // Read back, whatever happened: the status and what was sent come from what Forecast holds now.
        var after = await LoadAsync(date, CancellationToken.None);
        ForecastDayRead reread;
        try
        {
            reread = await ReadAsync(date, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Relecture de Forecast impossible pour le {Date}", date);
            reread = ForecastDayRead.Unreadable(date, "relecture impossible");
        }
        var basisStatus = reached.Count > 0 ? DayStatus.Sent : lease.PreviousStatus;
        var final = DayPlanner.Plan(weekStart, date, after.Entries, reread, Copy(context.Previous, basisStatus, Cells(sent)), after.Ignored, after.Tasks, plan.Capabilities);

        if (final.ForecastReadComplete)
        {
            foreach (var cell in creates)
            {
                var now = final.Cells.FirstOrDefault(c => c.ForecastTaskId == cell.ForecastTaskId);
                if (now?.ForecastHours is > 0 && results.Any(r => r.ForecastTaskId == cell.ForecastTaskId && r.Status == ForecastCellStatus.Ok))
                {
                    written++;
                    Log(JournalKind.Sent, JournalOutcome.Ok, $"Saisie créée : {now.ForecastHours:0.##} h.", cell.ForecastTaskId, now.ForecastHours);
                }
            }
            Record(final, sent);
        }
        else
        {
            foreach (var result in results.Where(r => r.Status == ForecastCellStatus.Ok && creates.Any(c => c.ForecastTaskId == r.ForecastTaskId)))
            {
                written++;
                sent[result.ForecastTaskId] = (result.Expected, null);
                Log(JournalKind.Sent, JournalOutcome.Ok, $"Saisie créée : {result.Expected:0.##} h (relecture incomplète).", result.ForecastTaskId, result.Expected);
            }
        }

        var notReached = plan.Cells.Where(c => (c.Operation != CellOperation.None || c.State == CellState.Conflict) && !reached.Contains(c.ForecastTaskId)).ToList();
        foreach (var cell in notReached)
        {
            results.Add(new ForecastCellResult(cell.ForecastTaskId, cell.TaskDisplayName, date, cell.DraftHours, cell.ForecastHours, ForecastCellStatus.Unknown,
                "Non tentée : l'envoi a été interrompu avant cette cellule.") { Issue = ForecastCellIssue.NotAttempted, EntryState = ForecastEntryState.NotEntered });
        }
        if (interrupted)
            Log(token.IsCancellationRequested ? JournalKind.Cancelled : JournalKind.Interrupted, JournalOutcome.Partial,
                $"Envoi du {date:yyyy-MM-dd} {(token.IsCancellationRequested ? "annulé" : "interrompu")} : {written} écriture(s) faite(s), "
                + $"{results.Count(r => r.Issue == ForecastCellIssue.NotAttempted)} cellule(s) non tentée(s).");

        var status = final.ForecastReadComplete
            ? final.Status
            : written > 0 ? DayStatus.Partial : errors > 0 ? DayStatus.Error : lease.PreviousStatus;
        if (final.ForecastReadComplete && (status is DayStatus.Modified or DayStatus.NotAttempted) && (errors > 0 || interrupted))
            status = written > 0 ? DayStatus.Partial : errors > 0 ? DayStatus.Error : status;

        await PersistAsync(lease, weekStart, date, status, status == DayStatus.Sent ? final.CurrentFingerprint : null, written > 0 || status == DayStatus.Sent, sent, journal);

        var message = $"{date:yyyy-MM-dd} : {written} écriture(s) dans Forecast, {errors} erreur(s)"
            + (interrupted ? ", envoi interrompu" : "") + $". Statut : {Label(status)}.";
        logger.LogInformation("{Message}", message);
        return new DaySendResult(date, status, written, Math.Max(0, plan.Cells.Count - reached.Count), results, message);
    }

    /// <summary>
    /// The draft given to the fill: the cells to create at the draft's hours, and before them the other tasks of the day at the hours Forecast
    /// holds once the deletions and updates are done, so the fill sees the hours of a shared project as theirs and only types what is missing.
    /// </summary>
    private static TimesheetDraft CreationDraft(DateOnly weekStart, DateOnly date, IReadOnlyDictionary<int, decimal> held, IReadOnlyList<CellPlan> creates, IReadOnlyList<TimesheetEntry> entries)
    {
        var draft = new TimesheetDraft { WeekStart = weekStart };
        foreach (var (taskId, hours) in held)
            draft.Entries.Add(new TimesheetEntry { Date = date, ForecastTaskId = taskId, Hours = hours });
        foreach (var cell in creates)
        {
            var notes = entries.Where(e => e.Date == date && e.ForecastTaskId == cell.ForecastTaskId && !string.IsNullOrWhiteSpace(e.Notes))
                .Select(e => e.Notes!.Trim()).Distinct().ToList();
            draft.Entries.Add(new TimesheetEntry
            {
                Date = date,
                ForecastTaskId = cell.ForecastTaskId,
                Hours = cell.DraftHours,
                Notes = notes.Count == 0 ? null : string.Join(" ; ", notes),
            });
        }
        return draft;
    }

    /// <summary>
    /// What Forecast holds for the cells other than the creations before the send: an ambiguous group (several tasks of one project sharing
    /// the same hours) counts once, or the fill would expect the hours several times.
    /// </summary>
    private static Dictionary<int, decimal> HeldHours(DayPlan plan)
    {
        var held = new Dictionary<int, decimal>();
        var groups = new HashSet<int>();
        foreach (var cell in plan.Cells.Where(c => c.Operation != CellOperation.Create && c.State != CellState.Ignored && c.ForecastHours is > 0))
        {
            if (cell.AmbiguousGroup is { } group && !groups.Add(group))
                continue;
            held[cell.ForecastTaskId] = cell.ForecastHours!.Value;
        }
        return held;
    }

    /// <summary>What is now known to be in Forecast as the draft wants it becomes what was sent; a removed cell is forgotten.</summary>
    private static void Record(DayPlan plan, Dictionary<int, (decimal Hours, string? EntryId)> sent)
    {
        foreach (var cell in plan.Cells.Where(c => c.State == CellState.Unchanged))
        {
            if (cell.DraftHours > 0 && cell.ForecastHours is { } f)
                sent[cell.ForecastTaskId] = (f, cell.ForecastEntryId ?? (sent.TryGetValue(cell.ForecastTaskId, out var known) ? known.EntryId : null));
            else if (cell.DraftHours == 0 && cell.ForecastHours is null)
                sent.Remove(cell.ForecastTaskId);
        }
    }

    /// <summary>The draft of the day takes the hours Forecast holds for the task (rounded to the quarter hour).</summary>
    private async Task AcceptForecastAsync(DateOnly weekStart, DateOnly date, int taskId, decimal hours, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var draft = await db.TimesheetDrafts.Include(d => d.Entries).OrderByDescending(d => d.Id).FirstOrDefaultAsync(d => d.WeekStart == weekStart, cancellationToken);
        if (draft is null)
        {
            draft = new TimesheetDraft { WeekStart = weekStart };
            db.TimesheetDrafts.Add(draft);
        }

        var rounded = ForecastHours.RoundToQuarter(hours);
        var existing = draft.Entries.Where(e => e.Date == date && e.ForecastTaskId == taskId).OrderBy(e => e.Id).ToList();
        if (existing.Count == 0)
        {
            draft.Entries.Add(new TimesheetEntry { Date = date, ForecastTaskId = taskId, Hours = rounded });
        }
        else
        {
            existing[0].Hours = rounded;
            db.TimesheetEntries.RemoveRange(existing.Skip(1));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task PersistAsync(
        DayLock.DayLease lease, DateOnly weekStart, DateOnly date, DayStatus status, string? fingerprint, bool stampSent,
        Dictionary<int, (decimal Hours, string? EntryId)> sent, IReadOnlyList<SubmissionJournalEntry> journal)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var day = await db.DaySubmissions.Include(d => d.Cells).FirstAsync(d => d.Id == lease.DaySubmissionId);
        day.Status = status;
        day.InProgressSinceUtc = null;
        if (fingerprint is not null)
            day.Fingerprint = fingerprint;
        if (stampSent)
            day.SentUtc = timeProvider.GetUtcNow().UtcDateTime;

        foreach (var cell in day.Cells.Where(c => !sent.ContainsKey(c.ForecastTaskId)).ToList())
            day.Cells.Remove(cell);
        foreach (var (taskId, value) in sent)
        {
            var cell = day.Cells.FirstOrDefault(c => c.ForecastTaskId == taskId);
            if (cell is null)
                day.Cells.Add(cell = new DaySubmissionCell { ForecastTaskId = taskId });
            cell.SentHours = ForecastHours.RoundToQuarter(value.Hours);
            cell.ForecastEntryId = value.EntryId;
        }

        db.SubmissionJournal.AddRange(journal);
        await db.SaveChangesAsync();
        logger.LogDebug("Jour {Date} de la semaine {WeekStart} : {Status}", date, weekStart, status);
    }

    private async Task RestoreAsync(DayLock.DayLease lease, string message)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            await db.DaySubmissions
                .Where(d => d.Id == lease.DaySubmissionId && d.Status == DayStatus.InProgress && d.InProgressSinceUtc == lease.SinceUtc)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(d => d.Status, lease.PreviousStatus)
                    .SetProperty(d => d.InProgressSinceUtc, (DateTime?)null));
            db.SubmissionJournal.Add(new SubmissionJournalEntry
            {
                DaySubmissionId = lease.DaySubmissionId,
                TimestampUtc = timeProvider.GetUtcNow().UtcDateTime,
                Kind = JournalKind.Skipped,
                Outcome = JournalOutcome.Failed,
                Message = message,
            });
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Statut du jour non restauré");
        }
    }

    private async Task<ForecastDayRead> ReadAsync(DateOnly date, CancellationToken cancellationToken)
    {
        try
        {
            var read = ForecastDayRead.From(date, await writer.ReadDayAsync(date, cancellationToken));
            if (read.IsComplete)
            {
                var now = timeProvider.GetUtcNow().UtcDateTime;
                cachedReads[date] = (read, now);
                lock (cacheLock)
                    lastSyncedUtc = now;
            }
            return read;
        }
        catch (Exception ex) when (ForecastErrors.IsRecoverable(ex, cancellationToken))
        {
            logger.LogWarning(ex, "Lecture de Forecast impossible pour le {Date}", date);
            return ForecastDayRead.Unreadable(date, ex.Message);
        }
    }

    private sealed record DayContext(
        DateOnly WeekStart, IReadOnlyList<TimesheetEntry> Entries, IReadOnlyDictionary<int, ForecastTask> Tasks, IReadOnlySet<int> Ignored, DaySubmission? Previous);

    private async Task<DayContext> LoadAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var weekStart = WeekStartOf(date);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var draft = await db.TimesheetDrafts.AsNoTracking().Include(d => d.Entries)
            .OrderByDescending(d => d.Id).FirstOrDefaultAsync(d => d.WeekStart == weekStart, cancellationToken);
        var tasks = await db.ForecastTasks.AsNoTracking().ToDictionaryAsync(t => t.Id, cancellationToken);
        var choices = (await db.WeekTaskChoices.AsNoTracking().Where(c => c.WeekStart == weekStart).OrderBy(c => c.Id).ToListAsync(cancellationToken))
            .GroupBy(c => c.ForecastTaskId).ToDictionary(g => g.Key, g => g.Last().Ignore);
        var previous = await db.DaySubmissions.AsNoTracking().Include(d => d.Cells)
            .FirstOrDefaultAsync(d => d.WeekStart == weekStart && d.Date == date, cancellationToken);
        var entries = draft?.Entries.Where(e => e.Date == date).ToList() ?? [];
        return new DayContext(weekStart, entries, tasks, SubmissionTaskScope.Effective(tasks.Values, choices), previous);
    }

    private static DaySubmission? Copy(DaySubmission? day, DayStatus status, IEnumerable<DaySubmissionCell> cells) =>
        day is null && status == DayStatus.NotAttempted && !cells.Any()
            ? null
            : new DaySubmission
            {
                Id = day?.Id ?? 0,
                WeekStart = day?.WeekStart ?? default,
                Date = day?.Date ?? default,
                Status = status,
                Fingerprint = day?.Fingerprint,
                SentUtc = day?.SentUtc,
                Cells = cells.ToList(),
            };

    private static IEnumerable<DaySubmissionCell> Cells(Dictionary<int, (decimal Hours, string? EntryId)> sent) =>
        sent.Select(kv => new DaySubmissionCell { ForecastTaskId = kv.Key, SentHours = kv.Value.Hours, ForecastEntryId = kv.Value.EntryId });

    private static JournalOutcome OutcomeOf(ForecastCellResult result) =>
        result.Status == ForecastCellStatus.Unknown || result.EntryState == ForecastEntryState.Unknown ? JournalOutcome.Unknown : JournalOutcome.Failed;

    private static string Label(ForecastCellStatus status) => status switch
    {
        ForecastCellStatus.Mismatch => "Forecast a changé entre-temps",
        ForecastCellStatus.Unknown => "résultat inconnu",
        ForecastCellStatus.RowNotFound => "tâche introuvable dans Forecast",
        _ => "erreur de Forecast",
    };

    private static string Label(DayStatus status) => status switch
    {
        DayStatus.NotAttempted => "non envoyé",
        DayStatus.InProgress => "envoi en cours",
        DayStatus.Sent => "envoyé",
        DayStatus.SentUnknownFingerprint => "envoyé (contenu inconnu)",
        DayStatus.Modified => "modifié depuis l'envoi",
        DayStatus.Partial => "envoi partiel",
        DayStatus.Conflict => "conflit avec Forecast",
        _ => "erreur",
    };

    private static string ReasonOf(DayGate gate) => gate switch
    {
        DayGate.NothingToSend => "Rien à envoyer pour ce jour.",
        DayGate.NothingNew => "Rien de nouveau : Forecast contient déjà ce jour.",
        DayGate.ReadForecastFirst => "Lecture de Forecast incomplète : relisez Forecast avant d'envoyer.",
        DayGate.ResolveConflicts => "Des conflits ou des cellules bloquées sont à régler d'abord.",
        DayGate.InProgress => "Un envoi de ce jour est déjà en cours.",
        _ => "Prêt à envoyer.",
    };
}
