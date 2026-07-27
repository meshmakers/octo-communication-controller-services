using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.BackgroundServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Configuration;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Extensions;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
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
using Microsoft.IdentityModel.Tokens;
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
    var signalRBuilder = builder.Services.AddSignalR(o =>
    {
        o.EnableDetailedErrors = true;
        o.MaximumReceiveMessageSize = 1024 * 1024 * 100;
    });

    // SignalR backplane for multi-replica deployments (AB#4493). Adapters keep
    // their SignalR connection on ONE replica; without a backplane a
    // DeployDataFlow / Update-Configuration request routed (by the LB) to a
    // DIFFERENT replica cannot reach the adapter and fails with
    // "…has no live SignalR connection". The Redis backplane makes
    // Clients.Client(connectionId) route to the replica that owns the connection.
    // Opt-in: no connection string ⇒ no backplane (single-replica behaviour,
    // unchanged). Bind directly from configuration since AddSignalR runs before
    // the DI container / IOptions is built.
    // IMPORTANT: the backplane is NECESSARY but NOT SUFFICIENT for >1 replica —
    // the per-replica in-memory adapter registry (AdapterCache) is not synced
    // across replicas (AdapterCache.PublishConfigurationAsync is stubbed), so the
    // handling replica still won't find the adapter to route to. See the AB#4493
    // PR: complete that registry sync, or run the controller single-replica.
    var signalRRedisConnectionString =
        builder.Configuration["CommunicationController:SignalRRedisConnectionString"];
    if (!string.IsNullOrWhiteSpace(signalRRedisConnectionString))
    {
        signalRBuilder.AddStackExchangeRedis(signalRRedisConnectionString);
        logger.Info("SignalR Redis backplane enabled — scale-out across controller replicas is active.");
    }

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

    builder.Services.AddAuthentication().AddJwtBearer(jwt =>
        {
            jwt.Audience = CommonConstants.OctoApi;
            jwt.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = JwtClaimTypes.Name,
                RoleClaimType = JwtClaimTypes.Role
            };
        });


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