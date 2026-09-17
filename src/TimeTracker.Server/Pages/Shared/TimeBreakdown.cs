using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;
using TimeTracker.Shared.Events;

namespace TimeTracker.Server.Pages;

public readonly record struct DeviceInterval(Guid DeviceId, DateTimeOffset Start, DateTimeOffset End);

// Ordered weakest to strongest; Resolve relies on this ordering for precedence.
public enum TimeState
{
    Active,
    Idle,
    Locked,
    Asleep,
}

public readonly record struct StateSegment(Guid DeviceId, DateTimeOffset Start, DateTimeOffset End, TimeState State)
{
    public double Seconds => (End - Start).TotalSeconds;
}

public record StateIntervals(
    List<DeviceInterval> Active,
    List<DeviceInterval> Idle,
    List<DeviceInterval> Locked,
    List<DeviceInterval> Asleep);

/// <summary>
/// Splits tracked time into four states that never count the same second twice.
/// The raw event streams overlap by construction: the idle tracker only watches for missing
/// input, so a locked screen is also "idle", and the activity tracker keeps timing whichever
/// window was last in front, so both of those are also "active". Summing each stream on its
/// own reported an hour-long lock as an hour idle *and* an hour active.
/// Precedence, strongest first: asleep/off, locked, idle, active. Time covered by none of
/// them is untracked (agent not running) and produces no segment.
/// </summary>
public record TimeBreakdown(double ActiveSeconds, double IdleSeconds, double LockedSeconds, double AsleepSeconds)
{
    public double TotalSeconds => ActiveSeconds + IdleSeconds + LockedSeconds + AsleepSeconds;

    public static TimeBreakdown Compute(StateIntervals intervals, DateTimeOffset? fromUtc, DateTimeOffset toUtcExclusive) =>
        Sum(Resolve(intervals, fromUtc, toUtcExclusive));

    public static TimeBreakdown Sum(IEnumerable<StateSegment> segments)
    {
        double active = 0, idle = 0, locked = 0, asleep = 0;
        foreach (var s in segments)
        {
            switch (s.State)
            {
                case TimeState.Active: active += s.Seconds; break;
                case TimeState.Idle: idle += s.Seconds; break;
                case TimeState.Locked: locked += s.Seconds; break;
                case TimeState.Asleep: asleep += s.Seconds; break;
            }
        }

        return new TimeBreakdown(active, idle, locked, asleep);
    }

