using Asp.Versioning;
using Duende.IdentityModel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.SystemApi.v1.Controllers;

/// <summary>
/// Manages the communication controller itself
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("system/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class CommunicationController: ControllerBase
{
    private readonly ILogger<CommunicationController> _logger;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger"></param>
    public CommunicationController(ILogger<CommunicationController> logger)
    {
        _logger = logger;
    }


    /// <summary>
    /// Pings the communication controller
    /// </summary>
    /// <returns></returns>
    [HttpGet("ping")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Get()
    {
        _logger.LogTrace("Ping TRACE");
        _logger.LogDebug("Ping DEBUG");
        _logger.LogInformation("Ping INFORMATION");
        _logger.LogError("Ping ERROR");
        _logger.LogCritical("Ping CRITICAL");
        return Ok("Pong");
    }
}