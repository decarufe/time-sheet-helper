using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Activities;

namespace TimeSheetHelper.Storage;

public sealed class TimeSheetDbContext(DbContextOptions<TimeSheetDbContext> options) : DbContext(options)
{
    public DbSet<ActivityEvent> Activities => Set<ActivityEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var activity = modelBuilder.Entity<ActivityEvent>();
        activity.ToTable("Activities");
        activity.Property(a => a.Source).HasConversion<string>().HasMaxLength(32);
        activity.Property(a => a.StartUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        activity.Property(a => a.EndUtc).HasConversion(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
        activity.Ignore(a => a.Duration);
        activity.HasIndex(a => a.StartUtc);
        activity.HasIndex(a => new { a.Source, a.ExternalId }).IsUnique().HasFilter("ExternalId IS NOT NULL");
    }
}
