using System.Security.Claims;
using VarPlats.Core;
using VarPlats.Web;
using Xunit;

namespace VarPlats.Tests;

public sealed class HideOnFlagTests
{
    private static readonly ReportingMode On = new(false, false, true, true, true,
        DirectPublishEnabled: true, HideOnFlagEnabled: true);

    [Fact]
    public void SwitchesOnlyTakeEffectWithTheGatesTheyNeed()
    {
        Assert.True(On.DirectPublish);
        Assert.True(On.HideOnFlag);
        // Direct publishing needs public writes; reads still hide flagged content when writes are off.
        var readOnly = On with { PublicWrites = false };
        Assert.False(readOnly.DirectPublish);
        Assert.True(readOnly.HideOnFlag);
        Assert.False((On with { FlagsEnabled = false }).HideOnFlag);
        Assert.False((On with { WorkflowEnabled = false }).DirectPublish);
        Assert.False((On with { Demo = true }).HideOnFlag);
        Assert.False((On with { LocalWrites = true }).DirectPublish);
        Assert.False(new ReportingMode(false, false, true, true, true).DirectPublish);
        Assert.False(new ReportingMode(false, false, true, true, true).HideOnFlag);
        Assert.Equal(1, new ReportingMode(false, false, false).HideOnFlagMinimumFlags);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ReportingMode.MaxHideOnFlagMinimumFlags + 1)]
    public void MinimumFlagsOutsideTheRangeStopStartup(int value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            ReportingMode.ValidateHideOnFlagMinimumFlags(value));
        Assert.Contains("HideOnFlag:MinimumFlags", exception.Message);
    }

    [Fact]
    public async Task VisibilityIsNotReadWhileTheSwitchIsOff()
    {
        var store = new HiddenContentStore { Hidden = new(true, false, new HashSet<Guid>()) };
        var commands = new ContentFlagCommands(On with { HideOnFlagEnabled = false }, store,
            SyntheticReportAccountAccess.Instance);
        var visibility = await commands.GetVisibilityAsync(new ClaimsPrincipal(), Guid.NewGuid());
        Assert.False(visibility.ReportHidden);
        Assert.Empty(store.Reads);
    }

    [Fact]
    public async Task HiddenReportsAreOnlyShownToAdminsAndTheAuthor()
    {
        var reportId = Guid.NewGuid();
        var store = new HiddenContentStore();
        var commands = new ContentFlagCommands(On, store, SyntheticReportAccountAccess.Instance);
        var memberId = Guid.NewGuid();
        var member = Account("Member", memberId);

        store.Hidden = new(true, false, new HashSet<Guid>());
        Assert.True((await commands.GetVisibilityAsync(new ClaimsPrincipal(), reportId)).ReportWithheld);
        Assert.Null(store.Reads[^1].ViewerId);
        Assert.True((await commands.GetVisibilityAsync(member, reportId)).ReportWithheld);
        Assert.Equal(memberId, store.Reads[^1].ViewerId);
        var admin = await commands.GetVisibilityAsync(Account("Admin"), reportId);
        Assert.False(admin.ReportWithheld);
        Assert.True(admin.IsModerator);
        // A recovery-only session is treated as anonymous.
        Assert.True((await commands.GetVisibilityAsync(Account("Admin", recovery: true), reportId)).ReportWithheld);
        Assert.Null(store.Reads[^1].ViewerId);

        store.Hidden = new(true, true, new HashSet<Guid>());
        var author = await commands.GetVisibilityAsync(member, reportId);
        Assert.False(author.ReportWithheld);
        Assert.False(author.IsModerator);
    }

    [Fact]
    public void HiddenActionTextsAreOnlyShownToAdminsAndTheirAuthor()
    {
        var hidden = new ReportAction(Guid.NewGuid(), "Syntetisk åtgärd", DateTimeOffset.UtcNow, 0, false, false);
        var shown = hidden with { Id = Guid.NewGuid() };
        var visibility = new ContentVisibility(false, false, new HashSet<Guid> { hidden.Id }, false);
        Assert.True(visibility.ActionWithheld(hidden));
        Assert.False(visibility.ActionWithheld(shown));
        Assert.False(visibility.ActionWithheld(hidden with { IsOwnAction = true }));
        Assert.False((visibility with { IsModerator = true }).ActionWithheld(hidden));
        Assert.False(ContentVisibility.Nothing.ActionWithheld(hidden));
    }

    [Fact]
    public void HiddenResultsHaveNeutralSwedishTexts()
    {
        Assert.Equal("Dolt i väntan på granskning", ContentFlagText.HiddenNote);
        Assert.Contains("dolt i väntan på granskning", PointsText.Heart(HeartResult.Hidden));
        Assert.Contains("Inga poäng skickades", PointsText.Gift(GiftResult.Hidden, 1));
    }

    internal static ClaimsPrincipal Account(string role, Guid? id = null, bool recovery = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, (id ?? Guid.NewGuid()).ToString()),
            new(ClaimTypes.Role, role)
        };
        if (recovery) claims.Add(new("recovery_only", "true"));
        return new(new ClaimsIdentity(claims, "test"));
    }
}

/// <summary>Flag store for page tests: returns a fixed hidden state and records nothing else.</summary>
internal sealed class HiddenContentStore : IContentFlagStore
{
    public HiddenContent Hidden { get; set; } = HiddenContent.None;
    public List<(Guid ReportId, Guid? ViewerId)> Reads { get; } = [];
    public Func<Guid?, bool>? IsAuthor { get; set; }

    public Task<FlagResult> FlagAsync(Guid reportId, Guid? actionId, Guid accountId, ContentFlagDraft draft,
        CancellationToken cancellationToken = default) => Task.FromResult(FlagResult.Flagged);

    public Task<IReadOnlyList<FlaggedContent>> ListOpenAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<FlaggedContent>>([]);

    public Task<int> DismissAsync(Guid reportId, Guid? actionId, CancellationToken cancellationToken = default)
        => Task.FromResult(0);

    public Task<HiddenContent> GetHiddenAsync(Guid reportId, Guid? viewerId,
        CancellationToken cancellationToken = default)
    {
        lock (Reads) Reads.Add((reportId, viewerId));
        return Task.FromResult(IsAuthor is null ? Hidden : Hidden with { ViewerIsReportAuthor = IsAuthor(viewerId) });
    }
}
