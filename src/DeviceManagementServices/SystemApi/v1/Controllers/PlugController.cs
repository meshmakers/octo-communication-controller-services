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
    public async Task<IActionResult> Get()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound("TenantId is null or empty");
        }

        using var systemSession = await _systemContext.StartSystemSessionAsync();
        systemSession.StartTransaction();
        var result = await _systemContext.IsTenantExistingAsync(systemSession, tenantId);

        await systemSession.CommitTransactionAsync();

        return Ok(result);
    }
}