using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace VarPlats.Web;

public sealed record ReportingMode(
    bool Demo, bool LocalWrites, bool PublicWrites, bool WorkflowEnabled = false, bool FlagsEnabled = false,
    bool ArchiveEnabled = false, bool ConfirmationWithdrawalEnabled = false, bool PointsEnabled = false,
    bool DirectPublishEnabled = false, bool HideOnFlagEnabled = false, int HideOnFlagMinimumFlags = 1)
{
    /// <summary>Upper bound for <see cref="HideOnFlagMinimumFlags"/>, validated at startup.</summary>
    public const int MaxHideOnFlagMinimumFlags = 50;

    public bool PublicWorkflow => PublicWrites && WorkflowEnabled && !Demo && !LocalWrites;
    public bool CanWrite => Demo || LocalWrites || PublicWorkflow;
    /// <summary>Admins can read the flag queue; requires the ContentFlags migration.</summary>
    public bool FlagQueue => FlagsEnabled && WorkflowEnabled && !Demo && !LocalWrites;
    /// <summary>Accounts can flag published content and admins can act on flags.</summary>
    public bool CanFlag => FlagQueue && PublicWorkflow;
    /// <summary>Reads hide archived reports; requires the ReportArchive migration.</summary>
    public bool Archive => ArchiveEnabled && WorkflowEnabled && !Demo && !LocalWrites;
    /// <summary>Admins can archive and restore reports.</summary>
    public bool CanArchive => Archive && PublicWorkflow;
    /// <summary>Reads show withdrawal history and re-confirmation is refused; requires the
    /// ConfirmationWithdrawals migration.</summary>
    public bool ConfirmationWithdrawal => ConfirmationWithdrawalEnabled && WorkflowEnabled && !Demo && !LocalWrites;
    /// <summary>Accounts can withdraw their own confirmations.</summary>
    public bool CanWithdrawConfirmation => ConfirmationWithdrawal && PublicWorkflow;
    /// <summary>Points, hearts and gifts are read and confirmations award points; requires the Points migration.</summary>
    public bool Points => PointsEnabled && WorkflowEnabled && !Demo && !LocalWrites;
    /// <summary>Accounts can give hearts and gifts, and confirmations write to the ledger.</summary>
    public bool CanUsePoints => Points && PublicWorkflow;
    /// <summary>A member's report is published in the same transaction as it is submitted, without review.</summary>
    public bool DirectPublish => DirectPublishEnabled && PublicWorkflow;
    /// <summary>Open flags from accounts other than the author hide content at read time; requires the
    /// ContentFlags migration. Applies to reads even when writes are off.</summary>
    public bool HideOnFlag => HideOnFlagEnabled && FlagQueue;

    /// <summary>Stops startup when the hide threshold is outside 1–50, even when the switch is off.</summary>
    public static void ValidateHideOnFlagMinimumFlags(int value)
    {
        if (value is < 1 or > MaxHideOnFlagMinimumFlags)
            throw new InvalidOperationException(
                $"HideOnFlag:MinimumFlags måste vara mellan 1 och {MaxHideOnFlagMinimumFlags}.");
    }
}

public sealed class MapOptions
{
    public string TileUrl { get; set; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
    public string Attribution { get; set; } = "© OpenStreetMap contributors";
}

public sealed class MailjetOptions
{
    public string ApiKey { get; set; } = "";
    public string ApiSecret { get; set; } = "";
    public string SenderEmail { get; set; } = "";
    public string SenderName { get; set; } = "Vår plats";
}

public sealed class PublicExceptionHandler(ILogger<PublicExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Begäran misslyckades. TraceId: {TraceId}", context.TraceIdentifier);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = 500,
            Title = "Tjänsten kunde inte slutföra begäran.",
            Detail = "Försök igen senare. Ange spårnings-id vid felrapport.",
            Extensions = { ["traceId"] = context.TraceIdentifier }
        }, cancellationToken);
        return true;
    }
}
