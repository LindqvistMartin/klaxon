using Klaxon.Infrastructure;
using Klaxon.Infrastructure.Notifications;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Klaxon.Tests.Unit;

public sealed class ServiceExtensionsTests
{
    // The integration harness swaps the real channel out for a recording one, so nothing there
    // notices if this registration disappears — and a dispatcher with no channels stamps every page
    // as delivered having sent it nowhere. Pin the production wiring here instead.
    [Fact]
    public void AddInfrastructure_RegistersTheLogChannel()
    {
        var services = new ServiceCollection();

        services.AddInfrastructure();

        services.Should().ContainSingle(d => d.ServiceType == typeof(INotificationChannel))
            .Which.ImplementationType.Should().Be(typeof(LogChannel));
    }
}
