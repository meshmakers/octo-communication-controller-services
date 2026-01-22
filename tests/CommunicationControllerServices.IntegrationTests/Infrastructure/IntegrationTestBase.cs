namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Infrastructure;

/// <summary>
/// Base class for HTTP-based integration tests.
/// Tests using this base class should be in the "WebFactory" collection.
/// </summary>
public abstract class IntegrationTestBase(CustomWebApplicationFactory factory)
{
    protected readonly HttpClient Client = factory.CreateClient();
    protected readonly CustomWebApplicationFactory Factory = factory;
}
