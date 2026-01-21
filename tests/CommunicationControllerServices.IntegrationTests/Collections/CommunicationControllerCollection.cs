using Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Fixtures;
using Xunit;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.IntegrationTests.Collections;

/// <summary>
/// Collection definition for tests that require the Communication Controller fixture.
/// All tests in this collection will share the same fixture instance.
/// </summary>
[CollectionDefinition("CommunicationController")]
public class CommunicationControllerCollection : ICollectionFixture<CommunicationControllerFixture>
{
}
