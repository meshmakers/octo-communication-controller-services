using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Models;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Microsoft.Extensions.Options;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Default <see cref="ISignalChannelService"/> (AB#5143). Singleton; every invariant that the
///     CK model cannot express (one channel per tenant, one tenant per number, number immutable
///     while Registered) lives here, in front of the repository.
///     <para>
///         Registration audit trail (WI rev 4): every register/verify/delete attempt is appended
///         to <c>SignalChannel.RegistrationHistory</c> — including bridge failures that surface as
///         4xx/429 to the API caller. Entries are newest-first and capped at
///         <see cref="MaxHistoryEntries"/>; the acting user arrives as a
///         <see cref="SignalChannelActor"/> from the controller. A successful delete erases the
///         definition INCLUDING its history (fresh definition = fresh history).
///     </para>
/// </summary>
internal sealed class SignalChannelService(
    ILogger<SignalChannelService> logger,
    ICommunicationRepository communicationRepository,
    ISignalBridgeClient bridgeClient,
    IAdapterCache adapterCache,
    ICommunicationEventService eventService,
    IOptions<CommunicationControllerOptions> options) : ISignalChannelService
{
    /// <summary>Stable well-known name of the per-tenant singleton entity.</summary>
    internal const string ChannelWellKnownName = "signal-channel";

    /// <summary>
    ///     Cap of the registration audit trail: the newest 50 attempts are kept, older entries are
    ///     trimmed on append.
    /// </summary>
    internal const int MaxHistoryEntries = 50;

    /// <inheritdoc />
    public async Task<SignalChannelDto?> GetChannelAsync(string tenantId)
    {
        var channel = await FindChannelAsync(tenantId);
        if (channel == null)
        {
            return null;
        }

        bool? bridgeRegistered = null;
        string? warning = null;
        try
        {
            var accounts = await bridgeClient.GetAccountsAsync(RequireApiUrl(channel));
            bridgeRegistered = accounts.Contains(channel.Number, StringComparer.Ordinal);
        }
        catch (SignalBridgeException e)
        {
            // Tolerated by contract: the persisted definition is still worth showing when the
            // bridge is down — the UI renders the warning instead of the cross-check.
            logger.LogWarning(e, "[{TenantId}] Could not cross-check the Signal channel against the bridge",
                tenantId);
            warning = $"The bridge state could not be determined: {e.Message}";
        }

        return ToDto(channel, bridgeRegistered, warning);
    }

    /// <inheritdoc />
    public async Task<SignalChannelDto> RegisterAsync(string tenantId, SignalChannelActor actor, string? number,
        string? apiUrl, string? captchaToken)
    {
        var validatedNumber = ValidateNumber(number);

        var existing = await FindChannelAsync(tenantId);
        if (existing is { RegistrationState: RtSignalRegistrationStateEnum.Registered })
        {
            // Immutable while Registered — for a different number AND for a re-register of the
            // same one: re-registering an active account would invalidate its Signal session.
            throw SignalChannelServiceException.AlreadyRegistered(tenantId, existing.Number ?? string.Empty);
        }

        await EnsureNumberIsUnclaimedAsync(tenantId, validatedNumber);

        var resolvedApiUrl = ResolveApiUrl(apiUrl, existing);

        // Persist the claim BEFORE the bridge call: register can take tens of seconds, and two
        // tenants racing for one number must collide on the stored definition, not on the bridge.
        // The RegisterRequested audit entry rides along with this save, so every attempt that
        // reaches the definition is recorded even if the process dies mid-flight.
        var channel = existing ?? new RtSignalChannel
        {
            RtId = OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkSignalChannelTypeId,
            RtWellKnownName = ChannelWellKnownName
        };
        channel.Number = validatedNumber;
        channel.ApiUrl = resolvedApiUrl;
        channel.RegistrationState = RtSignalRegistrationStateEnum.Unregistered;
        channel.RegisteredAt = null;
        channel.LastError = null;
        AppendHistory(channel, actor, RtSignalRegistrationActionEnum.RegisterRequested,
            $"number '{validatedNumber}'");
        await communicationRepository.SaveSignalChannelAsync(tenantId, channel, existing == null);

        // Adopt an account the bridge already holds for this number (pre-existing dev account, or
        // a backup/restore migration of the bridge state): POST /v1/register would answer
        // 400 "Account is already registered" and re-registration is neither possible nor wanted.
        // The tenant guards above still applied first — but note the cross-tenant scan only covers
        // THIS instance's tenants: a number claimed by another OctoMesh instance sharing the
        // cluster bridge is not detectable here; the bridge's NetworkPolicy trust boundary covers
        // that. When the account list cannot be read, fall back to the plain register attempt.
        if (await IsAlreadyOnBridgeAsync(tenantId, resolvedApiUrl, validatedNumber))
        {
            channel.RegistrationState = RtSignalRegistrationStateEnum.Registered;
            channel.RegisteredAt = DateTime.UtcNow;
            channel.LastError = null;
            AppendHistory(channel, actor, RtSignalRegistrationActionEnum.Adopted,
                "existing bridge account adopted");
            await communicationRepository.SaveSignalChannelAsync(tenantId, channel, false);

            await eventService.StoreInformationEventAsync(tenantId,
                $"Signal channel '{validatedNumber}' adopted the account already registered on the bridge; no verification needed.");

            // The caller detects registrationState = Registered in the answer and skips verify.
            return ToDto(channel, null, null);
        }

        try
        {
            await bridgeClient.RegisterAsync(resolvedApiUrl, validatedNumber, NormalizeCaptcha(captchaToken));
        }
        catch (SignalBridgeException e)
        {
            // Rate limit and unreachable keep the state at Unregistered (nothing was decided on
            // the Signal side, a plain retry is fine); a bridge rejection is a real Failed.
            if (e.Kind == SignalBridgeErrorKind.Rejected)
            {
                channel.RegistrationState = RtSignalRegistrationStateEnum.Failed;
            }

            channel.LastError = e.Message;
            // The failed attempt MUST be persisted even though the endpoint answers 4xx/429 —
            // appended exactly once here, saved best-effort so the original bridge error is
            // never masked.
            AppendHistory(channel, actor, RtSignalRegistrationActionEnum.RegisterFailed,
                BridgeFailureDetail(e));
            await SaveBestEffortAsync(tenantId, channel);
            await StoreErrorEventBestEffortAsync(tenantId,
                $"Signal channel registration of '{validatedNumber}' failed: {e.Message}");
            throw SignalChannelServiceException.FromBridge(e);
        }

        channel.RegistrationState = RtSignalRegistrationStateEnum.CodePending;
        channel.LastError = null;
        await communicationRepository.SaveSignalChannelAsync(tenantId, channel, false);

        await eventService.StoreInformationEventAsync(tenantId,
            $"Signal channel registration of '{validatedNumber}' started; waiting for the SMS verification code.");

        return ToDto(channel, null, null);
    }

    /// <inheritdoc />
    public async Task<SignalChannelDto> VerifyAsync(string tenantId, SignalChannelActor actor, string? code)
    {
        var validatedCode = ValidateVerificationCode(code);

        var channel = await FindChannelAsync(tenantId) ?? throw SignalChannelServiceException.ChannelNotFound(tenantId);
        switch (channel.RegistrationState)
        {
            case RtSignalRegistrationStateEnum.Registered:
                throw SignalChannelServiceException.AlreadyRegistered(tenantId, channel.Number ?? string.Empty);
            case RtSignalRegistrationStateEnum.CodePending:
                break;
            default:
                throw SignalChannelServiceException.NoVerificationPending(tenantId);
        }

        var number = channel.Number ?? string.Empty;
        try
        {
            await bridgeClient.VerifyAsync(RequireApiUrl(channel), number, validatedCode);
        }
        catch (SignalBridgeException e)
        {
            // A wrong code (or a rate limit) does not lose the pending registration — the state
            // stays CodePending so the tenant can retry with the correct code. The failed attempt
            // MUST be persisted even though the endpoint answers 4xx/429.
            channel.LastError = e.Message;
            AppendHistory(channel, actor, RtSignalRegistrationActionEnum.VerifyFailed,
                BridgeFailureDetail(e));
            await SaveBestEffortAsync(tenantId, channel);
            await StoreErrorEventBestEffortAsync(tenantId,
                $"Signal channel verification of '{number}' failed: {e.Message}");
            throw SignalChannelServiceException.FromBridge(e);
        }

        channel.RegistrationState = RtSignalRegistrationStateEnum.Registered;
        channel.RegisteredAt = DateTime.UtcNow;
        channel.LastError = null;
        AppendHistory(channel, actor, RtSignalRegistrationActionEnum.CodeVerified, "registered");
        await communicationRepository.SaveSignalChannelAsync(tenantId, channel, false);

        await eventService.StoreInformationEventAsync(tenantId,
            $"Signal channel '{number}' verified and registered.");

        return ToDto(channel, null, null);
    }

    /// <inheritdoc />
    public async Task DeleteChannelAsync(string tenantId, SignalChannelActor actor)
    {
        var channel = await FindChannelAsync(tenantId) ?? throw SignalChannelServiceException.ChannelNotFound(tenantId);
        var number = channel.Number ?? string.Empty;

        // Only unregister the bridge account when THIS definition actually owns the registration,
        // i.e. it completed the verify flow or adopted the account (state Registered). A definition
        // stuck in CodePending/Failed/Unregistered may reference a number whose bridge account
        // belongs to someone else (exactly the adopt scenario) — unregistering with
        // delete_local_data would destroy that working account.
        if (channel.RegistrationState == RtSignalRegistrationStateEnum.Registered)
        {
            try
            {
                await bridgeClient.UnregisterAsync(RequireApiUrl(channel), number);
            }
            catch (SignalBridgeException e)
            {
                // Best-effort by contract: a number that never completed registration answers
                // "not registered", and an unreachable bridge must not make the definition
                // undeletable. The delete below is what frees the claim.
                logger.LogWarning(e,
                    "[{TenantId}] Best-effort bridge unregister of '{Number}' failed; deleting the definition anyway",
                    tenantId, number);
            }
        }

        try
        {
            // Erase, and the registration history goes with the definition BY DESIGN: the stored
            // entities are the instance-wide number-claim registry, an archive tombstone must not
            // keep a number blocked, and a fresh definition starts with a fresh history. No
            // persisted "Deleted" audit entry can survive a successful delete — the system event
            // below is the durable record of the deletion.
            await communicationRepository.DeleteSignalChannelAsync(tenantId, channel.ToRtEntityId());
        }
        catch (Exception e)
        {
            // The definition survives a failed delete — record the failed attempt on it.
            AppendHistory(channel, actor, RtSignalRegistrationActionEnum.DeleteFailed, e.Message);
            await SaveBestEffortAsync(tenantId, channel);
            await StoreErrorEventBestEffortAsync(tenantId,
                $"Signal channel deletion of '{number}' failed: {e.Message}");
            throw;
        }

        await eventService.StoreInformationEventAsync(tenantId,
            $"Signal channel '{number}' deleted; the number is claimable again.");
    }

    private async Task<RtSignalChannel?> FindChannelAsync(string tenantId)
    {
        var channels = await communicationRepository.GetSignalChannelsAsync(tenantId);
        if (channels.Count > 1)
        {
            // Should be impossible (every write path goes through this service) — but if it ever
            // happens, deterministically treat the oldest as THE singleton and say so.
            logger.LogWarning("[{TenantId}] {Count} SignalChannel entities found; expected at most one",
                tenantId, channels.Count);
        }

        return channels.OrderBy(c => c.RtId).FirstOrDefault();
    }

    /// <summary>
    ///     Cross-tenant claim check: every other tenant on this instance that already stores a
    ///     SignalChannel with this number blocks the registration. An unreadable foreign tenant is
    ///     skipped with a warning — one broken tenant must not block every registration; the
    ///     per-tenant definitions remain the authoritative claim registry. Known limitation: the
    ///     scan covers THIS OctoMesh instance's tenants only — a number claimed by another
    ///     instance sharing the cluster bridge is not detectable here (the bridge's NetworkPolicy
    ///     trust boundary covers that).
    /// </summary>
    private async Task EnsureNumberIsUnclaimedAsync(string tenantId, string number)
    {
        foreach (var otherTenantId in adapterCache.GetEnabledTenantIds())
        {
            if (string.Equals(otherTenantId, tenantId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            IReadOnlyCollection<RtSignalChannel> otherChannels;
            try
            {
                otherChannels = await communicationRepository.GetSignalChannelsAsync(otherTenantId);
            }
            catch (Exception e)
            {
                logger.LogWarning(e,
                    "[{TenantId}] Could not check tenant '{OtherTenantId}' for a Signal number claim",
                    tenantId, otherTenantId);
                continue;
            }

            if (otherChannels.Any(c => string.Equals(c.Number, number, StringComparison.Ordinal)))
            {
                throw SignalChannelServiceException.NumberClaimedByOtherTenant(number);
            }
        }
    }

    /// <summary>
    ///     Whether the bridge already holds an account for the number (adopt-existing-account
    ///     check, see <see cref="RegisterAsync"/>). Answers <c>false</c> when the account list
    ///     cannot be read so the register flow falls back to the plain bridge register — bridge
    ///     errors then surface exactly as without the adoption check.
    /// </summary>
    private async Task<bool> IsAlreadyOnBridgeAsync(string tenantId, string apiUrl, string number)
    {
        try
        {
            var accounts = await bridgeClient.GetAccountsAsync(apiUrl);
            return accounts.Contains(number, StringComparer.Ordinal);
        }
        catch (SignalBridgeException e)
        {
            logger.LogWarning(e,
                "[{TenantId}] Could not read the bridge account list before registering '{Number}'; attempting a plain register",
                tenantId, number);
            return false;
        }
    }

    private string ResolveApiUrl(string? requestedApiUrl, RtSignalChannel? existing)
    {
        var resolved = !string.IsNullOrWhiteSpace(requestedApiUrl)
            ? requestedApiUrl.Trim()
            : !string.IsNullOrWhiteSpace(existing?.ApiUrl)
                ? existing.ApiUrl
                : options.Value.SignalBridgeApiUrl;

        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw SignalChannelServiceException.NoApiUrl();
        }

        return resolved.TrimEnd('/');
    }

    private static string RequireApiUrl(RtSignalChannel channel)
    {
        if (string.IsNullOrWhiteSpace(channel.ApiUrl))
        {
            // Mandatory in the CK model; belt-and-braces for hand-edited entities.
            throw SignalChannelServiceException.NoApiUrl();
        }

        return channel.ApiUrl;
    }

    /// <summary>
    ///     E.164: '+' followed by digits, 8..16 characters in total (7..15 digits).
    /// </summary>
    internal static string ValidateNumber(string? number)
    {
        var trimmed = number?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed[0] != '+' ||
            trimmed.Length is < 8 or > 16 ||
            !trimmed.Skip(1).All(char.IsAsciiDigit))
        {
            throw SignalChannelServiceException.InvalidNumber(number);
        }

        return trimmed;
    }

    /// <summary>
    ///     The SMS code arrives as "123-456"; the bridge accepts the digits. Tolerate the dash and
    ///     whitespace, demand digits.
    /// </summary>
    internal static string ValidateVerificationCode(string? code)
    {
        var normalized = code?.Trim().Replace("-", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrEmpty(normalized) || !normalized.All(char.IsAsciiDigit))
        {
            throw SignalChannelServiceException.InvalidVerificationCode();
        }

        return normalized;
    }

    private static string? NormalizeCaptcha(string? captchaToken)
    {
        var trimmed = captchaToken?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>
    ///     Prepends one audit entry to the channel's registration history (newest first) and trims
    ///     to the newest <see cref="MaxHistoryEntries"/>. The list is rebuilt and reassigned —
    ///     never mutated in place — because <c>AttributeRecordValueList</c> materializes a fresh
    ///     record per read, so in-place mutation is a silent no-op. The entry is only persisted by
    ///     the save the caller performs afterwards.
    /// </summary>
    private static void AppendHistory(RtSignalChannel channel, SignalChannelActor actor,
        RtSignalRegistrationActionEnum action, string? detail)
    {
        var entries = new List<RtSignalRegistrationEventRecord>
        {
            new()
            {
                At = DateTime.UtcNow,
                User = actor.ToAuditString(),
                Action = action,
                Detail = detail
            }
        };
        entries.AddRange(channel.RegistrationHistory ?? Enumerable.Empty<RtSignalRegistrationEventRecord>());

        channel.RegistrationHistory = new AttributeRecordValueList<RtSignalRegistrationEventRecord>(
            entries.Take(MaxHistoryEntries).Cast<RtRecord>().ToList());
    }

    /// <summary>
    ///     The audit detail for a failed bridge call — the bridge's message (e.g. "captcha
    ///     required"), prefixed with the rate-limit information (incl. Retry-After when known)
    ///     for a 429.
    /// </summary>
    private static string BridgeFailureDetail(SignalBridgeException e)
    {
        if (e.Kind != SignalBridgeErrorKind.RateLimited)
        {
            return e.Message;
        }

        return e.RetryAfter is { } retryAfter && retryAfter > TimeSpan.Zero
            ? $"rate limited (retry after {Math.Ceiling(retryAfter.TotalSeconds)}s): {e.Message}"
            : $"rate limited: {e.Message}";
    }

    /// <summary>
    ///     State updates on the failure paths must never mask the original bridge error.
    /// </summary>
    private async Task SaveBestEffortAsync(string tenantId, RtSignalChannel channel)
    {
        try
        {
            await communicationRepository.SaveSignalChannelAsync(tenantId, channel, false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "[{TenantId}] Could not persist the Signal channel error state", tenantId);
        }
    }

    private async Task StoreErrorEventBestEffortAsync(string tenantId, string message)
    {
        try
        {
            await eventService.StoreErrorEventAsync(tenantId, message);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "[{TenantId}] Could not store the Signal channel error event", tenantId);
        }
    }

    private static SignalChannelDto ToDto(RtSignalChannel channel, bool? bridgeRegistered, string? warning)
    {
        return new SignalChannelDto(
            channel.Number ?? string.Empty,
            channel.ApiUrl ?? string.Empty,
            (int)channel.RegistrationState,
            channel.RegisteredAt,
            channel.LastError,
            bridgeRegistered,
            warning,
            ToHistoryDtos(channel));
    }

    /// <summary>
    ///     Projects the stored history (already newest first by construction) 1:1 into the wire
    ///     shape <c>{at, user, action, detail}</c>.
    /// </summary>
    private static IReadOnlyList<SignalChannelHistoryEntryDto> ToHistoryDtos(RtSignalChannel channel)
    {
        return (channel.RegistrationHistory ?? Enumerable.Empty<RtSignalRegistrationEventRecord>())
            .Select(e => new SignalChannelHistoryEntryDto(
                e.At,
                e.User ?? "unknown",
                e.Action.ToString(),
                e.Detail))
            .ToList();
    }
}
