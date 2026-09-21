using System.Linq.Expressions;

namespace TimeTracker.Server.Pages;

/// <summary>
/// The Activity page's time-of-day filter: a working-hours window applied to every day of the
/// selected date range, rather than one continuous span.
///
/// This exists because a device left on overnight buries the day's real numbers - a machine that
/// was locked from 18:00 to 09:00 reports over ten hours of locked time against twenty minutes of
/// idle, and the working day is invisible underneath it.
///
/// The window is expressed in the viewer's display timezone, not UTC. That is the only reading
/// that matches what the page shows: its timeline and every timestamp on it are rendered in the
/// zone chosen in the top bar, so "09:00" has to mean 09:00 there.
/// </summary>
public static class TimeWindows
{
    /// <summary>
    /// One UTC interval per day in the range. A window whose end is at or before its start is
    /// read as crossing midnight (22:00-06:00 is a night shift), which otherwise would be an
    /// empty selection nobody would type on purpose.
    /// </summary>
    public static List<(DateTimeOffset Start, DateTimeOffset End)> Build(
        DateOnly fromDate, DateOnly toDate, TimeOnly timeFrom, TimeOnly timeTo, TimeZoneInfo zone)
    {
        var windows = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        var crossesMidnight = timeTo <= timeFrom;

        for (var day = fromDate; day <= toDate; day = day.AddDays(1))
        {
            var localStart = day.ToDateTime(timeFrom);
            var localEnd = crossesMidnight
                ? day.AddDays(1).ToDateTime(timeTo)
                : day.ToDateTime(timeTo);

            windows.Add((ToUtc(localStart, zone), ToUtc(localEnd, zone)));
        }

        return windows;
    }

    /// <summary>
    /// A wall-clock time can be skipped or repeated on a DST boundary, and ConvertTimeToUtc throws
    /// on the skipped hour rather than picking for you. Neither case should fail a page load, so a
    /// skipped time moves forward past the gap and an ambiguous one takes the earlier offset -
    /// which is the same choice a person reading a clock that day would make.
    /// </summary>
    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);

        if (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddHours(1);
        }

        if (zone.IsAmbiguousTime(unspecified))
        {
            var offsets = zone.GetAmbiguousTimeOffsets(unspecified);
            return new DateTimeOffset(unspecified, offsets.Max()).ToUniversalTime();
        }

        return new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
    }

    /// <summary>
    /// Clips segments to the windows, splitting any that straddle a boundary. This is what keeps
    /// the totals honest: a lock running 18:00-09:00 contributes only the part inside the window,
    /// not the whole night, and not nothing.
    /// </summary>
    public static IEnumerable<StateSegment> Clip(
        IEnumerable<StateSegment> segments, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> windows)
    {
        foreach (var segment in segments)
        {
            foreach (var window in windows)
            {
                var start = segment.Start > window.Start ? segment.Start : window.Start;
                var end = segment.End < window.End ? segment.End : window.End;

                if (end > start)
                {
                    yield return segment with { Start = start, End = end };
                }
            }
        }
    }

    /// <summary>
    /// Builds "matches window 1 OR window 2 OR ..." as a single expression, so the filtering
    /// happens in SQL rather than by materialising a month of rows and discarding most of them.
    /// EF cannot translate a loop over a local list of intervals, so the OR chain is assembled by
    /// hand and the per-window predicate is supplied by the caller - each event type decides what
    /// "in this window" means for its own columns.
    /// </summary>
    public static Expression<Func<T, bool>> AnyWindow<T>(
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> windows,
        Func<DateTimeOffset, DateTimeOffset, Expression<Func<T, bool>>> forWindow)
    {
        Expression<Func<T, bool>>? combined = null;

        foreach (var (start, end) in windows)
        {
            var predicate = forWindow(start, end);
            combined = combined is null ? predicate : Or(combined, predicate);
        }

        // No windows means the range held no days at all; match nothing rather than everything,
        // so an impossible filter shows an empty page instead of silently ignoring itself.
        return combined ?? (_ => false);
    }

    private static Expression<Func<T, bool>> Or<T>(Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
    {
        var parameter = left.Parameters[0];
        var rebound = new ParameterReplacer(right.Parameters[0], parameter).Visit(right.Body)!;
        return Expression.Lambda<Func<T, bool>>(Expression.OrElse(left.Body, rebound), parameter);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
    }
}
