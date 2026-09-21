using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace TimeTracker.Server.Data;

public class TimeTrackerDbContext : IdentityDbContext<ApplicationUser>
{
    public TimeTrackerDbContext(DbContextOptions<TimeTrackerDbContext> options) : base(options)
    {
    }

    public DbSet<AppUsageEvent> AppUsageEvents => Set<AppUsageEvent>();
    public DbSet<IdlePeriodEvent> IdlePeriods => Set<IdlePeriodEvent>();
    public DbSet<UrlVisitEvent> UrlVisits => Set<UrlVisitEvent>();
    public DbSet<SessionBreakEvent> SessionBreaks => Set<SessionBreakEvent>();
    public DbSet<ScreenshotEvent> Screenshots => Set<ScreenshotEvent>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();
    public DbSet<ExclusionRule> ExclusionRules => Set<ExclusionRule>();
    public DbSet<Alert> Alerts => Set<Alert>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUsageEvent>().HasKey(e => e.EventId);
        modelBuilder.Entity<IdlePeriodEvent>().HasKey(e => e.EventId);
        modelBuilder.Entity<UrlVisitEvent>().HasKey(e => e.EventId);
        modelBuilder.Entity<SessionBreakEvent>().HasKey(e => e.EventId);
        modelBuilder.Entity<ScreenshotEvent>().HasKey(e => e.EventId);
        modelBuilder.Entity<Device>().HasKey(e => e.DeviceId);
        modelBuilder.Entity<Group>().HasKey(e => e.GroupId);
        modelBuilder.Entity<GroupMember>().HasKey(e => new { e.GroupId, e.UserName });
        modelBuilder.Entity<ExclusionRule>().HasKey(e => e.ExclusionRuleId);

        modelBuilder.Entity<Alert>(e =>
        {
            e.HasKey(x => x.AlertId);
            // Every read is "the open ones, newest first", and the monitor matches on kind+scope
            // to decide between raising a new alert and refreshing the one already there.
            e.HasIndex(x => new { x.ResolvedUtc, x.LastSeenUtc }).IsDescending(false, true);
            e.HasIndex(x => new { x.Kind, x.Scope, x.ResolvedUtc });
        });

        // Every read path filters by device or user and sorts by time descending - Activity,
        // Dashboard, Reports and the retention sweep all do it. Without these, Postgres sorts
        // the whole table on each page load, which is invisible at a few thousand rows and
        // ruinous at the millions a handful of devices produce in a year. The time column
        // descends in the index so the newest rows, which is all any page asks for, are the
        // ones the scan reaches first.
        modelBuilder.Entity<AppUsageEvent>(e =>
        {
            e.HasIndex(x => new { x.DeviceId, x.StartedAtUtc }).IsDescending(false, true);
            e.HasIndex(x => new { x.UserName, x.StartedAtUtc }).IsDescending(false, true);
            e.HasIndex(x => x.StartedAtUtc);
        });

        modelBuilder.Entity<IdlePeriodEvent>(e =>
        {
            e.HasIndex(x => new { x.DeviceId, x.StartedAtUtc }).IsDescending(false, true);
            e.HasIndex(x => new { x.UserName, x.StartedAtUtc }).IsDescending(false, true);
            e.HasIndex(x => x.StartedAtUtc);
        });

        modelBuilder.Entity<UrlVisitEvent>(e =>
        {
            e.HasIndex(x => new { x.DeviceId, x.StartedAtUtc }).IsDescending(false, true);
            e.HasIndex(x => new { x.UserName, x.StartedAtUtc }).IsDescending(false, true);
            e.HasIndex(x => x.StartedAtUtc);
        });

        modelBuilder.Entity<SessionBreakEvent>(e =>
        {
            e.HasIndex(x => new { x.DeviceId, x.BreakStartUtc }).IsDescending(false, true);
            e.HasIndex(x => new { x.UserName, x.BreakStartUtc }).IsDescending(false, true);
            e.HasIndex(x => x.BreakStartUtc);
        });

        // Screenshots are also swept by capture time alone, by retention.
        modelBuilder.Entity<ScreenshotEvent>(e =>
        {
            e.HasIndex(x => new { x.DeviceId, x.CapturedAtUtc }).IsDescending(false, true);
            e.HasIndex(x => new { x.UserName, x.CapturedAtUtc }).IsDescending(false, true);
            e.HasIndex(x => x.CapturedAtUtc);
        });
    }
}
