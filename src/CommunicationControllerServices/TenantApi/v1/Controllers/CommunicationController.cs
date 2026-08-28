using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects.ApiErrors;
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
    private readonly IWorkloadTemplateResolver _templateResolver;

    /// <summary>
    /// Constructor
    /// </summary>
    public CommunicationController(ILogger<CommunicationController> logger,
        IConfigurationService configurationService,
        IExpressionValidationService expressionValidationService,
        IWorkloadEncryptionService encryptionService,
        IWorkloadTemplateResolver templateResolver)
    {
        _logger = logger;
        _configurationService = configurationService;
        _expressionValidationService = expressionValidationService;
        _encryptionService = encryptionService;
        _templateResolver = templateResolver;
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
    /// <remarks>
    /// Refused with 409 while pools or workloads of the tenant are still deployed (AB#4255); the body
    /// names them and the commands that undeploy them. Every other configuration error stays a 400.
    /// </remarks>
    [HttpPost("disable")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(OperationFailedErrorDto), StatusCodes.Status409Conflict)]
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
        catch (ConfigurationException e) when (e.IsConflict)
        {
            // The refusal is already logged (WARN) by the base DisableAsync.
            return Conflict(new OperationFailedErrorDto(e.Message));
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

    /// <summary>
    /// Returns the named public base domains configured on this Communication
    /// Controller instance. Workload editors in the Refinery Studio use the
    /// result to populate the choice list behind the <c>Hostname</c> template
    /// syntax — a user picks <c>default</c> and the editor inserts
    /// <c>{{domain.default}}</c>, which the controller resolves at deploy time
    /// against the same map.
    ///
    /// Read-only; result is identical for every tenant on the instance.
    /// </summary>
    [HttpGet("domains")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IEnumerable<DomainConfigurationDto>), StatusCodes.Status200OK)]
    public IActionResult GetDomains()
    {
        var dtos = _templateResolver.AvailableDomains
            .Select(kvp => new DomainConfigurationDto(kvp.Key, kvp.Value))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return Ok(dtos);
    }

    /// <summary>
    /// Returns every template placeholder a workload can reference in its
    /// <c>Hostname</c>, non-secret <c>ValueOverride.Value</c> or
    /// <c>ValuesYaml</c>. Spans all three families:
    /// <list type="bullet">
    ///   <item><description><c>{{domain.NAME}}</c> — one entry per configured named domain.</description></item>
    ///   <item><description><c>{{service.NAME}}</c> — one entry per configured public service URL.</description></item>
    ///   <item><description><c>{{context.tenantId}}</c> — single per-deploy placeholder, <c>SampleValue</c> is <c>null</c>.</description></item>
    /// </list>
    /// Read-only; result is identical for every tenant on the instance.
    /// </summary>
    [HttpGet("workload-variables")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IEnumerable<WorkloadVariableDto>), StatusCodes.Status200OK)]
    public IActionResult GetWorkloadVariables()
    {
        var dtos = new List<WorkloadVariableDto>
        {
            new("{{context.tenantId}}",
                "The tenant id of the deploying tenant. Substituted at deploy time.",
                SampleValue: null),
        };
        dtos.AddRange(_templateResolver.AvailableDomains
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => new WorkloadVariableDto(
                $"{{{{domain.{kvp.Key}}}}}",
                $"Named public base domain '{kvp.Key}' configured on this Communication Controller instance.",
                kvp.Value)));
        dtos.AddRange(_templateResolver.AvailableServiceUrls
            .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kvp => new WorkloadVariableDto(
                $"{{{{service.{kvp.Key}}}}}",
                $"Public URI of service '{kvp.Key}' configured on this Communication Controller instance.",
                kvp.Value)));
        return Ok(dtos);
    }

    /// <summary>
    /// Returns the tenant's on-demand lifecycle configuration (AB#4914). A tenant without a
    /// stored record answers with the defaults (scale-to-zero off).
    /// </summary>
    [HttpGet("lifecycle")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(CommunicationLifecycleDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetLifecycle([FromServices] ILifecycleConfigurationService lifecycleConfigurationService)
    {
        var tenantId = HttpContext.GetTenantId();
        ArgumentNullException.ThrowIfNull(tenantId);

        var configuration = await lifecycleConfigurationService.GetConfigurationAsync(tenantId);
        return Ok(new CommunicationLifecycleDto(configuration.ScaleToZeroEnabled));
    }

    /// <summary>
    /// Sets the tenant's on-demand lifecycle configuration (AB#4914). Runtime configuration —
    /// effective without a controller redeploy; gates and watchdog pick it up within the
    /// configuration cache TTL. Setting <c>ScaleToZeroEnabled=false</c> is the per-tenant
    /// emergency stop.
    /// </summary>
    [HttpPut("lifecycle")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(CommunicationLifecycleDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SetLifecycle([FromBody] CommunicationLifecycleDto request,
        [FromServices] ILifecycleConfigurationService lifecycleConfigurationService)
    {
        var tenantId = HttpContext.GetTenantId();
        ArgumentNullException.ThrowIfNull(tenantId);

        await lifecycleConfigurationService.SetConfigurationAsync(tenantId,
            new CommunicationLifecycleConfiguration { ScaleToZeroEnabled = request.ScaleToZeroEnabled });

        _logger.LogInformation("Lifecycle configuration set for tenant '{TenantId}': ScaleToZeroEnabled={Enabled}",
            tenantId, request.ScaleToZeroEnabled);
        return Ok(request);
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
