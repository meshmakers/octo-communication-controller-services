using System.Net;
using FluentAssertions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Infrastructure;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Api;

/// <summary>
/// HTTP-based integration tests for health endpoints.
/// NOTE: These tests are temporarily skipped due to a conflict with the Octo Runtime Engine's
/// global state management. The WebApplicationFactory initializes static state that interferes
/// with the CommunicationControllerFixture's CK cache initialization.
/// TODO: Move these tests to a separate test project for process isolation.
/// </summary>
[Collection("ZWebFactory")]
public class HealthCheckTests(CustomWebApplicationFactory factory) : IntegrationTestBase(factory)
{

    [Fact(Skip = "Conflicts with CommunicationControllerFixture due to shared static state in Octo Runtime Engine")]
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

    [Fact(Skip = "Conflicts with CommunicationControllerFixture due to shared static state in Octo Runtime Engine")]
    public async Task SwaggerEndpoint_ReturnsOk()
    {
        // Act - Swagger should be available in Development environment
        var response = await Client.GetAsync("/swagger/index.html", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact(Skip = "Conflicts with CommunicationControllerFixture due to shared static state in Octo Runtime Engine")]
    public async Task UnknownEndpoint_ReturnsNotFound()
    {
        // Act
        var response = await Client.GetAsync("/nonexistent", TestContext.Current.CancellationToken);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
