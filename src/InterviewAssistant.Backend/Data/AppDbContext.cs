using Microsoft.EntityFrameworkCore;

namespace InterviewAssistant.Backend.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<UserAccount> Users => Set<UserAccount>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<InterviewSession> Sessions => Set<InterviewSession>();
    public DbSet<UsageEntry> Usage => Set<UsageEntry>();
    public DbSet<WebhookEvent> WebhookEvents => Set<WebhookEvent>();
    public DbSet<UserDocument> Documents => Set<UserDocument>();
    public DbSet<PreparationJob> PreparationJobs => Set<PreparationJob>();
    public DbSet<ConfigEntry> Config => Set<ConfigEntry>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UserAccount>(e =>
        {
            e.ToTable("users"); e.HasKey(x => x.Id);
            e.Property(x => x.Email).HasMaxLength(320); e.HasIndex(x => x.Email);
            e.Property(x => x.Status).HasMaxLength(16);
            e.Property(x => x.PrivacyJson).HasColumnType("jsonb"); e.Property(x => x.FeatureFlagsJson).HasColumnType("jsonb");
        });
        b.Entity<Subscription>(e =>
        {
            e.ToTable("subscriptions"); e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId).IsUnique();
            e.HasIndex(x => x.ProviderSubscriptionId); e.HasIndex(x => x.ProviderCustomerId);
            e.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.PlanCode).HasMaxLength(64); e.Property(x => x.Status).HasMaxLength(16);
        });
        b.Entity<Device>(e =>
        {
            e.ToTable("devices"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.InstallationIdHash }).IsUnique();
            e.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(120); e.Property(x => x.InstallationIdHash).HasMaxLength(64);
        });
        b.Entity<InterviewSession>(e =>
        {
            e.ToTable("interview_sessions"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.Status });
            e.HasIndex(x => new { x.UserId, x.IdempotencyKey }).IsUnique();
            e.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Device>().WithMany().HasForeignKey(x => x.DeviceId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.IdempotencyKey).HasMaxLength(80);
        });
        b.Entity<UsageEntry>(e =>
        {
            e.ToTable("usage_ledger"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.Kind, x.CreatedUtc });
            e.HasIndex(x => new { x.Kind, x.CreatedUtc });
            e.HasIndex(x => new { x.UserId, x.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            e.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<WebhookEvent>(e => { e.ToTable("webhook_events"); e.HasKey(x => x.EventId); e.Property(x => x.EventId).HasMaxLength(100); });
        b.Entity<UserDocument>(e =>
        {
            e.ToTable("user_documents"); e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.Kind }); e.HasIndex(x => x.ParentId); e.HasIndex(x => x.RetainUntilUtc);
            e.Property(x => x.DataJson).HasColumnType("jsonb"); e.Property(x => x.Kind).HasMaxLength(16);
            e.Property(x => x.Version).IsConcurrencyToken();
            e.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<PreparationJob>(e =>
        {
            e.ToTable("preparation_jobs"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.UserId, x.CreatedUtc });
            e.HasOne<UserAccount>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<ConfigEntry>(e => { e.ToTable("runtime_config"); e.HasKey(x => x.Key); e.Property(x => x.ValueJson).HasColumnType("jsonb"); });
        b.Entity<AuditEntry>(e => { e.ToTable("audit_log"); e.HasKey(x => x.Id); e.HasIndex(x => x.AtUtc); });
    }
}
