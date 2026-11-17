namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal sealed partial class SecretDefaultProvisioningService
{
    /// <summary>
    ///     Stores the Information event of a target reconcile that wrote or removed a default, in the reconciled
    ///     tenant, with counts only. A failing event store does not fail the reconcile.
    /// </summary>
    private async Task ReportTargetChangesAsync(SecretDefaultTargetResult result)
    {
        if (result.DefaultsWritten == 0 && result.DefaultsRemoved == 0)
        {
            return;
        }

        try
        {
            await communicationEventService.StoreInformationEventAsync(result.TenantId,
                $"Secret defaults reconciled: {result.DefaultsCreated} created, {result.DefaultsUpdated} updated, " +
                $"{result.DefaultsRemoved} removed, {result.DefaultsKept} kept.");
        }
        catch (Exception eventFailure) when (eventFailure is not OperationCanceledException)
        {
            Logger.Warn(eventFailure, "[{TenantId}] Could not store the secret default reconcile event",
                result.TenantId);
        }
    }
}
