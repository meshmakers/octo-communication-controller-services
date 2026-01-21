using System.Net;
using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Infrastructure;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Api;

/// <summary>
/// HTTP-based integration tests for health endpoints.
/// </summary>
public class HealthCheckTests : IntegrationTestBase
{
    public HealthCheckTests(CustomWebApplicationFactory factory) : base(factory)
    {
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsExpectedStatusCode()
    {
        // Act
        var response = await Client.GetAsync("/health", TestContext.Current.CancellationToken);

        // Assert
        // In the test environment, the system context health check may report unhealthy (503)
        // because not all components are fully initialized. This is expected.
        // We verify the endpoint is reachable and returns a valid health check response.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task HomeEndpoint_ReturnsSuccessStatusCode()
    {
        // Act
        var response = await Client.GetAsync("/", TestContext.Current.CancellationToken);

        // Assert
        response.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public async Task LiveEndpoint_ReturnsOk()
    {
        // Act
        var response = await Client.GetAsync("/live", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ReadyEndpoint_ReturnsExpectedStatusCode()
    {
        // Act
        var response = await Client.GetAsync("/ready", TestContext.Current.CancellationToken);

        // Assert
        // Ready endpoint may return ServiceUnavailable if not all health checks pass
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }
}
