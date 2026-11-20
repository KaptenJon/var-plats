using Microsoft.EntityFrameworkCore;

namespace VarPlats.Web.Data;

/// <summary>
/// Read-time hiding for <see cref="ReportingMode.HideOnFlag"/>. Content is hidden while at least the configured
/// number of open (not dismissed) flags come from accounts other than its author. Nothing is stored: dismissing the
/// flags shows the content again, and removing it deletes the flags with it. Callers only use these queries while
/// the switch is on, so content_flags is never named in SQL otherwise.
/// </summary>
internal static class FlagVisibility
{
    // Report-level flags that count: open and not from the account that submitted the report.
    private static IQueryable<ContentFlagEntity> ReportFlags(ReportDbContext db)
        => db.ContentFlags.Where(flag => flag.ActionId == null && flag.DismissedAt == null
            && !db.ReportSubmissions.Any(submission => submission.PublishedReportId == flag.ReportId
                && submission.AuthorId == flag.ReporterId));

    // Action-level flags that count: open and not from the account that wrote the action text.
    private static IQueryable<ContentFlagEntity> ActionFlags(ReportDbContext db)
        => db.ContentFlags.Where(flag => flag.ActionId != null && flag.DismissedAt == null
            && !db.ReportActions.Any(action => action.Id == flag.ActionId && action.AuthorId == flag.ReporterId));

    public static IQueryable<ReportEntity> WithoutHiddenReports(
        ReportDbContext db, IQueryable<ReportEntity> reports, int minimum)
    {
        var flags = ReportFlags(db);
        return reports.Where(report => flags.Count(flag => flag.ReportId == report.Id) < minimum);
    }

    public static async Task<bool> IsReportHiddenAsync(
        ReportDbContext db, Guid reportId, int minimum, CancellationToken cancellationToken)
        => await ReportFlags(db).CountAsync(flag => flag.ReportId == reportId, cancellationToken) >= minimum;

    public static async Task<bool> IsActionHiddenAsync(
        ReportDbContext db, Guid actionId, int minimum, CancellationToken cancellationToken)
        => await ActionFlags(db).CountAsync(flag => flag.ActionId == actionId, cancellationToken) >= minimum;

    /// <summary>True when the action text or the report it belongs to is hidden.</summary>
    public static async Task<bool> IsActionOrReportHiddenAsync(
        ReportDbContext db, Guid reportId, Guid actionId, int minimum, CancellationToken cancellationToken)
        => await IsReportHiddenAsync(db, reportId, minimum, cancellationToken)
            || await IsActionHiddenAsync(db, actionId, minimum, cancellationToken);

    public static Task<Guid[]> HiddenReportIdsAsync(
        ReportDbContext db, Guid[] reportIds, int minimum, CancellationToken cancellationToken)
        => ReportFlags(db).Where(flag => reportIds.Contains(flag.ReportId))
            .GroupBy(flag => flag.ReportId)
            .Where(group => group.Count() >= minimum)
            .Select(group => group.Key)
            .ToArrayAsync(cancellationToken);

    public static Task<Guid[]> HiddenActionIdsAsync(
        ReportDbContext db, Guid[] actionIds, int minimum, CancellationToken cancellationToken)
        => ActionFlags(db).Where(flag => actionIds.Contains(flag.ActionId!.Value))
            .Select(flag => flag.ActionId!.Value)
            .GroupBy(id => id)
            .Where(group => group.Count() >= minimum)
            .Select(group => group.Key)
            .ToArrayAsync(cancellationToken);

    public static Task<Guid[]> HiddenActionIdsForReportAsync(
        ReportDbContext db, Guid reportId, int minimum, CancellationToken cancellationToken)
        => ActionFlags(db).Where(flag => flag.ReportId == reportId)
            .Select(flag => flag.ActionId!.Value)
            .GroupBy(id => id)
            .Where(group => group.Count() >= minimum)
            .Select(group => group.Key)
            .ToArrayAsync(cancellationToken);
}
