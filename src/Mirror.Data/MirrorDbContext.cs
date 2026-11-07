using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Mirror.Data;

public sealed class MirrorDbContext(DbContextOptions<MirrorDbContext> options) : DbContext(options)
{
    public DbSet<MissionEntity> Missions => Set<MissionEntity>();
    public DbSet<MissionRuleEntity> MissionRules => Set<MissionRuleEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<ProxyInstanceEntity> ProxyInstances => Set<ProxyInstanceEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MissionEntity>(e =>
        {
            e.ToTable("missions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => x.Slug).IsUnique();
            e.Property(x => x.Slug).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.MainUrl).HasMaxLength(2048);
            e.Property(x => x.DevUrl).HasMaxLength(2048);
            e.Property(x => x.HeaderPolicy).HasColumnType("jsonb");
            e.HasMany(x => x.Rules).WithOne().HasForeignKey(r => r.MissionId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<MissionRuleEntity>(e =>
        {
            e.ToTable("mission_rules");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.MissionId, x.Order });
            e.Property(x => x.Type).HasMaxLength(64);
            e.Property(x => x.Config).HasColumnType("jsonb");
        });
        b.Entity<JobEntity>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.MissionId, x.CreatedAt });
            e.Property(x => x.Type).HasMaxLength(32);
            e.Property(x => x.Params).HasColumnType("jsonb");
            e.Property(x => x.Result).HasColumnType("jsonb");
            e.Property(x => x.Checkpoint).HasColumnType("jsonb");
        });
        b.Entity<ProxyInstanceEntity>(e =>
        {
            e.ToTable("proxy_instances");
            e.HasKey(x => x.InstanceId);
            e.Property(x => x.Metrics).HasColumnType("jsonb");
        });
    }
}

/// <summary>Used by <c>dotnet ef migrations</c> only.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<MirrorDbContext>
{
    public MirrorDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MirrorDbContext>()
            .UseNpgsql("Host=localhost;Database=mirror;Username=mirror;Password=mirror")
            .UseSnakeCaseNamingConvention()
            .Options);
}
