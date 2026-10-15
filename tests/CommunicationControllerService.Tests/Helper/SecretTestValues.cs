using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;

/// <summary>
/// AB#5537: fixtures for Secret attributes (System.Communication 4.8). Stored values are built the
/// way the engine leaves them, written values are read back through a real protector.
/// </summary>
internal static class SecretTestValues
{
    private static readonly TestSecretEncryption Shared = TestSecretEncryption.Create();

    /// <summary>
    /// A secret as found in storage before the encrypt sweep: legacy plaintext; blank values are
    /// "not set" (<c>null</c>). A legacy seed placeholder (<c>&lt;…&gt;</c>, <c>TODO_SET_*</c>) stays a
    /// legacy string, as the engine finds it until the migration converts it — readers classify it as
    /// not set (decisions 2026-10-06, item 1).
    /// </summary>
    public static RtSecretValue? Stored(string? plaintext)
    {
        return string.IsNullOrWhiteSpace(plaintext) ? null : RtSecretValue.LegacyPlaintext(plaintext);
    }

    /// <summary>A protected (enc:v2) value under the shared test key ring.</summary>
    public static RtSecretValue Protected(string plaintext)
    {
        return Shared.Protector.Protect(plaintext);
    }

    /// <summary>The plaintext of a pending (written) or legacy value; <c>null</c> when not set.</summary>
    public static string? Plain(RtSecretValue? value)
    {
        return value == null ? null : Shared.Protector.Unprotect(value);
    }
}