    /// <summary>
    /// Non-overlapping, per-device segments in time order, each carrying the strongest state
    /// covering it, with adjacent same-state segments joined.
    /// </summary>
    public static List<StateSegment> Resolve(StateIntervals intervals, DateTimeOffset? fromUtc, DateTimeOffset toUtcExclusive)
    {
        var byState = new[]
        {
            MergeByDevice(intervals.Active, fromUtc, toUtcExclusive),
            MergeByDevice(intervals.Idle, fromUtc, toUtcExclusive),
            MergeByDevice(intervals.Locked, fromUtc, toUtcExclusive),
            MergeByDevice(intervals.Asleep, fromUtc, toUtcExclusive),
        };

        var result = new List<StateSegment>();
        foreach (var deviceId in byState.SelectMany(d => d.Keys).Distinct())
        {
            var lists = byState.Select(d => d.GetValueOrDefault(deviceId) ?? new()).ToArray();

            var boundaries = lists.SelectMany(l => l.SelectMany(x => new[] { x.Start, x.End })).Distinct().Order().ToList();
            var cursors = new int[lists.Length];

            for (var b = 0; b + 1 < boundaries.Count; b++)
            {
                var start = boundaries[b];
                var end = boundaries[b + 1];

                TimeState? state = null;
                for (var s = lists.Length - 1; s >= 0; s--)
                {
                    var list = lists[s];
                    while (cursors[s] < list.Count && list[cursors[s]].End <= start)
                    {
                        cursors[s]++;
                    }

                    if (state is null && cursors[s] < list.Count && list[cursors[s]].Start <= start)
                    {
                        state = (TimeState)s;
                    }
                }

                if (state is not { } resolved)
                {
                    continue;
                }

                if (result.Count > 0 && result[^1].DeviceId == deviceId && result[^1].State == resolved && result[^1].End == start)
                {
                    result[^1] = result[^1] with { End = end };
                }
                else
                {
                    result.Add(new StateSegment(deviceId, start, end, resolved));
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Selects by overlap with the window rather than by start time; Resolve clips to it. A lock
    /// that began before midnight still covers the first hours of the next day, and counting it
    /// all on the day it started would push that day's locked time into the previous one.
    /// </summary>
    public static async Task<StateIntervals> LoadIntervalsAsync(
        TimeTrackerDbContext db,
        HashSet<string> excludedUserNames,
        string? userName,
        Guid? deviceId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtcExclusive,
        CancellationToken cancellationToken)
    {
        var appUsage = db.AppUsageEvents.Where(e => !excludedUserNames.Contains(e.UserName));
        var idle = db.IdlePeriods.Where(e => !excludedUserNames.Contains(e.UserName));
        var breaks = db.SessionBreaks.Where(e => !excludedUserNames.Contains(e.UserName) && e.BreakEndUtc != null);

        if (!string.IsNullOrWhiteSpace(userName))
        {
            appUsage = appUsage.Where(e => e.UserName == userName);
            idle = idle.Where(e => e.UserName == userName);
            breaks = breaks.Where(e => e.UserName == userName);
        }

        if (deviceId is { } id)
        {
            appUsage = appUsage.Where(e => e.DeviceId == id);
            idle = idle.Where(e => e.DeviceId == id);
            breaks = breaks.Where(e => e.DeviceId == id);
        }

        if (fromUtc is { } from)
        {
            appUsage = appUsage.Where(e => e.EndedAtUtc > from);
            idle = idle.Where(e => e.EndedAtUtc > from);
            breaks = breaks.Where(e => e.BreakEndUtc > from);
        }

        if (toUtcExclusive is { } to)
        {
            appUsage = appUsage.Where(e => e.StartedAtUtc < to);
            idle = idle.Where(e => e.StartedAtUtc < to);
            breaks = breaks.Where(e => e.BreakStartUtc < to);
        }

        var appRows = await appUsage.Select(e => new { e.DeviceId, e.StartedAtUtc, e.EndedAtUtc }).ToListAsync(cancellationToken);
        var idleRows = await idle.Select(e => new { e.DeviceId, e.StartedAtUtc, e.EndedAtUtc }).ToListAsync(cancellationToken);
        var breakRows = await breaks.Select(e => new { e.DeviceId, e.BreakStartUtc, e.BreakEndUtc, e.Reason }).ToListAsync(cancellationToken);

        static bool IsAsleep(SessionBreakReason r) => r is SessionBreakReason.MachineSleep or SessionBreakReason.MachineShutdown;

        return new StateIntervals(
            appRows.Select(e => new DeviceInterval(e.DeviceId, e.StartedAtUtc, e.EndedAtUtc)).ToList(),
            idleRows.Select(e => new DeviceInterval(e.DeviceId, e.StartedAtUtc, e.EndedAtUtc)).ToList(),
            breakRows.Where(e => !IsAsleep(e.Reason)).Select(e => new DeviceInterval(e.DeviceId, e.BreakStartUtc, e.BreakEndUtc!.Value)).ToList(),
            breakRows.Where(e => IsAsleep(e.Reason)).Select(e => new DeviceInterval(e.DeviceId, e.BreakStartUtc, e.BreakEndUtc!.Value)).ToList());
    }

    private static Dictionary<Guid, List<(DateTimeOffset Start, DateTimeOffset End)>> MergeByDevice(
        IEnumerable<DeviceInterval> intervals, DateTimeOffset? fromUtc, DateTimeOffset toUtcExclusive) =>
        intervals
            .Select(x => x with
            {
                Start = fromUtc is { } from && x.Start < from ? from : x.Start,
                End = x.End > toUtcExclusive ? toUtcExclusive : x.End,
            })
            .Where(x => x.End > x.Start)
            .GroupBy(x => x.DeviceId)
            .ToDictionary(g => g.Key, g => Merge(g.Select(x => (x.Start, x.End))));

    private static List<(DateTimeOffset Start, DateTimeOffset End)> Merge(IEnumerable<(DateTimeOffset Start, DateTimeOffset End)> intervals)
    {
        var merged = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        foreach (var x in intervals.OrderBy(x => x.Start))
        {
            if (merged.Count > 0 && x.Start <= merged[^1].End)
            {
                if (x.End > merged[^1].End)
                {
                    merged[^1] = (merged[^1].Start, x.End);
                }
            }
            else
            {
                merged.Add(x);
            }
        }

        return merged;
    }
}
