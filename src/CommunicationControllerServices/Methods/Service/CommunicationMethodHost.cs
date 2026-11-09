using Meshmakers.Octo.Common.DistributionEventHub.Configuration;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using Meshmakers.Octo.Services.Methods;
using static Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service.CommunicationMethodHandlerRegistration;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Methods.Service;

/// <summary>One CK method of System.Communication that the controller handles in code (<c>handler: service</c>).</summary>
/// <param name="MethodId">Qualified method id, always the generated constant of <c>SystemCommunicationCkIds</c>.</param>
/// <param name="HandlerType">The handler class.</param>
/// <param name="RestEndpoint">The REST endpoint the method mirrors (documentation and the parity table of the tests).</param>
/// <param name="Register">Registers the handler with the host of octo-common-services.</param>
internal sealed record CommunicationMethodHandlerRegistration(
    string MethodId,
    Type HandlerType,
    string RestEndpoint,
    Action<ICkMethodHandlerRegistrationBuilder> Register)
{
    /// <summary>One row of <see cref="CommunicationMethodHost.Handlers" />.</summary>
    public static CommunicationMethodHandlerRegistration For<THandler>(string methodId, string restEndpoint)
        where THandler : class, ICkMethodHandler =>
        new(methodId, typeof(THandler), restEndpoint, b => b.Handle<THandler>(methodId));
}

/// <summary>
///     The one place that hosts the <c>handler: service</c> CK methods of System.Communication in the communication controller
///     (AB#8595, AB#8596, Feature AB#5697 F4.3). A new method adds a handler class and one row to <see cref="Handlers" />;
///     host, caller token validation and the switch are set up here.
/// </summary>
/// <remarks>
///     <para>
///         The handlers are registered with the typed handler SDK of octo-common-services (<c>AddCkMethodHandlers</c>): one
///         shared queue per method, announcement and heartbeat, caller token validation, the declared authorization (roles,
///         scope <c>octo_api</c>, tenant of the token) and redaction are the host's. A handler only does the operation, with the
///         same services as the REST endpoint it mirrors, so the effect is the same. The REST endpoints are unchanged.
///     </para>
///     <para>
///         Independent of the pipeline method handler (<see cref="PipelineMethodHost" />, <c>handler: pipeline</c>): that host
///         reconciles methods declared by tenant models at run time; these methods are declared by System.Communication itself
///         and are known when the service is configured.
///     </para>
///     <para>
///         The SDK validates the registrations at startup against the CK cache of the system tenant (Order 15, after the
///         default configuration of order 10 has applied the System.Communication blueprint to the system tenant). A system
///         tenant without System.Communication fails the startup with a message naming the method; switch the host off with
///         <c>Methods:Communication:Enabled=false</c> (<c>OCTO_METHODS__COMMUNICATION__ENABLED</c>) — the REST endpoints keep
///         working, a method call then answers <c>METHOD_HANDLER_NOT_AVAILABLE</c>.
///     </para>
/// </remarks>
internal static class CommunicationMethodHost
{
    /// <summary>Name of this service in the method handler registry.</summary>
    public const string ServiceName = "CommunicationControllerService";

