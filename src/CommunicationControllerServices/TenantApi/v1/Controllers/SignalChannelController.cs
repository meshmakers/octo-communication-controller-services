using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Tenant self-service activation of a Signal phone number (AB#5143). Exactly ONE
/// <c>SignalChannel</c> definition per tenant; the number is unique per instance and immutable
/// while <c>Registered</c>. Every bridge interaction (bbernhard/signal-cli-rest-api) goes through
/// this service — the bridge API is unauthenticated and cluster-internal, so the browser never
/// talks to it directly.
/// <para>
/// Registration audit trail (WI rev 4): the mutating endpoints append a server-side history entry
/// (UTC timestamp, acting user from the request principal, action, outcome detail) for every
/// attempt — including failures answered as 4xx/429. GET returns that history newest-first
/// (newest 50). Clients never write history; DELETE removes the definition including its history,
/// so a fresh definition starts with a fresh history.
/// </para>
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/signal/channel")]
[ApiVersion("1.0")]
public class SignalChannelController : ControllerBase
{
    private readonly ILogger<SignalChannelController> _logger;
    private readonly ISignalChannelService _signalChannelService;

    /// <summary>
    /// Constructor
    /// </summary>
    /// <param name="logger">Logging object</param>
    /// <param name="signalChannelService">Owner of the channel invariants and the bridge flow</param>
    public SignalChannelController(ILogger<SignalChannelController> logger,
        ISignalChannelService signalChannelService)
    {
        _logger = logger;
        _signalChannelService = signalChannelService;
    }

    /// <summary>
    /// The tenant's Signal channel, cross-checked against the bridge:
    /// <c>bridgeRegistered</c> reports whether the number is present in the bridge's
    /// <c>GET /v1/accounts</c>, or <c>null</c> with a <c>warning</c> when the bridge is
    /// unreachable. Includes <c>history</c>: the registration audit trail (<c>{at, user, action,
    /// detail}</c>, newest first, newest 50 attempts). 404 when no definition exists.
    /// </summary>
    [HttpGet]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(SignalChannelDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Get()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        try
        {
            var channel = await _signalChannelService.GetChannelAsync(tenantId);
            if (channel == null)
            {
                return NotFound(new ErrorResponse
                    { ErrorMessage = $"No Signal channel is defined for tenant '{tenantId}'." });
            }

            return Ok(channel);
        }
        catch (SignalChannelServiceException e)
        {
            return MapServiceError(e);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[{TenantId}] Reading the Signal channel failed", tenantId);
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Reading the Signal channel failed: {e.Message}" });
        }
    }

    /// <summary>
    /// Claims a phone number for this tenant and starts the bridge registration; on success the
    /// state is <c>CodePending</c> and Signal sends an SMS verification code. When the bridge
    /// already holds an account for the number, the definition adopts it instead: the answer's
    /// <c>registrationState</c> is <c>Registered</c> (2) right away and no verification is needed.
    /// Creates the singleton definition when absent.
    /// Responses: 200 with the state telling what happened (<c>Registered</c> = adopted,
    /// <c>CodePending</c> = SMS code under way); 400 on any other bridge rejection or validation
    /// failure; 409 when the channel is already <c>Registered</c> (number immutable — delete
    /// first) or when another tenant on this instance already claims the number;
    /// <b>422 when Signal demands a captcha</b> — machine-readable on purpose, so the Studio
    /// wizard attempts without a captcha first and only shows the captcha step on demand (retry
    /// with <c>captchaToken</c>); 429 (with Retry-After when known) on a Signal rate limit.
    /// </summary>
    /// <param name="request">
    /// Number (E.164), optional bridge ApiUrl, captcha token and profile display name (pushed to
    /// the bridge once the channel reaches <c>Registered</c>, so Signal users do not see
    /// "Unknown").
    /// </param>
    [HttpPost("register")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(SignalChannelDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Register([Required][FromBody] RegisterSignalChannelRequestDto request)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        try
        {
            return Ok(await _signalChannelService.RegisterAsync(tenantId, BuildActor(), request.Number,
                request.ApiUrl, request.CaptchaToken, request.DisplayName));
        }
        catch (SignalChannelServiceException e)
        {
            return MapServiceError(e);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[{TenantId}] Registering the Signal channel failed", tenantId);
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Registering the Signal channel failed: {e.Message}" });
        }
    }

    /// <summary>
    /// Verifies the SMS code against the bridge. On success the state becomes <c>Registered</c>
    /// (with <c>registeredAt</c> = utcnow); on a wrong code the state stays <c>CodePending</c>
    /// with <c>lastError</c> set, so the tenant can retry.
    /// </summary>
    /// <param name="request">The verification code received by SMS.</param>
    [HttpPost("verify")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(SignalChannelDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Verify([Required][FromBody] VerifySignalChannelRequestDto request)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        try
        {
            return Ok(await _signalChannelService.VerifyAsync(tenantId, BuildActor(), request.Code));
        }
        catch (SignalChannelServiceException e)
        {
            return MapServiceError(e);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[{TenantId}] Verifying the Signal channel failed", tenantId);
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Verifying the Signal channel failed: {e.Message}" });
        }
    }

    /// <summary>
    /// Changes the profile display name WITHOUT re-registering (camelCase sub-route — pinned by
    /// the Studio frontend, which calls <c>PUT .../signal/channel/displayName</c>). A non-empty
    /// name updates the stored attribute; when the channel is <c>Registered</c> the profile is
    /// additionally pushed to the bridge (<c>PUT /v1/profiles/{number}</c>) — a push failure keeps
    /// the stored name (retry by calling again) and surfaces as 400/429. When not registered, the
    /// name is stored only and pushed automatically once the channel reaches <c>Registered</c>.
    /// An empty/whitespace name clears the stored attribute WITHOUT a bridge call — Signal
    /// profiles need a non-empty name, so the previously pushed profile name remains on the
    /// account.
    /// </summary>
    /// <param name="request">The profile display name Signal users should see; empty to clear.</param>
    [HttpPut("displayName")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(SignalChannelDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> SetDisplayName(
        [Required][FromBody] UpdateSignalChannelDisplayNameRequestDto request)
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        try
        {
            return Ok(await _signalChannelService.SetDisplayNameAsync(tenantId, BuildActor(),
                request.DisplayName));
        }
        catch (SignalChannelServiceException e)
        {
            return MapServiceError(e);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[{TenantId}] Updating the Signal display name failed", tenantId);
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Updating the Signal display name failed: {e.Message}" });
        }
    }

    /// <summary>
    /// Deletes the channel definition — from EVERY state (Registered, CodePending, Failed,
    /// Unregistered) — INCLUDING its registration history: a fresh definition starts with a
    /// fresh history. The bridge account is only unregistered (<c>delete_local_data</c>,
    /// best-effort: a "not registered" answer or an unreachable bridge never blocks the delete)
    /// when this definition owns the registration, i.e. its state is <c>Registered</c> — a
    /// definition stuck before that may reference a bridge account it does not own, which must
    /// not be destroyed. Afterwards the number is claimable again for this instance.
    /// </summary>
    [HttpDelete]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Delete()
    {
        var tenantId = HttpContext.GetTenantId();
        if (string.IsNullOrEmpty(tenantId))
        {
            return NotFound(new ErrorResponse { ErrorMessage = "TenantId is null or empty" });
        }

        try
        {
            await _signalChannelService.DeleteChannelAsync(tenantId, BuildActor());
            return NoContent();
        }
        catch (SignalChannelServiceException e)
        {
            return MapServiceError(e);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "[{TenantId}] Deleting the Signal channel failed", tenantId);
            return BadRequest(new ErrorResponse
                { ErrorMessage = $"Deleting the Signal channel failed: {e.Message}" });
        }
    }

    /// <summary>
    /// Projects the authenticated principal into the actor recorded on every audit-trail entry —
    /// the same claim mapping the pipeline execute endpoint uses for its caller projection:
    /// subject (<c>sub</c>, falling back to <c>client_id</c> for pure service credentials) and
    /// name (<c>name</c>, falling back to <c>preferred_username</c>). Defensive: the routes
    /// require a bearer, but an unauthenticated principal degrades to
    /// <see cref="SignalChannelActor.Unknown"/> instead of failing the audit write.
    /// </summary>
    private SignalChannelActor BuildActor()
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return SignalChannelActor.Unknown;
        }

        return new SignalChannelActor(
            User.FindFirstValue(JwtClaimTypes.Subject) ?? User.FindFirstValue("client_id"),
            User.FindFirstValue(JwtClaimTypes.Name) ?? User.FindFirstValue(JwtClaimTypes.PreferredUserName));
    }

    /// <summary>
    /// Maps the service's error kinds onto the fixed HTTP contract: NotFound → 404,
    /// Validation / BridgeRejected / BridgeUnreachable → 400, Conflict → 409,
    /// BridgeCaptchaRequired → 422 (typed flag from the bridge client — no error-text parsing
    /// here), and BridgeRateLimited → 429 passthrough with Retry-After when the bridge sent one.
    /// </summary>
    private IActionResult MapServiceError(SignalChannelServiceException e)
    {
        switch (e.Kind)
        {
            case SignalChannelErrorKind.NotFound:
                return NotFound(new ErrorResponse { ErrorMessage = e.Message });
            case SignalChannelErrorKind.Conflict:
                return Conflict(new ErrorResponse { ErrorMessage = e.Message });
            case SignalChannelErrorKind.BridgeCaptchaRequired:
                return UnprocessableEntity(new ErrorResponse { ErrorMessage = e.Message });
            case SignalChannelErrorKind.BridgeRateLimited:
                if (e.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero)
                {
                    Response.Headers.RetryAfter =
                        Math.Ceiling(retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                return StatusCode(StatusCodes.Status429TooManyRequests,
                    new ErrorResponse { ErrorMessage = e.Message });
            default:
                return BadRequest(new ErrorResponse { ErrorMessage = e.Message });
        }
    }
}
