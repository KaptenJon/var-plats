using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VarPlats.Web;
using Xunit;

namespace VarPlats.Tests;

public sealed class AdminEmailsTests
{
    [Fact]
    public void ParsesCommaAndSemicolonSeparatedAddressesCaseInsensitively()
    {
        var admins = AdminEmails.Parse(" admin@example.test, Andra@Example.test ;admin@example.test ");

        Assert.Equal(2, admins.Count);
        Assert.True(admins.Matches(User("ADMIN@example.test", confirmed: true)));
        Assert.True(admins.Matches(User("andra@example.test", confirmed: true)));
    }

    [Fact]
    public void EmptyConfigurationGivesNoAdmins()
    {
        Assert.Equal(0, AdminEmails.Parse(null).Count);
        Assert.Equal(0, AdminEmails.Parse(" ").Count);
        Assert.False(AdminEmails.Parse("").Matches(User("admin@example.test", confirmed: true)));
    }

    [Fact]
    public void UnconfirmedOrOtherAddressesNeverMatch()
    {
        var admins = AdminEmails.Parse("admin@example.test");

        Assert.False(admins.Matches(User("admin@example.test", confirmed: false)));
        Assert.False(admins.Matches(User("annan@example.test", confirmed: true)));
        Assert.False(admins.Matches(User(null, confirmed: true)));
    }

