using VarPlats.Core;

namespace VarPlats.Web;

public static class ReportListFilter
{
    public static IReadOnlyList<Report> Apply(
        IReadOnlyList<Report> reports, string? category, string? searchText)
    {
        var query = searchText?.Trim();
        return reports.Where(report =>
                (string.IsNullOrEmpty(category) || report.Category.ToString() == category)
                && (string.IsNullOrEmpty(query)
                    || report.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || report.Description.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}
