using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
///     AB#5279 — the invoker of a queued work item, as it is persisted on the
///     <see cref="RtPipelineExecution"/> and read back for the lease. One place for the mapping in
///     both directions, because the seven attributes have to agree with
///     <see cref="ExecutePipelineCaller"/> field for field and three call sites write or read them
///     (enqueue, requeue after an interrupted lease, the queue projection).
/// </summary>
internal static class QueuedCaller
{
    /// <summary>
    ///     Writes the token-free principal and the (already encrypted) token onto the entity. A null
    ///     caller writes nothing — the attributes stay unset, which is what "queued without an
    ///     invoker" looks like on read.
    /// </summary>
    public static void Apply(RtPipelineExecution execution, ExecutePipelineCaller? caller,
        string? encryptedAccessToken)
    {
        if (caller is null)
        {
            return;
        }

        execution.CallerSubjectId = caller.SubjectId;
        execution.CallerTenantId = caller.TenantId;
        execution.CallerEmail = caller.Email;
        execution.CallerName = caller.Name;
        execution.CallerRoles = new AttributeStringValueList(caller.Roles.ToList());
        execution.CallerTrustLevel = caller.TrustLevel;
        execution.CallerAccessToken = encryptedAccessToken;
    }

    /// <summary>
    ///     The principal the entity was queued for, or null when it carries no subject — the
    ///     subject is the one field without which a caller is not a caller; every other attribute
    ///     is decoration on it.
    /// </summary>
    public static ExecutePipelineCaller? Read(RtPipelineExecution execution)
    {
        if (string.IsNullOrEmpty(execution.CallerSubjectId))
        {
            return null;
        }

        return new ExecutePipelineCaller
        {
            SubjectId = execution.CallerSubjectId,
            TenantId = execution.CallerTenantId,
            Email = execution.CallerEmail,
            Name = execution.CallerName,
            Roles = execution.CallerRoles?.ToArray() ?? [],
            TrustLevel = execution.CallerTrustLevel ?? 0
        };
    }

    /// <summary>The encrypted token as stored, or null. Decrypting is the lease service's job.</summary>
    public static string? ReadEncryptedAccessToken(RtPipelineExecution execution) =>
        string.IsNullOrEmpty(execution.CallerAccessToken) ? null : execution.CallerAccessToken;
}
