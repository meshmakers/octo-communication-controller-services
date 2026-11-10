using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal sealed partial class SecretDefaultProvisioningService
{
    /// <inheritdoc />
    public async Task<SecretDefaultProvisioningResult> WithdrawForSourceAsync(string sourceTenantId,
        OctoObjectId applicationRtId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTenantId);

        var workload = await communicationRepository.GetWorkloadByRtIdAsync(sourceTenantId, applicationRtId);
        if (workload is not null and not RtApplication)
        {
            return SecretDefaultProvisioningResult.Nothing;
        }

        var applicationName = workload?.Name;
        var servedTenantIds = await ResolveServedTenantsAsync(sourceTenantId, cancellationToken);
        var result = await RemoveWithdrawnDefaultsAsync(sourceTenantId, applicationRtId.ToString(), applicationName,
            [], servedTenantIds, cancellationToken);

        if (result != SecretDefaultProvisioningResult.Nothing)
        {
            Logger.Info("[{TenantId}] Secret defaults of application '{ApplicationName}' ({ApplicationRtId}) " +
                        "withdrawn: {DefaultsRemoved} removed, {TargetsFailed} failed", sourceTenantId,
                applicationName, applicationRtId, result.DefaultsRemoved, result.TargetsFailed);
            await communicationEventService.StoreInformationEventAsync(sourceTenantId,
                $"Secret defaults of application '{applicationName}' withdrawn: {result.DefaultsRemoved} removed, " +
                $"{result.TargetsFailed} failed.",
                new RtEntityId(SystemCommunicationCkIds.RtCkApplicationTypeId, applicationRtId));
        }

        return result;
    }

    /// <summary>
    ///     Only an Application whose deploy was accepted (Pending) or completed (Deployed) is a source. One that was
    ///     never deployed, is undeployed or disabled, or was refused (Error) offers nothing.
    /// </summary>
    internal static bool IsReleasedSource(RtApplication application)
    {
        return application.DeploymentState is RtDeploymentStateEnum.Pending or RtDeploymentStateEnum.Deployed;
    }

    private async Task<SecretDefaultProvisioningResult> RemoveWithdrawnDefaultsAsync(string sourceTenantId,
        string applicationRtId, string? applicationName,
        IReadOnlyList<CheckedSecretDefaultDeclaration> checkedDeclarations, IReadOnlySet<string> servedTenantIds,
        CancellationToken cancellationToken)
    {
        var removed = 0;
        var failed = 0;

        foreach (var servedTenantId in servedTenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SecretDefaultInventory inventory;
            try
            {
                inventory = await communicationRepository.GetSecretDefaultsAsync(servedTenantId);
            }
            catch (Exception readFailure) when (readFailure is not OperationCanceledException)
            {
                Logger.Warn(readFailure, "[{TenantId}] Could not read the secret defaults of tenant '{TargetTenantId}'; " +
                                         "withdrawn defaults of application '{ApplicationName}' stay there until its " +
                                         "next reconcile", sourceTenantId, servedTenantId, applicationName);
                continue;
            }

            var withdrawnDefaults = inventory.Defaults
                .Where(storedDefault => IsProjectedBy(storedDefault, sourceTenantId, applicationRtId) &&
                                        !StillProvides(checkedDeclarations, storedDefault.Key, servedTenantId))
                .ToList();
            var removedInTenant = 0;
            foreach (var withdrawnDefault in withdrawnDefaults)
            {
                switch (await TryDeleteAsync(servedTenantId, withdrawnDefault))
                {
                    case RemovalAttempt.Removed:
                        removedInTenant++;
                        break;
                    case RemovalAttempt.Failed:
                        failed++;
                        break;
                }
            }

            removed += removedInTenant;
            if (removedInTenant > 0)
            {
                await HandOverToRemainingSourcesAsync(servedTenantId, cancellationToken);
            }
        }

        return new SecretDefaultProvisioningResult(0, 0, failed, removed);
    }

    private async Task HandOverToRemainingSourcesAsync(string targetTenantId, CancellationToken cancellationToken)
    {
        try
        {
            await ProvisionForTargetAsync(targetTenantId, cancellationToken);
        }
        catch (Exception reconcileFailure) when (reconcileFailure is not OperationCanceledException)
        {
            Logger.Warn(reconcileFailure, "[{TenantId}] Secret defaults of the tenant could not be reconciled after a " +
                                          "removal; the next tenant load retries", targetTenantId);
        }
    }

    private async Task<RemovalAttempt> TryDeleteAsync(string tenantId, StoredSecretDefault storedDefault)
    {
        try
        {
            var deleteOutcome = await communicationRepository.DeleteSecretDefaultIfUnchangedAsync(tenantId,
                storedDefault);
            if (deleteOutcome != SecretDefaultDeleteOutcome.Deleted)
            {
                Logger.Info("[{TenantId}] Secret default '{Key}' from tenant '{SourceTenantId}' was not removed: it " +
                            "{DeleteOutcome} since it was read", tenantId, storedDefault.Key,
                    storedDefault.SourceTenantId,
                    deleteOutcome == SecretDefaultDeleteOutcome.Changed ? "was written again" : "was removed");
                return RemovalAttempt.Skipped;
            }

            Logger.Info("[{TenantId}] Secret default '{Key}' from tenant '{SourceTenantId}' removed: no application " +
                        "provides it to this tenant any more", tenantId, storedDefault.Key, storedDefault.SourceTenantId);
            return RemovalAttempt.Removed;
        }
        catch (Exception deleteFailure) when (deleteFailure is not OperationCanceledException)
        {
            Logger.Error(deleteFailure, "[{TenantId}] Could not remove secret default '{Key}'", tenantId,
                storedDefault.Key);
            await TryStoreErrorEventAsync(tenantId,
                $"Secret default '{storedDefault.Key}' of application '{storedDefault.SourceWorkloadRtId}' in tenant " +
                $"'{storedDefault.SourceTenantId}' is no longer provided to this tenant but could not be removed. " +
                "The next reconcile retries it.",
                ApplicationLinkIn(tenantId, storedDefault.SourceTenantId, storedDefault.SourceWorkloadRtId));
            return RemovalAttempt.Failed;
        }
    }

    /// <summary>
    ///     Stores the Error event of a failed write or removal. The failure usually means the tenant's database is
    ///     unreachable, so the event write may fail too; that must not end the run for the remaining tenants.
    /// </summary>
    private async Task TryStoreErrorEventAsync(string tenantId, string message, RtEntityId? applicationLink)
    {
        try
        {
            await communicationEventService.StoreErrorEventAsync(tenantId, message, applicationLink);
        }
        catch (Exception eventFailure) when (eventFailure is not OperationCanceledException)
        {
            Logger.Warn(eventFailure, "[{TenantId}] Could not store a secret default failure event", tenantId);
        }
    }

    private static bool StillProvides(IReadOnlyList<CheckedSecretDefaultDeclaration> checkedDeclarations, string key,
        string targetTenantId)
    {
        return checkedDeclarations.Any(checkedDeclaration =>
            checkedDeclaration.Key == key &&
            (checkedDeclaration.Issue != SecretDefaultDeclarationIssue.None ||
             AllowsTenant(checkedDeclaration.Declaration!, targetTenantId)));
    }

    private enum RemovalAttempt
    {
        Removed,
        Skipped,
        Failed
    }
}
