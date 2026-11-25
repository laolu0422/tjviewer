using Microsoft.EntityFrameworkCore;

namespace TjViewer.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<User> Users => Set<User>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
    public DbSet<SessionRecord> Sessions => Set<SessionRecord>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<SystemConfig> SystemConfigs => Set<SystemConfig>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>().HasIndex(u => u.Username).IsUnique();
        b.Entity<User>().HasIndex(u => u.Email).IsUnique();
        b.Entity<Device>().HasIndex(d => d.DeviceKey).IsUnique();
        b.Entity<Device>().HasOne(d => d.Owner).WithMany(u => u.Devices).HasForeignKey(d => d.OwnerId);
        b.Entity<SystemConfig>().HasKey(c => c.Key);
    }
}
