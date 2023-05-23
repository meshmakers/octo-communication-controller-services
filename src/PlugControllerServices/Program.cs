using MassTransit;
using Meshmakers.Octo.Backend.DistributedCache;
using Meshmakers.Octo.Backend.PlugControllerServices.Configuration;
using Meshmakers.Octo.Backend.PlugControllerServices.DataSink;
using Meshmakers.Octo.Backend.PlugControllerServices.Hubs;
using Meshmakers.Octo.Backend.PlugControllerServices.Routing;
using Meshmakers.Octo.Backend.PlugControllerServices.Services;
using Meshmakers.Octo.Backend.Swagger.Configuration;
using Meshmakers.Octo.SystematizedData.Persistence;
using Meshmakers.Octo.SystematizedData.Persistence.Configuration;
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


    builder.Services.Configure<RouteOptions>(options =>
        options.ConstraintMap.Add("tenantId", typeof(TenantIdRouteConstraint)));
    builder.Services.ConfigureOptions<ConfigureOctoSwaggerOptions>();
    builder.Services.Configure<OctoSystemConfiguration>(options => builder.Configuration.GetSection("System").Bind(options));

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
        
        // elided...
        x.UsingRabbitMq((context,cfg) =>
        {
            cfg.Host("localhost", "/", h => {
                h.Username("guest");
                h.Password("guest");
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
    builder.Services.AddSingleton<IPlugManagementService, PlugManagementService>();

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