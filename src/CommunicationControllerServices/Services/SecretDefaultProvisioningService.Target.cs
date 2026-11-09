using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal sealed partial class SecretDefaultProvisioningService
{
    /// <inheritdoc />
    public async Task<SecretDefaultTargetResult> ProvisionForTargetAsync(string targetTenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetTenantId);

        var inventory = await communicationRepository.GetSecretDefaultsAsync(targetTenantId);
        switch (inventory.Availability)
        {
            case SecretDefaultTenantAvailability.TenantUnavailable:
                return SecretDefaultTargetResult.Unchanged(targetTenantId, SecretDefaultTargetStatus.TenantUnavailable);
            case SecretDefaultTenantAvailability.ModelMissing:
                return SecretDefaultTargetResult.Unchanged(targetTenantId, SecretDefaultTargetStatus.ModelMissing);
        }

        IReadOnlyList<SourceTenant> sourceTenants;
        try
        {
            sourceTenants = await ResolveSourceTenantsAsync(targetTenantId, cancellationToken);
        }
        catch (Exception treeFailure) when (treeFailure is not OperationCanceledException)
        {
            Logger.Warn(treeFailure, "[{TenantId}] Secret defaults not reconciled: the tenant tree could not be read; " +
                                     "the {DefaultCount} existing default(s) are kept", targetTenantId,
                inventory.Defaults.Count);
            await communicationEventService.StoreWarningEventAsync(targetTenantId,
                "Secret defaults were not reconciled because the tenant tree could not be read completely; the " +
                $"{inventory.Defaults.Count} existing default(s) are kept.");
            return SecretDefaultTargetResult.Unchanged(targetTenantId, SecretDefaultTargetStatus.TreeUnreadable) with
            {
                DefaultsKept = inventory.Defaults.Count
            };
        }

        var desiredState = await ReadDesiredStateAsync(targetTenantId, sourceTenants, cancellationToken);
        var removal = await RemoveUnprovidedDefaultsAsync(targetTenantId, inventory, desiredState);
        var writes = await WriteWinningOffersAsync(targetTenantId, inventory, desiredState, removal.RemovedRtIds,
            cancellationToken);

        var result = new SecretDefaultTargetResult(targetTenantId, SecretDefaultTargetStatus.Reconciled,
            writes.Created, writes.Updated, removal.RemovedRtIds.Count, removal.Kept + writes.Kept,
            desiredState.UnreadableSourceTenantIds.Count, removal.Failed + writes.Failed);
        if (result.DefaultsCreated + result.DefaultsRemoved + result.WritesFailed + result.SourcesUnreadable > 0)
        {
            Logger.Info(
                "[{TenantId}] Secret defaults reconciled: {Created} created, {Updated} updated, {Removed} removed, " +
                "{Kept} kept, {SourcesUnreadable} source tenant(s) unreadable, {Failed} failed", targetTenantId,
                result.DefaultsCreated, result.DefaultsUpdated, result.DefaultsRemoved, result.DefaultsKept,
                result.SourcesUnreadable, result.WritesFailed);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SecretDefaultTargetResult>> ReconcileTenantAndSubtreeAsync(string tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var descendantTenantIds = await tenantScopeResolver.ResolveLendableTenantsAsync(tenantId,
            new LendingScope(LendingScope.Descendants, null), cancellationToken);
        var reconciledTenantIds = new List<string> { tenantId };
        reconciledTenantIds.AddRange(descendantTenantIds
            .Where(descendantTenantId => !string.Equals(descendantTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(descendantTenantId => descendantTenantId, StringComparer.Ordinal));

        var results = new List<SecretDefaultTargetResult>();
        foreach (var reconciledTenantId in reconciledTenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                results.Add(await ProvisionForTargetAsync(reconciledTenantId, cancellationToken));
            }
            catch (Exception reconcileFailure) when (reconcileFailure is not OperationCanceledException)
            {
                Logger.Error(reconcileFailure, "[{TenantId}] Secret defaults of tenant '{TargetTenantId}' could not " +
                                               "be reconciled", tenantId, reconciledTenantId);
                results.Add(SecretDefaultTargetResult.Unchanged(reconciledTenantId,
                    SecretDefaultTargetStatus.Reconciled) with { WritesFailed = 1 });
            }
        }

        return results;
    }

    private async Task<SecretDefaultProvisioningResult> RemoveWithdrawnDefaultsAsync(ProjectionContext projection,
        IReadOnlyList<CheckedSecretDefaultDeclaration> checkedDeclarations, IReadOnlySet<string> servedTenantIds,
        CancellationToken cancellationToken)
    {
        var applicationRtId = projection.Application.RtId.ToString();
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
                                         "next reconcile", projection.SourceTenantId, servedTenantId,
                    projection.Application.Name);
                continue;
            }

            var withdrawnDefaults = inventory.Defaults
                .Where(storedDefault => IsProjectedBy(storedDefault, projection.SourceTenantId, applicationRtId) &&
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

    private async Task<IReadOnlyList<SourceTenant>> ResolveSourceTenantsAsync(string targetTenantId,
        CancellationToken cancellationToken)
    {
        var targetAncestorTenantIds =
            await tenantScopeResolver.ResolveAncestorTenantsAsync(targetTenantId, cancellationToken);
        var sourceTenants = new List<SourceTenant> { new(targetTenantId, targetAncestorTenantIds) };
        foreach (var ancestorTenantId in targetAncestorTenantIds)
        {
            var ancestorsOfAncestor =
                await tenantScopeResolver.ResolveAncestorTenantsAsync(ancestorTenantId, cancellationToken);
            sourceTenants.Add(new SourceTenant(ancestorTenantId, ancestorsOfAncestor));
        }

        return sourceTenants;
    }

    private async Task<DesiredSecretDefaults> ReadDesiredStateAsync(string targetTenantId,
        IReadOnlyList<SourceTenant> sourceTenants, CancellationToken cancellationToken)
    {
        var desiredState = new DesiredSecretDefaults();
        foreach (var sourceTenant in sourceTenants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyCollection<RtApplication>? applications;
            try
            {
                applications = await communicationRepository.GetSecretDefaultSourceApplicationsAsync(
                    sourceTenant.TenantId);
            }
            catch (Exception readFailure) when (readFailure is not OperationCanceledException)
            {
                Logger.Warn(readFailure, "[{TenantId}] Could not read the applications of source tenant " +
                                         "'{SourceTenantId}'", targetTenantId, sourceTenant.TenantId);
                applications = null;
            }

            if (applications is null)
            {
                desiredState.UnreadableSourceTenantIds.Add(sourceTenant.TenantId);
                await communicationEventService.StoreWarningEventAsync(targetTenantId,
                    $"Secret defaults: the applications of source tenant '{sourceTenant.TenantId}' could not be " +
                    "read; the defaults it projected into this tenant are kept until it can be read again.");
                continue;
            }

            foreach (var application in applications)
            {
                CollectOffers(targetTenantId, sourceTenant, application, desiredState);
            }
        }

        return desiredState;
    }

    private static void CollectOffers(string targetTenantId, SourceTenant sourceTenant, RtApplication application,
        DesiredSecretDefaults desiredState)
    {
        var applicationRtId = application.RtId.ToString();
        var checkedDeclarations = SecretDefaultDeclarationValidator.Check(
            application.SecretDefaults ?? Enumerable.Empty<RtSecretDefaultProjectionRecord>());
        foreach (var checkedDeclaration in checkedDeclarations)
        {
            if (string.IsNullOrEmpty(checkedDeclaration.Key))
            {
                continue;
            }

            if (checkedDeclaration.Issue != SecretDefaultDeclarationIssue.None)
            {
                desiredState.ProvidedDefaults.Add(new ProvidedDefault(sourceTenant.TenantId, applicationRtId,
                    checkedDeclaration.Key));
                continue;
            }

            if (!AllowsTenant(checkedDeclaration.Declaration!, targetTenantId))
            {
                continue;
            }

            desiredState.ProvidedDefaults.Add(new ProvidedDefault(sourceTenant.TenantId, applicationRtId,
                checkedDeclaration.Key));
            desiredState.Offers.Add(new SecretDefaultOffer(sourceTenant, application, checkedDeclaration));
        }
    }

    private async Task<RemovalOutcome> RemoveUnprovidedDefaultsAsync(string targetTenantId,
        SecretDefaultInventory inventory, DesiredSecretDefaults desiredState)
    {
        var removal = new RemovalOutcome();
        foreach (var storedDefault in inventory.Defaults)
        {
            if (storedDefault.SourceTenantId is { } storedSourceTenantId &&
                desiredState.UnreadableSourceTenantIds.Contains(storedSourceTenantId))
            {
                removal.Kept++;
                continue;
            }

            if (storedDefault is { SourceTenantId: { } sourceTenantId, SourceWorkloadRtId: { } sourceWorkloadRtId } &&
                desiredState.ProvidedDefaults.Contains(new ProvidedDefault(sourceTenantId, sourceWorkloadRtId,
                    storedDefault.Key)))
            {
                continue;
            }

            switch (await TryDeleteAsync(targetTenantId, storedDefault))
            {
                case RemovalAttempt.Removed:
                    removal.RemovedRtIds.Add(storedDefault.RtId.ToString());
                    break;
                case RemovalAttempt.Failed:
                    removal.Failed++;
                    break;
            }
        }

        return removal;
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
            await communicationEventService.StoreErrorEventAsync(tenantId,
                $"Secret default '{storedDefault.Key}' is no longer provided to this tenant but could not be " +
                "removed. The next reconcile retries it.");
            return RemovalAttempt.Failed;
        }
    }

    private async Task<WriteOutcome> WriteWinningOffersAsync(string targetTenantId, SecretDefaultInventory inventory,
        DesiredSecretDefaults desiredState, IReadOnlySet<string> removedRtIds, CancellationToken cancellationToken)
    {
        var outcome = new WriteOutcome();
        foreach (var offersOfKey in desiredState.Offers.GroupBy(offer => offer.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remainingStoredDefault = inventory.Defaults.FirstOrDefault(storedDefault =>
                storedDefault.Key == offersOfKey.Key && !removedRtIds.Contains(storedDefault.RtId.ToString()));
            var winningOffer = ChooseWinningOffer(offersOfKey.ToList(), remainingStoredDefault);
            await WriteOfferAsync(targetTenantId, winningOffer, outcome);
        }

        return outcome;
    }

    private static SecretDefaultOffer ChooseWinningOffer(IReadOnlyList<SecretDefaultOffer> offersOfKey,
        StoredSecretDefault? remainingStoredDefault)
    {
        var closestDepth = offersOfKey.Min(offer => offer.SourceTenant.Depth);
        var closestOffers = offersOfKey
            .Where(offer => offer.SourceTenant.Depth == closestDepth)
            .OrderBy(offer => offer.ApplicationRtId, StringComparer.Ordinal)
            .ToList();
        return closestOffers.FirstOrDefault(offer => remainingStoredDefault is not null &&
                                                     IsProjectedBy(remainingStoredDefault,
                                                         offer.SourceTenant.TenantId, offer.ApplicationRtId))
               ?? closestOffers[0];
    }

    private async Task WriteOfferAsync(string targetTenantId, SecretDefaultOffer offer, WriteOutcome outcome)
    {
        var application = offer.Application;
        var sourceTenantId = offer.SourceTenant.TenantId;
        var revealAccess = new SecretAccessContext(sourceTenantId, application.CkTypeId?.ToString(),
            SecretValueAttributePath);
        var plaintext = encryptionService.RevealOrNull(offer.CheckedDeclaration.Declaration!.SecretValue,
            revealAccess);
        if (string.IsNullOrEmpty(plaintext))
        {
            outcome.Failed++;
            Logger.Error("[{TenantId}] Secret default '{Key}' of application '{ApplicationName}' in tenant " +
                         "'{SourceTenantId}' has no readable value; it was not written", targetTenantId, offer.Key,
                application.Name, sourceTenantId);
            await communicationEventService.StoreErrorEventAsync(targetTenantId,
                $"Secret default '{offer.Key}' of application '{application.Name}' in tenant '{sourceTenantId}' has " +
                "no readable value and was not written into this tenant.");
            return;
        }

        var write = new SecretDefaultWrite(offer.Key, RtSecretValue.Pending(plaintext), sourceTenantId,
            offer.ApplicationRtId, timeProvider.GetUtcNow().UtcDateTime, offer.SourceTenant.AncestorTenantIds);
        try
        {
            var writeResult = await communicationRepository.UpsertSecretDefaultAsync(targetTenantId, write);
            switch (writeResult.Outcome)
            {
                case SecretDefaultWriteOutcome.Created:
                    outcome.Created++;
                    break;
                case SecretDefaultWriteOutcome.Updated:
                    outcome.Updated++;
                    break;
                default:
                    outcome.Kept++;
                    break;
            }
        }
        catch (Exception writeFailure) when (writeFailure is not OperationCanceledException)
        {
            outcome.Failed++;
            Logger.Error(writeFailure, "[{TenantId}] Could not write secret default '{Key}' from tenant " +
                                       "'{SourceTenantId}'", targetTenantId, offer.Key, sourceTenantId);
            await communicationEventService.StoreErrorEventAsync(targetTenantId,
                $"Secret default '{offer.Key}' from tenant '{sourceTenantId}' could not be written into this tenant. " +
                "The next reconcile retries it.");
        }
    }

    private static bool IsProjectedBy(StoredSecretDefault storedDefault, string sourceTenantId,
        string applicationRtId)
    {
        return string.Equals(storedDefault.SourceTenantId, sourceTenantId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(storedDefault.SourceWorkloadRtId, applicationRtId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StillProvides(IReadOnlyList<CheckedSecretDefaultDeclaration> checkedDeclarations, string key,
        string targetTenantId)
    {
        return checkedDeclarations.Any(checkedDeclaration =>
            checkedDeclaration.Key == key &&
            (checkedDeclaration.Issue != SecretDefaultDeclarationIssue.None ||
             AllowsTenant(checkedDeclaration.Declaration!, targetTenantId)));
    }

    private static bool AllowsTenant(RtSecretDefaultProjectionRecord declaration, string targetTenantId)
    {
        var allowedTenantIds = declaration.AllowedTenantIds?.ToList();
        return allowedTenantIds is null || allowedTenantIds.Count == 0 ||
               allowedTenantIds.Any(allowedTenantId =>
                   string.Equals(allowedTenantId.Trim(), targetTenantId, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SourceTenant(string tenantId, IReadOnlyCollection<string> ancestorTenantIds)
    {
        public string TenantId { get; } = tenantId;

        public IReadOnlyCollection<string> AncestorTenantIds { get; } = ancestorTenantIds;

        public int Depth => AncestorTenantIds.Count;
    }

    private sealed class SecretDefaultOffer(
        SourceTenant sourceTenant,
        RtApplication application,
        CheckedSecretDefaultDeclaration checkedDeclaration)
    {
        public SourceTenant SourceTenant { get; } = sourceTenant;

        public RtApplication Application { get; } = application;

        public CheckedSecretDefaultDeclaration CheckedDeclaration { get; } = checkedDeclaration;

        public string Key => CheckedDeclaration.Key;

        public string ApplicationRtId => Application.RtId.ToString();
    }

    private readonly record struct ProvidedDefault(string SourceTenantId, string SourceWorkloadRtId, string Key)
    {
        public bool Equals(ProvidedDefault other)
        {
            return string.Equals(SourceTenantId, other.SourceTenantId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(SourceWorkloadRtId, other.SourceWorkloadRtId, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(Key, other.Key, StringComparison.Ordinal);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(SourceTenantId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(SourceWorkloadRtId),
                StringComparer.Ordinal.GetHashCode(Key));
        }
    }

    private sealed class DesiredSecretDefaults
    {
        public HashSet<string> UnreadableSourceTenantIds { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<ProvidedDefault> ProvidedDefaults { get; } = [];

        public List<SecretDefaultOffer> Offers { get; } = [];
    }

    private enum RemovalAttempt
    {
        Removed,
        Skipped,
        Failed
    }

    private sealed class RemovalOutcome
    {
        public HashSet<string> RemovedRtIds { get; } = new(StringComparer.Ordinal);

        public int Kept { get; set; }

        public int Failed { get; set; }
    }

    private sealed class WriteOutcome
    {
        public int Created { get; set; }

        public int Updated { get; set; }

        public int Kept { get; set; }

        public int Failed { get; set; }
    }
}
