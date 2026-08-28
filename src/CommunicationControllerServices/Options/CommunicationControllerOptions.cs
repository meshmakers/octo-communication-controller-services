using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Options;

/// <summary>
/// General Device Management initialization options
/// </summary>
public class CommunicationControllerOptions
{
    /// <summary>
    /// Constructor
    /// </summary>
    public CommunicationControllerOptions()
    {
        PublicUrl = "https://localhost:5015";
        AuthorityUrl = "https://localhost:5003";

        BrokerHost = "localhost";
        BrokerVirtualHost = "/";
        BrokerPort = 5672;
        BrokerUser = "guest";
        BrokerPassword = "guest";
#if DEBUGL || DEBUG
        MinLogLevel = LogLevelDto.Trace;
#else
        MinLogLevel = LogLevelDto.Warn;
#endif
    }
    
    /// <summary>
    ///    (Public) base address of the service
    /// </summary>
    public string PublicUrl { get; set; }

    /// <summary>
    ///     (Public) base address of the CAS (Central Authorization Services)
    /// </summary>
    public string AuthorityUrl { get; set; }

    /// <summary>
    ///     Gets or sets the prefix for the OctoMesh installation instance.
    /// </summary>
    public string? InstancePrefix { get; set; }

    /// <summary>
    /// Gets or sets the RabbitMQ broker host name
    /// </summary>
    public string BrokerHost { get; set; }

    /// <summary>
    /// Gets or sets the RabbitMQ broker virtual host
    /// </summary>
    public string BrokerVirtualHost { get; set; }

    /// <summary>
    /// Gets or sets the RabbitMQ broker port
    /// </summary>
    public ushort BrokerPort { get; set; }

    /// <summary>
    /// Gets or sets the RabbitMQ broker username
    /// </summary>
    public string? BrokerUser { get; set; }

    /// <summary>
    /// Gets or sets the RabbitMQ broker password
    /// </summary>
    public string? BrokerPassword { get; set; }

    /// <summary>
    /// Gets or sets the minimal log level to be logged
    /// </summary>
    public LogLevelDto MinLogLevel { get; set; }

    /// <summary>
    /// Gets or sets the number of days after which execution records are deleted unconditionally.
    /// Safety net behind the hourly fold (see PipelineExecutionRetentionHours): catches orphaned
    /// executions whose pipeline no longer exists and which therefore never get folded.
    /// </summary>
    public int PipelineExecutionRetentionDays { get; set; } = 3;

    /// <summary>
    /// Gets or sets the number of hours a terminal pipeline execution is retained before it is
    /// folded into the hourly statistics buckets and physically deleted (AB#4370). Executions are
    /// telemetry — RtPipelineStatistics is the durable record. Running executions are never
    /// touched regardless of age.
    /// </summary>
    public int PipelineExecutionRetentionHours { get; set; } = 1;

    /// <summary>
    /// Gets or sets the interval in minutes for updating pipeline statistics.
    /// Unused since AB#4370: statistics are refreshed by the execution fold in
    /// ExecutionCleanupBackgroundService on the stuck-check cadence.
    /// </summary>
    public int StatisticsUpdateIntervalMinutes { get; set; } = 60;

    /// <summary>
    /// Gets or sets whether to store input data with execution records
    /// </summary>
    public bool StoreInputData { get; set; } = false;

    /// <summary>
    /// Gets or sets the maximum length of input data to store
    /// </summary>
    public int MaxInputDataLength { get; set; } = 10000;

    /// <summary>
    /// Gets or sets the timeout in hours after which running executions are marked as failed
    /// </summary>
    public int PipelineExecutionTimeoutHours { get; set; } = 24;

    /// <summary>
    /// Gets or sets the grace period in minutes used by the connection-aware stuck-execution
    /// reaper. An execution is only failed once it is older than this grace period AND its owning
    /// adapter is not <c>Online</c> (or it is already <c>Interrupted</c>). Running executions on a
    /// live adapter are never failed, regardless of how long they run — this protects legitimate
    /// long-running ETL pipelines. The grace period only bounds how long an <em>orphaned</em>
    /// execution (adapter restarted / disconnected) stays visible in a non-terminal state.
    /// </summary>
    public int PipelineExecutionStuckGraceMinutes { get; set; } = 15;

