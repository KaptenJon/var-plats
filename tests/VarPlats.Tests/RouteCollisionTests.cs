using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace VarPlats.Tests;

public sealed class RouteCollisionTests
{
    /// <summary>
    /// Blazor-sidor tar emot POST för formulär. Om ett API-endpoint har samma väg blir POST tvetydig
    /// (AmbiguousMatchException, 500) – så gick det för /auth/email/confirm.
    /// </summary>
    [Fact]
    public async Task NoTwoEndpointsAcceptPostOnTheSameRoute()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            foreach (var setting in new Dictionary<string, string?>
            {
                ["ConnectionStrings:varplats"] = "Host=127.0.0.1;Database=unused",
                ["Reporting:PublicWrites"] = "true",
                ["Reporting:WorkflowEnabled"] = "true",
                ["Reporting:FlagsEnabled"] = "true",
                ["Reporting:ArchiveEnabled"] = "true",
                ["Reporting:ConfirmationWithdrawalEnabled"] = "true",
                ["Reporting:PointsEnabled"] = "true",
                ["Reporting:RateLimits:Enabled"] = "true",
                ["Authentication:Enabled"] = "true",
                ["Authentication:Google:ClientId"] = "synthetic",
                ["Authentication:Google:ClientSecret"] = "synthetic",
                ["Authentication:GitHub:ClientId"] = "synthetic",
                ["Authentication:GitHub:ClientSecret"] = "synthetic",
                ["Authentication:AdminGitHubId"] = "1",
                ["Authentication:TrustedProxyIp"] = "127.0.0.1",
                ["Authentication:Mailjet:ApiKey"] = "synthetic",
                ["Authentication:Mailjet:ApiSecret"] = "synthetic",
                ["Authentication:Mailjet:SenderEmail"] = "synthetic@example.test",
                ["Authentication:Passkeys:ServerDomain"] = "example.test",
                ["Authentication:Passkeys:Origins:0"] = "https://example.test",
                ["Authentication:PublicBaseUrl"] = "https://example.test"
            })
                builder.UseSetting(setting.Key, setting.Value);
        });
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);

        var collisions = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IHttpMethodMetadata>() is not { } methods
                || methods.HttpMethods.Contains(HttpMethods.Post, StringComparer.OrdinalIgnoreCase))
            .GroupBy(endpoint => endpoint.RoutePattern.RawText?.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        Assert.Empty(collisions);
    }
}
