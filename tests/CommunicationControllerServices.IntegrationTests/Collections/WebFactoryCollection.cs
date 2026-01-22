using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Infrastructure;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Collections;

/// <summary>
/// Collection definition for tests that require the WebApplicationFactory.
/// All tests in this collection will share the same fixture instance.
/// Name starts with "Z" to ensure it runs after other collections.
/// </summary>
[CollectionDefinition("ZWebFactory")]
public class WebFactoryCollection : ICollectionFixture<CustomWebApplicationFactory>
{
}
