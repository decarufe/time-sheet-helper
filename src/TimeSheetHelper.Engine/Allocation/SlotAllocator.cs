using Microsoft.Extensions.Options;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Timesheets;

namespace TimeSheetHelper.Engine.Allocation;

/// <summary>
/// Splits a local day into 15-minute slots and picks, for each one, the candidate (task or unclassified group)
/// with the most weight. Overrides from the user win over everything.
/// </summary>
public sealed class SlotAllocator(IOptions<EngineOptions> options, TimeProvider timeProvider, ISourceCatalog? catalog = null)
{
    private readonly ISourceCatalog _catalog = catalog ?? SourceCatalog.Default;

    public TimeZoneInfo TimeZone => options.Value.ResolveTimeZone(timeProvider);

    /// <summary>UTC bounds of a local calendar day (23 or 25 hours on DST changes).</summary>
    public (DateTime StartUtc, DateTime EndUtc) DayBounds(DateOnly day)
    {
        var tz = TimeZone;
        return (LocalMidnightUtc(day, tz), LocalMidnightUtc(day.AddDays(1), tz));
    }

    /// <summary>Local calendar day containing a UTC instant.</summary>
    public DateOnly LocalDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZone));

    /// <param name="ignoredTaskIds">Tasks that remove their slots from the timesheet (the built-in "Ignoré" task).</param>
    /// <param name="removedSlots">Start of the slots the user deleted: their activities are ignored.</param>
    public IReadOnlyList<SlotAssignment> AllocateDay(DateOnly day, IReadOnlyList<ResolvedActivity> activities, IEnumerable<SlotOverride> overrides, IReadOnlySet<int>? ignoredTaskIds = null, IReadOnlySet<DateTime>? removedSlots = null)
    {
        var (start, end) = DayBounds(day);
        return Allocate(new Interval(start, end), activities, overrides, ignoredTaskIds, removedSlots);
    }

    /// <summary>Allocates every slot of <paramref name="range"/> (which should start on a slot boundary).</summary>
    public IReadOnlyList<SlotAssignment> Allocate(Interval range, IReadOnlyList<ResolvedActivity> activities, IEnumerable<SlotOverride> overrides, IReadOnlySet<int>? ignoredTaskIds = null, IReadOnlySet<DateTime>? removedSlots = null)
    {
        var overrideBySlot = overrides
            .GroupBy(o => DateTime.SpecifyKind(o.SlotStartUtc, DateTimeKind.Utc))
            .ToDictionary(g => g.Key, g => g.Last());
        var inRange = activities.Where(a => a.Window.Overlaps(range)).ToList();

        var slots = new List<SlotAssignment>();
        for (var slotStart = range.Start; slotStart < range.End; slotStart += EngineOptions.SlotLength)
        {
            var slot = new Interval(slotStart, slotStart + EngineOptions.SlotLength);
            overrideBySlot.TryGetValue(slotStart, out var slotOverride);
            slots.Add(AllocateSlot(slot, inRange, slotOverride, ignoredTaskIds, removedSlots?.Contains(slotStart) == true));
        }
        return slots;
    }

    internal SlotAssignment AllocateSlot(Interval slot, IReadOnlyList<ResolvedActivity> activities, SlotOverride? slotOverride, IReadOnlySet<int>? ignoredTaskIds = null, bool removed = false)
    {
        var o = options.Value;
        var here = removed ? new List<ResolvedActivity>() : activities.Where(a => a.Window.Overlaps(slot)).ToList();

        var masks = Interval.Merge(here.Where(a => a.Role == ActivityRole.Mask).Select(a => a.Window.Clip(slot)));

        // Effective (non-idle) parts of each active segment.
        var activeParts = here
            .Where(a => a.Role == ActivityRole.Active)
            .Select(a => (Activity: a, Parts: Interval.Subtract(a.Window.Clip(slot), masks)))
            .ToList();

        var meetings = here.Where(a => a.Role == ActivityRole.Meeting).ToList();

        var editing = Interval.TotalLength(activeParts
            .Where(p => _catalog.Get(p.Activity.Activity.Source).IsEditing)
            .SelectMany(p => p.Parts));
        var meetingOverrides = editing <= o.MeetingYieldsToEditingAbove;

        var covered = Interval.TotalLength(activeParts.SelectMany(p => p.Parts)
            .Concat(meetings.Select(m => m.Window.Clip(slot))));

        // Weight per candidate; remember which candidates got their weight from an overriding meeting.
        var weights = new Dictionary<Candidate, double>();
        var meetingWeights = new Dictionary<Candidate, double>();
        var evidence = new Dictionary<long, double>();

        void Add(ResolvedActivity a, double weight, bool fromMeeting = false)
        {
            if (weight <= 0 || a.Candidate is not { } c)
                return;
            weights[c] = weights.GetValueOrDefault(c) + weight;
            if (fromMeeting)
                meetingWeights[c] = meetingWeights.GetValueOrDefault(c) + weight;
            evidence[a.Activity.Id] = evidence.GetValueOrDefault(a.Activity.Id) + weight;
        }

        foreach (var (activity, parts) in activeParts)
        {
            var seconds = parts.Sum(p => p.Length.TotalSeconds);
            Add(activity, seconds * o.GetSourceWeight(activity.Activity.Source, _catalog.Get(activity.Activity.Source)));
        }

        foreach (var meeting in meetings)
        {
            var seconds = meeting.Window.Clip(slot).Length.TotalSeconds;
            Add(meeting, seconds * (meetingOverrides ? o.MeetingWeight : 1.0), fromMeeting: meetingOverrides);
        }

        foreach (var point in here.Where(a => a.Role == ActivityRole.Evidence))
            Add(point, point.Window.Clip(slot).Length.TotalSeconds * o.EvidenceWeight);

        var total = weights.Values.Sum();
        var ranked = weights
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key.TaskId is null) // classified first on ties
            .ThenBy(kv => kv.Key.TaskId)
            .ThenBy(kv => kv.Key.GroupKey, StringComparer.Ordinal)
            .Select(kv => new SlotCandidate(kv.Key.TaskId, kv.Key.GroupKey, kv.Value, total > 0 ? kv.Value / total : 0))
            .ToList();
        var evidenceIds = evidence
            .Where(kv => kv.Key != 0)
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        bool IsIgnored(int? taskId) => taskId is { } id && ignoredTaskIds?.Contains(id) == true;

        if (slotOverride is not null)
        {
            if (IsIgnored(slotOverride.ForecastTaskId))
                return new SlotAssignment(slot.Start, SlotKind.Ignored, slotOverride.ForecastTaskId, null, 1.0,
                    ranked, evidenceIds, covered, slotOverride.Notes);

            return slotOverride.ForecastTaskId is { } taskId
                ? new SlotAssignment(slot.Start, SlotKind.Override, taskId, null, 1.0,
                    ranked.Where(c => c.TaskId != taskId).ToList(), evidenceIds, covered, slotOverride.Notes)
                : new SlotAssignment(slot.Start, SlotKind.ExcludedByOverride, null, null, 1.0, ranked, evidenceIds, covered);
        }

        if (removed)
            return new SlotAssignment(slot.Start, SlotKind.Removed, null, null, 0, [], [], TimeSpan.Zero);

        if (covered < o.MinActivityPerSlot || ranked.Count == 0)
            return new SlotAssignment(slot.Start, SlotKind.Empty, null, null, 0, ranked, evidenceIds, covered);

        var winner = ranked[0];
        if (IsIgnored(winner.TaskId))
            return new SlotAssignment(slot.Start, SlotKind.Ignored, winner.TaskId, null, winner.Share, ranked.Skip(1).ToList(), evidenceIds, covered);

        var winnerKey = new Candidate(winner.TaskId, winner.GroupKey);
        var kind = meetingWeights.GetValueOrDefault(winnerKey) > 0 ? SlotKind.Meeting : SlotKind.Activity;

        return new SlotAssignment(slot.Start, kind, winner.TaskId, winner.GroupKey, winner.Share, ranked.Skip(1).ToList(), evidenceIds, covered);
    }

    private static DateTime LocalMidnightUtc(DateOnly day, TimeZoneInfo tz)
    {
        var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        // Some zones skip midnight on DST days; the day then starts at the first valid local time.
        while (tz.IsInvalidTime(local))
            local = local.AddMinutes(15);
        return TimeZoneInfo.ConvertTimeToUtc(local, tz);
    }
}
