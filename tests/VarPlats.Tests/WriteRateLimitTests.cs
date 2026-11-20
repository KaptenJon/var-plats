using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using VarPlats.Core;
using VarPlats.Web;
using Xunit;

namespace VarPlats.Tests;

public sealed class WriteRateLimitTests
{
    private static readonly ReportingMode Writable = new(false, false, true, true);

    [Fact]
    public async Task ActionLimitStopsTheThirdWriteBeforeAnythingIsSaved()
    {
        var store = new ReportWorkflowTestStore();
        var commands = Actions(store, Limiter(new ManualTime(), options => options.Actions = new() { PermitLimit = 2, WindowSeconds = 600 }));
        var member = Account(Guid.NewGuid());
        var reportId = (await store.ListAsync()).First().Id;

        Assert.True(await commands.AddAsync(member, reportId, Draft()));
        Assert.True(await commands.AddAsync(member, reportId, Draft()));
        var exception = await Assert.ThrowsAsync<WriteRateLimitException>(() =>
            commands.AddAsync(member, reportId, Draft()));

        Assert.Equal(WriteKind.Action, exception.Kind);
        Assert.Equal(TimeSpan.FromMinutes(10), exception.RetryAfter);
        Assert.StartsWith("Gräns nådd:", exception.Message);
        Assert.Contains("åtgärdsuppgifter", exception.Message);
        Assert.Contains("Inget sparades", exception.Message);
        Assert.Contains("10 minuter", exception.Message);
        Assert.Equal(2, (await store.ListAsync(reportId, null)).Count);
    }

