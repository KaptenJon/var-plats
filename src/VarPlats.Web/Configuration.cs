using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace VarPlats.Web;

public sealed record ReportingMode(bool Demo, bool LocalWrites, bool PublicWrites)
{
    public bool CanWrite => Demo || LocalWrites || PublicWrites;
}

public sealed class MapOptions
{
    public string TileUrl { get; set; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
    public string Attribution { get; set; } = "© OpenStreetMap contributors";
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
