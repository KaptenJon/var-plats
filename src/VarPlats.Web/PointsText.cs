using VarPlats.Core;

namespace VarPlats.Web;

public static class PointsText
{
    public const string SafetyNote = "Poäng ges aldrig för farliga insatser – ta inga risker.";

    public static string Hearts(int count) => count switch
    {
        0 => "Inga hjärtan",
        1 => "1 hjärta",
        _ => $"{count} hjärtan"
    };

    public static string Points(int amount) => amount == 1 ? "1 poäng" : $"{amount} poäng";

    public static string Reason(PointsReason reason, PointsReversalCause? cause) => reason switch
    {
        PointsReason.ActionConfirmed => "Åtgärd bekräftad av ett annat konto",
        PointsReason.Reversal => $"Återtagna poäng: {Cause(cause)}",
        PointsReason.GiftSent => "Poäng skickade till en åtgärd",
        PointsReason.GiftReceived => "Poäng fått från ett annat konto",
        _ => "Okänd händelse"
    };

    public static string Cause(PointsReversalCause? cause) => cause switch
    {
        PointsReversalCause.ConfirmationWithdrawn => "bekräftelsen togs tillbaka",
        PointsReversalCause.ActionRemoved => "åtgärdsuppgiften togs bort",
        PointsReversalCause.ReportRemoved => "rapporten togs bort",
        PointsReversalCause.ReportArchived => "rapporten arkiverades",
        PointsReversalCause.AdminRevoked => "en administratör tog tillbaka poängen",
        _ => "okänd anledning"
    };

    public static string Heart(HeartResult result) => result switch
    {
        HeartResult.Added => "Du har gett åtgärden ett hjärta. Hjärtan ger inga poäng.",
        HeartResult.Removed => "Ditt hjärta är borttaget.",
        HeartResult.OwnAction => "Du kan inte ge hjärta till en egen åtgärd.",
        HeartResult.Archived => "Arkiverade rapporter tar inte emot nya hjärtan.",
        HeartResult.Hidden => "Innehåll som är dolt i väntan på granskning tar inte emot nya hjärtan.",
        _ => "Åtgärdsuppgiften finns inte längre."
    };

    public static string Gift(GiftResult result, int amount) => result switch
    {
        GiftResult.Sent => $"{Points(amount)} har skickats från ditt saldo.",
        GiftResult.OwnAction => "Du kan inte skicka poäng till en egen åtgärd.",
        GiftResult.NoRecipient => "Kontot som gjorde åtgärden finns inte längre. Inga poäng skickades.",
        GiftResult.InsufficientBalance => "Ditt saldo räcker inte. Inga poäng skickades.",
        GiftResult.OverTransferLimit => "Det är fler poäng än vad som får skickas åt gången. Inga poäng skickades.",
        GiftResult.OverDailyLimit => "Du har nått gränsen för hur många poäng du får skicka per dygn. Inga poäng skickades.",
        GiftResult.Archived => "Arkiverade rapporter tar inte emot poäng. Inga poäng skickades.",
        GiftResult.Hidden => "Innehåll som är dolt i väntan på granskning tar inte emot poäng. Inga poäng skickades.",
        _ => "Åtgärdsuppgiften finns inte längre. Inga poäng skickades."
    };
}
