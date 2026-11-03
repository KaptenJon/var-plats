using System.Net;
using System.Net.Http.Json;
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
