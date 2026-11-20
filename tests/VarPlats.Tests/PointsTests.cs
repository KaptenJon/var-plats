using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using VarPlats.Core;
using VarPlats.Web;
using VarPlats.Web.Data;
using Xunit;

namespace VarPlats.Tests;

public sealed class PointsTests
{
    private static readonly ReportingMode Enabled = new(false, false, true, true, false, false, true);
    private static readonly ReportingMode ReadOnly = new(false, false, false, true, false, false, true);
    private static readonly ReportingMode SwitchedOff = new(false, false, true, true);

    [Fact]
    public void PointsNeedTheirOwnSwitchTheWorkflowAndPublicWritesToChange()
    {
        Assert.False(SwitchedOff.Points);
        Assert.False(new ReportingMode(false, false, false, false, false, false, true).Points);
        Assert.False(new ReportingMode(true, false, false, true, false, false, true).Points);
        Assert.False(new ReportingMode(false, true, false, true, false, false, true).Points);
        Assert.True(ReadOnly.Points);
        Assert.False(ReadOnly.CanUsePoints);
        Assert.True(Enabled.CanUsePoints);
    }

    [Fact]
    public void OptionsRejectUnsafeLimitsAndRuleVersions()
    {
        new PointsOptions().Validate();
        Assert.Throws<InvalidOperationException>(() => new PointsOptions { ConfirmedActionPoints = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PointsOptions { MaxGiftPerTransfer = 0 }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new PointsOptions { MaxGiftPerTransfer = 20, MaxGiftPerDay = 10 }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PointsOptions { RuleVersion = " " }.Validate());
        Assert.Throws<InvalidOperationException>(() => new PointsOptions { RuleVersion = " v2" }.Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new PointsOptions { RuleVersion = new string('v', PointsOptions.RuleVersionMaxLength + 1) }.Validate());
    }

    [Fact]
    public void GiftDraftEnforcesThePerGiftLimit()
    {
        var options = new PointsOptions { MaxGiftPerTransfer = 5, MaxGiftPerDay = 10 };
        Assert.Equal(5, new PointsGiftDraft { Amount = 5 }.Validated(options));
        Assert.Throws<ValidationException>(() => new PointsGiftDraft { Amount = 6 }.Validated(options));
        Assert.Throws<ValidationException>(() => new PointsGiftDraft { Amount = 0 }.Validated(options));
        Assert.Throws<ValidationException>(() => new PointsGiftDraft { Amount = -3 }.Validated(options));
    }

    [Fact]
    public async Task SwitchedOffPointsNeverReachTheStore()
    {
        var store = new RecordingPointsStore();
        var commands = new PointsCommands(SwitchedOff, new PointsOptions(), store, SyntheticReportAccountAccess.Instance);
        Assert.Empty(await commands.ListForActionsAsync(Account("Member"), [Guid.NewGuid()]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.GetOwnAsync(Account("Member")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commands.ToggleHeartAsync(Account("Member"), Guid.NewGuid(), Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commands.GiftAsync(Account("Member"), Guid.NewGuid(), Guid.NewGuid(), new PointsGiftDraft()));
        Assert.False(commands.GiftsEnabled);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task ReadOnlyModeShowsPointsButRefusesHeartsAndGifts()
    {
        var store = new RecordingPointsStore();
        var commands = new PointsCommands(ReadOnly, new PointsOptions(), store, SyntheticReportAccountAccess.Instance);
        await commands.ListForActionsAsync(Account("Member"), [Guid.NewGuid()]);
        await commands.GetOwnAsync(Account("Member"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commands.ToggleHeartAsync(Account("Member"), Guid.NewGuid(), Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commands.GiftAsync(Account("Member"), Guid.NewGuid(), Guid.NewGuid(), new PointsGiftDraft()));
        Assert.Empty(store.Hearts);
        Assert.Empty(store.Gifts);
    }

    [Fact]
    public async Task OnlyVerifiedAccountsGiveHeartsAndGiftsAndSeeTheirBalance()
    {
        var store = new RecordingPointsStore();
        var commands = new PointsCommands(Enabled, new PointsOptions(), store, SyntheticReportAccountAccess.Instance);
        var unverified = new PointsCommands(Enabled, new PointsOptions(), store);
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.ToggleHeartAsync(anonymous, Guid.NewGuid(), Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.ToggleHeartAsync(Account("Member", recovery: true), Guid.NewGuid(), Guid.NewGuid()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            unverified.GiftAsync(Account("Member"), Guid.NewGuid(), Guid.NewGuid(), new PointsGiftDraft()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.GetOwnAsync(anonymous));
        Assert.Empty(store.Hearts);
        Assert.Empty(store.Gifts);

        var member = Account("Member");
        var memberId = Guid.Parse(member.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var actionId = Guid.NewGuid();
        Assert.Equal(HeartResult.Added, await commands.ToggleHeartAsync(member, Guid.NewGuid(), actionId));
        Assert.Equal(GiftResult.Sent, await commands.GiftAsync(member, Guid.NewGuid(), actionId,
            new PointsGiftDraft { Amount = 3 }));
        Assert.Equal((actionId, memberId), Assert.Single(store.Hearts));
        Assert.Equal((actionId, memberId, 3), Assert.Single(store.Gifts));
        Assert.Equal(memberId, (await commands.GetOwnAsync(member)).History.Single().Id);
    }

    [Fact]
    public async Task ViewerIdIsOnlyPassedForVerifiedAccounts()
    {
        var store = new RecordingPointsStore();
        var commands = new PointsCommands(Enabled, new PointsOptions(), store, SyntheticReportAccountAccess.Instance);
        await commands.ListForActionsAsync(new ClaimsPrincipal(new ClaimsIdentity()), [Guid.NewGuid()]);
        await commands.ListForActionsAsync(Account("Member", recovery: true), [Guid.NewGuid()]);
        Assert.All(store.Viewers, viewer => Assert.Null(viewer));
        var member = Account("Member");
        await commands.ListForActionsAsync(member, [Guid.NewGuid()]);
        Assert.Equal(Guid.Parse(member.FindFirstValue(ClaimTypes.NameIdentifier)!), store.Viewers[^1]);
    }

    [Fact]
    public async Task GiftsOverTheLimitOrWithGiftsOffAreRefusedBeforeTheStore()
    {
        var store = new RecordingPointsStore();
        var options = new PointsOptions { MaxGiftPerTransfer = 5, MaxGiftPerDay = 10 };
        var commands = new PointsCommands(Enabled, options, store, SyntheticReportAccountAccess.Instance);
        await Assert.ThrowsAsync<ValidationException>(() => commands.GiftAsync(
            Account("Member"), Guid.NewGuid(), Guid.NewGuid(), new PointsGiftDraft { Amount = 6 }));
        var giftsOff = new PointsCommands(Enabled, new PointsOptions { GiftsEnabled = false }, store,
            SyntheticReportAccountAccess.Instance);
        Assert.False(giftsOff.GiftsEnabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() => giftsOff.GiftAsync(
            Account("Member"), Guid.NewGuid(), Guid.NewGuid(), new PointsGiftDraft()));
        Assert.Empty(store.Gifts);
    }

    [Fact]
    public async Task PointsDatabaseStoreRejectsDisabledModesBeforeConnecting()
    {
        var options = new DbContextOptionsBuilder<ReportDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", postgres => postgres.UseNetTopologySuite()).Options;
        var off = new DatabasePointsStore(new TestContextFactory(options), SwitchedOff, new PointsOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(() => off.ListForActionsAsync([Guid.NewGuid()], null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => off.GetSummaryAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            off.ToggleHeartAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            off.ReverseActionAsync(Guid.NewGuid(), PointsReversalCause.AdminRevoked));
        var readOnly = new DatabasePointsStore(new TestContextFactory(options), ReadOnly, new PointsOptions());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            readOnly.GiftAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            readOnly.ReverseConfirmationAsync(Guid.NewGuid(), Guid.NewGuid(), PointsReversalCause.ConfirmationWithdrawn));
        var giftsOff = new DatabasePointsStore(new TestContextFactory(options), Enabled,
            new PointsOptions { GiftsEnabled = false });
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            giftsOff.GiftAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1));
        // Points on without options is a wiring error and fails loudly instead of skipping the award.
        var actions = new DatabaseReportActionStore(new TestContextFactory(options), Enabled);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actions.ConfirmAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => actions.RemoveAsync(Guid.NewGuid(), Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new DatabaseReportStore(new TestContextFactory(options), Enabled).RemoveAsync(Guid.NewGuid()));
    }

    [Fact]
    public void LedgerModelAndMigrationAreAppendOnlyAndAnonymousToOthers()
    {
        using var db = new ReportDbContext(new DbContextOptionsBuilder<ReportDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=unused", postgres => postgres.UseNetTopologySuite()).Options);
        var ledger = db.Model.FindEntityType(typeof(PointsEventEntity))!;
        Assert.Equal("points_events", ledger.GetTableName());
        // No foreign key to actions or reports: the history survives their removal.
        Assert.DoesNotContain(ledger.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(ReportActionEntity));
        Assert.Contains(ledger.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(IdentityUser<Guid>)
            && key.Properties.Single().Name == nameof(PointsEventEntity.AccountId) && key.DeleteBehavior == DeleteBehavior.Cascade);
        Assert.Contains(ledger.GetForeignKeys(), key => key.PrincipalEntityType.ClrType == typeof(IdentityUser<Guid>)
            && key.Properties.Single().Name == nameof(PointsEventEntity.ConfirmerId) && key.DeleteBehavior == DeleteBehavior.SetNull);
        Assert.Contains(ledger.GetIndexes(), index => index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(["ReversesEventId"]));
        Assert.Contains(ledger.GetIndexes(), index => index.IsUnique && index.GetFilter() == "\"Reason\" = 0"
            && index.Properties.Select(property => property.Name).SequenceEqual(["ActionId", "ConfirmerId"]));
        var hearts = db.Model.FindEntityType(typeof(ActionHeartEntity))!;
        Assert.Equal(["ActionId", "AccountId"], hearts.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.False(db.Database.HasPendingModelChanges());

        var script = db.GetService<IMigrator>().GenerateScript("20261119222822_ReportArchive", "20261120090000_Points");
        Assert.Contains("CREATE TABLE points_events", script);
        Assert.Contains("CREATE TABLE action_hearts", script);
        Assert.Contains("CK_points_events_Shape", script);
        Assert.DoesNotContain("ALTER TABLE report_actions", script);
        Assert.DoesNotContain("ALTER TABLE reports", script);
    }

    [Fact]
    public void TextsAreSwedishAndExplainResults()
    {
        Assert.Equal("Poäng ges aldrig för farliga insatser – ta inga risker.", PointsText.SafetyNote);
        Assert.Equal("1 hjärta", PointsText.Hearts(1));
        Assert.Equal("3 hjärtan", PointsText.Hearts(3));
        Assert.Contains("ger inga poäng", PointsText.Heart(HeartResult.Added));
        Assert.Contains("räcker inte", PointsText.Gift(GiftResult.InsufficientBalance, 5));
        Assert.Contains("bekräftelsen togs tillbaka",
            PointsText.Reason(PointsReason.Reversal, PointsReversalCause.ConfirmationWithdrawn));
        foreach (var result in Enum.GetValues<GiftResult>())
            Assert.False(string.IsNullOrWhiteSpace(PointsText.Gift(result, 1)));
    }

    private static ClaimsPrincipal Account(string role, bool recovery = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Role, role)
        };
        if (recovery) claims.Add(new Claim("recovery_only", "true"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}

internal sealed class TestContextFactory(DbContextOptions<ReportDbContext> options) : IDbContextFactory<ReportDbContext>
{
    public ReportDbContext CreateDbContext() => new(options);
}

/// <summary>In-memory points store for command and page tests; it does not implement the ledger rules.</summary>
internal sealed class RecordingPointsStore : IPointsStore
{
    public int Calls { get; private set; }
    public List<(Guid ActionId, Guid AccountId)> Hearts { get; } = [];
    public List<(Guid ActionId, Guid GiverId, int Amount)> Gifts { get; } = [];
    public List<Guid?> Viewers { get; } = [];

    public Task<IReadOnlyDictionary<Guid, ActionAppreciation>> ListForActionsAsync(
        IReadOnlyCollection<Guid> actionIds, Guid? viewerId, CancellationToken cancellationToken = default)
    {
        Calls++;
        Viewers.Add(viewerId);
        IReadOnlyDictionary<Guid, ActionAppreciation> result = actionIds.ToDictionary(id => id, id => new ActionAppreciation(
            id, Hearts.Count(heart => heart.ActionId == id), 10,
            viewerId is { } viewer && Hearts.Contains((id, viewer))));
        return Task.FromResult(result);
    }

    public Task<HeartResult> ToggleHeartAsync(
        Guid reportId, Guid actionId, Guid accountId, CancellationToken cancellationToken = default)
    {
        Calls++;
        if (Hearts.Remove((actionId, accountId)))
            return Task.FromResult(HeartResult.Removed);
        Hearts.Add((actionId, accountId));
        return Task.FromResult(HeartResult.Added);
    }

    public Task<GiftResult> GiftAsync(
        Guid reportId, Guid actionId, Guid giverId, int amount, CancellationToken cancellationToken = default)
    {
        Calls++;
        Gifts.Add((actionId, giverId, amount));
        return Task.FromResult(GiftResult.Sent);
    }

    // The single history row reuses the account id so tests can check which account was asked for.
    public Task<PointsSummary> GetSummaryAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(new PointsSummary(25, 0,
            [new PointsEvent(accountId, 10, PointsReason.ActionConfirmed, null, null, "poang-v1", DateTimeOffset.UtcNow)]));
    }

    public Task<int> ReverseConfirmationAsync(Guid actionId, Guid confirmerId, PointsReversalCause cause,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(0);
    }

    public Task<int> ReverseActionAsync(Guid actionId, PointsReversalCause cause,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(0);
    }
}
