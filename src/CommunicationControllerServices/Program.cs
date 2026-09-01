using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Configuration;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Extensions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Middleware;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Resources;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Routing;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Sdk.Common.Encryption;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Blueprints;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Services;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Configuration;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Notifications.Generated.System.Notification.v2;
using Meshmakers.Octo.Services.Notifications.Services;
using Meshmakers.Octo.Services.Observability;
using Meshmakers.Octo.Services.Swagger.Configuration;
using NLog;
using NLog.Web;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

// NLog: set up the logger first to catch all errors
var nLogFactory = LogManager.Setup().RegisterNLogWeb().LoadConfigurationFromFile("nlog.config").LogFactory;
var logger = nLogFactory.GetCurrentClassLogger();

try
{
    logger.Debug("init main");

    var builder = WebApplication.CreateBuilder(args);

    builder.AddObservability()
        .AddSystemContextHealthCheck()
        .AddAdapterHealthChecks();

    builder.Services.Configure<OctoSystemConfiguration>(options =>
        builder.Configuration.GetSection("System").Bind(options));
    builder.Services.Configure<CommunicationControllerOptions>(options =>
        builder.Configuration.GetSection("CommunicationController").Bind(options));
    // Bind blueprint variable context (octo.version/environment/systemTenantId) so the
    // default IBlueprintVariableProvider surfaces values from helm-injected
    // OCTO_BLUEPRINTS__* environment variables instead of falling back to defaults.
    builder.Services.Configure<OctoBlueprintVariablesOptions>(options =>
        builder.Configuration.GetSection(OctoBlueprintVariablesOptions.SectionName).Bind(options));
    builder.Services.Configure<RouteOptions>(options =>
        options.ConstraintMap.Add("tenantId", typeof(TenantIdRouteConstraint)));

    // NLog: Setup NLog for Dependency injection
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Trace);
    builder.Host.UseNLog();

    // additional providers here needed.
    // allow environment variables to override values from other providers.
    builder.Configuration.AddEnvironmentVariables("OCTO_").AddCommandLine(args)
        .AddUserSecrets(typeof(Program).Assembly, true);

    builder.Services.AddSingleton<ICommunicationRepository, CommunicationRepository>();
    builder.Services.AddSingleton<ICommunicationEventService, CommunicationEventService>();
    builder.Services.AddSingleton<IInstanceSecretCrypto, InstanceSecretCrypto>();
    builder.Services.AddSingleton<IWorkloadEncryptionService, WorkloadEncryptionService>();
    builder.Services.AddSingleton<IWorkloadTemplateResolver, WorkloadTemplateResolver>();
    builder.Services.AddSingleton<IShutdownState, HostApplicationShutdownState>();
    builder.Services.AddSingleton<ILifecycleConfigurationService, LifecycleConfigurationService>();
    builder.Services.AddSingleton<IWorkloadLifecycleService, WorkloadLifecycleService>();
    builder.Services.AddSingleton<IWorkloadOnDemandCapabilityService, WorkloadOnDemandCapabilityService>();
    builder.Services.AddSingleton<IPipelineServiceAccountResolver, PipelineServiceAccountResolver>();
    builder.Services
        .AddSingleton<IPipelineServiceAccountProvisioningService, PipelineServiceAccountProvisioningService>();
    builder.Services.AddSingleton<IWorkloadHostnameIndex, WorkloadHostnameIndex>();
    builder.Services.AddSingleton<IAdapterConnectionTracker, AdapterConnectionTracker>();
    builder.Services.AddSingleton<IPipelineSchemaValidator, PipelineSchemaValidator>();
    builder.Services.AddSingleton<IExpressionValidationService, ExpressionValidationService>();
    builder.Services.AddSingleton<IPipelineDefinitionService, PipelineDefinitionService>();
    builder.Services.AddSingleton<IAdapterService, AdapterService>();
    builder.Services.AddSingleton<IPoolService, PoolService>();
    builder.Services.AddSingleton<IPipelineDebugService, PipelineDebugService>();
    builder.Services.AddSingleton<IPipelineExecutionService, PipelineExecutionService>();
    builder.Services.AddTransient<ITriggerManagementService, TriggerManagementService>();

    builder.Services
        .AddScopedMultipleInterfaces<DefaultConfigurationCreatorService, IDefaultConfigurationCreatorService,
            IConfigurationService>();
    builder.Services.AddSingletonMultipleInterfaces<PoolHubCache, IPoolCache, IPoolCachePublish>();
    builder.Services.AddSingletonMultipleInterfaces<AdapterCache, IAdapterCache, IAdapterCachePublish>();

    builder.Services.AddSingleton<IOperatorConnectionManager, OperatorConnectionManager>();
    builder.Services.AddSingleton<IAdapterHubCallbacks, AdapterHubCallbacks>();

    // Add background service for pipeline execution metrics. Statistics folding runs inside
    // ExecutionCleanupBackgroundService since AB#4370 (fold-then-prune), so no separate
    // statistics service is registered any more.
    builder.Services.AddHostedService<ExecutionCleanupBackgroundService>();

    // Reconciles adapters stuck at a stale Online state with no live SignalR connection (AB#4699).
    builder.Services.AddHostedService<AdapterOfflineReconciliationBackgroundService>();
    builder.Services.AddHostedService<WorkloadLifecycleWatchdogBackgroundService>();

    // HTTP activator (AB#4923): hostname index plus the client that forwards a held request to the
    // woken workload. The client gets no timeout of its own — the wake already ran to completion by
    // the time it is used, and an adapter route may legitimately be a long-runner; the ingress's
    // proxy-read-timeout is the bound that matters.
    builder.Services.AddHostedService<WorkloadHostnameIndexBackgroundService>();
    builder.Services.AddHttpClient(WorkloadActivatorMiddleware.HttpClientName,
        client => client.Timeout = Timeout.InfiniteTimeSpan);

    // Add execution report background processor - decouples heavy DB writes from SignalR hub
    // method processing so that execution reports don't block deployment results
    builder.Services.AddSingleton<PipelineExecutionReportProcessor>();
    builder.Services.AddSingleton<IPipelineExecutionReportQueue>(sp =>
        sp.GetRequiredService<PipelineExecutionReportProcessor>());
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<PipelineExecutionReportProcessor>());

    // Add services to the container.
    builder.Services.AddCors();
    builder.Services.AddControllers();
    // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
    builder.Services.AddSignalR(o =>
    {
        o.EnableDetailedErrors = true;
        o.MaximumReceiveMessageSize = 1024 * 1024 * 100;
    });

    builder.Services.ConfigureOptions<ConfigureDistributionEventHubOptions>();
    builder.Services.ConfigureOptions<ConfigureJwtBearerOptions>();
    builder.Services.ConfigureOptions<ConfigureOpenIdConnectOptions>();
    builder.Services.ConfigureOptions<ConfigureOctoOpenApiOptions>();

    builder.Services.AddOctoServiceInfrastructure("CommunicationControllerServices",
        c =>
        {
            c.AddCommandClient<CreateIdentityDataCommandRequest>(QueueNames.CreateIdentityDataCommand);
            c.AddCommandClient<RemoveRecurringJobsByScheduleGroupRequest>(QueueNames
                .RemoveRecurringJobsByScheduleGroupCommand);
            c.AddRoutedCommandClient<ExecutePipelineRequest>();

            // c.AddBroadcastEventConsumer<ComControllerAdapterUpdateConsumer, ComControllerAdapterUpdate>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PreUpdateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PosUpdateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PreDeleteTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PosCreateTenant>();

            // AB#4918: durable co-wake queue. Cron companion schedules of pipelines on OnDemand
            // workloads land here; a wake tick fired while the controller restarts must survive,
            // so this is a routed (durable, named) endpoint — not a temporary command queue.
            c.AddRoutedEventConsumer<LifecycleWakeConsumer, LifecycleWakeMessage>(
                PipelineQueueNames.LifecycleWakeQueue);
        });

    builder.Services.AddRuntimeEngine()
        .AddMongoDbRuntimeRepository()
        // Persist blueprint installations + history as System.BlueprintInstallation /
        // System.BlueprintHistory CK entities in the tenant database. Without this the engine
        // defaults to in-memory stores — the apply succeeds and Mongo gets the seed entities,
        // but no installation row is recorded, so Studio can't list the tenant's blueprints and
        // the per-startup auto-update can't detect "already applied at this version".
        .AddMongoBlueprintSupport();

    builder.Services.AddCkModelSystemCommunicationV3();
    // Auto-import System.Communication at its embedded version into every tenant on resolve (engine
    // descriptor mechanism), decoupled from the blueprint floors. Production previously imported the
    // model only via the System.Communication.Release/MainLatest blueprint floors, so a CK-model bump
    // left non-comm tenants stale until the floor was bumped too; the descriptor keeps every tenant at
    // the embedded version. Bumping ConstructionKit/ckModel.yaml now propagates on its own.
    builder.Services.AddSingleton<IServiceManagedCkModelDescriptor>(
        _ => new ServiceManagedCkModelDescriptor(
            Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3
                .SystemCommunicationCkIds.CkModelId));

    // Register the System.Communication blueprint embedded with the CK-model package. OctoMesh
    // convention: blueprints named "System.*" are service-managed (BlueprintId.IsServiceManaged
    // returns true) — the Communication Controller auto-applies this on tenant enable / startup,
    // Studio surfaces it as read-only. The optional HelloCommunication demo lives in
    // samples/Blueprints/ and is admin-installable through a regular catalog (LocalFileSystem,
    // GitHub) — not embedded here.
    // Embedded blueprints — both registered, DefaultConfigurationCreatorService picks
    // the right one per tenant via each blueprint's requires.octo.environment block.
    builder.Services.AddBlueprintSystemCommunicationReleaseV1();
    builder.Services.AddBlueprintSystemCommunicationMainLatestV1();

    builder.Services.AddOctoNotification();

    // AB#5054: start the *user*-token half of the tenant gate in its migration mode. AB#5054 set
    // TokenValidationParameters.AuthenticationType = "Bearer" (ConfigureJwtBearerOptions), which is
    // what makes TenantAuthorizationMiddleware run at all here — before that it was a silent no-op
    // on every bearer request, so neither the user check nor the AB#5032 service-token audit log
    // ever produced anything. The service half has always been staged; the user half was not, so
    // switching the label on would flip it from "never checked" to "always 403" in one step.
    //
    // A static sweep of this service's callers found no cross-tenant user-token caller (Studio
    // re-mints per tenant and guards the route client-side, octo-cli derives the URL tenant and the
    // acr_values from the same context value, octo-mcp-service RFC 8693-exchanges before calling).
    // That is an argument, not the evidence — and the evidence is exactly what this gate has never
    // produced here. One release in LogOnly writes it, at zero behavioural cost; then arm the
    // environment with OCTO_TENANTAUTHORIZATION__USERTOKENENFORCEMENT=Enforce. The code default is
    // deliberately registered BEFORE the section binding, so configuration wins over it.
    builder.Services.AddOctoTenantAuthorization(o =>
        o.UserTokenEnforcement = UserTokenTenantEnforcementMode.LogOnly);

    // AB#5032: lets an operator narrow the client-credentials exemption of
    // UseOctoTenantAuthorization() per environment (OCTO_TENANTAUTHORIZATION__…). The defaults
    // reproduce the previous behaviour and only add the audit log.
    builder.Services.AddOctoTenantAuthorization(builder.Configuration);

    // 🔴 AB#5054 — no configuration delegate here. Audience, claim types, issuer and the "Bearer"
    // AuthenticationType all live in ConfigureJwtBearerOptions (registered above). A delegate here
    // runs LAST in the options factory, so an assignment to TokenValidationParameters silently
    // discards what the configurator set — including the label TenantAuthorizationMiddleware keys
    // its whole tenant check off, which turns the gate back into a no-op with no compile error and
    // no red test. See the remarks on ConfigureJwtBearerOptions.
    builder.Services.AddAuthentication().AddJwtBearer();


    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy(Constants.SystemCommunicationApiPolicy, authorizationPolicyBuilder =>
        {
            authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                CommonConstants.OctoApiFullAccess);
        });

        options.AddPolicy(Constants.TenantCommunicationApiReadWritePolicy, authorizationPolicyBuilder =>
        {
            authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                CommonConstants.OctoApiFullAccess);
        });

        options.AddPolicy(Constants.TenantCommunicationApiReadOnlyPolicy,
            authorizationPolicyBuilder =>
            {
                authorizationPolicyBuilder.RequireClaim(InfrastructureCommon.ClaimScope,
                    CommonConstants.OctoApiFullAccess,
                    CommonConstants.OctoApiReadOnly);
            });
    });

    builder.Services.AddOctoApiVersioningAndDocumentation(options =>
    {
        options.Scopes = new Dictionary<string, string>
        {
            {
                CommonConstants.OctoApiFullAccess,
                CommonConstants.OctoApiFullAccessDisplayName
            },
            {
                CommonConstants.OctoApiReadOnly,
                CommonConstants.OctoApiReadOnlyDisplayName
            }
        };

        options.PolicyScopeMapping = new Dictionary<string, IEnumerable<string>>
        {
            { Constants.SystemCommunicationApiPolicy, [CommonConstants.OctoApiFullAccess] },
            {
                Constants.TenantCommunicationApiReadWritePolicy,
                [CommonConstants.OctoApiFullAccess]
            },
            { Constants.TenantCommunicationApiReadOnlyPolicy, [CommonConstants.OctoApiReadOnly] }
        };

        options.XmlDocDataTransferObjectAssemblies =
            [typeof(AdapterConfigurationDto).Assembly, typeof(RtEntityId).Assembly];
        options.XmlDocOperationAssemblies = [typeof(Program).Assembly];

        options.ApiTitle = CommunicationControllerTexts.Api_Title;
        options.ApiDescription = CommunicationControllerTexts.Api_Description;

        options.ClientId = CommonConstants.CommunicationControllerServicesSwaggerClientId;
        options.AppName = CommunicationControllerTexts.SwaggerClient_Description;
    }).AddVersion();

    var app = builder.Build();

    app.MapObservability();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
        //  app.UseDeveloperExceptionPage();
    }

    // AB#4923: runs first because the requests it handles belong to an adapter's URL space, not the
    // controller's — neither this service's auth policies nor its route table apply to them. Every
    // other request falls through on a single dictionary miss.
    app.UseMiddleware<WorkloadActivatorMiddleware>();

    app.UseCors();

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseOctoTenantAuthorization();
    app.UseOctoApiVersioningAndDocumentation();

    app.MapHub<AdapterHub>("/{tenantId:tenantId}/adapterHub");
    app.MapHub<OperatorHub>("/operatorHub");
    app.MapControllerRoute(name: "default",
        pattern: "{tenantId:tenantId}/system/v{version:apiVersion}/{controller}/{action}/{id?}");

    // Log service start event
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        var eventRepository = app.Services.GetRequiredService<IEventRepository>();
        eventRepository.StoreSystemInformationEvent(RtEventSourcesEnum.CommunicationService,
            "Communication Controller Services started.");
    });

    app.Run();
}
catch (Exception ex)
{
    //NLog: catch setup errors
    logger.Error(ex, "Stopped program because of exception");
    throw;
}
finally
{
    // Ensure to flush and stop internal timers/threads before application-exit (Avoid segmentation fault on Linux)
    LogManager.Shutdown();
}