    [Theory]
    [InlineData("inte-en-adress")]
    [InlineData("@example.test")]
    [InlineData("admin@")]
    [InlineData("a@b@example.test")]
    [InlineData("admin @example.test")]
    public void InvalidAddressesStopStartup(string value)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => AdminEmails.Parse(value));
        Assert.Contains(AdminEmails.ConfigurationKey, exception.Message);
    }

    [Theory]
    [InlineData(".admin@example.test")]
    [InlineData("admin.@example.test")]
    [InlineData("admin..ops@example.test")]
    [InlineData("admin@.example.test")]
    [InlineData("admin@example")]
    [InlineData("admin@exa_mple.test")]
    [InlineData("Admin <admin@example.test>")]
    public void MalformedAddressesStopStartup(string value)
        => Assert.Throws<InvalidOperationException>(() => AdminEmails.Parse(value));

    [Fact]
    public async Task ConfirmedMatchGetsTheRoleInTheStoreAndInTheCookieBeingIssued()
    {
        var store = new RoleStore();
        var user = store.Add("admin@example.test", confirmed: true);
        var principal = Principal(user);

        await AdminEmails.Parse("admin@example.test").PromoteAsync(principal, user.Id, Users(store), NullLogger.Instance);

        Assert.Contains("ADMIN", store.Roles[user.Id]);
        Assert.True(principal.IsInRole("Admin"));
    }

    [Fact]
    public async Task UnconfirmedOrUnlistedAccountsGetNothing()
    {
        var store = new RoleStore();
        var unconfirmed = store.Add("admin@example.test", confirmed: false);
        var other = store.Add("annan@example.test", confirmed: true);
        var admins = AdminEmails.Parse("admin@example.test");

        foreach (var user in new[] { unconfirmed, other })
        {
            var principal = Principal(user);
            await admins.PromoteAsync(principal, user.Id, Users(store), NullLogger.Instance);
            Assert.DoesNotContain("ADMIN", store.Roles[user.Id]);
            Assert.False(principal.IsInRole("Admin"));
        }
    }

    [Fact]
    public async Task AConcurrentInsertOfTheSameRoleCountsAsSuccess()
    {
        var store = new RoleStore { Behaviour = AddBehaviour.ConcurrentDuplicate };
        var user = store.Add("admin@example.test", confirmed: true);
        var principal = Principal(user);

        await AdminEmails.Parse("admin@example.test").PromoteAsync(principal, user.Id, Users(store), NullLogger.Instance);

        Assert.True(principal.IsInRole("Admin"));
    }

    [Fact]
    public async Task AFailedRoleInsertGivesNoAdminClaim()
    {
        var store = new RoleStore { Behaviour = AddBehaviour.Fail };
        var user = store.Add("admin@example.test", confirmed: true);
        var principal = Principal(user);

        await AdminEmails.Parse("admin@example.test").PromoteAsync(principal, user.Id, Users(store), NullLogger.Instance);

        Assert.DoesNotContain("ADMIN", store.Roles[user.Id]);
        Assert.False(principal.IsInRole("Admin"));
    }

    private static ClaimsPrincipal Principal(IdentityUser<Guid> user)
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Role, "Member")], "Identity.Application"));

    private static UserManager<IdentityUser<Guid>> Users(RoleStore store)
        => new(store, Options.Create(new IdentityOptions()), new PasswordHasher<IdentityUser<Guid>>(), [], [],
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<IdentityUser<Guid>>>.Instance);

    private enum AddBehaviour { Normal, ConcurrentDuplicate, Fail }

    /// <summary>Minimal Identity-butik i minnet för rolltilldelning, med syntetiska konton.</summary>
    private sealed class RoleStore : IUserRoleStore<IdentityUser<Guid>>
    {
        private readonly Dictionary<Guid, IdentityUser<Guid>> users = [];
        public Dictionary<Guid, HashSet<string>> Roles { get; } = [];
        public AddBehaviour Behaviour { get; init; }

        public IdentityUser<Guid> Add(string email, bool confirmed)
        {
            var user = new IdentityUser<Guid> { Id = Guid.NewGuid(), Email = email, UserName = email, EmailConfirmed = confirmed };
            users[user.Id] = user;
            Roles[user.Id] = [];
            return user;
        }

        public Task AddToRoleAsync(IdentityUser<Guid> user, string roleName, CancellationToken cancellationToken)
        {
            switch (Behaviour)
            {
                case AddBehaviour.ConcurrentDuplicate:
                    Roles[user.Id].Add(roleName); // den andra inloggningen hann före
                    throw new DbUpdateException("Syntetisk dubblett för (UserId, RoleId).");
                case AddBehaviour.Fail:
                    throw new DbUpdateException("Syntetiskt databasfel.");
                default:
                    Roles[user.Id].Add(roleName);
                    return Task.CompletedTask;
            }
        }

        public Task<bool> IsInRoleAsync(IdentityUser<Guid> user, string roleName, CancellationToken cancellationToken)
            => Task.FromResult(Roles[user.Id].Contains(roleName));
        public Task RemoveFromRoleAsync(IdentityUser<Guid> user, string roleName, CancellationToken cancellationToken)
        {
            Roles[user.Id].Remove(roleName);
            return Task.CompletedTask;
        }
        public Task<IList<string>> GetRolesAsync(IdentityUser<Guid> user, CancellationToken cancellationToken)
            => Task.FromResult<IList<string>>([.. Roles[user.Id]]);
        public Task<IList<IdentityUser<Guid>>> GetUsersInRoleAsync(string roleName, CancellationToken cancellationToken)
            => Task.FromResult<IList<IdentityUser<Guid>>>([.. users.Values.Where(user => Roles[user.Id].Contains(roleName))]);
        public Task<IdentityUser<Guid>?> FindByIdAsync(string userId, CancellationToken cancellationToken)
            => Task.FromResult(users.GetValueOrDefault(Guid.Parse(userId)));
        public Task<IdentityUser<Guid>?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken)
            => Task.FromResult(users.Values.FirstOrDefault(user => user.NormalizedUserName == normalizedUserName));
        public Task<IdentityResult> UpdateAsync(IdentityUser<Guid> user, CancellationToken cancellationToken)
            => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> CreateAsync(IdentityUser<Guid> user, CancellationToken cancellationToken)
            => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> DeleteAsync(IdentityUser<Guid> user, CancellationToken cancellationToken)
            => Task.FromResult(IdentityResult.Success);
        public Task<string> GetUserIdAsync(IdentityUser<Guid> user, CancellationToken cancellationToken)
            => Task.FromResult(user.Id.ToString());
        public Task<string?> GetUserNameAsync(IdentityUser<Guid> user, CancellationToken cancellationToken)
            => Task.FromResult(user.UserName);
        public Task SetUserNameAsync(IdentityUser<Guid> user, string? userName, CancellationToken cancellationToken)
        {
            user.UserName = userName;
            return Task.CompletedTask;
        }
        public Task<string?> GetNormalizedUserNameAsync(IdentityUser<Guid> user, CancellationToken cancellationToken)
            => Task.FromResult(user.NormalizedUserName);
        public Task SetNormalizedUserNameAsync(IdentityUser<Guid> user, string? normalizedName, CancellationToken cancellationToken)
        {
            user.NormalizedUserName = normalizedName;
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }

    private static IdentityUser<Guid> User(string? email, bool confirmed)
        => new() { Id = Guid.NewGuid(), Email = email, UserName = email ?? "konto", EmailConfirmed = confirmed };
}
