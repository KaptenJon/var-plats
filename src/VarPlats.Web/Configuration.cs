using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace VarPlats.Web;

public sealed record ReportingMode(
    bool Demo, bool LocalWrites, bool PublicWrites, bool WorkflowEnabled = false, bool FlagsEnabled = false,
    bool ArchiveEnabled = false, bool ConfirmationWithdrawalEnabled = false, bool PointsEnabled = false)
{
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
