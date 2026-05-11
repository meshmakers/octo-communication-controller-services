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
    private readonly IWorkloadEncryptionService _encryptionService;

    /// <summary>
    /// Constructor
    /// </summary>
    public CommunicationController(ILogger<CommunicationController> logger,
        IConfigurationService configurationService,
        IExpressionValidationService expressionValidationService,
        IWorkloadEncryptionService encryptionService)
    {
        _logger = logger;
        _configurationService = configurationService;
        _expressionValidationService = expressionValidationService;
        _encryptionService = encryptionService;
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

    /// <summary>
    /// Encrypts a plaintext value with the controller's at-rest encryption
    /// key and returns the sentinel-prefixed ciphertext (<c>enc:v1:…</c>).
    /// Used by the Studio to encrypt Helm value overrides flagged
    /// <c>IsSecret</c> before storing them via the regular GraphQL mutations.
    ///
    /// Plaintext travels only over TLS and is never persisted on the
    /// controller. The same response can be re-supplied as input — the
    /// service is idempotent and tolerant of already-encrypted values
    /// (they pass through unchanged).
    /// </summary>
    [HttpPost("encrypt-value")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(EncryptValueResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult EncryptValue([FromBody] EncryptValueRequest request)
    {
        if (request.Plaintext is null)
        {
            return BadRequest("Plaintext is required.");
        }

        try
        {
            var ciphertext = _encryptionService.IsEncrypted(request.Plaintext)
                ? request.Plaintext
                : _encryptionService.Encrypt(request.Plaintext);
            return Ok(new EncryptValueResponse(ciphertext));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogError(ex, "Encrypt-value failed: instance key not configured");
            return Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}

/// <summary>
/// Request body for the encrypt-value endpoint.
/// </summary>
/// <param name="Plaintext">The value to encrypt. May already be a ciphertext (<c>enc:v1:…</c>) — in that case it is returned unchanged.</param>
public record EncryptValueRequest([Required] string Plaintext);

/// <summary>
/// Response body for the encrypt-value endpoint.
/// </summary>
/// <param name="Ciphertext">Sentinel-prefixed ciphertext (<c>enc:v1:…</c>) ready to be stored as a CK attribute.</param>
public record EncryptValueResponse(string Ciphertext);

/// <summary>
/// Request body for expression validation.
/// </summary>
/// <param name="Expression">The mXparser expression to validate (e.g., "value &gt; 0 ? value : 0").</param>
/// <param name="TestValue">Optional test value for the 'value' variable (default: 42.0).</param>
public record ValidateExpressionRequest(
    [Required] string Expression,
    double? TestValue = null);
