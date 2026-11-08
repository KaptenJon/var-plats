using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using VarPlats.Core;

namespace VarPlats.Web.Data;

public sealed class ReportEntity
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public ReportCategory Category { get; set; }
    public required Point Location { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ReportStatus Status { get; set; }
}

public sealed class IdentityAccountActivity
{
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? LastSignInAtUtc { get; set; }
}

public sealed class ReportDbContext(DbContextOptions<ReportDbContext> options)
    : IdentityDbContext<
        IdentityUser<Guid>,
        IdentityRole<Guid>,
        Guid,
        IdentityUserClaim<Guid>,
        IdentityUserRole<Guid>,
        IdentityUserLogin<Guid>,
        IdentityRoleClaim<Guid>,
        IdentityUserToken<Guid>,
        IdentityUserPasskey<Guid>>(options)
{
    public DbSet<ReportEntity> Reports => Set<ReportEntity>();
    public DbSet<IdentityAccountActivity> IdentityAccountActivities => Set<IdentityAccountActivity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.HasPostgresExtension("postgis");
        modelBuilder.Entity<IdentityUserPasskey<Guid>>(entity =>
        {
            entity.HasKey(passkey => passkey.CredentialId);
            entity.ToTable("AspNetUserPasskeys");
            entity.Property(passkey => passkey.CredentialId).HasMaxLength(1024);
            entity.ComplexProperty(passkey => passkey.Data).ToJson();
            entity.HasOne<IdentityUser<Guid>>()
                .WithMany()
                .HasForeignKey(passkey => passkey.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });
        modelBuilder.Entity<ReportEntity>(entity =>
        {
            entity.ToTable("reports");
            entity.HasKey(report => report.Id);
            entity.Property(report => report.Title).HasMaxLength(100);
            entity.Property(report => report.Description).HasMaxLength(1000);
            entity.Property(report => report.Location).HasColumnType("geography (point, 4326)");
            entity.HasIndex(report => report.Location).HasMethod("gist");
        });
        modelBuilder.Entity<IdentityAccountActivity>(entity =>
        {
            entity.ToTable("IdentityAccountActivity");
            entity.HasKey(activity => activity.UserId);
            entity.Property(activity => activity.CreatedAtUtc)
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
            entity.HasOne<IdentityUser<Guid>>()
                .WithOne()
                .HasForeignKey<IdentityAccountActivity>(activity => activity.UserId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            entity.HasIndex(activity => activity.CreatedAtUtc);
            entity.HasIndex(activity => activity.LastSignInAtUtc);
        });
    }
}

public sealed class DatabaseReportStore(
    IDbContextFactory<ReportDbContext> factory, ReportingMode mode) : IReportStore
{
    public async Task<IReadOnlyList<Report>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var entities = await db.Reports.AsNoTracking().OrderByDescending(report => report.CreatedAt)
            .Take(200).ToListAsync(cancellationToken);
        return entities.Select(report => new Report(report.Id, report.Title, report.Description,
            report.Category, report.Location.Y, report.Location.X, report.CreatedAt, report.Status)).ToArray();
    }

    public async Task<Report> AddAsync(ReportDraft draft, CancellationToken cancellationToken = default)
    {
        if (!mode.CanWrite)
            throw new InvalidOperationException("Publika skrivningar är inte aktiverade.");
        var report = draft.ToReport();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        db.Reports.Add(new()
        {
            Id = report.Id,
            Title = report.Title,
            Description = report.Description,
            Category = report.Category,
            CreatedAt = report.CreatedAt,
            Status = report.Status,
            Location = new(report.Longitude, report.Latitude) { SRID = 4326 }
        });
        await db.SaveChangesAsync(cancellationToken);
        return report;
    }

    public async Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!mode.CanWrite)
            throw new InvalidOperationException("Publika skrivningar är inte aktiverade.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        return await db.Reports.Where(report => report.Id == id)
            .ExecuteDeleteAsync(cancellationToken) > 0;
    }
}
