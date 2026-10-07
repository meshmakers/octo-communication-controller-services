namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// AB#5827: what <see cref="IAdapterService.RecordMetricsSample(string, string, Communication.Contracts.DataTransferObjects.AdapterMetricsSampleDto)" />
/// did with a sample.
/// </summary>
public enum MetricsSampleOutcome
{
    /// <summary>The sample was stored in the adapter's ring buffer.</summary>
    Recorded,

    /// <summary>
    /// The sample was dropped: the tenant or adapter is not cached right now (tenant update,
    /// registration still in flight, adapter restarting). Nothing to do for the caller.
    /// </summary>
    Dropped,

    /// <summary>
    /// The sample was dropped and the connection has lost its registration: the controller
    /// accepted it, but the adapter has not been in the tenant's adapter cache for the grace period
    /// and was never asked to restart. The caller must ask the adapter to register again.
    /// </summary>
    RegistrationLost
}
