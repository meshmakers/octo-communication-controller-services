using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="ISecretDefaultProvisioningService" />
internal sealed partial class SecretDefaultProvisioningService(
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
        if (workload is not RtApplication application)
        {
            return SecretDefaultProvisioningResult.Nothing;
        }

        var projection = new ProjectionContext(sourceTenantId, application,
            new RtEntityId(SystemCommunicationCkIds.RtCkApplicationTypeId, application.RtId));
        var result = SecretDefaultProvisioningResult.Nothing;

        var checkedDeclarations =
            SecretDefaultDeclarationValidator.Check(application.SecretDefaults ?? Enumerable.Empty<RtSecretDefaultProjectionRecord>());
        var validDeclarations = new List<CheckedSecretDefaultDeclaration>();
        foreach (var checkedDeclaration in checkedDeclarations)
        {
            if (checkedDeclaration.Issue == SecretDefaultDeclarationIssue.None)
            {
                validDeclarations.Add(checkedDeclaration);
                continue;
            }

            await ReportInvalidDeclarationAsync(projection, checkedDeclaration);
            result = Combine(result, new SecretDefaultProvisioningResult(0, 0, 1));
        }

        var servedTenantIds = await ResolveServedTenantsAsync(sourceTenantId, cancellationToken);
        if (validDeclarations.Count > 0)
        {
            var ancestorTenantIds =
                await tenantScopeResolver.ResolveAncestorTenantsAsync(sourceTenantId, cancellationToken);

            foreach (var validDeclaration in validDeclarations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = Combine(result,
                    await ProvisionDeclarationAsync(projection, validDeclaration, servedTenantIds, ancestorTenantIds,
                        cancellationToken));
            }
        }

        result = Combine(result,
            await RemoveWithdrawnDefaultsAsync(sourceTenantId, application.RtId.ToString(), application.Name,
                checkedDeclarations, servedTenantIds, cancellationToken));

        if (result != SecretDefaultProvisioningResult.Nothing)
        {
            Logger.Info(
                "[{TenantId}] Secret defaults of application '{ApplicationName}' ({ApplicationRtId}) projected: " +
                "{DefaultsWritten} written, {DefaultsRemoved} removed, {TargetsSkipped} skipped, {TargetsFailed} failed",
                sourceTenantId, application.Name, application.RtId, result.DefaultsWritten, result.DefaultsRemoved,
                result.TargetsSkipped, result.TargetsFailed);
            await communicationEventService.StoreInformationEventAsync(sourceTenantId,
                $"Secret defaults of application '{application.Name}' projected: {result.DefaultsWritten} written, " +
                $"{result.DefaultsRemoved} removed, {result.TargetsSkipped} tenant(s) skipped, " +
                $"{result.TargetsFailed} failed.", projection.ApplicationEntityId);
        }

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

    private async Task<SecretDefaultProvisioningResult> ProvisionDeclarationAsync(ProjectionContext projection,
        CheckedSecretDefaultDeclaration checkedDeclaration, IReadOnlySet<string> servedTenantIds,
        IReadOnlyCollection<string> ancestorTenantIds, CancellationToken cancellationToken)
    {
        var declaration = checkedDeclaration.Declaration!;
        var key = checkedDeclaration.Key;
        var application = projection.Application;
        var revealAccess = new SecretAccessContext(projection.SourceTenantId, application.CkTypeId?.ToString(),
            SecretValueAttributePath);
        string? plaintext;
        try
        {
            plaintext = encryptionService.RevealOrNull(declaration.SecretValue, revealAccess);
        }
        catch (Exception revealFailure) when (revealFailure is not OperationCanceledException)
        {
            Logger.Error(revealFailure, "[{TenantId}] Secret default '{Key}' of application '{ApplicationName}' " +
                                        "({ApplicationRtId}) cannot be revealed on this host; it was not projected",
                projection.SourceTenantId, key, application.Name, application.RtId);
            await TryStoreErrorEventAsync(projection.SourceTenantId,
                $"Secret default '{key}' of application '{application.Name}' cannot be revealed on this host (the " +
                "controller has no key ring for secrets, or strict mode refuses a value stored in clear text) and was " +
                "not projected; a tenant the entry still reaches keeps the value it holds. Fix the controller's secret encryption, then " +
                "deploy the application again or run the secret defaults reconcile.", projection.ApplicationEntityId);
            return new SecretDefaultProvisioningResult(0, 0, 1);
        }

        if (string.IsNullOrEmpty(plaintext))
        {
            Logger.Error("[{TenantId}] Secret default '{Key}' of application '{ApplicationName}' ({ApplicationRtId}) " +
                         "has no readable value; it was not projected", projection.SourceTenantId, key,
                application.Name, application.RtId);
            await TryStoreErrorEventAsync(projection.SourceTenantId,
                $"Secret default '{key}' of application '{application.Name}' has no readable value and was not " +
                "projected. Set its SecretValue again and redeploy the application.", projection.ApplicationEntityId);
            return new SecretDefaultProvisioningResult(0, 0, 1);
        }

        var targetTenantIds = NarrowToAllowedTenants(servedTenantIds, declaration.AllowedTenantIds?.ToList());
        var written = 0;
        var skipped = 0;
        var failed = 0;

        foreach (var targetTenantId in targetTenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var write = new SecretDefaultWrite(key, RtSecretValue.Pending(plaintext), projection.SourceTenantId,
                application.RtId.ToString(), timeProvider.GetUtcNow().UtcDateTime, ancestorTenantIds);
            try
            {
                var writeResult = await communicationRepository.UpsertSecretDefaultAsync(targetTenantId, write);
                switch (writeResult.Outcome)
                {
                    case SecretDefaultWriteOutcome.Created:
                    case SecretDefaultWriteOutcome.Updated:
                        written++;
                        break;
                    default:
                        skipped++;
                        await ReportSkippedTargetAsync(projection, key, targetTenantId, writeResult);
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
                    "({ApplicationRtId}) into tenant '{TargetTenantId}'", projection.SourceTenantId, key,
                    application.Name, application.RtId, targetTenantId);
                await TryStoreErrorEventAsync(projection.SourceTenantId,
                    $"Secret default '{key}' of application '{application.Name}' could not be written into tenant " +
                    $"'{targetTenantId}'. The next deploy of the application retries it.",
                    projection.ApplicationEntityId);
            }
        }

        return new SecretDefaultProvisioningResult(written, skipped, failed);
    }

    private async Task ReportInvalidDeclarationAsync(ProjectionContext projection,
        CheckedSecretDefaultDeclaration checkedDeclaration)
    {
        var entryName = string.IsNullOrEmpty(checkedDeclaration.Key)
            ? "an entry without a key"
            : $"'{checkedDeclaration.Key}'";
        var problem = checkedDeclaration.Issue switch
        {
            SecretDefaultDeclarationIssue.KeyEmpty => "its key is empty",
            SecretDefaultDeclarationIssue.KeyInvalid => "its key has invalid characters or is too long",
            SecretDefaultDeclarationIssue.KeyDuplicate => "its key is declared more than once",
            _ => "it has no SecretValue"
        };
        var application = projection.Application;
        Logger.Error("[{TenantId}] Secret default {EntryName} of application '{ApplicationName}' ({ApplicationRtId}) " +
                     "was not projected: {Problem}", projection.SourceTenantId, entryName, application.Name,
            application.RtId, problem);
        await TryStoreErrorEventAsync(projection.SourceTenantId,
            $"Secret default {entryName} of application '{application.Name}' was not projected: {problem}.",
            projection.ApplicationEntityId);
    }

    private async Task ReportSkippedTargetAsync(ProjectionContext projection, string key, string targetTenantId,
        SecretDefaultWriteResult writeResult)
    {
        var reason = writeResult.Outcome switch
        {
            SecretDefaultWriteOutcome.ModelMissing =>
                "it does not have the System.Communication version that defines SecretDefault",
            SecretDefaultWriteOutcome.KeptAncestorSource =>
                $"it keeps the default of tenant '{writeResult.KeptSourceTenantId}', which is closer to the root " +
                $"than the refused source '{projection.SourceTenantId}'",
            _ => "it cannot be resolved"
        };
        var application = projection.Application;
        Logger.Warn("[{TenantId}] Secret default '{Key}' of application '{ApplicationName}' ({ApplicationRtId}) " +
                    "skipped tenant '{TargetTenantId}': {Reason}", projection.SourceTenantId, key, application.Name,
            application.RtId, targetTenantId, reason);
        await communicationEventService.StoreWarningEventAsync(projection.SourceTenantId,
            $"Secret default '{key}' of application '{application.Name}' was not written into tenant " +
            $"'{targetTenantId}': {reason}.", projection.ApplicationEntityId);
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
            total.TargetsSkipped + part.TargetsSkipped, total.TargetsFailed + part.TargetsFailed,
            total.DefaultsRemoved + part.DefaultsRemoved);
    }

    private sealed class ProjectionContext(
        string sourceTenantId,
        RtApplication application,
        RtEntityId applicationEntityId)
    {
        public string SourceTenantId { get; } = sourceTenantId;

        public RtApplication Application { get; } = application;

        public RtEntityId ApplicationEntityId { get; } = applicationEntityId;
    }
}