    /// <summary>
    /// Gets or sets the interval in minutes at which the stuck-execution reaper runs. Retention
    /// cleanup of old execution records still runs once per day independent of this value.
    /// </summary>
    public int PipelineExecutionStuckCheckIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Gets or sets whether to validate pipeline definitions against the adapter's JSON Schema before deployment
    /// </summary>
    public bool EnablePipelineSchemaValidation { get; set; } = false;

    /// <summary>
    /// Gets or sets the interval in minutes at which the adapter offline-reconciliation sweep runs
    /// (AB#4699). The sweep marks any adapter persisted as <c>Online</c> but without a live SignalR
    /// connection on this pod as <c>Offline</c>, catching the case where an adapter's connection was
    /// lost during the controller's own shutdown (the rolling-upgrade race guard deliberately skips
    /// the Offline write there) and the adapter never reconnected.
    ///
    /// The same value is used as the startup grace: the first sweep only runs after this delay so
    /// adapters have time to (re)connect after a controller (re)start or rolling upgrade before any
    /// of them is judged orphaned. It must comfortably exceed the worst-case adapter reconnect time.
    /// </summary>
    public int AdapterOfflineReconciliationIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Maximum seconds a wake gate waits for a woken OnDemand workload to reach
    /// <c>ConfigurationState=Configured</c> before it reverts the workload to Hibernated and
    /// fails the caller with a typed error (AB#4918). Baseline measured wake-to-Configured is
    /// ~16 s (staging-1, warm image); 60 s covers image pulls and slow nodes. Must stay below
    /// the SDK caller cap of 100 s on the execute-pipeline path.
    /// </summary>
    public int LifecycleWakeBudgetSeconds { get; set; } = 60;

