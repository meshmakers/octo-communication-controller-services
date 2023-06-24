using MassTransit;
using Meshmakers.Octo.Backend.DistributedCache;
using Meshmakers.Octo.Backend.PlugControllerServices.BackgroundServices;
using Meshmakers.Octo.Backend.PlugControllerServices.Caches.Plugs;
using Meshmakers.Octo.Backend.PlugControllerServices.Caches.Pools;
using Meshmakers.Octo.Backend.PlugControllerServices.Configuration;
using Meshmakers.Octo.Backend.PlugControllerServices.DataSink;
using Meshmakers.Octo.Backend.PlugControllerServices.Hubs;
using Meshmakers.Octo.Backend.PlugControllerServices.Models;
using Meshmakers.Octo.Backend.PlugControllerServices.Options;
using Meshmakers.Octo.Backend.PlugControllerServices.Repository;
using Meshmakers.Octo.Backend.PlugControllerServices.Routing;
using Meshmakers.Octo.Backend.PlugControllerServices.Services;
using Meshmakers.Octo.Backend.Swagger.Configuration;
using Meshmakers.Octo.Communication.Contracts.Hubs;
using Meshmakers.Octo.Communication.Plugs.Contracts.Hubs;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.Configuration;
using Microsoft.Extensions.Options;
using NLog;
using NLog.Web;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

// NLog: setup the logger first to catch all errors
var nlogFactory = NLogBuilder.ConfigureNLog("nlog.config");
var logger = nlogFactory.GetCurrentClassLogger();

try
{
    logger.Debug("init main");

    var builder = WebApplication.CreateBuilder(args);

    builder.Services.Configure<OctoSystemConfiguration>(options => builder.Configuration.GetSection("System").Bind(options));
    builder.Services.Configure<PlugControllerOptions>(options => builder.Configuration.GetSection("PlugController").Bind(options));
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
    
    builder.Services.AddMassTransit(x =>
    {
        x.AddConsumer<MessageConsumer>();
        
        x.UsingRabbitMq((context,cfg) =>
        {
            var plugOptions = context.GetService<IOptions<PlugControllerOptions>>();
            if (plugOptions == null)
                throw new InvalidOperationException("PlugOptions not configured");
                    
            cfg.Host(plugOptions.Value.BrokerHost, plugOptions.Value.BrokerVirtualHost, h => {
                h.Username(plugOptions.Value.BrokerUsername);
                h.Password(plugOptions.Value.BrokerPassword);
            });
            
            cfg.ConfigureEndpoints(context);
        });
    });
    
    
    builder.Services.ConfigureOptions<ConfigureDistributeCacheWithPubSubOptions>();
    builder.Services.AddDistributedPubSubCache();
    builder.Services.AddOctoPersistence();

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

    builder.Services.AddSingleton<IConfigurationService, ConfigurationService>();
    builder.Services.AddSingleton<IPlugService, PlugService>();
    builder.Services.AddSingleton<IPoolService, PoolService>();
    builder.Services.AddSingleton<IPlugRepository, PlugRepository>();
    builder.Services.AddSingleton<IPoolCache, PoolHubCache>();
    builder.Services.AddSingleton<IPlugCache, PlugCache>();
    builder.Services.AddSingleton<IPoolHubCallbacks, PoolHubCallbacks>();
    builder.Services.AddSingleton<IPlugHubCallbacks, PlugHubCallbacks>();
    builder.Services.AddHostedService<PlugControllerBackgroundService>();

    var app = builder.Build();

// Configure the HTTP request pipeline.
    if (app.Environment.IsDevelopment())
    {
      //  app.UseDeveloperExceptionPage();
    }

    app.UseHttpsRedirection();

    app.UseAuthorization();
    app.UseOctoApiVersioningAndDocumentation();
    app.UseOctoPersistence();

    app.MapHub<PlugHub>("/{tenantId:tenantId}/plugHub");
    app.MapHub<PoolHub>("/{tenantId:tenantId}/plugPoolHub");
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