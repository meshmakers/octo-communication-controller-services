using System.Security.Cryptography;
using System.Text;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Secrets;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal sealed partial class SecretDefaultProvisioningService
{
    private const string StoredValueAttributePath = "Value";

    private async Task<WriteOutcome> WriteWinningOffersAsync(string targetTenantId, SecretDefaultInventory inventory,
        DesiredSecretDefaults desiredState, CancellationToken cancellationToken)
    {
        var outcome = new WriteOutcome();
        var offeredKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var offersOfKey in desiredState.Offers.GroupBy(offer => offer.Key, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            offeredKeys.Add(offersOfKey.Key);
            var closestOfferDepth = offersOfKey.Min(offer => offer.SourceTenant.Depth);
            if (desiredState.HeldDefaults.Any(heldDefault =>
                    heldDefault.Key == offersOfKey.Key && heldDefault.SourceDepth <= closestOfferDepth))
            {
                outcome.Unchanged++;
                continue;
            }

            var storedDefaultsOfKey = inventory.Defaults
                .Where(storedDefault => storedDefault.Key == offersOfKey.Key)
                .ToList();
            var winningOffer = ChooseWinningOffer(offersOfKey.ToList(), storedDefaultsOfKey);
            var supersededDefaults = storedDefaultsOfKey
                .Where(storedDefault => Classify(storedDefault, desiredState) == StoredDefaultStanding.Unprovided)
                .ToList();
            await WriteOfferAsync(targetTenantId, winningOffer, storedDefaultsOfKey, supersededDefaults, outcome);
        }

        outcome.Unchanged += desiredState.HeldDefaults
            .Select(heldDefault => heldDefault.Key)
            .Where(heldKey => !offeredKeys.Contains(heldKey))
            .Distinct(StringComparer.Ordinal)
            .Count();
        return outcome;
    }

    private static SecretDefaultOffer ChooseWinningOffer(IReadOnlyList<SecretDefaultOffer> offersOfKey,
        IReadOnlyList<StoredSecretDefault> storedDefaultsOfKey)
    {
        var closestDepth = offersOfKey.Min(offer => offer.SourceTenant.Depth);
        var closestOffers = offersOfKey
            .Where(offer => offer.SourceTenant.Depth == closestDepth)
            .OrderBy(offer => offer.ApplicationRtId, StringComparer.Ordinal)
            .ToList();
        return closestOffers.FirstOrDefault(offer => storedDefaultsOfKey.Any(storedDefault =>
                   IsProjectedBy(storedDefault, offer.SourceTenant.TenantId, offer.ApplicationRtId)))
               ?? closestOffers[0];
    }

    private async Task WriteOfferAsync(string targetTenantId, SecretDefaultOffer offer,
        IReadOnlyList<StoredSecretDefault> storedDefaultsOfKey, IReadOnlyList<StoredSecretDefault> supersededDefaults,
        WriteOutcome outcome)
    {
        var application = offer.Application;
        var sourceTenantId = offer.SourceTenant.TenantId;
        var applicationLink = ApplicationLinkIn(targetTenantId, sourceTenantId, offer.ApplicationRtId);
        var revealAccess = new SecretAccessContext(sourceTenantId, application.CkTypeId?.ToString(),
            SecretValueAttributePath);
        string? plaintext;
        try
        {
            plaintext = encryptionService.RevealOrNull(offer.CheckedDeclaration.Declaration!.SecretValue,
                revealAccess);
        }
        catch (Exception revealFailure) when (revealFailure is not OperationCanceledException)
        {
            Logger.Warn(revealFailure, "[{TenantId}] Secret default '{Key}' of application '{ApplicationName}' in " +
                                       "tenant '{SourceTenantId}' could not be revealed", targetTenantId, offer.Key,
                application.Name, sourceTenantId);
            plaintext = null;
        }

        if (string.IsNullOrEmpty(plaintext))
        {
            outcome.Failed++;
            outcome.FailedKeys.Add(offer.Key);
            Logger.Error("[{TenantId}] Secret default '{Key}' of application '{ApplicationName}' in tenant " +
                         "'{SourceTenantId}' has no readable value; it was not written", targetTenantId, offer.Key,
                application.Name, sourceTenantId);
            await TryStoreErrorEventAsync(targetTenantId,
                $"Secret default '{offer.Key}' of application '{application.Name}' in tenant '{sourceTenantId}' has " +
                "no readable value and was not written into this tenant.", applicationLink);
            return;
        }

        if (IsAlreadyCurrent(targetTenantId, offer, storedDefaultsOfKey, plaintext))
        {
            outcome.Unchanged++;
            return;
        }

        var write = new SecretDefaultWrite(offer.Key, RtSecretValue.Pending(plaintext), sourceTenantId,
            offer.ApplicationRtId, timeProvider.GetUtcNow().UtcDateTime, offer.SourceTenant.AncestorTenantIds,
            supersededDefaults);
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
            outcome.FailedKeys.Add(offer.Key);
            Logger.Error(writeFailure, "[{TenantId}] Could not write secret default '{Key}' of application " +
                                       "'{ApplicationName}' from tenant '{SourceTenantId}'", targetTenantId,
                offer.Key, application.Name, sourceTenantId);
            await TryStoreErrorEventAsync(targetTenantId,
                $"Secret default '{offer.Key}' of application '{application.Name}' from tenant '{sourceTenantId}' " +
                "could not be written into this tenant. The next reconcile retries it.", applicationLink);
        }
    }

    /// <summary>
    ///     True when the tenant holds exactly this default already: one entity for the key, written by the winning
    ///     Application, named after the key, with the same value. The stored value is revealed in the target and
    ///     compared in constant time; neither value leaves this method. An unreadable stored value counts as changed.
    /// </summary>
    private bool IsAlreadyCurrent(string targetTenantId, SecretDefaultOffer offer,
        IReadOnlyList<StoredSecretDefault> storedDefaultsOfKey, string plaintext)
    {
        if (storedDefaultsOfKey.Count != 1)
        {
            return false;
        }

        var storedDefault = storedDefaultsOfKey[0];
        if (!IsProjectedBy(storedDefault, offer.SourceTenant.TenantId, offer.ApplicationRtId) ||
            !string.Equals(storedDefault.Name, offer.Key, StringComparison.Ordinal) ||
            storedDefault.Value is null)
        {
            return false;
        }

        string? storedPlaintext;
        try
        {
            storedPlaintext = encryptionService.RevealOrNull(storedDefault.Value,
                new SecretAccessContext(targetTenantId, storedDefault.CkTypeId, StoredValueAttributePath));
        }
        catch (Exception revealFailure) when (revealFailure is not OperationCanceledException)
        {
            Logger.Warn(revealFailure, "[{TenantId}] The stored value of secret default '{Key}' cannot be read; it " +
                                       "is written again", targetTenantId, storedDefault.Key);
            return false;
        }

        return storedPlaintext is not null &&
               CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(storedPlaintext),
                   Encoding.UTF8.GetBytes(plaintext));
    }

    private sealed class WriteOutcome
    {
        public int Created { get; set; }

        public int Updated { get; set; }

        public int Kept { get; set; }

        public int Unchanged { get; set; }

        public int Failed { get; set; }

        public HashSet<string> FailedKeys { get; } = new(StringComparer.Ordinal);
    }
}
