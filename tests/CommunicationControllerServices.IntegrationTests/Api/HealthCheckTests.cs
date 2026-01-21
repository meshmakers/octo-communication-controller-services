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
        // In the test environment, the health check may report unhealthy (503)
        // because MassTransit health checks fail without RabbitMQ. This is expected.
        // We verify the endpoint is reachable and returns a valid health check response.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task SwaggerEndpoint_ReturnsOk()
    {
        // Act - Swagger should be available in Development environment
        var response = await Client.GetAsync("/swagger/index.html", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UnknownEndpoint_ReturnsNotFound()
    {
        // Act
        var response = await Client.GetAsync("/nonexistent", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
