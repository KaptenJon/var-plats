using VarPlats.Core;

namespace VarPlats.Web;

public static class ContentFlagText
{
    public static string Reason(FlagReason reason) => reason switch
    {
        FlagReason.PersonalData => "Personuppgifter, till exempel namn, adress eller registreringsnummer",
        FlagReason.Offensive => "Kränkande eller hotfullt innehåll",
        FlagReason.Dangerous => "Uppmanar till en farlig åtgärd",
        FlagReason.Spam => "Reklam eller spam",
        FlagReason.WrongPlace => "Fel plats eller påhittad rapport",
        _ => "Annat"
    };

    public static string Result(FlagResult result) => result switch
    {
        FlagResult.Flagged => "Tack. Anmälan är skickad till administratörerna.",
        FlagResult.AlreadyFlagged => "Ditt konto har redan anmält det här innehållet.",
        FlagResult.LimitReached =>
            $"Ditt konto har nått gränsen på {ContentFlagLimits.PerAccountPerDay} anmälningar per dygn. Försök igen senare.",
        _ => "Innehållet finns inte längre."
    };
}
