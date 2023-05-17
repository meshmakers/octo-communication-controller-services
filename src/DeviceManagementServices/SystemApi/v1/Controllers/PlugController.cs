using Meshmakers.Octo.SystematizedData.Persistence;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.DeviceManagementServices.SystemApi.v1.Controllers;

[ApiController]
[Route("{tenantId:tenantId}/system/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class PlugController : ControllerBase
{
    private static readonly string[] Summaries = new[]
    {
        "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
    };

    private readonly ILogger<PlugController> _logger;
    private readonly ISystemContext _systemContext;

    public PlugController(ILogger<PlugController> logger , ISystemContext systemContext)
    {
        _logger = logger;
        _systemContext = systemContext;
    }

    [HttpGet()]
    public IEnumerable<WeatherForecast> Get()
    {
        return Enumerable.Range(1, 5).Select(index => new WeatherForecast
            {
                Date = DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
                TemperatureC = Random.Shared.Next(-20, 55),
                Summary = Summaries[Random.Shared.Next(Summaries.Length)]
            })
            .ToArray();
    }
}