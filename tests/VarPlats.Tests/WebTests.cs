using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VarPlats.Core;
using VarPlats.Web.Photos;
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
    public void PublicWritesRequireExplicitWorkflowOptIn()
    {
        using var factory = CreateFactory("Production", new()
        {
            ["Reporting:PublicWrites"] = "true",
            ["Authentication:Enabled"] = "true"
        });
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Reporting:WorkflowEnabled", exception.Message);
    }

    [Fact]
    public void WorkflowRequiresAuthentication()
    {
        using var factory = CreateFactory("Production", new() { ["Reporting:WorkflowEnabled"] = "true" });
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("Authentication:Enabled", exception.Message);
    }

    [Fact]
    public void WorkflowCannotBeCombinedWithLocalWrites()
    {
        using var factory = CreateFactory("Development", new()
        {
            ["Reporting:LocalWrites"] = "true",
            ["Reporting:WorkflowEnabled"] = "true",
            ["Authentication:Enabled"] = "true"
        });
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("kan inte kombineras", exception.Message);
    }

    [Fact]
    public async Task PublicApiOnlyReturnsPublishedReportsWithoutAuthorIdentifiers()
    {
        var store = new ReportWorkflowTestStore();
        var authorId = Guid.NewGuid();
        var pendingDraft = ReportTests.ValidDraft();
        pendingDraft.Title = "Syntetisk väntande rapport";
        var rejectedDraft = ReportTests.ValidDraft();
        rejectedDraft.Title = "Syntetisk avvisad rapport";
        var approvedDraft = ReportTests.ValidDraft();
        approvedDraft.Title = "Syntetisk godkänd rapport";
        await store.SubmitAsync(authorId, pendingDraft);
        var rejected = await store.SubmitAsync(authorId, rejectedDraft);
        var approved = await store.SubmitAsync(authorId, approvedDraft);
        await store.DecideAsync(rejected.Id, ReportReviewStatus.Rejected);
        await store.DecideAsync(approved.Id, ReportReviewStatus.Approved);

        await using var factory = CreateFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IReportStore>();
            services.AddSingleton<IReportStore>(store);
        }));
        using var client = factory.CreateClient();
        var json = await client.GetStringAsync("/api/reports");

        Assert.Contains(approvedDraft.Title, json);
        Assert.DoesNotContain(pendingDraft.Title, json);
        Assert.DoesNotContain(rejectedDraft.Title, json);
        Assert.DoesNotContain(authorId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("authorId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DemoReportDetailsWorkWithoutTheActionSchemaOrWriteControls()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var home = WebUtility.HtmlDecode(await client.GetStringAsync("/"));
        Assert.Contains("/rapporter/00000000-0000-0000-0000-000000000001", home);

        var detail = WebUtility.HtmlDecode(await client.GetStringAsync(
            "/rapporter/00000000-0000-0000-0000-000000000001"));
        Assert.Contains("Skräp vid gångvägen", detail);
        Assert.Contains("Status:", detail);
        Assert.Contains("Rapporterad", detail);
        Assert.Contains("funktionen är avstängd", detail);
        Assert.DoesNotContain("Publicera åtgärdsuppgift", detail);
        Assert.Contains("Rapporten finns inte", WebUtility.HtmlDecode(
            await client.GetStringAsync($"/rapporter/{Guid.NewGuid()}")));
    }

    [Fact]
    public async Task DisabledPhotosNeedNoStorageScannerOrPhotoRoutes()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();
        Assert.Null(factory.Services.GetService<IPhotoFileStorage>());
        Assert.Null(factory.Services.GetService<IPhotoMalwareScanner>());
        Assert.False(factory.Services.GetRequiredService<PhotoOptions>().Enabled);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/photos/reports/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/photos/actions/{Guid.NewGuid()}")).StatusCode);
        Assert.DoesNotContain("type=\"file\"", await client.GetStringAsync("/"));
    }

    [Fact]
    public void PhotosCannotBeEnabledWithoutTheAuthenticatedWorkflow()
    {
        using var factory = CreateFactory("Production", new() { ["Photos:Enabled"] = "true" });
        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains("autentisering och rapportgranskning", exception.Message);
    }

    [Fact]
    public async Task PhotoResponsesArePrivateNoStoreAndActionUploadExplainsRetention()
    {
        var store = new ReportWorkflowTestStore();
        var report = await store.SubmitAsync(Guid.NewGuid(), ReportTests.ValidDraft());
        Assert.True(await store.DecideAsync(report.Id, ReportReviewStatus.Approved));
        Assert.True(await store.AddAsync(report.Id, Guid.NewGuid(), new ReportActionDraft
        {
            Description = "Syntetisk åtgärd utan personuppgifter."
        }));
        var action = Assert.Single(await store.ListAsync(report.Id, null));
        var image = PhotoWorkflowTests.SyntheticPngWithText("synthetic-marker");
        var clean = PhotoSanitizer.Sanitize(image).Bytes.ToArray();
        var root = Path.GetPathRoot(AppContext.BaseDirectory)!;

        await using var factory = CreateWorkflowFactory(store).WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Photos:Enabled", "true");
            builder.UseSetting("Photos:StoragePath", Path.Combine(root, "synthetic-private-photo-storage"));
            builder.UseSetting("Photos:ScannerSocketPath", Path.Combine(root, "synthetic-scanner", "clamd.sock"));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IPhotoFileStorage>();
                services.RemoveAll<IPhotoReader>();
                services.AddSingleton<IPhotoFileStorage, UnusedPhotoStorage>();
                services.AddSingleton<IPhotoReader>(new SyntheticPhotoReader(report.Id, action.Id, clean));
            });
        });
        using var client = factory.CreateClient();
        var response = await client.GetAsync($"/photos/reports/{report.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(clean, await response.Content.ReadAsByteArrayAsync());
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("same-origin", response.Headers.GetValues("Cross-Origin-Resource-Policy").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());

        var missing = await client.GetAsync($"/photos/reports/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.True(missing.Headers.CacheControl?.NoStore);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/photos/actions/{action.Id}")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Synthetic-Role", "Member");
        client.DefaultRequestHeaders.Add("X-Synthetic-Id", Guid.NewGuid().ToString());
        var pageResponse = await client.GetAsync($"/rapporter/{report.Id}");
        Assert.True(pageResponse.Headers.CacheControl?.NoStore);
        var page = WebUtility.HtmlDecode(await pageResponse.Content.ReadAsStringAsync());
        Assert.Contains($"id=\"action-photo\"", page);
        Assert.Contains("Bilden får inte innehålla människor", page);
        Assert.Contains("ansikten eller personuppgifter", page);
        Assert.Contains("kan finnas kvar även om du tar bort kontot", page);
        Assert.Contains("Metadatarensning tar inte bort synliga personer", page);
        Assert.Contains($"/photos/actions/{action.Id}", page);
    }

    [Fact]
    public async Task ReportDetailsExposeOnlyPublicActionTextAndAccurateConfirmationState()
    {
        var store = new ReportWorkflowTestStore();
        var authorId = Guid.NewGuid();
        var reporterId = Guid.NewGuid();
        var draft = ReportTests.ValidDraft();
        draft.Title = "Syntetisk publicerad plats";
        var published = await store.SubmitAsync(reporterId, draft);
        var pendingDraft = ReportTests.ValidDraft();
        pendingDraft.Title = "Syntetisk hemlig köplats";
        var pending = await store.SubmitAsync(reporterId, pendingDraft);
        Assert.True(await store.DecideAsync(published.Id, ReportReviewStatus.Approved));
        Assert.True(await store.AddAsync(published.Id, authorId, new ReportActionDraft
        {
            Description = "Syntetisk åtgärd <script>alert('x')</script>"
        }));

        await using var factory = CreateWorkflowFactory(store);
        using var client = factory.CreateClient();
        var pendingPage = WebUtility.HtmlDecode(await client.GetStringAsync($"/rapporter/{pending.Id}"));
        Assert.Contains("Rapporten finns inte", pendingPage);
        Assert.DoesNotContain(pendingDraft.Title, pendingPage);

        var url = $"/rapporter/{published.Id}";
        var publicHtml = await client.GetStringAsync(url);
        var publicPage = WebUtility.HtmlDecode(publicHtml);
        Assert.Contains("Syntetisk åtgärd", publicPage);
        Assert.Contains("obekräftad", publicPage);
        Assert.Contains("inte verifierat en fysisk person", publicPage);
        Assert.DoesNotContain("<script>alert('x')</script>", publicHtml);
        Assert.DoesNotContain(authorId.ToString(), publicHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(reporterId.ToString(), publicHtml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Publicera åtgärdsuppgift", publicPage);

        client.DefaultRequestHeaders.Add("X-Synthetic-Role", "Member");
        client.DefaultRequestHeaders.Add("X-Synthetic-Id", reporterId.ToString());
        var reporterPage = WebUtility.HtmlDecode(await client.GetStringAsync(url));
        Assert.Contains("Bekräfta uppgiften", reporterPage);
        Assert.Contains("Publicera åtgärdsuppgift", reporterPage);
        Assert.Contains("visas texten direkt och utan förhandsgranskning", reporterPage);
        Assert.Contains("personuppgifter", reporterPage);
        Assert.Contains("Utsätt inte dig själv", reporterPage);

        var action = Assert.Single(await store.ListAsync(published.Id, reporterId));
        Assert.Equal(ActionConfirmationResult.Confirmed,
            await store.ConfirmAsync(published.Id, action.Id, reporterId));
        var confirmedPage = WebUtility.HtmlDecode(await client.GetStringAsync(url));
        Assert.Contains("Bekräftad av ett annat konto", confirmedPage);
        Assert.Contains("Ditt konto har redan bekräftat", confirmedPage);
        var json = await client.GetStringAsync("/api/reports");
        Assert.DoesNotContain(authorId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(reporterId.ToString(), json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accountId", json, StringComparison.OrdinalIgnoreCase);

        client.DefaultRequestHeaders.Remove("X-Synthetic-Id");
        client.DefaultRequestHeaders.Add("X-Synthetic-Id", authorId.ToString());
        var authorPage = WebUtility.HtmlDecode(await client.GetStringAsync(url));
        Assert.Contains("Ditt konto kan inte bekräfta en egen uppgift", authorPage);
        client.DefaultRequestHeaders.Add("X-Synthetic-Recovery", "true");
        var recoveryPage = WebUtility.HtmlDecode(await client.GetStringAsync(url));
        Assert.DoesNotContain("Publicera åtgärdsuppgift", recoveryPage);
        Assert.DoesNotContain("Bekräfta uppgiften</button>", recoveryPage);
    }

    [Fact]
    public async Task WorkflowPagesShowOnlyAuthorizedQueueAndOwnStatuses()
    {
        var store = new ReportWorkflowTestStore();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var firstDraft = ReportTests.ValidDraft();
        firstDraft.Title = "Syntetiskt första inskick";
        var secondDraft = ReportTests.ValidDraft();
        secondDraft.Title = "Syntetiskt andra inskick";
        await store.SubmitAsync(firstId, firstDraft);
        await store.SubmitAsync(secondId, secondDraft);

        await using var factory = CreateWorkflowFactory(store);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/moderering")).StatusCode);

        client.DefaultRequestHeaders.Add("X-Synthetic-Role", "Member");
        client.DefaultRequestHeaders.Add("X-Synthetic-Id", firstId.ToString());
        var ownPage = WebUtility.HtmlDecode(await client.GetStringAsync("/mina-rapporter"));
        Assert.Contains(firstDraft.Title, ownPage);
        Assert.Contains("Väntar på granskning", ownPage);
        Assert.DoesNotContain(secondDraft.Title, ownPage);
        Assert.DoesNotContain(firstId.ToString(), ownPage, StringComparison.OrdinalIgnoreCase);
        var memberQueue = await client.GetStringAsync("/moderering");
        Assert.Contains("Endast administratörer", memberQueue);
        Assert.DoesNotContain(firstDraft.Title, memberQueue);

        client.DefaultRequestHeaders.Remove("X-Synthetic-Role");
        client.DefaultRequestHeaders.Add("X-Synthetic-Role", "Admin");
        client.DefaultRequestHeaders.Add("X-Synthetic-Recovery", "true");
        var recoveryQueue = await client.GetStringAsync("/moderering");
        Assert.Contains("Endast administratörer", recoveryQueue);
        Assert.DoesNotContain(firstDraft.Title, recoveryQueue);
        client.DefaultRequestHeaders.Remove("X-Synthetic-Recovery");
        var adminQueue = WebUtility.HtmlDecode(await client.GetStringAsync("/moderering"));
        Assert.Contains(firstDraft.Title, adminQueue);
        Assert.Contains(secondDraft.Title, adminQueue);
        Assert.DoesNotContain(firstId.ToString(), adminQueue, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secondId.ToString(), adminQueue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConfiguredLoginChallengesGoogleOnlyWithAntiforgeryToken()
    {
        await using var factory = CreateFactory("Development", new()
        {
            ["ConnectionStrings:varplats"] = "Host=127.0.0.1;Database=unused",
            ["Reporting:PublicWrites"] = "true",
            ["Reporting:WorkflowEnabled"] = "true",
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

    private static WebApplicationFactory<Program> CreateWorkflowFactory(ReportWorkflowTestStore store)
        => CreateFactory("Development", new()
        {
            ["ConnectionStrings:varplats"] = "Host=127.0.0.1;Database=unused",
            ["Reporting:PublicWrites"] = "true",
            ["Reporting:WorkflowEnabled"] = "true",
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
        }).WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IReportStore>();
            services.RemoveAll<IReportWorkflowStore>();
            services.RemoveAll<IReportActionStore>();
            services.AddSingleton<IReportStore>(store);
            services.AddSingleton<IReportWorkflowStore>(store);
            services.AddSingleton<IReportActionStore>(store);
            services.AddSyntheticTestAuthentication();
        }));

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

    private sealed class UnusedPhotoStorage : IPhotoFileStorage
    {
        public IReadOnlyList<PhotoDiskEntry> List() => throw new NotSupportedException();
        public long AvailableBytes() => throw new NotSupportedException();
        public Task WriteAsync(string name, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<byte[]> ReadAsync(string name, long length, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public void Delete(string name) => throw new NotSupportedException();
    }

    private sealed class SyntheticPhotoReader(Guid reportId, Guid actionId, byte[] bytes) : IPhotoReader
    {
        public Task<IReadOnlySet<Guid>> VisibleReportIdsAsync(
            System.Security.Claims.ClaimsPrincipal viewer, IReadOnlyCollection<Guid> ids,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<Guid>>(ids.Where(id => id == reportId).ToHashSet());
        public Task<IReadOnlySet<Guid>> VisibleActionIdsAsync(
            IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<Guid>>(ids.Where(id => id == actionId).ToHashSet());
        public Task<PhotoContent?> ReadReportAsync(
            System.Security.Claims.ClaimsPrincipal viewer, Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<PhotoContent?>(id == reportId ? new(bytes, "image/png") : null);
        public Task<PhotoContent?> ReadActionAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult<PhotoContent?>(id == actionId ? new(bytes, "image/png") : null);
    }
}
