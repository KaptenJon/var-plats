using Microsoft.AspNetCore.Identity;
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

    private static IdentityUser<Guid> User(string? email, bool confirmed)
        => new() { Id = Guid.NewGuid(), Email = email, UserName = email ?? "konto", EmailConfirmed = confirmed };
}
