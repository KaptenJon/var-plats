using System.Net;
using Microsoft.EntityFrameworkCore;
using Radzen;
using VarPlats.Core;
using VarPlats.ServiceDefaults;
using VarPlats.Web;
using VarPlats.Web.Components;
using VarPlats.Web.Data;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var demo = builder.Configuration.GetValue<bool>("Reporting:Demo");
var localWrites = builder.Configuration.GetValue<bool>("Reporting:LocalWrites");
if ((demo || localWrites) && !builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Demo och lokala skrivningar är endast tillåtna i Development.");

builder.Services.AddSingleton(new ReportingMode(demo, localWrites));
builder.Services.AddOptions<MapOptions>().BindConfiguration("Map")
    .Validate(options => Uri.TryCreate(options.TileUrl, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && !string.IsNullOrWhiteSpace(options.Attribution),
        "Map:TileUrl måste vara en HTTPS-URL och attribution måste anges.")
    .ValidateOnStart();
if (demo)
    builder.Services.AddScoped<IReportStore, DemoReportStore>();
else
{
    var connection = builder.Configuration.GetConnectionString("varplats")
        ?? throw new InvalidOperationException("ConnectionStrings:varplats krävs. Se README för lokal demo.");
    builder.Services.AddDbContextFactory<ReportDbContext>(options =>
        options.UseNpgsql(connection, postgres => postgres.UseNetTopologySuite()));
    builder.Services.AddScoped<IReportStore, DatabaseReportStore>();
    builder.Services.AddHealthChecks().AddDbContextCheck<ReportDbContext>();
}
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddRadzenComponents();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<PublicExceptionHandler>();
var app = builder.Build();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
    app.UseHsts();
app.Use(async (context, next) =>
{
    // Local write mode must not become an unauthenticated public preview.
    if ((demo || localWrites) && (context.Connection.RemoteIpAddress is not { } address || !IPAddress.IsLoopback(address)))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    await next();
});
app.UseAntiforgery();
app.MapStaticAssets();
app.MapDefaultEndpoints();
app.MapGet("/api/reports", async (IReportStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.ListAsync(cancellationToken)));
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
