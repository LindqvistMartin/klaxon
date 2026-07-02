using Klaxon.Core.Entities;
using FluentAssertions;
using Xunit;

namespace Klaxon.Tests.Unit.Domain;

public sealed class UserTests
{
    [Fact]
    public void Constructor_Valid_SetsProperties()
    {
        var teamId = Guid.NewGuid();
        var user = new User(teamId, "Ada Lovelace", "ada@example.com", "Europe/London");

        user.TeamId.Should().Be(teamId);
        user.Name.Should().Be("Ada Lovelace");
        user.Email.Should().Be("ada@example.com");
        user.TimeZoneId.Should().Be("Europe/London");
        user.Id.Should().NotBeEmpty();
    }

    [Fact]
    public void Constructor_InvalidTimeZone_Throws()
    {
        var act = () => new User(Guid.NewGuid(), "Ada", "ada@example.com", "Nowhere/Void");
        act.Should().Throw<ArgumentException>().WithParameterName("timeZoneId");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_BlankEmail_Throws(string email)
    {
        var act = () => new User(Guid.NewGuid(), "Ada", email, "Europe/London");
        act.Should().Throw<ArgumentException>().WithParameterName("email");
    }

    [Fact]
    public void Constructor_EmptyTeamId_Throws()
    {
        var act = () => new User(Guid.Empty, "Ada", "ada@example.com", "Europe/London");
        act.Should().Throw<ArgumentException>().WithParameterName("teamId");
    }
}
