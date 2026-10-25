using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;

/// <summary>
/// AB#7472: the REAL <see cref="ExecutionEnvelope" /> over the real engine key ring with freshly generated keys,
/// so seal and open tests exercise the actual authenticated encryption rather than a stub.
/// </summary>
internal static class TestExecutionEnvelope
{
    /// <param name="withKeyRing">false = a controller without any <c>SecretEncryption</c> configuration</param>
    public static ExecutionEnvelope Create(bool withKeyRing = true) =>
        new(TestSecretEncryption.Create(withKeyRing).Protector);
}
