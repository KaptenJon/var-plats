using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using VarPlats.Core;
using VarPlats.Web;
using Xunit;

namespace VarPlats.Tests;

public sealed class ContentFlagTests
{
    private static readonly ReportingMode Enabled = new(false, false, true, true, true);

    [Fact]
    public void BlankNoteBecomesNullAndNoteIsTrimmed()
    {
        Assert.Null(new ContentFlagDraft { Reason = FlagReason.Spam, Note = "   " }.Validated().Note);
        Assert.Equal("Syntetisk kommentar",
            new ContentFlagDraft { Reason = FlagReason.Spam, Note = "  Syntetisk kommentar " }.Validated().Note);
    }

    [Fact]
    public void OtherRequiresANoteAndNotesAreBounded()
    {
        Assert.Throws<ValidationException>(() => new ContentFlagDraft { Reason = FlagReason.Other }.Validated());
        Assert.Equal(FlagReason.Other,
            new ContentFlagDraft { Reason = FlagReason.Other, Note = "Syntetisk förklaring" }.Validated().Reason);
        Assert.Throws<ValidationException>(() => new ContentFlagDraft
        {
            Reason = FlagReason.Spam,
            Note = new string('a', ContentFlagLimits.NoteMaxLength + 1)
        }.Validated());
        Assert.Throws<ValidationException>(() => new ContentFlagDraft { Reason = (FlagReason)99 }.Validated());
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    public async Task FlaggingRequiresFlagsWorkflowAndPublicWrites(bool publicWrites, bool workflow, bool flags)
    {
        var store = new RecordingFlagStore();
        var commands = new ContentFlagCommands(new ReportingMode(false, false, publicWrites, workflow, flags), store,
            SyntheticReportAccountAccess.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commands.FlagAsync(Account("Member"), Guid.NewGuid(), null, Draft()));
        Assert.Empty(store.Flags);
    }

    [Fact]
    public void LocalAndDemoModesNeverExposeFlags()
    {
        Assert.False(new ReportingMode(true, false, false, true, true).FlagQueue);
        Assert.False(new ReportingMode(false, true, true, true, true).CanFlag);
        Assert.True(new ReportingMode(false, false, false, true, true).FlagQueue);
        Assert.False(new ReportingMode(false, false, false, true, true).CanFlag);
    }

    [Fact]
    public async Task MembersCanFlagButAnonymousRecoveryAndUnverifiedAccountsCannot()
    {
        var store = new RecordingFlagStore();
        var commands = new ContentFlagCommands(Enabled, store, SyntheticReportAccountAccess.Instance);
        var member = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var actionId = Guid.NewGuid();

        Assert.Equal(FlagResult.Flagged, await commands.FlagAsync(Account("Member", member), reportId, actionId, Draft()));
        Assert.Equal((reportId, (Guid?)actionId, member), Assert.Single(store.Flags));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.FlagAsync(Account("Member", authenticated: false), reportId, null, Draft()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.FlagAsync(Account("Member", recovery: true), reportId, null, Draft()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.FlagAsync(Account("Visitor"), reportId, null, Draft()));
        var unverified = new ContentFlagCommands(Enabled, store);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            unverified.FlagAsync(Account("Member"), reportId, null, Draft()));
        Assert.Single(store.Flags);
    }

    [Fact]
    public async Task InvalidDraftIsRejectedBeforeTheStore()
    {
        var store = new RecordingFlagStore();
        var commands = new ContentFlagCommands(Enabled, store, SyntheticReportAccountAccess.Instance);
        await Assert.ThrowsAsync<ValidationException>(() => commands.FlagAsync(
            Account("Member"), Guid.NewGuid(), null, new ContentFlagDraft { Reason = FlagReason.Other }));
        Assert.Empty(store.Flags);
    }

    [Fact]
    public async Task OnlyAdminsCanReadAndDismissTheQueue()
    {
        var store = new RecordingFlagStore();
        var commands = new ContentFlagCommands(Enabled, store, SyntheticReportAccountAccess.Instance);
        var reportId = Guid.NewGuid();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.ListOpenAsync(Account("Member")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.DismissAsync(Account("Member"), reportId, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.ListOpenAsync(Account("Admin", recovery: true)));
        Assert.Empty(store.Dismissed);

        Assert.Empty(await commands.ListOpenAsync(Account("Admin")));
        Assert.Equal(1, await commands.DismissAsync(Account("Admin"), reportId, null));
        Assert.Equal((reportId, (Guid?)null), Assert.Single(store.Dismissed));
    }

    [Fact]
    public async Task ReadOnlyQueueCannotDismiss()
    {
        var store = new RecordingFlagStore();
        var commands = new ContentFlagCommands(new ReportingMode(false, false, false, true, true), store,
            SyntheticReportAccountAccess.Instance);

        Assert.Empty(await commands.ListOpenAsync(Account("Admin")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            commands.DismissAsync(Account("Admin"), Guid.NewGuid(), null));
        Assert.Empty(store.Dismissed);
    }

    private static ContentFlagDraft Draft() => new() { Reason = FlagReason.PersonalData };

    private static ClaimsPrincipal Account(
        string role, Guid? userId = null, bool authenticated = true, bool recovery = false)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, (userId ?? Guid.NewGuid()).ToString()),
            new(ClaimTypes.Role, role)
        };
        if (recovery) claims.Add(new Claim("recovery_only", "true"));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticated ? "test" : null));
    }

    [Fact]
    public async Task FlagsAreRateLimitedPerAccount()
    {
        var store = new RecordingFlagStore();
        var commands = new ContentFlagCommands(Enabled, store, SyntheticReportAccountAccess.Instance,
            WriteRateLimitTests.Limiter(new WriteRateLimitTests.ManualTime(),
                options => options.Flags = new() { PermitLimit = 1, WindowSeconds = 600 }));
        var member = Guid.NewGuid();

        await commands.FlagAsync(Account("Member", member), Guid.NewGuid(), null, Draft());
        var exception = await Assert.ThrowsAsync<WriteRateLimitException>(() =>
            commands.FlagAsync(Account("Member", member), Guid.NewGuid(), null, Draft()));
        Assert.Contains("anmälningar", exception.Message);
        await commands.FlagAsync(Account("Member"), Guid.NewGuid(), null, Draft());
        Assert.Equal(2, store.Flags.Count);
    }

    private sealed class RecordingFlagStore : IContentFlagStore
    {
        public List<(Guid ReportId, Guid? ActionId, Guid AccountId)> Flags { get; } = [];
        public List<(Guid ReportId, Guid? ActionId)> Dismissed { get; } = [];

        public Task<FlagResult> FlagAsync(Guid reportId, Guid? actionId, Guid accountId, ContentFlagDraft draft,
            CancellationToken cancellationToken = default)
        {
            Flags.Add((reportId, actionId, accountId));
            return Task.FromResult(FlagResult.Flagged);
        }

        public Task<IReadOnlyList<FlaggedContent>> ListOpenAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<FlaggedContent>>([]);

        public Task<int> DismissAsync(Guid reportId, Guid? actionId, CancellationToken cancellationToken = default)
        {
            Dismissed.Add((reportId, actionId));
            return Task.FromResult(1);
        }

        public HiddenContent Hidden { get; set; } = HiddenContent.None;
        public List<(Guid ReportId, Guid? ViewerId)> HiddenReads { get; } = [];

        public Task<HiddenContent> GetHiddenAsync(Guid reportId, Guid? viewerId,
            CancellationToken cancellationToken = default)
        {
            HiddenReads.Add((reportId, viewerId));
            return Task.FromResult(Hidden);
        }
    }
}
