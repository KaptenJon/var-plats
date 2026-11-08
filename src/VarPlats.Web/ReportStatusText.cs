using VarPlats.Core;

namespace VarPlats.Web;

public static class ReportStatusText
{
    public static string Describe(ReportStatus status, bool workflowEnabled) => status switch
    {
        ReportStatus.ActionDocumented when !workflowEnabled => "Tidigare status: åtgärd dokumenterad",
        ReportStatus.Confirmed when !workflowEnabled => "Tidigare status: bekräftad",
        ReportStatus.ActionDocumented => "Åtgärd uppges · obekräftad",
        ReportStatus.Confirmed => "Minst en åtgärdsuppgift bekräftad av ett annat konto",
        _ => "Rapporterad"
    };
}
