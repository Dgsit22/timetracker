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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUsageEvent>().HasKey(e => e.EventId);
        modelBuilder.Entity<IdlePeriodEvent>().HasKey(e => e.EventId);
        modelBuilder.Entity<UrlVisitEvent>().HasKey(e => e.EventId);
        modelBuilder.Entity<SessionBreakEvent>().HasKey(e => e.EventId);
        modelBuilder.Entity<ScreenshotEvent>().HasKey(e => e.EventId);
        modelBuilder.Entity<Device>().HasKey(e => e.DeviceId);
    }
}
