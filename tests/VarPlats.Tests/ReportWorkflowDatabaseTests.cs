using System.Security.Claims;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using VarPlats.Core;
using VarPlats.Web;
using VarPlats.Web.Data;
using Xunit;

namespace VarPlats.Tests;

public sealed class ReportWorkflowDatabaseTests
{
    [IsolatedPostgresFact]
    public async Task LegacyReadsReviewTransitionsAndBothAccountDeletionPaths()
    {
        var connectionString = Environment.GetEnvironmentVariable("VARPLATS_REPORT_TEST_DB")!;
        var connection = new NpgsqlConnectionStringBuilder(connectionString);
        if (connection.Host != "127.0.0.1" || connection.Database != "varplats_workflow_test")
            throw new InvalidOperationException("The test requires an isolated loopback database named varplats_workflow_test.");

        var options = new DbContextOptionsBuilder<ReportDbContext>()
            .UseNpgsql(connectionString, postgres => postgres.UseNetTopologySuite()).Options;
        var contexts = new TestContextFactory(options);
        await using var db = contexts.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE reports (
                "Id" uuid PRIMARY KEY,
                "Title" varchar(100) NOT NULL,
                "Description" varchar(1000) NOT NULL,
                "Category" integer NOT NULL,
                "Location" geography(Point,4326) NOT NULL,
                "CreatedAt" timestamptz NOT NULL,
                "Status" integer NOT NULL DEFAULT 0
            );
            INSERT INTO reports ("Id", "Title", "Description", "Category", "Location", "CreatedAt", "Status")
            VALUES ('00000000-0000-0000-0000-000000000001', 'Syntetisk äldre rapport',
                'Ett påhittat exempel utan personuppgifter.', 0,
                ST_GeogFromText('SRID=4326;POINT(18.0686 59.3293)'), '2026-11-01T10:00:00Z', 2);
            """);

        var publicStore = new DatabaseReportStore(contexts, new ReportingMode(false, false, false));
        var legacy = Assert.Single(await publicStore.ListAsync());
        Assert.Equal(59.3293, legacy.Latitude, 6);
        Assert.Equal(18.0686, legacy.Longitude, 6);
        Assert.Equal(ReportStatus.Confirmed, legacy.Status);
        Assert.Equal(ReportStatus.Confirmed, (await publicStore.GetAsync(legacy.Id))!.Status);
        var localStore = new DatabaseReportStore(contexts, new ReportingMode(false, true, false));
        var localReport = await localStore.AddAsync(ReportTests.ValidDraft());
        Assert.Contains(await publicStore.ListAsync(), item => item.Id == localReport.Id);
        Assert.True(await localStore.RemoveAsync(localReport.Id));

        await using var web = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:varplats", connectionString);
            builder.UseSetting("Reporting:Demo", "false");
            builder.UseSetting("Reporting:LocalWrites", "false");
            builder.UseSetting("Reporting:PublicWrites", "false");
            builder.UseSetting("Reporting:WorkflowEnabled", "false");
            builder.UseSetting("Authentication:Enabled", "false");
        });
        using var client = web.CreateClient();
        Assert.Contains("Syntetisk äldre rapport", await client.GetStringAsync("/api/reports"));
        Assert.Contains("endast läsning", await client.GetStringAsync("/"));
        Assert.Contains("Syntetisk äldre rapport", WebUtility.HtmlDecode(
            await client.GetStringAsync($"/rapporter/{legacy.Id}")));

        await db.GetService<IMigrator>().MigrateAsync("20261108035738_IdentityAccountActivity");
        Assert.Single(await publicStore.ListAsync());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication();
        services.AddDbContext<ReportDbContext>(configuration =>
            configuration.UseNpgsql(connectionString, postgres => postgres.UseNetTopologySuite()));
        services.AddIdentityCore<IdentityUser<Guid>>(options => options.SignIn.RequireConfirmedEmail = true)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<ReportDbContext>()
            .AddSignInManager();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
        var identityDb = scope.ServiceProvider.GetRequiredService<ReportDbContext>();
        var oldDate = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var previewUser = new IdentityUser<Guid> { Id = Guid.NewGuid(), UserName = "synthetic-preview" };
        var previewInactive = new IdentityUser<Guid> { Id = Guid.NewGuid(), UserName = "synthetic-inactive-preview" };
        Assert.True((await userManager.CreateAsync(previewUser)).Succeeded);
        Assert.True((await userManager.CreateAsync(previewInactive)).Succeeded);
        await DeleteAccountViaEndpoint(connectionString, previewUser.Id, legacy.Id);
        await identityDb.IdentityAccountActivities.Where(activity => activity.UserId == previewInactive.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(activity => activity.CreatedAtUtc, oldDate));
        var retention = new IdentityAccountActivityStore(identityDb);
        Assert.Equal(1, await retention.DeleteInactiveAccountsAsync(
            userManager, new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero)));
        await CheckCurrentAccountAccess(provider);

        await db.GetService<IMigrator>().MigrateAsync("20261108154722_ReportReviewWorkflow");
        Assert.Single(await publicStore.ListAsync());
        Assert.Contains("Syntetisk äldre rapport", WebUtility.HtmlDecode(
            await client.GetStringAsync($"/rapporter/{legacy.Id}")));
        await db.GetService<IMigrator>().MigrateAsync("20261108162158_ReportActionsAndConfirmations");
        Assert.Single(await publicStore.ListAsync());
        var firstUser = new IdentityUser<Guid> { Id = Guid.NewGuid(), UserName = "synthetic-first" };
        var secondUser = new IdentityUser<Guid> { Id = Guid.NewGuid(), UserName = "synthetic-second" };
        Assert.True((await userManager.CreateAsync(firstUser)).Succeeded);
        Assert.True((await userManager.CreateAsync(secondUser)).Succeeded);

        var mode = new ReportingMode(false, false, true, true);
        var workflow = new DatabaseReportWorkflowStore(contexts, mode);
        var commands = new ReportCommands(new DatabaseReportStore(contexts, mode), mode, workflow,
            accountAccess: SyntheticReportAccountAccess.Instance);
        var firstMember = Account(firstUser.Id, "Member");
        var secondMember = Account(secondUser.Id, "Member");
        var admin = Account(Guid.NewGuid(), "Admin");
        var approvedDraft = ReportTests.ValidDraft();
        approvedDraft.Title = "Syntetiskt godkänd rapport";
        var rejectedDraft = ReportTests.ValidDraft();
        rejectedDraft.Title = "Syntetiskt avvisad rapport";
        var pendingDraft = ReportTests.ValidDraft();
        pendingDraft.Title = "Syntetiskt väntande rapport";
        var approved = await commands.AddAsync(firstMember, approvedDraft);
        var rejected = await commands.AddAsync(secondMember, rejectedDraft);
        var pending = await commands.AddAsync(secondMember, pendingDraft);

        Assert.Single(await publicStore.ListAsync());
        Assert.Equal(3, (await commands.ListPendingAsync(admin)).Count);
        Assert.Single(await commands.ListOwnAsync(firstMember));
        Assert.Equal(2, (await commands.ListOwnAsync(secondMember)).Count);
        var beforeReview = await client.GetStringAsync("/api/reports");
        Assert.DoesNotContain(approvedDraft.Title, beforeReview);
        Assert.DoesNotContain(rejectedDraft.Title, beforeReview);
        Assert.DoesNotContain(pendingDraft.Title, beforeReview);

        Assert.True(await commands.DecideAsync(admin, approved.Id, ReportReviewStatus.Approved));
        Assert.True(await commands.DecideAsync(admin, rejected.Id, ReportReviewStatus.Rejected));
        Assert.False(await commands.DecideAsync(admin, approved.Id, ReportReviewStatus.Rejected));
        Assert.False(await commands.DecideAsync(admin, rejected.Id, ReportReviewStatus.Approved));
        var visible = await publicStore.ListAsync();
        var published = Assert.Single(visible, report => report.Id == approved.Id);
        Assert.Equal(approved.Latitude, published.Latitude, 6);
        Assert.Equal(approved.Longitude, published.Longitude, 6);
        Assert.DoesNotContain(visible, report => report.Id == rejected.Id || report.Id == pending.Id);
        var json = await client.GetStringAsync("/api/reports");
        Assert.Contains(approvedDraft.Title, json);
        Assert.DoesNotContain(rejectedDraft.Title, json);
        Assert.DoesNotContain(pendingDraft.Title, json);
        Assert.DoesNotContain(firstUser.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secondUser.Id.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorId", json, StringComparison.OrdinalIgnoreCase);

        var contestedDraft = ReportTests.ValidDraft();
        contestedDraft.Title = "Syntetiskt samtidigt beslut";
        var contested = await commands.AddAsync(firstMember, contestedDraft);
        var decisions = await Task.WhenAll(
                commands.DecideAsync(admin, contested.Id, ReportReviewStatus.Approved),
                commands.DecideAsync(admin, contested.Id, ReportReviewStatus.Rejected))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(decisions, changed => changed);
        var finalStatus = Assert.Single(await commands.ListOwnAsync(firstMember),
            item => item.Report.Id == contested.Id).ReviewStatus;
        Assert.Contains(finalStatus, new[] { ReportReviewStatus.Approved, ReportReviewStatus.Rejected });
        Assert.Equal(finalStatus == ReportReviewStatus.Approved,
            (await publicStore.ListAsync()).Any(item => item.Id == contested.Id));

        var (secondAuthorActionId, firstAuthorActionId) = await CheckActionRulesAndConstraints(
            contexts, approved.Id, rejected.Id, pending.Id,
            firstMember, secondMember, admin, firstUser.Id, secondUser.Id);
        var enabledStore = new DatabaseReportStore(contexts, mode);

        await db.GetService<IMigrator>().MigrateAsync("20261108164953_ReportPhotos");
        using var photoScenario = new PhotoWorkflowDatabaseScenario(contexts, firstMember, secondMember, admin);
        await photoScenario.BeforeAccountDeletionAsync();

        await DeleteAccountViaEndpoint(connectionString, firstUser.Id, legacy.Id);
        await using (var check = contexts.CreateDbContext())
        {
            var retained = await check.ReportSubmissions.AsNoTracking().SingleAsync(item => item.Id == approved.Id);
            Assert.Null(retained.AuthorId);
            Assert.Equal(ReportReviewStatus.Approved, retained.ReviewStatus);
            Assert.Null((await check.ReportActions.AsNoTracking().SingleAsync(item => item.Id == firstAuthorActionId)).AuthorId);
            Assert.False(await check.ReportActionConfirmations.AnyAsync(item => item.AccountId == firstUser.Id));
            Assert.True(await check.ReportActionConfirmations.AnyAsync(item =>
                item.ActionId == firstAuthorActionId && item.AccountId == secondUser.Id));
        }
        Assert.Contains(await publicStore.ListAsync(), item => item.Id == approved.Id);
        Assert.Equal(ReportStatus.Confirmed, (await enabledStore.GetAsync(approved.Id))!.Status);
        await photoScenario.AfterAccountDeletionAndRemovalAsync();

        await identityDb.IdentityAccountActivities.Where(activity => activity.UserId == secondUser.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(activity => activity.CreatedAtUtc, oldDate));
        Assert.Equal(1, await retention.DeleteInactiveAccountsAsync(
            userManager, new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero)));
        await using (var check = contexts.CreateDbContext())
        {
            var retained = await check.ReportSubmissions.AsNoTracking()
                .Where(item => item.Id == rejected.Id || item.Id == pending.Id).ToArrayAsync();
            Assert.Equal(2, retained.Length);
            Assert.All(retained, item => Assert.Null(item.AuthorId));
            Assert.Null((await check.ReportActions.AsNoTracking().SingleAsync(item => item.Id == secondAuthorActionId)).AuthorId);
            Assert.False(await check.ReportActionConfirmations.AnyAsync(item => item.AccountId == secondUser.Id));
        }
        Assert.Equal(ReportStatus.ActionDocumented, (await enabledStore.GetAsync(approved.Id))!.Status);
        Assert.Equal(ReportStatus.ActionDocumented, Assert.Single(await enabledStore.ListAsync(),
            item => item.Id == approved.Id).Status);
        Assert.Equal(pending.Id, Assert.Single(await commands.ListPendingAsync(admin)).Report.Id);
        Assert.True(await commands.DecideAsync(admin, pending.Id, ReportReviewStatus.Approved));
        Assert.Contains(await publicStore.ListAsync(), item => item.Id == pending.Id);

        var actionCommands = new ReportActionCommands(mode, new DatabaseReportActionStore(contexts, mode),
            accountAccess: SyntheticReportAccountAccess.Instance);
        Assert.True(await actionCommands.RemoveAsync(admin, approved.Id, firstAuthorActionId));
        Assert.Equal(ReportStatus.ActionDocumented, (await enabledStore.GetAsync(approved.Id))!.Status);
        Assert.True(await actionCommands.RemoveAsync(admin, approved.Id, secondAuthorActionId));
        Assert.Equal(ReportStatus.Reported, (await enabledStore.GetAsync(approved.Id))!.Status);

        var finalUser = new IdentityUser<Guid> { Id = Guid.NewGuid(), UserName = "synthetic-final" };
        Assert.True((await userManager.CreateAsync(finalUser)).Succeeded);
        Assert.True(await actionCommands.AddAsync(Account(finalUser.Id, "Member"), approved.Id,
            new ReportActionDraft { Description = "Syntetisk åtgärd för borttagningstest." }));
        var lastAction = Assert.Single(await actionCommands.ListAsync(admin, approved.Id));
        Assert.True(await commands.RemoveAsync(admin, approved.Id));
        await using (var check = contexts.CreateDbContext())
        {
            Assert.False(await check.ReportSubmissions.AnyAsync(item => item.Id == approved.Id));
            Assert.False(await check.ReportActions.AnyAsync(item => item.Id == lastAction.Id));
        }
    }

    private static async Task CheckCurrentAccountAccess(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<IdentityUser<Guid>>>();
        Assert.NotNull(await roles.FindByNameAsync("Admin"));
        var account = new IdentityUser<Guid>
        {
            Id = Guid.NewGuid(),
            UserName = "synthetic-current-admin",
            EmailConfirmed = true
        };
        Assert.True((await users.CreateAsync(account)).Succeeded);
        Assert.True((await users.AddToRoleAsync(account, "Admin")).Succeeded);
        var principal = await signIn.CreateUserPrincipalAsync(account);
        var access = new IdentityReportAccountAccess(provider.GetRequiredService<IServiceScopeFactory>());
        var store = new ReportWorkflowTestStore();
        var commands = new ReportCommands(store, new ReportingMode(false, false, true, true), store,
            accountAccess: access);
        var pending = await commands.AddAsync(principal, ReportTests.ValidDraft());
        Assert.Single(await commands.ListPendingAsync(principal));

        using (var change = provider.CreateScope())
        {
            var otherUsers = change.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
            var current = (await otherUsers.FindByIdAsync(account.Id.ToString()))!;
            Assert.True((await otherUsers.RemoveFromRoleAsync(current, "Admin")).Succeeded);
        }
        Assert.False((await access.VerifyAsync(principal)).CanModerate);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.ListPendingAsync(principal));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.DecideAsync(principal, pending.Id, ReportReviewStatus.Approved));

        ClaimsPrincipal refreshed;
        using (var change = provider.CreateScope())
        {
            var otherUsers = change.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
            var current = (await otherUsers.FindByIdAsync(account.Id.ToString()))!;
            Assert.True((await otherUsers.AddToRoleAsync(current, "Admin")).Succeeded);
            Assert.True((await otherUsers.UpdateSecurityStampAsync(current)).Succeeded);
            refreshed = await change.ServiceProvider.GetRequiredService<SignInManager<IdentityUser<Guid>>>()
                .CreateUserPrincipalAsync(current);
        }
        Assert.False((await access.VerifyAsync(principal)).CanReport);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.AddAsync(principal, ReportTests.ValidDraft()));
        Assert.True((await access.VerifyAsync(refreshed)).CanModerate);

        using (var change = provider.CreateScope())
        {
            var otherUsers = change.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
            var current = (await otherUsers.FindByIdAsync(account.Id.ToString()))!;
            Assert.True((await otherUsers.SetLockoutEnabledAsync(current, true)).Succeeded);
            Assert.True((await otherUsers.SetLockoutEndDateAsync(current, DateTimeOffset.UtcNow.AddMinutes(5))).Succeeded);
        }
        Assert.False((await access.VerifyAsync(refreshed)).CanReport);
        using (var change = provider.CreateScope())
        {
            var otherUsers = change.ServiceProvider.GetRequiredService<UserManager<IdentityUser<Guid>>>();
            var current = (await otherUsers.FindByIdAsync(account.Id.ToString()))!;
            Assert.True((await otherUsers.DeleteAsync(current)).Succeeded);
        }
        Assert.False((await access.VerifyAsync(refreshed)).CanModerate);
    }

    private static async Task<(Guid SecondAuthorActionId, Guid FirstAuthorActionId)> CheckActionRulesAndConstraints(
        TestContextFactory contexts, Guid reportId, Guid rejectedId, Guid pendingId,
        ClaimsPrincipal firstMember, ClaimsPrincipal secondMember, ClaimsPrincipal admin,
        Guid firstUserId, Guid secondUserId)
    {
        var mode = new ReportingMode(false, false, true, true);
        var actions = new ReportActionCommands(mode, new DatabaseReportActionStore(contexts, mode),
            accountAccess: SyntheticReportAccountAccess.Instance);
        var reports = new DatabaseReportStore(contexts, mode);
        var draft = new ReportActionDraft { Description = "Syntetisk åtgärd utan personuppgifter." };
        Assert.False(await actions.AddAsync(firstMember, pendingId, draft));
        Assert.False(await actions.AddAsync(firstMember, rejectedId, draft));
        Assert.True(await actions.AddAsync(secondMember, reportId, draft));
        var temporary = Assert.Single(await actions.ListAsync(firstMember, reportId));
        Assert.False(temporary.IsOwnAction);
        Assert.True(Assert.Single(await actions.ListAsync(secondMember, reportId)).IsOwnAction);
        Assert.Equal(ReportStatus.ActionDocumented, (await reports.GetAsync(reportId))!.Status);
        Assert.Equal(ActionConfirmationResult.OwnAction, await actions.ConfirmAsync(secondMember, reportId, temporary.Id));
        Assert.Equal(ActionConfirmationResult.NotFound, await actions.ConfirmAsync(firstMember, pendingId, temporary.Id));
        Assert.Equal(ActionConfirmationResult.Confirmed, await actions.ConfirmAsync(firstMember, reportId, temporary.Id));
        Assert.True(Assert.Single(await actions.ListAsync(firstMember, reportId)).HasConfirmed);
        Assert.Equal(ReportStatus.Confirmed, (await reports.GetAsync(reportId))!.Status);
        Assert.True(await actions.RemoveAsync(admin, reportId, temporary.Id));
        Assert.Equal(ReportStatus.Reported, (await reports.GetAsync(reportId))!.Status);
        await using (var check = contexts.CreateDbContext())
            Assert.False(await check.ReportActionConfirmations.AnyAsync(item => item.ActionId == temporary.Id));

        Assert.True(await actions.AddAsync(secondMember, reportId,
            new ReportActionDraft { Description = "Syntetisk åtgärd av andra kontot." }));
        var secondAuthorAction = Assert.Single(await actions.ListAsync(firstMember, reportId));
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => actions.ConfirmAsync(firstMember, reportId, secondAuthorAction.Id)))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Single(results, result => result == ActionConfirmationResult.Confirmed);
        Assert.Equal(7, results.Count(result => result == ActionConfirmationResult.AlreadyConfirmed));
        Assert.Equal(ReportStatus.Confirmed, (await reports.GetAsync(reportId))!.Status);
        Assert.True(await actions.AddAsync(firstMember, reportId,
            new ReportActionDraft { Description = "Syntetisk åtgärd av första kontot." }));
        var firstAuthorAction = Assert.Single(await actions.ListAsync(secondMember, reportId),
            item => item.Description == "Syntetisk åtgärd av första kontot.");
        Assert.Equal(ActionConfirmationResult.Confirmed,
            await actions.ConfirmAsync(secondMember, reportId, firstAuthorAction.Id));

        var publicActions = JsonSerializer.Serialize(await actions.ListAsync(new ClaimsPrincipal(), reportId));
        Assert.DoesNotContain(firstUserId.ToString(), publicActions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secondUserId.ToString(), publicActions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AuthorId", publicActions, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AccountId", publicActions, StringComparison.OrdinalIgnoreCase);
        await ExpectConstraintFailure(contexts, db => db.ReportActionConfirmations.Add(new()
        {
            ActionId = secondAuthorAction.Id,
            AccountId = firstUserId,
            CreatedAt = DateTimeOffset.UtcNow
        }), PostgresErrorCodes.UniqueViolation);
        await ExpectConstraintFailure(contexts, db => db.ReportActions.Add(new()
        {
            Id = Guid.NewGuid(),
            ReportId = Guid.NewGuid(),
            AuthorId = firstUserId,
            Description = "Syntetisk åtgärd för FK-test.",
            CreatedAt = DateTimeOffset.UtcNow
        }), PostgresErrorCodes.ForeignKeyViolation);
        await ExpectConstraintFailure(contexts, db => db.ReportActions.Add(new()
        {
            Id = Guid.NewGuid(),
            ReportId = reportId,
            AuthorId = Guid.NewGuid(),
            Description = "Syntetisk åtgärd för FK-test.",
            CreatedAt = DateTimeOffset.UtcNow
        }), PostgresErrorCodes.ForeignKeyViolation);
        await ExpectConstraintFailure(contexts, db => db.ReportActionConfirmations.Add(new()
        {
            ActionId = secondAuthorAction.Id,
            AccountId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        }), PostgresErrorCodes.ForeignKeyViolation);
        await ExpectConstraintFailure(contexts, db => db.ReportActionConfirmations.Add(new()
        {
            ActionId = Guid.NewGuid(),
            AccountId = firstUserId,
            CreatedAt = DateTimeOffset.UtcNow
        }), PostgresErrorCodes.ForeignKeyViolation);
        return (secondAuthorAction.Id, firstAuthorAction.Id);
    }

    private static async Task ExpectConstraintFailure(
        TestContextFactory contexts, Action<ReportDbContext> insert, string expectedSqlState)
    {
        await using var db = contexts.CreateDbContext();
        insert(db);
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(expectedSqlState, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
    }

    private static ClaimsPrincipal Account(Guid id, string role) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, role)], "test"));

    private static async Task DeleteAccountViaEndpoint(string connectionString, Guid userId, Guid legacyReportId)
    {
        await using var web = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:varplats", connectionString);
            builder.UseSetting("Authentication:Enabled", "true");
            builder.UseSetting("Authentication:Google:ClientId", "synthetic");
            builder.UseSetting("Authentication:Google:ClientSecret", "synthetic");
            builder.UseSetting("Authentication:GitHub:ClientId", "synthetic");
            builder.UseSetting("Authentication:GitHub:ClientSecret", "synthetic");
            builder.UseSetting("Authentication:AdminGitHubId", "1");
            builder.UseSetting("Authentication:TrustedProxyIp", "127.0.0.1");
            builder.UseSetting("Authentication:Mailjet:ApiKey", "synthetic");
            builder.UseSetting("Authentication:Mailjet:ApiSecret", "synthetic");
            builder.UseSetting("Authentication:Mailjet:SenderEmail", "synthetic@example.test");
            builder.UseSetting("Authentication:Passkeys:ServerDomain", "example.test");
            builder.UseSetting("Authentication:Passkeys:Origins:0", "https://example.test");
            builder.UseSetting("Authentication:PublicBaseUrl", "https://example.test");
            builder.ConfigureServices(services => services.AddSyntheticTestAuthentication());
        });
        using var client = web.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        client.DefaultRequestHeaders.Add("X-Synthetic-Role", "Member");
        client.DefaultRequestHeaders.Add("X-Synthetic-Id", userId.ToString());
        var page = await client.GetStringAsync("/");
        Assert.Contains("Syntetisk äldre rapport", WebUtility.HtmlDecode(page));
        Assert.Contains("endast läsning", WebUtility.HtmlDecode(page));
        var details = WebUtility.HtmlDecode(await client.GetStringAsync($"/rapporter/{legacyReportId}"));
        Assert.Contains("Syntetisk äldre rapport", details);
        Assert.Contains("Åtgärdsuppgifter är dolda", details);
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")
            .Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(token));
        var response = await client.PostAsync("/auth/account/delete",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login?deleted=1", response.Headers.Location?.OriginalString);
    }

    private sealed class TestContextFactory(DbContextOptions<ReportDbContext> options) : IDbContextFactory<ReportDbContext>
    {
        public ReportDbContext CreateDbContext() => new(options);
    }
}

public sealed class IsolatedPostgresFactAttribute : FactAttribute
{
    public IsolatedPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("VARPLATS_REPORT_TEST_DB")))
            Skip = "Requires an empty, isolated loopback PostgreSQL/PostGIS test database.";
    }
}
