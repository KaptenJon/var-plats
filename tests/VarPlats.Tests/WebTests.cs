using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using VarPlats.Core;
using Xunit;

namespace VarPlats.Tests;

public sealed class WebTests
{
    [Fact]
    public async Task DemoServesMapAndReadOnlyApi()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Lokal demo", html);
        Assert.Contains("id=\"map\"", html);
        Assert.Contains("lang=\"sv\"", html);
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
        var reports = await client.GetFromJsonAsync<Report[]>("/api/reports");
        Assert.Equal(2, reports!.Length);
        Assert.Equal(HttpStatusCode.MethodNotAllowed,
            (await client.PostAsJsonAsync("/api/reports", ReportTests.ValidDraft())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
    }

    [Fact]
    public async Task LoginPageKeepsExternalSignInDisabledUntilConfigured()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/login");
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Inloggning är inte aktiverad", html);
        Assert.DoesNotContain("Fortsätt med Google", html);
        Assert.DoesNotContain("Fortsätt med GitHub", html);
    }

    [Fact]
    public async Task PublicPoliciesDescribeAccountOnlyPreviewAndRightsDeadline()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var privacy = await client.GetStringAsync("/integritet");
        Assert.Contains("Vem ansvarar för uppgifterna?", privacy);
        Assert.Contains("12 månaders inaktivitet", privacy);
        Assert.Contains("senast inom en månad", privacy);
        Assert.Contains("Rapportering är avstängd", privacy);

        var terms = await client.GetStringAsync("/villkor");
        Assert.Contains("Operatör och kontakt", terms);
        Assert.Contains("inte en officiell kommunal felanmälan", terms);
        Assert.Contains("betalning är avstängda", terms);
    }

    [Fact]
    public void ExternalSignInRequiresBothProvidersAndInitialAdmin()
    {
        using var factory = CreateFactory("Development", new() { ["Authentication:Enabled"] = "true" });
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("OAuth credentials", exception.Message);
    }

    [Fact]
    public void PublicWritesRequireAuthentication()
    {
        using var factory = CreateFactory("Production", new() { ["Reporting:PublicWrites"] = "true" });
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Authentication:Enabled", exception.Message);
    }

    [Fact]
    public async Task ConfiguredLoginChallengesGoogleOnlyWithAntiforgeryToken()
    {
        await using var factory = CreateFactory("Development", new()
        {
            ["ConnectionStrings:varplats"] = "Host=127.0.0.1;Database=unused",
            ["Reporting:PublicWrites"] = "true",
            ["Authentication:Enabled"] = "true",
            ["Authentication:Google:ClientId"] = "test-google-client",
            ["Authentication:Google:ClientSecret"] = "test-google-secret",
            ["Authentication:GitHub:ClientId"] = "test-github-client",
            ["Authentication:GitHub:ClientSecret"] = "test-github-secret",
            ["Authentication:AdminGitHubId"] = "10761546",
            ["Authentication:TrustedProxyIp"] = "127.0.0.1",
            ["Authentication:Mailjet:ApiKey"] = "test-mailjet-key",
            ["Authentication:Mailjet:ApiSecret"] = "test-mailjet-secret",
            ["Authentication:Mailjet:SenderEmail"] = "test@example.test",
            ["Authentication:Passkeys:ServerDomain"] = "example.test",
            ["Authentication:Passkeys:Origins:0"] = "https://example.test",
            ["Authentication:PublicBaseUrl"] = "https://example.test"
        });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var page = await client.GetStringAsync("/login");
        var token = Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")
            .Groups[1].Value;
        Assert.False(string.IsNullOrWhiteSpace(token));

        var response = await client.PostAsync("/auth/signin/google",
            new FormUrlEncodedContent([new("__RequestVerificationToken", token)]));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("https://accounts.google.com/", response.Headers.Location!.ToString());

        var deletionResponse = await client.PostAsync("/auth/account/delete", new FormUrlEncodedContent([]));
        Assert.Equal(HttpStatusCode.Redirect, deletionResponse.StatusCode);

        using var passkeyRequest = new HttpRequestMessage(HttpMethod.Post, "/auth/passkeys/request-options")
        {
            Content = JsonContent.Create(new Dictionary<string, string>())
        };
        passkeyRequest.Headers.Add("RequestVerificationToken", token);
        var passkeyOptions = await client.SendAsync(passkeyRequest);
        Assert.Equal(HttpStatusCode.OK, passkeyOptions.StatusCode);
        Assert.DoesNotContain("test@example.test", await passkeyOptions.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("198.51.100.1", "198.51.100.1", true)]
    [InlineData("192.0.2.1", "127.0.0.1", false)]
    public async Task ForwardedHttpsIsTrustedOnlyFromConfiguredProxy(
        string proxyAddress, string trustedProxyIp, bool expectHsts)
    {
        await using var factory = CreateFactory("Production",
            new()
            {
                ["ConnectionStrings:varplats"] = "Host=127.0.0.1;Database=varplats",
                ["AllowedHosts"] = "varplats.test",
                ["Authentication:TrustedProxyIp"] = trustedProxyIp
            },
            IPAddress.Parse(proxyAddress));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/login");
        request.Headers.Host = "varplats.test";
        request.Headers.Add("X-Forwarded-Proto", "https");

        var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Equal(expectHsts, response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task DemoRejectsNonLoopbackClients()
    {
        await using var factory = CreateFactory(address: IPAddress.Parse("192.0.2.1"));
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/")).StatusCode);
    }

    [Theory]
    [InlineData("Reporting:Demo")]
    [InlineData("Reporting:LocalWrites")]
    public void ProductionRejectsDevelopmentWriteModes(string key)
    {
        using var factory = CreateFactory("Production", new() { [key] = "true" });
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("endast tillåtna", exception.Message);
    }

    [Fact]
    public async Task ServiceWorkerDoesNotCacheOrFetchTiles()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var script = await client.GetStringAsync("/service-worker.js");
        Assert.DoesNotContain("caches.", script);
        Assert.DoesNotContain("fetch(", script);
        Assert.DoesNotContain("tile.openstreetmap.org", script);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string environment = "Development", Dictionary<string, string?>? overrides = null, IPAddress? address = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            foreach (var setting in overrides ?? new() { ["Reporting:Demo"] = "true" })
                builder.UseSetting(setting.Key, setting.Value);
            builder.ConfigureServices(services =>
                services.AddSingleton<IStartupFilter>(new ClientAddressFilter(address ?? IPAddress.Loopback)));
        });

    private sealed class ClientAddressFilter(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = address;
                return nextMiddleware();
            });
            next(app);
        };
    }
}
