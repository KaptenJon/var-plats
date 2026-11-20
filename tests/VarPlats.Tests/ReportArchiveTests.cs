using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using VarPlats.Core;
using VarPlats.Web;
using Xunit;

namespace VarPlats.Tests;

public sealed class ReportArchiveTests
{
    private static readonly ReportingMode Enabled = new(false, false, true, true, false, true);

    [Fact]
    public void DraftTrimsNoteAndRequiresItForOther()
    {
        Assert.Null(new ReportArchiveDraft { Reason = ArchiveReason.Resolved, Note = "  " }.Validated().Note);
        Assert.Equal("Syntetisk notering",
            new ReportArchiveDraft { Reason = ArchiveReason.Duplicate, Note = " Syntetisk notering " }.Validated().Note);
        Assert.Throws<ValidationException>(() => new ReportArchiveDraft { Reason = ArchiveReason.Other }.Validated());
        Assert.Throws<ValidationException>(() => new ReportArchiveDraft
        {
            Note = new string('a', ReportArchiveDraft.NoteMaxLength + 1)
        }.Validated());
        Assert.Throws<ValidationException>(() => new ReportArchiveDraft { Reason = (ArchiveReason)9 }.Validated());
    }

    [Fact]
    public void ArchiveNeedsItsOwnSwitchAndTheWorkflow()
    {
        Assert.False(new ReportingMode(false, false, true, true).Archive);
        Assert.False(new ReportingMode(true, false, false, true, false, true).Archive);
        Assert.False(new ReportingMode(false, true, false, true, false, true).Archive);
        Assert.True(new ReportingMode(false, false, false, true, false, true).Archive);
        Assert.False(new ReportingMode(false, false, false, true, false, true).CanArchive);
        Assert.True(Enabled.CanArchive);
    }

    [Fact]
    public async Task ArchiveStateIsNotReadWhenTheSwitchIsOff()
    {
        var store = new RecordingArchiveStore();
        var commands = new ReportArchiveCommands(new ReportingMode(false, false, true, true), store,
            SyntheticReportAccountAccess.Instance);
        Assert.Null(await commands.GetAsync(Guid.NewGuid()));
        Assert.Equal(0, store.Reads);
    }

    [Fact]
    public async Task OnlyVerifiedAdminsCanArchiveRestoreAndListArchive()
    {
        var store = new RecordingArchiveStore();
        var commands = new ReportArchiveCommands(Enabled, store, SyntheticReportAccountAccess.Instance);
        var reportId = Guid.NewGuid();
        var draft = new ReportArchiveDraft { Reason = ArchiveReason.Resolved };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.ArchiveAsync(Account("Member"), reportId, draft));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            commands.ArchiveAsync(Account("Admin", recovery: true), reportId, draft));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.RestoreAsync(Account("Member"), reportId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.ListAsync(Account("Member")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => commands.HistoryAsync(Account("Member"), reportId));
        var unverified = new ReportArchiveCommands(Enabled, store);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => unverified.ArchiveAsync(Account("Admin"), reportId, draft));
        Assert.Empty(store.Changes);

        Assert.True(await commands.ArchiveAsync(Account("Admin"), reportId, draft));
        Assert.True(await commands.RestoreAsync(Account("Admin"), reportId));
        Assert.Equal(["archive", "restore"], store.Changes.Select(change => change.Kind));
        Assert.Empty(await commands.ListAsync(Account("Admin")));
    }

    [Fact]
    public async Task ReadOnlyWorkflowCannotChangeArchive()
    {
        var store = new RecordingArchiveStore();
        var commands = new ReportArchiveCommands(new ReportingMode(false, false, false, true, false, true), store,
            SyntheticReportAccountAccess.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.ArchiveAsync(
            Account("Admin"), Guid.NewGuid(), new ReportArchiveDraft()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => commands.RestoreAsync(Account("Admin"), Guid.NewGuid()));
        Assert.Empty(store.Changes);
        Assert.Empty(await commands.ListAsync(Account("Admin")));
    }

    [Fact]
    public async Task InvalidDraftIsRejectedBeforeTheStore()
    {
        var store = new RecordingArchiveStore();
        var commands = new ReportArchiveCommands(Enabled, store, SyntheticReportAccountAccess.Instance);
        await Assert.ThrowsAsync<ValidationException>(() => commands.ArchiveAsync(
            Account("Admin"), Guid.NewGuid(), new ReportArchiveDraft { Reason = ArchiveReason.Other }));
        Assert.Empty(store.Changes);
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

    private sealed class RecordingArchiveStore : IReportArchiveStore
    {
        public List<(string Kind, Guid ReportId)> Changes { get; } = [];
        public int Reads { get; private set; }

        public Task<bool> ArchiveAsync(Guid reportId, ReportArchiveDraft draft, CancellationToken cancellationToken = default)
        {
            Changes.Add(("archive", reportId));
            return Task.FromResult(true);
        }

        public Task<bool> RestoreAsync(Guid reportId, CancellationToken cancellationToken = default)
        {
            Changes.Add(("restore", reportId));
            return Task.FromResult(true);
        }

        public Task<ReportArchive?> GetAsync(Guid reportId, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult<ReportArchive?>(null);
        }

        public Task<IReadOnlyList<ArchivedReport>> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ArchivedReport>>([]);

        public Task<IReadOnlyList<ReportArchiveEvent>> HistoryAsync(Guid reportId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ReportArchiveEvent>>([]);
    }
}
