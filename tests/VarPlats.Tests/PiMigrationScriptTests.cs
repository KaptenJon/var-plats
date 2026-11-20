using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VarPlats.Web.Data;
using Xunit;

namespace VarPlats.Tests;

public sealed class PiMigrationScriptTests
{
    // The Pi's Identity schema comes from deploy/identity-schema.sql, which ends at this migration.
    private const string PiBaseMigration = "20261108035738_IdentityAccountActivity";
    private const string LatestReleasedMigration = "20261120080000_ConfirmationWithdrawals";

    [Fact]
    public void CommittedPiMigrationScriptMatchesEfMigrations()
    {
        using var db = new ReportDbContext(new DbContextOptionsBuilder<ReportDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=unused", postgres => postgres.UseNetTopologySuite()).Options);
        var migrations = db.Database.GetMigrations().ToList();
        Assert.Equal(LatestReleasedMigration, migrations[^1]);

        var generated = db.GetService<IMigrator>()
            .GenerateScript(PiBaseMigration, LatestReleasedMigration, MigrationsSqlGenerationOptions.Idempotent);
        var path = Path.Combine(FindRepositoryRoot(), "deploy", "pi-workflow-migrations.sql");
        var committed = File.Exists(path) ? File.ReadAllText(path) : "";
        if (Normalize(committed) != Normalize(generated))
        {
            var output = Path.Combine(Path.GetTempPath(), "pi-workflow-migrations.generated.sql");
            File.WriteAllText(output, generated);
            Assert.Fail($"deploy/pi-workflow-migrations.sql matchar inte EF-migreringarna. Granska och kopiera {output}.");
        }
    }

    [Fact]
    public void PiGrantScriptCoversEveryWorkflowTable()
    {
        var grants = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "deploy", "pi-workflow-grants.sql"));
        foreach (var table in new[]
        {
            "report_submissions", "report_actions", "report_action_confirmations", "report_photos",
            "content_flags", "report_archives", "report_archive_events",
            "report_action_confirmation_withdrawals"
        })
            Assert.Contains(table, grants, StringComparison.Ordinal);
        Assert.DoesNotContain("GRANT ALL", grants, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TRUNCATE", grants, StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string sql) => sql.TrimStart('﻿').Replace("\r\n", "\n").Trim();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "VarPlats.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("VarPlats.slnx hittades inte.");
    }
}
