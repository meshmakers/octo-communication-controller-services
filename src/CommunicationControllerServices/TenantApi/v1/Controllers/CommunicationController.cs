using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Services.Infrastructure;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Manages the communication controller for a specific tenant
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class CommunicationController : ControllerBase
{
    private readonly ILogger<CommunicationController> _logger;
    private readonly IConfigurationService _configurationService;
    private readonly IExpressionValidationService _expressionValidationService;

    /// <summary>
    /// Constructor
    /// </summary>
    public CommunicationController(ILogger<CommunicationController> logger,
        IConfigurationService configurationService,
        IExpressionValidationService expressionValidationService)
    {
        _logger = logger;
        _configurationService = configurationService;
        _expressionValidationService = expressionValidationService;
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
        return Ok("Pong");
    }

    /// <summary>
    /// Enables the communication controller for the current tenant
    /// </summary>
    /// <returns></returns>
    [HttpPost("enable")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Enable()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest("Tenant ID is required.");
        }

        try
        {
            await _configurationService.EnableAsync(tenantId);
            return NoContent();
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Disables the communication controller for the current tenant
    /// </summary>
    /// <returns></returns>
    [HttpPost("disable")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Disable()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return BadRequest("Tenant ID is required.");
        }

        try
        {
            await _configurationService.DisableAsync(tenantId);
            return NoContent();
        }
        catch (ConfigurationException e)
        {
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Validates a mapping expression using the mXparser engine.
    /// Uses the same evaluation path as ApplyDataPointMappingsNode in the Mesh Adapter.
    /// </summary>
    /// <param name="request">The expression and optional test value.</param>
    /// <returns>Validation result with success/error state and evaluated result.</returns>
    [HttpPost("validate-expression")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(ExpressionValidationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult ValidateExpression([FromBody] ValidateExpressionRequest request)
    {
        var result = _expressionValidationService.Validate(request.Expression, request.TestValue ?? 42.0);
        return Ok(result);
    }
}

/// <summary>
/// Request body for expression validation.
/// </summary>
/// <param name="Expression">The mXparser expression to validate (e.g., "value &gt; 0 ? value : 0").</param>
/// <param name="TestValue">Optional test value for the 'value' variable (default: 42.0).</param>
public record ValidateExpressionRequest(
    [Required] string Expression,
    double? TestValue = null);