    /// <summary>All handlers of the controller (AB#8595, AB#8596).</summary>
    public static IReadOnlyList<CommunicationMethodHandlerRegistration> Handlers { get; } =
    [
        For<AdapterDeployUpdateHandler>(SystemCommunicationCkIds.AdapterDeployUpdateMethodId,
            "POST {tenantId}/v1/Adapter/deployUpdate?adapterRtEntityId="),
        For<DeployableWorkloadWakeHandler>(SystemCommunicationCkIds.DeployableWorkloadWakeMethodId,
            "POST {tenantId}/v1/Adapter/{workloadRtId}/wake"),
        For<AdapterReconcileServiceAccountHandler>(SystemCommunicationCkIds.AdapterReconcileServiceAccountMethodId,
            "POST {tenantId}/v1/Adapter/{adapterRtId}/serviceAccount/reconcile"),
        For<AdapterRotateServiceAccountSecretHandler>(
            SystemCommunicationCkIds.AdapterRotateServiceAccountSecretMethodId,
            "POST {tenantId}/v1/Adapter/{adapterRtId}/serviceAccount/rotateSecret"),
        For<AdapterPoolRefreshMirrorsHandler>(SystemCommunicationCkIds.AdapterPoolRefreshMirrorsMethodId,
            "POST {tenantId}/v1/AdapterPool/mirrors/refresh"),
        For<AdapterPoolPublishMirrorsHandler>(SystemCommunicationCkIds.AdapterPoolPublishMirrorsMethodId,
            "POST {tenantId}/v1/AdapterPool/mirrors/publish"),
        For<AdapterPoolGrantLeaseHandler>(SystemCommunicationCkIds.AdapterPoolGrantLeaseMethodId,
            "POST {tenantId}/v1/AdapterPool/{adapterPoolRtId}/lease"),
        For<AdapterPoolCancelQueuedExecutionHandler>(SystemCommunicationCkIds.AdapterPoolCancelQueuedExecutionMethodId,
            "DELETE {tenantId}/v1/AdapterPool/{adapterPoolRtId}/queue/{executionId}"),

        // AB#8596: Pipeline, DataFlow and the workload chart version.
        For<PipelineDeployHandler>(SystemCommunicationCkIds.PipelineDeployMethodId,
            "POST {tenantId}/v1/Pipeline/deploy?adapterRtEntityId=&pipelineRtEntityId="),
        For<PipelineExecuteHandler>(SystemCommunicationCkIds.PipelineExecuteMethodId,
            "POST {tenantId}/v1/Pipeline/execute?pipelineRtId=&isDryRun="),
        For<PipelineSetDebuggingHandler>(SystemCommunicationCkIds.PipelineSetDebuggingMethodId,
            "PATCH {tenantId}/v1/Pipeline/{pipelineRtId}/debug"),
        For<PipelineMoveToAdapterHandler>(SystemCommunicationCkIds.PipelineMoveToAdapterMethodId,
            "PATCH {tenantId}/v1/Pipeline/move-to-adapter"),
        For<DataFlowDeployHandler>(SystemCommunicationCkIds.DataFlowDeployMethodId,
            "POST {tenantId}/v1/DataFlow/deploy?dataFlowRtId="),
        For<DataFlowUndeployHandler>(SystemCommunicationCkIds.DataFlowUndeployMethodId,
            "POST {tenantId}/v1/DataFlow/undeploy?dataFlowRtId="),
        For<DeployableWorkloadSetChartVersionHandler>(SystemCommunicationCkIds.DeployableWorkloadSetChartVersionMethodId,
            "PATCH {tenantId}/v1/Workload/{workloadRtId}/chart-version")
    ];

    /// <summary>Reads the options (the host registration runs before the service provider exists).</summary>
    public static CommunicationMethodsOptions ReadOptions(IConfiguration configuration) =>
        configuration.GetSection(CommunicationMethodsOptions.SectionName).Get<CommunicationMethodsOptions>() ??
        new CommunicationMethodsOptions();

    /// <summary>
    ///     Registers the handlers with the method host of octo-common-services and the caller token validation of this
    ///     service. Nothing is registered when <see cref="CommunicationMethodsOptions.Enabled" /> is false.
    /// </summary>
    /// <returns><c>true</c> when the handlers are hosted; the caller then adds <see cref="AddCommunicationMethodDiscovery" />.</returns>
    public static bool AddCommunicationCkMethodHost(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<CommunicationMethodsOptions>().BindConfiguration(CommunicationMethodsOptions.SectionName);
        if (!ReadOptions(configuration).Enabled)
        {
            return false;
        }

        services.AddCkMethodHandlers(ServiceName, builder =>
        {
            foreach (var handler in Handlers)
            {
                handler.Register(builder);
            }
        });

        // The bus is not authenticated per message: the host validates the forwarded caller token with the authority of the
        // HTTP API of this service (shared with the pipeline method handler).
        services.AddControllerCkMethodCallerTokenValidation();
        return true;
    }

    /// <summary>Lets the controller answer discovery requests of dispatchers for these handlers (event hub configuration).</summary>
    public static void AddCommunicationMethodDiscovery(this IDistributionEventHubConfiguration configuration)
    {
        configuration.AddCkMethodHandlerDiscovery();
    }
}

/// <summary>
///     Settings of the <c>handler: service</c> methods of System.Communication (section <c>Methods:Communication</c>,
///     environment variable prefix <c>OCTO_METHODS__COMMUNICATION__</c>).
/// </summary>
public sealed class CommunicationMethodsOptions
{
    /// <summary>The configuration section name.</summary>
    public const string SectionName = "Methods:Communication";

    /// <summary>
    ///     Hosts the handlers (default true). Off: no queue, no announcement; the gateway answers
    ///     <c>METHOD_HANDLER_NOT_AVAILABLE</c> and the REST endpoints keep working.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
