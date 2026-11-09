using System.Security.Cryptography;
using System.Text;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using MongoDB.Driver;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
///     The RtId of a new SecretDefault is derived from its key. Two writers that create the same key at the same time
///     therefore insert the same document id: one of them fails and retries as an update, instead of both inserting
///     and leaving two entities with one name, which the consumer refuses.
/// </summary>
internal static class SecretDefaultIdentity
{
    /// <summary>Attempts of one upsert; the repeats only follow a create collision.</summary>
    internal const int MaximumUpsertAttempts = 3;

    private const string RtIdNamespace = "System.Communication/SecretDefault:";
    private const int ObjectIdByteLength = 12;
    private const int WriteConflictCode = 112;
    private const int DuplicateKeyCode = 11000;
    private const string TransientTransactionErrorLabel = "TransientTransactionError";

    /// <summary>The RtId a SecretDefault with this key is created with.</summary>
    public static OctoObjectId RtIdFor(string key)
    {
        var keyHash = SHA256.HashData(Encoding.UTF8.GetBytes(RtIdNamespace + key));
        var objectIdHex = Convert.ToHexStringLower(keyHash.AsSpan(0, ObjectIdByteLength));
        return new OctoObjectId(objectIdHex);
    }

    /// <summary>True when the stored entity is still the default that was read as <paramref name="expectedDefault" />.</summary>
    public static bool IsUnchanged(RtSecretDefault currentDefault, StoredSecretDefault expectedDefault)
    {
        return currentDefault.RtId == expectedDefault.RtId &&
               string.Equals(currentDefault.RtWellKnownName, expectedDefault.Key, StringComparison.Ordinal) &&
               string.Equals(currentDefault.SourceTenantId, expectedDefault.SourceTenantId, StringComparison.Ordinal) &&
               string.Equals(currentDefault.SourceWorkloadRtId, expectedDefault.SourceWorkloadRtId,
                   StringComparison.Ordinal) &&
               Nullable.Equals(currentDefault.LastProjectedAt, expectedDefault.LastProjectedAt) &&
               currentDefault.RtVersion == expectedDefault.RtVersion;
    }

    /// <summary>
    ///     True when the exception or one of its causes is a write conflict, a transient transaction error or a
    ///     duplicate key: what a concurrent create of the same SecretDefault produces.
    /// </summary>
    public static bool IsConcurrentCreateCollision(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AggregateException aggregate && aggregate.InnerExceptions.Any(IsConcurrentCreateCollision))
            {
                return true;
            }

            switch (current)
            {
                case MongoException mongoException when mongoException.HasErrorLabel(TransientTransactionErrorLabel):
                case MongoCommandException { Code: WriteConflictCode or DuplicateKeyCode }:
                case MongoWriteException { WriteError.Code: WriteConflictCode or DuplicateKeyCode }:
                case MongoBulkWriteException bulkWriteException when bulkWriteException.WriteErrors.Any(writeError =>
                    writeError.Code is WriteConflictCode or DuplicateKeyCode):
                    return true;
            }
        }

        return false;
    }
}
