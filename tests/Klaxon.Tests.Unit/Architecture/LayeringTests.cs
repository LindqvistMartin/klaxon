using System.Reflection;
using Klaxon.Core.Entities;
using FluentAssertions;
using Xunit;

namespace Klaxon.Tests.Unit.Architecture;

public sealed class LayeringTests
{
    // Clean Architecture: the domain layer must not reach into ASP.NET, EF Core, or a database
    // driver. GetReferencedAssemblies reports the assemblies Core actually binds types from, so
    // pulling one of these into Core — the usual way the dependency inverts — trips this test.
    // A Core -> Infrastructure project reference needs no assertion here: Infrastructure already
    // references Core, so the reverse edge is a build-breaking cycle caught before any test runs.
    [Fact]
    public void Core_DoesNotDependOnWebEfOrDataDrivers()
    {
        var referenced = typeof(Alert).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToArray();

        referenced.Should().NotContain(name =>
            name != null && (
                name.StartsWith("Microsoft.AspNetCore") ||
                name.StartsWith("Microsoft.EntityFrameworkCore") ||
                name == "Npgsql"));
    }
}
