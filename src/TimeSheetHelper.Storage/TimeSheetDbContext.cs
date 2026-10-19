using Microsoft.EntityFrameworkCore;
using TimeSheetHelper.Core.Activities;
using TimeSheetHelper.Core.Forecast;
using TimeSheetHelper.Core.Plugins;
using TimeSheetHelper.Core.Rules;
using TimeSheetHelper.Core.Timesheets;

namespace TimeSheetHelper.Storage;

public sealed class TimeSheetDbContext(DbContextOptions<TimeSheetDbContext> options) : DbContext(options)
{
    public DbSet<ActivityEvent> Activities => Set<ActivityEvent>();
    public DbSet<ForecastTask> ForecastTasks => Set<ForecastTask>();
    public DbSet<Rule> Rules => Set<Rule>();
    public DbSet<TimesheetDraft> TimesheetDrafts => Set<TimesheetDraft>();
    public DbSet<TimesheetEntry> TimesheetEntries => Set<TimesheetEntry>();
    public DbSet<SlotOverride> SlotOverrides => Set<SlotOverride>();
    public DbSet<WeekTaskChoice> WeekTaskChoices => Set<WeekTaskChoice>();
    public DbSet<RemovedSlot> RemovedSlots => Set<RemovedSlot>();
    public DbSet<ClassificationCacheEntry> ClassificationCache => Set<ClassificationCacheEntry>();
    public DbSet<InstalledPlugin> InstalledPlugins => Set<InstalledPlugin>();
    public DbSet<DaySubmission> DaySubmissions => Set<DaySubmission>();
    public DbSet<DaySubmissionCell> DaySubmissionCells => Set<DaySubmissionCell>();
    public DbSet<SubmissionJournalEntry> SubmissionJournal => Set<SubmissionJournalEntry>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no time zone; everything is stored as UTC and read back as UTC.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcDateTimeConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var activity = modelBuilder.Entity<ActivityEvent>();
        activity.ToTable("Activities");
        activity.Property(a => a.Source).HasConversion(s => s.Id, id => new ActivitySource(id)).HasMaxLength(32);
        activity.Ignore(a => a.Duration);
        activity.HasIndex(a => a.StartUtc);
        activity.HasIndex(a => new { a.Source, a.ExternalId }).IsUnique().HasFilter("ExternalId IS NOT NULL");

        var task = modelBuilder.Entity<ForecastTask>();
        task.Ignore(t => t.DisplayName);
        task.HasIndex(t => t.ExternalId).IsUnique();

        var rule = modelBuilder.Entity<Rule>();
        rule.Property(r => r.Field).HasConversion<string>().HasMaxLength(32);
        rule.Property(r => r.Operator).HasConversion<string>().HasMaxLength(32);
        rule.HasOne<ForecastTask>().WithMany().HasForeignKey(r => r.ForecastTaskId);

        var draft = modelBuilder.Entity<TimesheetDraft>();
        draft.HasIndex(d => d.WeekStart).IsUnique();
        draft.HasMany(d => d.Entries).WithOne().HasForeignKey(e => e.TimesheetDraftId).OnDelete(DeleteBehavior.Cascade);

        var entry = modelBuilder.Entity<TimesheetEntry>();
        entry.Property(e => e.Hours).HasConversion<double>();
        entry.HasOne<ForecastTask>().WithMany().HasForeignKey(e => e.ForecastTaskId);

        var choice = modelBuilder.Entity<WeekTaskChoice>();
        choice.HasIndex(c => new { c.WeekStart, c.ForecastTaskId }).IsUnique();
        choice.HasOne<ForecastTask>().WithMany().HasForeignKey(c => c.ForecastTaskId).OnDelete(DeleteBehavior.Cascade);

        // No foreign key from cells or journal to ForecastTasks: deleting or merging a task must never erase what was sent to Forecast.
        var day = modelBuilder.Entity<DaySubmission>();
        day.HasIndex(d => new { d.WeekStart, d.Date }).IsUnique();
        day.Property(d => d.Fingerprint).HasMaxLength(64);
        day.HasMany(d => d.Cells).WithOne().HasForeignKey(c => c.DaySubmissionId).OnDelete(DeleteBehavior.Cascade);

        var cell = modelBuilder.Entity<DaySubmissionCell>();
        cell.HasIndex(c => new { c.DaySubmissionId, c.ForecastTaskId }).IsUnique();
        cell.Property(c => c.SentHours).HasConversion<double>();
        cell.Property(c => c.ForecastEntryId).HasMaxLength(64);

        var journal = modelBuilder.Entity<SubmissionJournalEntry>();
        journal.ToTable("SubmissionJournal");
        journal.HasOne<DaySubmission>().WithMany().HasForeignKey(j => j.DaySubmissionId).OnDelete(DeleteBehavior.Cascade);
        journal.Property(j => j.Kind).HasConversion<string>().HasMaxLength(32);
        journal.Property(j => j.Outcome).HasConversion<string>().HasMaxLength(32);
        journal.Property(j => j.Hours).HasConversion<double?>();
        journal.Property(j => j.Message).HasMaxLength(SubmissionJournalEntry.MessageMaxLength);

        modelBuilder.Entity<SlotOverride>().HasIndex(s => s.SlotStartUtc).IsUnique();
        modelBuilder.Entity<RemovedSlot>().HasIndex(s => s.SlotStartUtc).IsUnique();
        modelBuilder.Entity<ClassificationCacheEntry>().HasIndex(c => c.Fingerprint).IsUnique();
        modelBuilder.Entity<InstalledPlugin>().HasKey(p => p.Id);
    }
}
