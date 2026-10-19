using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Timesheets;
using TimeSheetHelper.Storage;

namespace TimeSheetHelper.Forecast;

/// <summary>
/// The user's explicit "do not send" / "send anyway" choices per week and task, kept between sessions.
/// A week without a choice for a task follows the task's <c>ExcludeFromSubmission</c> flag.
/// </summary>
public sealed class SubmissionTaskChoices(IDbContextFactory<TimeSheetDbContext> dbFactory)
{
    /// <summary>Task id → true (left out) / false (put back in) for the week of <paramref name="weekStart"/>.</summary>
    public async Task<Dictionary<int, bool>> LoadAsync(DateOnly weekStart, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.WeekTaskChoices.AsNoTracking()
            .Where(c => c.WeekStart == weekStart)
            .ToDictionaryAsync(c => c.ForecastTaskId, c => c.Ignore, cancellationToken);
    }

    /// <summary>Forgets every choice of the week: its tasks follow their <c>ExcludeFromSubmission</c> flag again.</summary>
    public async Task ResetWeekAsync(DateOnly weekStart, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.WeekTaskChoices.Where(c => c.WeekStart == weekStart).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Records the choice, or forgets it when <paramref name="ignore"/> is null (the task follows its flag again).</summary>
    public async Task SetAsync(DateOnly weekStart, int taskId, bool? ignore, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.WeekTaskChoices.SingleOrDefaultAsync(c => c.WeekStart == weekStart && c.ForecastTaskId == taskId, cancellationToken);
        if (ignore is null)
        {
            if (existing is null)
                return;
            db.WeekTaskChoices.Remove(existing);
        }
        else if (existing is null)
        {
            if (!await db.ForecastTasks.AnyAsync(t => t.Id == taskId, cancellationToken))
                return; // The task vanished meanwhile: nothing to remember.
            db.WeekTaskChoices.Add(new WeekTaskChoice { WeekStart = weekStart, ForecastTaskId = taskId, Ignore = ignore.Value });
        }
        else
        {
            existing.Ignore = ignore.Value;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