    [Fact]
    public async Task WindowResetAllowsWritesAgain()
    {
        var time = new ManualTime();
        var store = new ReportWorkflowTestStore();
        var commands = Actions(store, Limiter(time, options => options.Actions = new() { PermitLimit = 1, WindowSeconds = 60 }));
        var member = Account(Guid.NewGuid());
        var reportId = (await store.ListAsync()).First().Id;

        Assert.True(await commands.AddAsync(member, reportId, Draft()));
        time.Advance(TimeSpan.FromSeconds(59));
        var exception = await Assert.ThrowsAsync<WriteRateLimitException>(() => commands.AddAsync(member, reportId, Draft()));
        Assert.Contains("en minut", exception.Message);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await commands.AddAsync(member, reportId, Draft()));
        await Assert.ThrowsAsync<WriteRateLimitException>(() => commands.AddAsync(member, reportId, Draft()));
    }

    [Fact]
    public async Task AccountsAndWriteKindsHaveSeparateLimits()
    {
        var store = new ReportWorkflowTestStore();
        var limiter = Limiter(new ManualTime(), options =>
        {
            options.Actions = new() { PermitLimit = 1, WindowSeconds = 600 };
            options.Confirmations = new() { PermitLimit = 1, WindowSeconds = 600 };
        });
        var commands = Actions(store, limiter);
        var first = Account(Guid.NewGuid());
        var second = Account(Guid.NewGuid());
        var reportId = (await store.ListAsync()).First().Id;

        Assert.True(await commands.AddAsync(first, reportId, Draft()));
        await Assert.ThrowsAsync<WriteRateLimitException>(() => commands.AddAsync(first, reportId, Draft()));
        Assert.True(await commands.AddAsync(second, reportId, Draft()));

        var third = Account(Guid.NewGuid());
        var actions = await commands.ListAsync(third, reportId);
        Assert.Equal(2, actions.Count);
        Assert.Equal(ActionConfirmationResult.Confirmed, await commands.ConfirmAsync(third, reportId, actions[0].Id));
        var exception = await Assert.ThrowsAsync<WriteRateLimitException>(() =>
            commands.ConfirmAsync(third, reportId, actions[1].Id));
        Assert.Equal(WriteKind.Confirmation, exception.Kind);
        Assert.False((await commands.ListAsync(third, reportId)).Single(action => action.Id == actions[1].Id).HasConfirmed);
        // A used-up confirmation limit does not block the same account's first action text.
        Assert.True(await commands.AddAsync(third, reportId, Draft()));
    }

    [Fact]
    public async Task SwitchOffMeansNoLimiting()
    {
        var store = new ReportWorkflowTestStore();
        var limiter = Limiter(new ManualTime(), options =>
        {
            options.Enabled = false;
            options.Actions = new() { PermitLimit = 1, WindowSeconds = 3600 };
            options.Reports = new() { PermitLimit = 1, WindowSeconds = 3600 };
        });
        Assert.False(limiter.Enabled);
        var actions = Actions(store, limiter);
        var reports = new ReportCommands(store, Writable, store,
            accountAccess: SyntheticReportAccountAccess.Instance, rateLimiter: limiter);
        var member = Account(Guid.NewGuid());
        var reportId = (await store.ListAsync()).First().Id;

        for (var i = 0; i < 5; i++)
        {
            Assert.True(await actions.AddAsync(member, reportId, Draft()));
            await reports.AddAsync(member, ReportTests.ValidDraft());
        }
        Assert.Equal(5, (await store.ListAsync(reportId, null)).Count);
        for (var i = 0; i < 5; i++)
            limiter.Acquire(Guid.NewGuid(), WriteKind.Photo, WriteKind.Flag, WriteKind.Confirmation);
    }

    [Fact]
    public async Task ReportSubmissionsAreLimitedButInvalidDraftsDoNotUseAPermit()
    {
        var store = new ReportWorkflowTestStore();
        var reports = new ReportCommands(store, Writable, store, accountAccess: SyntheticReportAccountAccess.Instance,
            rateLimiter: Limiter(new ManualTime(), options => options.Reports = new() { PermitLimit = 1, WindowSeconds = 3600 }));
        var member = Account(Guid.NewGuid());

        await Assert.ThrowsAsync<ValidationException>(() => reports.AddAsync(member, new ReportDraft()));
        await reports.AddAsync(member, ReportTests.ValidDraft());
        var exception = await Assert.ThrowsAsync<WriteRateLimitException>(() =>
            reports.AddAsync(member, ReportTests.ValidDraft()));
        Assert.Equal(WriteKind.Report, exception.Kind);
        Assert.Contains("rapporter", exception.Message);
        Assert.Single(await reports.ListOwnAsync(member));
    }

    [Fact]
    public void CombinedPermitsAreTakenTogetherOrNotAtAll()
    {
        var limiter = Limiter(new ManualTime(), options =>
        {
            options.Reports = new() { PermitLimit = 5, WindowSeconds = 3600 };
            options.Photos = new() { PermitLimit = 1, WindowSeconds = 3600 };
        });
        var account = Guid.NewGuid();

        limiter.Acquire(account, WriteKind.Report, WriteKind.Photo);
        var exception = Assert.Throws<WriteRateLimitException>(() =>
            limiter.Acquire(account, WriteKind.Report, WriteKind.Photo));
        Assert.Equal(WriteKind.Photo, exception.Kind);
        Assert.Contains("bilduppladdningar", exception.Message);
        // The refused photo upload did not use one of the remaining four report permits.
        for (var i = 0; i < 4; i++)
            limiter.Acquire(account, WriteKind.Report);
        Assert.Throws<WriteRateLimitException>(() => limiter.Acquire(account, WriteKind.Report));
    }

    [Fact]
    public void ExpiredWindowsAreSweptAndStartFresh()
    {
        var time = new ManualTime();
        var limiter = Limiter(time, options => options.Flags = new() { PermitLimit = 1, WindowSeconds = 60 });
        var accounts = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToList();
        foreach (var account in accounts)
            limiter.Acquire(account, WriteKind.Flag);
        time.Advance(TimeSpan.FromMinutes(10));
        foreach (var account in accounts)
            limiter.Acquire(account, WriteKind.Flag);
        Assert.Throws<WriteRateLimitException>(() => limiter.Acquire(accounts[0], WriteKind.Flag));
    }

    [Theory]
    [InlineData(0, 60)]
    [InlineData(-1, 60)]
    [InlineData(10_001, 60)]
    [InlineData(5, 0)]
    [InlineData(5, 86_401)]
    public void InvalidLimitsAreRejected(int permits, int seconds)
    {
        foreach (var assign in new Action<RateLimitOptions>[]
        {
            options => options.Reports = new() { PermitLimit = permits, WindowSeconds = seconds },
            options => options.Actions = new() { PermitLimit = permits, WindowSeconds = seconds },
            options => options.Confirmations = new() { PermitLimit = permits, WindowSeconds = seconds },
            options => options.Flags = new() { PermitLimit = permits, WindowSeconds = seconds },
            options => options.Photos = new() { PermitLimit = permits, WindowSeconds = seconds },
            options => options.Api = new() { PermitLimit = permits, WindowSeconds = seconds }
        })
        {
            var options = new RateLimitOptions();
            assign(options);
            var exception = Assert.Throws<InvalidOperationException>(options.Validate);
            Assert.Contains("Reporting:RateLimits:", exception.Message);
        }
    }

    [Fact]
    public void DefaultsAreOffAndValid()
    {
        var options = new RateLimitOptions();
        Assert.False(options.Enabled);
        options.Validate();
    }

    internal static WriteRateLimiter Limiter(TimeProvider time, Action<RateLimitOptions> configure)
    {
        var options = new RateLimitOptions { Enabled = true };
        configure(options);
        options.Validate();
        return new WriteRateLimiter(options, time, NullLogger<WriteRateLimiter>.Instance);
    }

    private static ReportActionCommands Actions(ReportWorkflowTestStore store, WriteRateLimiter limiter)
        => new(Writable, store, accountAccess: SyntheticReportAccountAccess.Instance, rateLimiter: limiter);

    private static ReportActionDraft Draft() => new() { Description = "Syntetisk åtgärd utan personuppgifter." };

    internal static ClaimsPrincipal Account(Guid id, string role = "Member")
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, id.ToString()), new Claim(ClaimTypes.Role, role)], "test"));

    internal sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }
}
