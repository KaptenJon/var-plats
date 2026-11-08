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

public sealed class ReportSubmissionEntity
{
    public Guid Id { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public ReportCategory Category { get; set; }
    public required Point Location { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public ReportReviewStatus ReviewStatus { get; set; }
    public Guid? AuthorId { get; set; }
    public Guid? PublishedReportId { get; set; }
}

public sealed class ReportActionEntity
{
    public Guid Id { get; set; }
    public Guid ReportId { get; set; }
    public Guid? AuthorId { get; set; }
    public required string Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ReportActionConfirmationEntity
{
    public Guid ActionId { get; set; }
    public Guid AccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class ReportPhotoEntity
{
    public Guid Id { get; set; }
    public Guid? SubmissionId { get; set; }
    public Guid? ActionId { get; set; }
    public required string StorageName { get; set; }
    public required string ContentType { get; set; }
    public required string Sha256 { get; set; }
    public long ByteLength { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
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
    public DbSet<ReportSubmissionEntity> ReportSubmissions => Set<ReportSubmissionEntity>();
    public DbSet<ReportActionEntity> ReportActions => Set<ReportActionEntity>();
    public DbSet<ReportActionConfirmationEntity> ReportActionConfirmations => Set<ReportActionConfirmationEntity>();
    public DbSet<ReportPhotoEntity> ReportPhotos => Set<ReportPhotoEntity>();
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
        modelBuilder.Entity<ReportSubmissionEntity>(entity =>
        {
            entity.ToTable("report_submissions", table =>
            {
                table.HasCheckConstraint("CK_report_submissions_Category", "\"Category\" BETWEEN 0 AND 2");
                table.HasCheckConstraint("CK_report_submissions_Title", "length(trim(\"Title\")) >= 5");
                table.HasCheckConstraint("CK_report_submissions_Description", "length(trim(\"Description\")) >= 10");
                table.HasCheckConstraint("CK_report_submissions_ReviewStatus", "\"ReviewStatus\" IN (0, 1, 2)");
                table.HasCheckConstraint("CK_report_submissions_Publication",
                    "(\"ReviewStatus\" = 1 AND \"PublishedReportId\" IS NOT NULL AND \"PublishedReportId\" = \"Id\")"
                    + " OR (\"ReviewStatus\" IN (0, 2) AND \"PublishedReportId\" IS NULL)");
            });
            entity.HasKey(submission => submission.Id);
            entity.Property(submission => submission.Title).HasMaxLength(100);
            entity.Property(submission => submission.Description).HasMaxLength(1000);
            entity.Property(submission => submission.Location).HasColumnType("geography (point, 4326)");
            entity.Property(submission => submission.ReviewStatus).HasDefaultValue(ReportReviewStatus.Pending);
            entity.HasIndex(submission => new { submission.ReviewStatus, submission.CreatedAt });
            entity.HasOne<IdentityUser<Guid>>()
                .WithMany()
                .HasForeignKey(submission => submission.AuthorId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasOne<ReportEntity>()
                .WithOne()
                .HasForeignKey<ReportSubmissionEntity>(submission => submission.PublishedReportId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        modelBuilder.Entity<ReportActionEntity>(entity =>
        {
            entity.ToTable("report_actions", table =>
                table.HasCheckConstraint("CK_report_actions_Description", "length(trim(\"Description\")) >= 10"));
            entity.HasKey(action => action.Id);
            entity.Property(action => action.Description).HasMaxLength(1000);
            entity.HasIndex(action => new { action.ReportId, action.CreatedAt });
            entity.HasOne<ReportEntity>()
                .WithMany()
                .HasForeignKey(action => action.ReportId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            entity.HasOne<IdentityUser<Guid>>()
                .WithMany()
                .HasForeignKey(action => action.AuthorId)
                .OnDelete(DeleteBehavior.SetNull);
        });
        modelBuilder.Entity<ReportActionConfirmationEntity>(entity =>
        {
            entity.ToTable("report_action_confirmations");
            entity.HasKey(confirmation => new { confirmation.ActionId, confirmation.AccountId });
            entity.HasOne<ReportActionEntity>()
                .WithMany()
                .HasForeignKey(confirmation => confirmation.ActionId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
            entity.HasOne<IdentityUser<Guid>>()
                .WithMany()
                .HasForeignKey(confirmation => confirmation.AccountId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired();
        });
        modelBuilder.Entity<ReportPhotoEntity>(entity =>
        {
            entity.ToTable("report_photos", table =>
            {
                table.HasCheckConstraint("CK_report_photos_Parent",
                    "(\"SubmissionId\" IS NOT NULL AND \"ActionId\" IS NULL)"
                    + " OR (\"SubmissionId\" IS NULL AND \"ActionId\" IS NOT NULL)");
                table.HasCheckConstraint("CK_report_photos_StorageName",
                    "\"StorageName\" ~ '^[0-9a-f]{32}\\.(jpg|png)$'");
                table.HasCheckConstraint("CK_report_photos_ContentType",
                    "(\"ContentType\" = 'image/jpeg' AND \"StorageName\" LIKE '%.jpg')"
                    + " OR (\"ContentType\" = 'image/png' AND \"StorageName\" LIKE '%.png')");
                table.HasCheckConstraint("CK_report_photos_Sha256", "\"Sha256\" ~ '^[0-9a-f]{64}$'");
                table.HasCheckConstraint("CK_report_photos_Size",
                    "\"ByteLength\" BETWEEN 1 AND 5242880 AND \"Width\" BETWEEN 1 AND 4096"
                    + " AND \"Height\" BETWEEN 1 AND 4096 AND \"Width\"::bigint * \"Height\" <= 16000000");
            });
            entity.HasKey(photo => photo.Id);
            entity.Property(photo => photo.StorageName).HasMaxLength(36);
            entity.Property(photo => photo.ContentType).HasMaxLength(10);
            entity.Property(photo => photo.Sha256).HasMaxLength(64);
            entity.HasIndex(photo => photo.StorageName).IsUnique();
            entity.HasIndex(photo => photo.SubmissionId).IsUnique();
            entity.HasIndex(photo => photo.ActionId).IsUnique();
            entity.HasOne<ReportSubmissionEntity>()
                .WithOne()
                .HasForeignKey<ReportPhotoEntity>(photo => photo.SubmissionId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ReportActionEntity>()
                .WithOne()
                .HasForeignKey<ReportPhotoEntity>(photo => photo.ActionId)
                .OnDelete(DeleteBehavior.Cascade);
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
        return await ReadAsync(db, db.Reports.AsNoTracking().OrderByDescending(report => report.CreatedAt)
            .Take(200), cancellationToken);
    }

    public async Task<Report?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var reports = await ReadAsync(db, db.Reports.AsNoTracking().Where(report => report.Id == id),
            cancellationToken);
        return reports.SingleOrDefault();
    }

    public async Task<Report> AddAsync(ReportDraft draft, CancellationToken cancellationToken = default)
    {
        if (!mode.LocalWrites || mode.PublicWrites || mode.WorkflowEnabled)
            throw new InvalidOperationException("Direkt rapportskrivning är endast tillåten lokalt.");
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

    private async Task<IReadOnlyList<Report>> ReadAsync(
        ReportDbContext db, IQueryable<ReportEntity> query, CancellationToken cancellationToken)
    {
        if (!mode.WorkflowEnabled)
        {
            var legacy = await query.ToArrayAsync(cancellationToken);
            return legacy.Select(report => new Report(report.Id, report.Title, report.Description,
                report.Category, report.Location.Y, report.Location.X, report.CreatedAt,
                report.Status)).ToArray();
        }

        var rows = await query.Select(report => new
        {
            report.Id,
            report.Title,
            report.Description,
            report.Category,
            report.Location,
            report.CreatedAt,
            HasAction = db.ReportActions.Any(action => action.ReportId == report.Id),
            HasConfirmation = db.ReportActions.Any(action => action.ReportId == report.Id
                && db.ReportActionConfirmations.Any(confirmation => confirmation.ActionId == action.Id))
        }).ToArrayAsync(cancellationToken);
        return rows.Select(report => new Report(report.Id, report.Title, report.Description,
            report.Category, report.Location.Y, report.Location.X, report.CreatedAt,
            report.HasConfirmation ? ReportStatus.Confirmed
                : report.HasAction ? ReportStatus.ActionDocumented : ReportStatus.Reported)).ToArray();
    }
}
