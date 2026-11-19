using VarPlats.Core;

namespace VarPlats.Web;

public static class ArchiveText
{
    public static string Reason(ArchiveReason reason) => reason switch
    {
        ArchiveReason.Resolved => "Åtgärdad",
        ArchiveReason.Outdated => "Inaktuell",
        ArchiveReason.Duplicate => "Dubblett",
        _ => "Annat"
    };
}
