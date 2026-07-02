using System.Reflection;
using FluentAssertions;
using Xunit;

namespace Klaxon.Tests.Unit.Architecture;

public sealed class LayeringTests
{
    // Clean Architecture: the domain layer must not depend on infrastructure, the web
    // host, or a database driver. Enforced structurally so a stray using-directive that
    // inverts the dependency fails the build instead of shipping.
    [Fact]
    public void Core_DoesNotDependOnInfrastructureWebOrData()
    {
        var referenced = Assembly.Load("Klaxon.Core")
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        referenced.Should().NotContain("Klaxon.Infrastructure");
        referenced.Should().NotContain(name =>
            name != null && (
                name.StartsWith("Microsoft.AspNetCore") ||
                name.StartsWith("Microsoft.EntityFrameworkCore") ||
                name == "Npgsql"));
    }
}
