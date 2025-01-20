using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Configuration;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Resources;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Routing;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Services.Common;
using Meshmakers.Octo.Services.Common.Cors;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Observability;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Cors.Infrastructure;
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
        .AddSystemContextHealthCheck();

    builder.Services.Configure<OctoSystemConfiguration>(options =>
        builder.Configuration.GetSection("System").Bind(options));
    builder.Services.Configure<CommunicationControllerOptions>(options =>
        builder.Configuration.GetSection("CommunicationController").Bind(options));
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
    builder.Services.AddSingleton<IAdapterService, AdapterService>();
    builder.Services.AddSingleton<IPoolService, PoolService>();
    builder.Services.AddSingleton<IPipelineDebugService, PipelineDebugService>();
    builder.Services.AddTransient<ITriggerManagementService, TriggerManagementService>();

    builder.Services
        .AddScopedMultipleInterfaces<DefaultConfigurationCreatorService, IDefaultConfigurationCreatorService,
            IConfigurationService>();
    builder.Services.AddSingletonMultipleInterfaces<CorsPolicyProvider, ICorsPolicyProvider>();
    builder.Services.AddSingletonMultipleInterfaces<PoolHubCache, IPoolCache, IPoolCachePublish>();
    builder.Services.AddSingletonMultipleInterfaces<AdapterCache, IAdapterCache, IAdapterCachePublish>();

    builder.Services.AddSingleton<IPoolHubCallbacks, PoolHubCallbacks>();
    builder.Services.AddSingleton<IAdapterHubCallbacks, AdapterHubCallbacks>();

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
            c.AddRoutedCommandClient<ExecuteMeshPipelineRequest>();

            // c.AddBroadcastEventConsumer<ComControllerAdapterUpdateConsumer, ComControllerAdapterUpdate>();
            // c.AddBroadcastEventConsumer<ComControllerPoolUpdateConsumer, ComControllerPoolUpdate>();
            //
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PreUpdateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PosUpdateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PreDeleteTenant>();
        });

    builder.Services.AddRuntimeEngine()
        .AddMongoDbRuntimeRepository();

    builder.Services.AddCkModelSystemCommunication();

    builder.Services.AddAuthentication(authenticationOptions =>
        {
            authenticationOptions.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            authenticationOptions.DefaultChallengeScheme = BackendCommon.OidcAuthenticationScheme;
        })
        .AddOpenIdConnect(BackendCommon.OidcAuthenticationScheme, options =>
        {
            options.ClientId = CommonConstants.BotServicesClientId;

            options.Scope.Clear();
            options.Scope.Add(CommonConstants.Scopes.OpenId);
            options.Scope.Add(CommonConstants.Scopes.Profile);
            options.Scope.Add(CommonConstants.Scopes.Email);
            options.Scope.Add(CommonConstants.Scopes.Role);

            options.SaveTokens = true;
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.GetClaimsFromUserInfoEndpoint = true;

            options.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = JwtClaimTypes.Name,
                RoleClaimType = JwtClaimTypes.Role
            };
        }).AddJwtBearer(jwt =>
        {
            jwt.Audience = CommonConstants.CommunicationSystemApi;
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
            authorizationPolicyBuilder.RequireClaim(BackendCommon.ClaimScope,
                CommonConstants.CommunicationSystemApiFullAccess,
                CommonConstants.BotApiReadOnly);
        });

        options.AddPolicy(Constants.TenantCommunicationApiReadWritePolicy, authorizationPolicyBuilder =>
        {
            authorizationPolicyBuilder.RequireClaim(BackendCommon.ClaimScope,
                CommonConstants.CommunicationTenantApiFullAccess,
                CommonConstants.CommunicationTenantApiReadOnly);
        });

        options.AddPolicy(Constants.TenantCommunicationApiReadOnlyPolicy,
            authorizationPolicyBuilder =>
            {
                authorizationPolicyBuilder.RequireClaim(BackendCommon.ClaimScope,
                    CommonConstants.CommunicationTenantApiReadOnly);
            });
    });

    builder.Services.AddOctoApiVersioningAndDocumentation(options =>
    {
        options.Scopes = new Dictionary<string, string>
        {
            {
                CommonConstants.CommunicationSystemApiFullAccess,
                CommunicationControllerTexts.Scope_SystemFullAccess_Description
            },
            {
                CommonConstants.CommunicationTenantApiFullAccess,
                CommunicationControllerTexts.Scope_TenantFullAccess_Description
            },
            {
                CommonConstants.CommunicationTenantApiReadOnly,
                CommunicationControllerTexts.Scope_TenantReadonlyAccess_Description
            }
        };

        options.PolicyScopeMapping = new Dictionary<string, IEnumerable<string>>
        {
            { Constants.SystemCommunicationApiPolicy, [CommonConstants.CommunicationSystemApiFullAccess] },
            {
                Constants.TenantCommunicationApiReadWritePolicy,
                [CommonConstants.CommunicationTenantApiFullAccess, CommonConstants.CommunicationTenantApiReadOnly]
            },
            { Constants.TenantCommunicationApiReadOnlyPolicy, [CommonConstants.CommunicationTenantApiReadOnly] }
        };

        options.XmlDocDataTransferObjectAssemblies =
            [typeof(AdapterConfigurationDto).Assembly, typeof(RtEntityId).Assembly];
        options.XmlDocOperationAssemblies = [typeof(Program).Assembly];

        options.ApiTitle = CommunicationControllerTexts.Api_Title;
        options.ApiDescription = CommunicationControllerTexts.Api_Description;

        options.ClientId = CommonConstants.AsserRepositoryServicesSwaggerClientId;
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

    app.UseAuthorization();
    app.UseOctoApiVersioningAndDocumentation();

    app.MapHub<AdapterHub>("/{tenantId:tenantId}/adapterHub");
    app.MapHub<PoolHub>("/{tenantId:tenantId}/poolHub");
    app.MapControllerRoute(name: "default",
        pattern: "{tenantId:tenantId}/system/v{version:apiVersion}/{controller}/{action}/{id?}");


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