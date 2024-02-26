using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Pools;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Configuration;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Nodes;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Nodes.Extracts;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Routing;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Services.Common;
using Meshmakers.Octo.Services.Common.Cors;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.AspNetCore.Cors.Infrastructure;
using NLog;
using NLog.Web;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

// NLog: setup the logger first to catch all errors
var nLogFactory = LogManager.Setup().RegisterNLogWeb().LoadConfigurationFromFile("nlog.config").LogFactory;
var logger = nLogFactory.GetCurrentClassLogger();

try
{
    logger.Debug("init main");

    var builder = WebApplication.CreateBuilder(args);

    builder.Services.Configure<OctoSystemConfiguration>(options => builder.Configuration.GetSection("System").Bind(options));
    builder.Services.Configure<CommunicationControllerOptions>(options => builder.Configuration.GetSection("CommunicationController").Bind(options));
    builder.Services.Configure<RouteOptions>(options =>
        options.ConstraintMap.Add("tenantId", typeof(TenantIdRouteConstraint)));
    builder.Services.ConfigureOptions<ConfigureOctoSwaggerOptions>();

    // NLog: Setup NLog for Dependency injection
    builder.Logging.ClearProviders();
    builder.Logging.SetMinimumLevel(LogLevel.Trace);
    builder.Host.UseNLog();

    // additional providers here needed.
    // allow environment variables to override values from other providers.
    builder.Configuration.AddEnvironmentVariables("OCTO_").AddCommandLine(args)
        .AddUserSecrets(typeof(Program).Assembly, true);

    // Add services to the container.

    builder.Services.AddControllers();
    // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
    builder.Services.AddSignalR();
    
    builder.Services.ConfigureOptions<ConfigureDistributionEventHubOptions>();
    
    builder.Services.AddOctoServiceInfrastructure("CommunicationControllerServices",
        c =>
        {
            c.AddRoutedEventConsumer<MessageConsumer, UpdatedValueMessageDto>();
            c.AddBroadcastEventConsumer<ComControllerAdapterUpdateConsumer, ComControllerAdapterUpdate>();
            c.AddBroadcastEventConsumer<ComControllerPoolUpdateConsumer, ComControllerPoolUpdate>();
            
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PosUpdateTenant>();
            c.AddBroadcastEventConsumer<TenantManagementConsumer, PreDeleteTenant>();
        });

    builder.Services.AddRuntimeEngine()
        .AddMongoDbRuntimeRepository();

    builder.Services.AddCkModelSystemCommunication();

    builder.Services.AddOctoApiVersioningAndDocumentation(options =>
    {
        options.AddXmlDocAssembly<Program>();
        // options.Scopes = new Dictionary<string, string>
        // {
        //     {
        //         CommonConstants.SystemApiFullAccess,
        //         AssetTexts.Backend_AssetServices_Api_FullAccess
        //     },
        //     {
        //         CommonConstants.SystemApiReadOnly,
        //         AssetTexts.Backend_AssetServices_Api_ReadOnlyAccess
        //     }
        // };

        options.ApiTitle = "Device Management API";
        options.ApiDescription = "Device Management Services.";

        // options.ClientId = CommonConstants.AsserRepositoryServicesSwaggerClientId;
        // options.AppName = AssetTexts.Backend_AssetServices_UserSchema_Swagger_DisplayName;
    });

    builder.Services.AddSingleton<ICommunicationRepository, CommunicationRepository>();
    
    builder.Services.AddSingletonMultipleInterfaces<DefaultConfigurationCreatorService, IDefaultConfigurationCreatorService, IConfigurationService>();
    builder.Services.AddSingletonMultipleInterfaces<CorsPolicyProvider, ICorsPolicyProvider, CorsPolicyProvider>();
    builder.Services.AddSingletonMultipleInterfaces<AdapterService, IAdapterService, IAdapterServiceUpdates>();
    builder.Services.AddSingletonMultipleInterfaces<PoolService, IPoolService, IPoolServiceUpdates>();
    builder.Services.AddSingletonMultipleInterfaces<PoolHubCache, IPoolCache, IPoolCachePublish>();
    builder.Services.AddSingletonMultipleInterfaces<AdapterCache, IAdapterCache, IAdapterCachePublish>();
    
    builder.Services.AddSingleton<IPoolHubCallbacks, PoolHubCallbacks>();
    builder.Services.AddSingleton<IAdapterHubCallbacks, AdapterHubCallbacks>();
    builder.Services.AddDataPipeline()
        .RegisterNode<GetRtEntitiesByTypeNode>()
        .RegisterNode<RetrieveFromMessageNode>();
    
    var app = builder.Build();

    // Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
      //  app.UseDeveloperExceptionPage();
    }

    app.UseHttpsRedirection();

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