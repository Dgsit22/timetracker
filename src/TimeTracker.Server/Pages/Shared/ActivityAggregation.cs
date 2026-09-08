using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TimeTracker.Server.Data;

namespace TimeTracker.Server.Pages;

/// <summary>
/// Shared between Activity, Dashboard, and Reports: top-N-by-duration ranking, duration
/// formatting, URL host extraction, and resolving which tracked usernames are currently
/// excluded (directly, or via group membership) so all three pages filter identically.
/// </summary>
public static class ActivityAggregation
{
    public static readonly JsonSerializerOptions ChartJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string GetUrlHost(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(unknown)";

    public static List<CategorySlice> BuildTopSlices(IEnumerable<(string Label, double Seconds)> items, int topN = 5)
    {
        var ranked = items.OrderByDescending(x => x.Seconds).ToList();
        var top = ranked.Take(topN).Select(x => new CategorySlice(x.Label, x.Seconds)).ToList();
        var otherTotal = ranked.Skip(topN).Sum(x => x.Seconds);
        if (otherTotal > 0)
        {
            top.Add(new CategorySlice("Other", otherTotal));
        }

        return top;
    }

    // Whole-minute granularity, for aggregate totals (KPI cards, summary cards).
    public static string FormatDuration(double seconds)
    {
        if (seconds <= 0)
        {
            return "0m";
        }

        var span = TimeSpan.FromSeconds(seconds);
        var hours = (int)span.TotalHours;
        var minutes = span.Minutes;
        return hours > 0 ? $"{hours}h {minutes}m" : $"{minutes}m";
    }

    // Seconds-precision, for individual row durations which can be well under a minute.
    public static string FormatRowDuration(double seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds:F0}s";
        }

        var totalSeconds = (int)Math.Round(seconds);
        return $"{totalSeconds / 60}m {totalSeconds % 60}s";
    }

    /// <summary>
    /// Tracked usernames (not Identity accounts - see Group/ExclusionRule doc comments)
    /// currently hidden from Activity/Dashboard/Reports: directly excluded, plus every
    /// member of any excluded group.
    /// </summary>
    public static async Task<HashSet<string>> GetExcludedUserNamesAsync(TimeTrackerDbContext db, CancellationToken cancellationToken)
    {
        var rules = await db.ExclusionRules.ToListAsync(cancellationToken);

        var excluded = new HashSet<string>(rules.Where(r => r.UserName != null).Select(r => r.UserName!));

        var excludedGroupIds = rules.Where(r => r.GroupId != null).Select(r => r.GroupId!.Value).ToHashSet();
        if (excludedGroupIds.Count > 0)
        {
            var groupMemberNames = await db.GroupMembers
                .Where(m => excludedGroupIds.Contains(m.GroupId))
                .Select(m => m.UserName)
                .ToListAsync(cancellationToken);
            excluded.UnionWith(groupMemberNames);
        }

        return excluded;
    }
}

public record CategorySlice(string Label, double Seconds);
