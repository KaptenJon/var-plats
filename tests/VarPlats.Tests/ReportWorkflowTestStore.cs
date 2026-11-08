using System.Security.Claims;
using VarPlats.Core;
using VarPlats.Web;

namespace VarPlats.Tests;

internal sealed class SyntheticReportAccountAccess : IReportAccountAccess
{
    public static SyntheticReportAccountAccess Instance { get; } = new();

    public Task<ReportAccountRights> VerifyAsync(
        ClaimsPrincipal principal, CancellationToken cancellationToken = default)
        => Task.FromResult(Rights(principal));

    public static ReportAccountRights Rights(ClaimsPrincipal principal)
        => ReportCommands.CanReport(principal)
            ? new(Guid.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!), true,
                ReportCommands.CanModerate(principal))
            : default;
}

internal sealed class ReportWorkflowTestStore : IReportStore, IReportWorkflowStore, IReportActionStore
{
    private readonly object gate = new();
    private readonly List<Report> published =
    [
        new(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Syntetisk äldre rapport",
            "Ett påhittat exempel utan personuppgifter.", ReportCategory.Litter,
            59.3293, 18.0686, DateTimeOffset.Parse("2026-11-01T10:00:00Z"))
    ];
    private readonly List<Entry> submissions = [];
    private readonly List<ActionEntry> actions = [];

    public Task<IReadOnlyList<Report>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (gate) return Task.FromResult<IReadOnlyList<Report>>(published.Select(WithStatus).ToArray());
    }

    public Task<Report?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var report = published.SingleOrDefault(item => item.Id == id);
            return Task.FromResult(report is null ? null : WithStatus(report));
        }
    }

    public Task<Report> AddAsync(ReportDraft draft, CancellationToken cancellationToken = default)
    {
        var report = draft.ToReport();
        lock (gate) published.Add(report);
        return Task.FromResult(report);
    }

    public Task<bool> RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var removed = published.RemoveAll(report => report.Id == id) != 0;
            if (removed)
            {
                submissions.RemoveAll(entry => entry.Report.Id == id);
                actions.RemoveAll(entry => entry.ReportId == id);
            }
            return Task.FromResult(removed);
        }
    }

    public Task<Report> SubmitAsync(Guid authorId, ReportDraft draft, CancellationToken cancellationToken = default)
    {
        var report = draft.ToReport();
        lock (gate) submissions.Add(new(report, authorId));
        return Task.FromResult(report);
    }

    public Task<IReadOnlyList<ReportSubmission>> ListOwnAsync(
        Guid authorId, CancellationToken cancellationToken = default)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<ReportSubmission>>(submissions
                .Where(entry => entry.AuthorId == authorId)
                .Select(entry => new ReportSubmission(entry.Report, entry.Status)).ToArray());
    }

    public Task<IReadOnlyList<ReportSubmission>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<ReportSubmission>>(submissions
                .Where(entry => entry.Status == ReportReviewStatus.Pending)
                .Select(entry => new ReportSubmission(entry.Report, entry.Status)).ToArray());
    }

    public Task<bool> DecideAsync(Guid id, ReportReviewStatus decision, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var entry = submissions.SingleOrDefault(item => item.Report.Id == id);
            if (entry is null || entry.Status != ReportReviewStatus.Pending)
                return Task.FromResult(false);
            entry.Status = decision;
            if (decision == ReportReviewStatus.Approved) published.Add(entry.Report);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<ReportAction>> ListAsync(
        Guid reportId, Guid? viewerId, CancellationToken cancellationToken = default)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<ReportAction>>(actions
                .Where(entry => entry.ReportId == reportId)
                .OrderByDescending(entry => entry.CreatedAt)
                .Select(entry => new ReportAction(entry.Id, entry.Description, entry.CreatedAt,
                    entry.Confirmations.Count, viewerId.HasValue && entry.AuthorId == viewerId,
                    viewerId.HasValue && entry.Confirmations.Contains(viewerId.Value)))
                .ToArray());
    }

    public Task<bool> AddAsync(
        Guid reportId, Guid authorId, ReportActionDraft draft, CancellationToken cancellationToken = default)
    {
        var description = draft.ValidatedDescription();
        lock (gate)
        {
            if (!published.Any(report => report.Id == reportId))
                return Task.FromResult(false);
            actions.Add(new(reportId, authorId, description));
            return Task.FromResult(true);
        }
    }

    public Task<ActionConfirmationResult> ConfirmAsync(
        Guid reportId, Guid actionId, Guid accountId, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            var action = actions.SingleOrDefault(item => item.Id == actionId && item.ReportId == reportId);
            return Task.FromResult(action is null ? ActionConfirmationResult.NotFound
                : action.AuthorId == accountId ? ActionConfirmationResult.OwnAction
                : action.Confirmations.Add(accountId) ? ActionConfirmationResult.Confirmed
                : ActionConfirmationResult.AlreadyConfirmed);
        }
    }

    public Task<bool> RemoveAsync(Guid reportId, Guid actionId, CancellationToken cancellationToken = default)
    {
        lock (gate) return Task.FromResult(actions.RemoveAll(action =>
            action.Id == actionId && action.ReportId == reportId) != 0);
    }

    private Report WithStatus(Report report)
    {
        var reportActions = actions.Where(action => action.ReportId == report.Id).ToArray();
        var status = reportActions.Any(action => action.Confirmations.Count > 0) ? ReportStatus.Confirmed
            : reportActions.Length > 0 ? ReportStatus.ActionDocumented : ReportStatus.Reported;
        return report with { Status = status };
    }

    private sealed class Entry(Report report, Guid authorId)
    {
        public Report Report { get; } = report;
        public Guid AuthorId { get; } = authorId;
        public ReportReviewStatus Status { get; set; }
    }

    private sealed class ActionEntry(Guid reportId, Guid authorId, string description)
    {
        public Guid Id { get; } = Guid.NewGuid();
        public Guid ReportId { get; } = reportId;
        public Guid AuthorId { get; } = authorId;
        public string Description { get; } = description;
        public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
        public HashSet<Guid> Confirmations { get; } = [];
    }
}
