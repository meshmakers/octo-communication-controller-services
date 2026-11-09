using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
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

        var desiredState = await ReadDesiredStateAsync(targetTenantId, inventory, sourceTenants, cancellationToken);
        var writes = await WriteWinningOffersAsync(targetTenantId, inventory, desiredState, cancellationToken);
        var removal = await RemoveUnprovidedDefaultsAsync(targetTenantId, inventory, desiredState, writes.FailedKeys);

        var result = new SecretDefaultTargetResult(targetTenantId, SecretDefaultTargetStatus.Reconciled,
            writes.Created, writes.Updated, removal.RemovedRtIds.Count, removal.Kept + writes.Kept,
            desiredState.UnreadableSourceTenantIds.Count, removal.Failed + writes.Failed, writes.Unchanged);
        if (result.DefaultsWritten + result.DefaultsRemoved + result.WritesFailed + result.SourcesUnreadable > 0)
        {
            Logger.Info(
                "[{TenantId}] Secret defaults reconciled: {Created} created, {Updated} updated, {Unchanged} " +
                "unchanged, {Removed} removed, {Kept} kept, {SourcesUnreadable} source tenant(s) unreadable, " +
                "{Failed} failed", targetTenantId, result.DefaultsCreated, result.DefaultsUpdated,
                result.DefaultsUnchanged, result.DefaultsRemoved, result.DefaultsKept, result.SourcesUnreadable,
                result.WritesFailed);
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
                    SecretDefaultTargetStatus.Failed));
            }
        }

        return results;
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
        SecretDefaultInventory inventory, IReadOnlyList<SourceTenant> sourceTenants,
        CancellationToken cancellationToken)
    {
        var desiredState = new DesiredSecretDefaults();
        foreach (var sourceTenant in sourceTenants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SecretDefaultSources? sources;
            try
            {
                sources = await communicationRepository.GetSecretDefaultSourceApplicationsAsync(sourceTenant.TenantId);
            }
            catch (Exception readFailure) when (readFailure is not OperationCanceledException)
            {
                Logger.Warn(readFailure, "[{TenantId}] Could not read the applications of source tenant " +
                                         "'{SourceTenantId}'", targetTenantId, sourceTenant.TenantId);
                sources = null;
            }

            if (sources is not { Availability: SecretDefaultTenantAvailability.Available })
            {
                desiredState.UnreadableSourceTenantIds.Add(sourceTenant.TenantId);
                await ReportUnreadableSourceAsync(targetTenantId, inventory, sourceTenant.TenantId,
                    sources?.Availability);
                continue;
            }

            foreach (var application in sources.Applications.Where(IsReleasedSource))
            {
                CollectOffers(targetTenantId, sourceTenant, application, desiredState);
            }
        }

        return desiredState;
    }

    private async Task ReportUnreadableSourceAsync(string targetTenantId, SecretDefaultInventory inventory,
        string sourceTenantId, SecretDefaultTenantAvailability? sourceAvailability)
    {
        if (sourceAvailability == SecretDefaultTenantAvailability.ModelMissing)
        {
            var keptDefaultCount = inventory.Defaults.Count(storedDefault =>
                string.Equals(storedDefault.SourceTenantId, sourceTenantId, StringComparison.OrdinalIgnoreCase));
            Logger.Info("[{TenantId}] Source tenant '{SourceTenantId}' lacks the System.Communication version that " +
                        "defines SecretDefaults; its {KeptCount} default(s) here are kept", targetTenantId,
                sourceTenantId, keptDefaultCount);
            if (keptDefaultCount == 0)
            {
                return;
            }

            await communicationEventService.StoreWarningEventAsync(targetTenantId,
                $"Secret defaults: source tenant '{sourceTenantId}' lacks the System.Communication version that " +
                $"defines SecretDefaults; the {keptDefaultCount} default(s) it projected into this tenant are kept.");
            return;
        }

        await communicationEventService.StoreWarningEventAsync(targetTenantId,
            $"Secret defaults: the applications of source tenant '{sourceTenantId}' could not be read; the " +
            "defaults it projected into this tenant are kept until it can be read again.");
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
        SecretDefaultInventory inventory, DesiredSecretDefaults desiredState, IReadOnlySet<string> failedWriteKeys)
    {
        var removal = new RemovalOutcome();
        foreach (var storedDefault in inventory.Defaults)
        {
            if (failedWriteKeys.Contains(storedDefault.Key))
            {
                removal.Kept++;
                continue;
            }

            switch (Classify(storedDefault, desiredState))
            {
                case StoredDefaultStanding.SourceUnreadable:
                    removal.Kept++;
                    continue;
                case StoredDefaultStanding.Provided:
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

    /// <summary>
    ///     Whether a stored default is still provided, kept because its source could not be read, or no longer
    ///     provided by anybody. A default without source fields was not written by a projection: nobody provides it.
    /// </summary>
    private static StoredDefaultStanding Classify(StoredSecretDefault storedDefault, DesiredSecretDefaults desiredState)
    {
        if (storedDefault.SourceTenantId is { } storedSourceTenantId &&
            desiredState.UnreadableSourceTenantIds.Contains(storedSourceTenantId))
        {
            return StoredDefaultStanding.SourceUnreadable;
        }

        return storedDefault is { SourceTenantId: { } sourceTenantId, SourceWorkloadRtId: { } sourceWorkloadRtId } &&
               desiredState.ProvidedDefaults.Contains(new ProvidedDefault(sourceTenantId, sourceWorkloadRtId,
                   storedDefault.Key))
            ? StoredDefaultStanding.Provided
            : StoredDefaultStanding.Unprovided;
    }

    /// <summary>
    ///     The Application entity an event about a default may be linked to: only in the Application's own tenant,
    ///     because an event cannot relate to an entity of another tenant.
    /// </summary>
    private static RtEntityId? ApplicationLinkIn(string eventTenantId, string? sourceTenantId,
        string? applicationRtId)
    {
        if (applicationRtId is null ||
            !string.Equals(eventTenantId, sourceTenantId, StringComparison.OrdinalIgnoreCase) ||
            !OctoObjectId.TryParse(applicationRtId, out var parsedApplicationRtId))
        {
            return null;
        }

        return new RtEntityId(SystemCommunicationCkIds.RtCkApplicationTypeId, parsedApplicationRtId);
    }

    private static bool IsProjectedBy(StoredSecretDefault storedDefault, string sourceTenantId,
        string applicationRtId)
    {
        return string.Equals(storedDefault.SourceTenantId, sourceTenantId, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(storedDefault.SourceWorkloadRtId, applicationRtId, StringComparison.OrdinalIgnoreCase);
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

    private enum StoredDefaultStanding
    {
        Provided,
        SourceUnreadable,
        Unprovided
    }

    private sealed class RemovalOutcome
    {
        public HashSet<string> RemovedRtIds { get; } = new(StringComparer.Ordinal);

        public int Kept { get; set; }

        public int Failed { get; set; }
    }
}
