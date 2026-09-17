namespace TimeTracker.Server.Pages;

public readonly record struct DeviceInterval(Guid DeviceId, DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Splits tracked time into four buckets that never count the same second twice.
/// The raw event streams overlap by construction: the idle tracker only watches for missing
/// input, so a locked screen is also "idle", and the activity tracker keeps timing whichever
/// window was last in front, so both of those are also "active". Summing each stream on its
/// own reported an hour-long lock as an hour idle *and* an hour active.
/// Precedence, strongest first: asleep/off, locked, idle, active. Each bucket is its own
/// merged intervals minus everything already claimed by a stronger one, per device.
/// </summary>
public record TimeBreakdown(double ActiveSeconds, double IdleSeconds, double LockedSeconds, double AsleepSeconds)
{
    public double TotalSeconds => ActiveSeconds + IdleSeconds + LockedSeconds + AsleepSeconds;

    public static TimeBreakdown Compute(
        IEnumerable<DeviceInterval> active,
        IEnumerable<DeviceInterval> idle,
        IEnumerable<DeviceInterval> locked,
        IEnumerable<DeviceInterval> asleep,
        DateTimeOffset? fromUtc,
        DateTimeOffset toUtcExclusive)
    {
        var activeByDevice = MergeByDevice(active, fromUtc, toUtcExclusive);
        var idleByDevice = MergeByDevice(idle, fromUtc, toUtcExclusive);
        var lockedByDevice = MergeByDevice(locked, fromUtc, toUtcExclusive);
        var asleepByDevice = MergeByDevice(asleep, fromUtc, toUtcExclusive);

        double activeSeconds = 0, idleSeconds = 0, lockedSeconds = 0, asleepSeconds = 0;

        var devices = activeByDevice.Keys.Concat(idleByDevice.Keys).Concat(lockedByDevice.Keys).Concat(asleepByDevice.Keys).Distinct();
        foreach (var deviceId in devices)
        {
            var a = activeByDevice.GetValueOrDefault(deviceId) ?? new();
            var i = idleByDevice.GetValueOrDefault(deviceId) ?? new();
            var l = lockedByDevice.GetValueOrDefault(deviceId) ?? new();
            var s = asleepByDevice.GetValueOrDefault(deviceId) ?? new();

            var claimedBySleep = s;
            var claimedByLock = Merge(s.Concat(l));
            var claimedByIdle = Merge(claimedByLock.Concat(i));

            asleepSeconds += Length(s);
            lockedSeconds += Length(l) - Overlap(l, claimedBySleep);
            idleSeconds += Length(i) - Overlap(i, claimedByLock);
            activeSeconds += Length(a) - Overlap(a, claimedByIdle);
        }

        return new TimeBreakdown(activeSeconds, idleSeconds, lockedSeconds, asleepSeconds);
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

    private static double Length(List<(DateTimeOffset Start, DateTimeOffset End)> merged) =>
        merged.Sum(x => (x.End - x.Start).TotalSeconds);

    // Both lists are merged (sorted, non-overlapping), so a single two-pointer pass suffices.
    private static double Overlap(List<(DateTimeOffset Start, DateTimeOffset End)> a, List<(DateTimeOffset Start, DateTimeOffset End)> b)
    {
        double total = 0;
        int i = 0, j = 0;
        while (i < a.Count && j < b.Count)
        {
            var start = a[i].Start > b[j].Start ? a[i].Start : b[j].Start;
            var end = a[i].End < b[j].End ? a[i].End : b[j].End;
            if (end > start)
            {
                total += (end - start).TotalSeconds;
            }

            if (a[i].End < b[j].End)
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return total;
    }
}
