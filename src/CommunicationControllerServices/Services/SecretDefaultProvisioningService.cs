using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="ISecretDefaultProvisioningService" />
internal sealed class SecretDefaultProvisioningService(
    ICommunicationRepository communicationRepository,
    ITenantLendingScopeResolver tenantScopeResolver,
    IWorkloadEncryptionService encryptionService,
    ICommunicationEventService communicationEventService,
    TimeProvider timeProvider)
    : ISecretDefaultProvisioningService
{
    private const string SecretValueAttributePath =
        $"{nameof(RtApplication.SecretDefaults)}.{nameof(RtSecretDefaultProjectionRecord.SecretValue)}";

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    public async Task<SecretDefaultProvisioningResult> ProvisionForSourceAsync(string sourceTenantId,
        OctoObjectId applicationRtId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceTenantId);

        var workload = await communicationRepository.GetWorkloadByRtIdAsync(sourceTenantId, applicationRtId);
        if (workload is not RtApplication application || application.SecretDefaults is not { Count: > 0 } declarations)
        {
            return SecretDefaultProvisioningResult.Nothing;
        }

        var servedTenantIds = await ResolveServedTenantsAsync(sourceTenantId, cancellationToken);
        var applicationEntityId = new RtEntityId(SystemCommunicationCkIds.RtCkApplicationTypeId, application.RtId);
        var result = SecretDefaultProvisioningResult.Nothing;

        foreach (var declaration in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = Combine(result,
                await ProvisionDeclarationAsync(sourceTenantId, application, applicationEntityId, declaration,
                    servedTenantIds, cancellationToken));
        }

        Logger.Info(
            "[{TenantId}] Secret defaults of application '{ApplicationName}' ({ApplicationRtId}) projected: " +
            "{DefaultsWritten} written, {TargetsSkipped} skipped, {TargetsFailed} failed",
            sourceTenantId, application.Name, application.RtId, result.DefaultsWritten, result.TargetsSkipped,
            result.TargetsFailed);
        return result;
    }

    private async Task<HashSet<string>> ResolveServedTenantsAsync(string sourceTenantId,
        CancellationToken cancellationToken)
    {
        var descendantTenantIds = await tenantScopeResolver.ResolveLendableTenantsAsync(sourceTenantId,
            new LendingScope(LendingScope.Descendants, null), cancellationToken);

        var servedTenantIds = new HashSet<string>(descendantTenantIds, StringComparer.OrdinalIgnoreCase)
        {
            sourceTenantId
        };
        return servedTenantIds;
    }

    private async Task<SecretDefaultProvisioningResult> ProvisionDeclarationAsync(string sourceTenantId,
        RtApplication application, RtEntityId applicationEntityId, RtSecretDefaultProjectionRecord declaration,
        IReadOnlySet<string> servedTenantIds, CancellationToken cancellationToken)
    {
        var key = declaration.Key.Trim();
        var revealAccess = new SecretAccessContext(sourceTenantId, application.CkTypeId?.ToString(),
            SecretValueAttributePath);
        var plaintext = encryptionService.RevealOrNull(declaration.SecretValue, revealAccess);
        if (string.IsNullOrEmpty(plaintext))
        {
            Logger.Error("[{TenantId}] Secret default '{Key}' of application '{ApplicationName}' ({ApplicationRtId}) " +
                         "has no readable value; it was not projected", sourceTenantId, key, application.Name,
                application.RtId);
            await communicationEventService.StoreErrorEventAsync(sourceTenantId,
                $"Secret default '{key}' of application '{application.Name}' has no readable value and was not " +
                "projected. Set its SecretValue again and redeploy the application.", applicationEntityId);
            return new SecretDefaultProvisioningResult(0, 0, 1);
        }

        var targetTenantIds = NarrowToAllowedTenants(servedTenantIds, declaration.AllowedTenantIds?.ToList());
        var written = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var targetTenantId in targetTenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var write = new SecretDefaultWrite(key, RtSecretValue.Pending(plaintext), sourceTenantId,
                application.RtId.ToString(), timeProvider.GetUtcNow().UtcDateTime);
            try
            {
                var outcome = await communicationRepository.UpsertSecretDefaultAsync(targetTenantId, write);
                switch (outcome)
                {
                    case SecretDefaultWriteOutcome.Created:
                    case SecretDefaultWriteOutcome.Updated:
                        written++;
                        break;
                    default:
                        skipped++;
                        await ReportSkippedTargetAsync(sourceTenantId, application, applicationEntityId, key,
                            targetTenantId, outcome);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception writeFailure)
            {
                failed++;
                Logger.Error(writeFailure,
                    "[{TenantId}] Could not write secret default '{Key}' of application '{ApplicationName}' " +
                    "({ApplicationRtId}) into tenant '{TargetTenantId}'", sourceTenantId, key, application.Name,
                    application.RtId, targetTenantId);
                await communicationEventService.StoreErrorEventAsync(sourceTenantId,
                    $"Secret default '{key}' of application '{application.Name}' could not be written into tenant " +
                    $"'{targetTenantId}'. The next deploy of the application retries it.", applicationEntityId);
            }
        }

        return new SecretDefaultProvisioningResult(written, skipped, failed);
    }

    private async Task ReportSkippedTargetAsync(string sourceTenantId, RtApplication application,
        RtEntityId applicationEntityId, string key, string targetTenantId, SecretDefaultWriteOutcome outcome)
    {
        var reason = outcome == SecretDefaultWriteOutcome.ModelMissing
            ? "it does not have the System.Communication version that defines SecretDefault"
            : "it cannot be resolved";
        Logger.Warn("[{TenantId}] Secret default '{Key}' of application '{ApplicationName}' ({ApplicationRtId}) " +
                    "skipped tenant '{TargetTenantId}': {Reason}", sourceTenantId, key, application.Name,
            application.RtId, targetTenantId, reason);
        await communicationEventService.StoreWarningEventAsync(sourceTenantId,
            $"Secret default '{key}' of application '{application.Name}' was not written into tenant " +
            $"'{targetTenantId}': {reason}.", applicationEntityId);
    }

    private static IReadOnlyCollection<string> NarrowToAllowedTenants(IReadOnlySet<string> servedTenantIds,
        IReadOnlyCollection<string>? allowedTenantIds)
    {
        if (allowedTenantIds is null || allowedTenantIds.Count == 0)
        {
            return servedTenantIds.ToList();
        }

        var allowed = new HashSet<string>(allowedTenantIds.Select(tenantId => tenantId.Trim()),
            StringComparer.OrdinalIgnoreCase);
        return servedTenantIds.Where(allowed.Contains).ToList();
    }

    private static SecretDefaultProvisioningResult Combine(SecretDefaultProvisioningResult total,
        SecretDefaultProvisioningResult part)
    {
        return new SecretDefaultProvisioningResult(total.DefaultsWritten + part.DefaultsWritten,
            total.TargetsSkipped + part.TargetsSkipped, total.TargetsFailed + part.TargetsFailed);
    }
}