    /// <summary>
    /// Interval in minutes of the on-demand lifecycle idle watchdog (AB#4918). Each sweep
    /// hibernates OnDemand workloads of scale-to-zero-enabled tenants whose last activity is
    /// older than their <c>IdleTimeoutMinutes</c>, and reconciles stale <c>Waking</c> states
    /// left behind by a controller restart. The same value is used as the startup grace.
    /// </summary>
    public int LifecycleWatchdogIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Enables the HTTP activator (AB#4923): an inbound request for a hibernated OnDemand
    /// workload is held while the workload wakes and is then forwarded to it, instead of the
    /// bare 502/503 the ingress would answer with no pod behind the workload's Service.
    ///
    /// Wiring is an ingress annotation, not a route: adapter Ingresses carry
    /// <c>nginx.ingress.kubernetes.io/default-backend</c> pointing at this service, which nginx
    /// uses exactly when the primary Service has no ready endpoint — precisely the hibernated
    /// case. Steady-state traffic never passes through here.
    ///
    /// Off by default; the annotation is what actually routes traffic, so switching this on
    /// without it is inert, and switching it off with the annotation in place turns the activator
    /// back into a plain 404 (the ingress's own error page behaviour).
    /// </summary>
    public bool ActivatorEnabled { get; set; }

    /// <summary>
    /// Template for the in-cluster address the activator forwards to once the workload is awake.
    /// <c>{release}</c> is replaced by the workload's helm release name
    /// (<c>{tenantId}-{workloadRtId}</c>, DNS-sanitised) — the operator names Deployment, Service
    /// and Ingress after it, so it is also the Service name.
    ///
    /// The default has no namespace on purpose: the pod's own search domain resolves it inside
    /// the controller's namespace, which is where the operator deploys workloads
    /// (<c>OPERATOR__POOLNAMESPACE</c>). Override with an explicit
    /// <c>http://{release}.other-namespace.svc.cluster.local</c> when they differ, or to reach a
    /// workload whose chart sets its own <c>fullnameOverride</c>.
    /// </summary>
    public string ActivatorWorkloadAddressTemplate { get; set; } = "http://{release}";

    /// <summary>
    /// How often the activator rebuilds its hostname index (minutes). The index maps the public
    /// hostname of every ingress-enabled workload to its tenant and rtId, so an inbound request
    /// can be attributed without a per-request repository lookup.
    ///
    /// A freshly deployed workload is not reachable through the activator until the next refresh.
    /// That is harmless in practice: a workload only routes through the activator once it has
    /// hibernated, which takes at least its <c>IdleTimeoutMinutes</c>.
    /// </summary>
    public int ActivatorHostnameRefreshIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Named public base domains available to workloads as <c>{{domain.NAME}}</c>
    /// placeholders in <c>Hostname</c>, non-secret <c>ValueOverride.Value</c>
    /// entries and <c>ValuesYaml</c>. Resolved by
    /// <c>IWorkloadTemplateResolver</c> at deploy time (not at blueprint-apply
    /// time) so workload entities stay portable across clusters.
    ///
    /// Bound from <c>OCTO_COMMUNICATIONCONTROLLER__DOMAINS__{NAME}={baseDomain}</c>
    /// env vars (helm chart emits one entry per <c>services.communication.domains</c>
    /// map key). NAME is case-insensitive at lookup; baseDomain is the raw value
    /// (no scheme, no trailing dot). Example: a workload with
    /// <c>Hostname="adapter.{{domain.default}}"</c> and the option
    /// <c>Domains["default"]="staging.octo-mesh.com"</c> deploys with
    /// <c>publicUri=https://adapter.staging.octo-mesh.com</c>.
    ///
    /// Empty / missing is tolerated; only workloads that reference a non-existent
    /// domain key fail at deploy time with
    /// <c>PoolServiceException.WorkloadTemplateUnknownPlaceholder</c>.
    /// </summary>
    public Dictionary<string, string> Domains { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Named public service URIs available to workloads as <c>{{service.NAME}}</c>
    /// placeholders in <c>Hostname</c>, non-secret <c>ValueOverride.Value</c>
    /// entries and <c>ValuesYaml</c>. Mirrors the <see cref="Domains"/> shape:
    /// late-bound at deploy time so workload entities stay cluster-portable.
    ///
    /// Bound from <c>OCTO_COMMUNICATIONCONTROLLER__SERVICEURLS__{NAME}={url}</c>
    /// env vars (helm chart emits one entry per known
    /// <c>services.&lt;name&gt;.publicUri</c>). NAME is case-insensitive at lookup;
    /// the value is substituted verbatim, including scheme. The semantic key
    /// <c>authority</c> maps to the Identity Service's public URI; other keys
    /// follow the helm-section name (<c>assetRepository</c>, <c>bot</c>,
    /// <c>communication</c>, <c>adminPanel</c>, <c>studio</c>).
    ///
    /// Empty / missing is tolerated; only workloads that reference a
    /// non-existent service key fail at deploy time with
    /// <c>PoolServiceException.WorkloadTemplateUnknownPlaceholder</c>.
    /// </summary>
    public Dictionary<string, string> ServiceUrls { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Symmetric AES-256-GCM master key used to encrypt secret attributes
    /// at rest (e.g. <c>ValueOverride.Value</c> for secret-flagged Helm value
    /// overrides, <c>HelmRepositoryConfiguration.Password</c>). Base64-encoded
    /// 32-byte key. Loaded from configuration; in local dev typically
    /// <c>OCTO_COMMUNICATIONCONTROLLER__INSTANCESECRETKEY</c>, in production
    /// mounted as a Kubernetes Secret (recommended Vault path
    /// <c>meshmakers/{cluster}/instance-secret-key</c>).
    ///
    /// Empty / unset is tolerated at startup but every attempt to use the
    /// encryption service throws a clear configuration error.
    /// </summary>
    public string? InstanceSecretKey { get; set; }
}