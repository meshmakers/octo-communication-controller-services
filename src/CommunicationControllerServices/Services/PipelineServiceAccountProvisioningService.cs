using System.Security.Cryptography;
using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Communication.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="IPipelineServiceAccountProvisioningService" />
/// <remarks>
/// 🔴 The identity command client is resolved <b>per call from a fresh scope</b>, exactly like
/// <see cref="CommunicationEventService" /> resolves <c>IEventRepository</c>. Constructor-injecting
/// it fails DI validation: <c>ICommandClient&lt;T&gt;</c> wraps MassTransit's
/// <c>IRequestClient&lt;T&gt;</c>, which is <b>scoped</b>, and this service is a singleton consumed
/// by the singleton <c>PoolService</c>.
/// </remarks>
internal class PipelineServiceAccountProvisioningService(
    ICommunicationRepository communicationRepository,
    IPipelineServiceAccountResolver serviceAccountResolver,
    IServiceProvider serviceProvider,
    ICommunicationEventService eventService,
    IOptions<CommunicationControllerOptions> options)
    : IPipelineServiceAccountProvisioningService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Entropy of a generated client secret. 384 bits, well past anything brute-forceable, and it
    /// base64url-encodes to exactly 64 characters with no padding to strip.
    /// </summary>
    internal const int SecretByteLength = 48;

    /// <summary>
    /// Bounds how long one adapter's identity round trip may take. Deliberately a plain
    /// <c>GetResponse</c> with a timeout instead of <c>GetResponseWithRetry</c> (5 × 30 s): this runs
    /// on the tenant-start path, and a retry storm against an identity service that is still coming
    /// up would serialise into the cold start. The sweep is idempotent and re-runs on every tenant
    /// load, so a missed pass costs nothing but a delay.
    /// </summary>
    private static readonly TimeSpan IdentityCommandTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task<PipelineServiceAccountProvisioningReport> EnsureTenantProvisionedAsync(string tenantId)
    {
        IReadOnlyCollection<RtAdapter> adapters;
        try
        {
            adapters = await communicationRepository.GetAdaptersAsync(tenantId);
        }
        catch (Exception e)
        {
            // A tenant whose CK cache is being unloaded concurrently (tenant update / disable) is the
            // common case here. Report it; the next tenant load sweeps again.
            Logger.Warn(e, "[{TenantId}] Pipeline service account backfill skipped: adapter lookup failed", tenantId);
            return new PipelineServiceAccountProvisioningReport(0, 0, 0,
                [$"Adapter lookup failed: {e.Message}"]);
        }

        if (adapters.Count == 0)
        {
            return PipelineServiceAccountProvisioningReport.Empty;
        }

        var provisioned = 0;
        var repaired = 0;
        var alreadyProvisioned = 0;
        var failures = new List<string>();

        foreach (var adapter in adapters)
        {
            // Per-adapter isolation: one adapter whose provisioning fails must not stop the others,
            // exactly like DefaultConfigurationInitializationService isolates one tenant from the rest.
            var outcome = await EnsureAdapterProvisionedAsync(tenantId, adapter);
            switch (outcome)
            {
                case PipelineServiceAccountProvisioningOutcome.Provisioned:
                    provisioned++;
                    break;
                case PipelineServiceAccountProvisioningOutcome.Repaired:
                    repaired++;
                    break;
                case PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned:
                    alreadyProvisioned++;
                    break;
                default:
                    failures.Add(
                        $"Adapter '{adapter.Name ?? adapter.RtId.ToString()}' ({adapter.RtId}) has no pipeline service account.");
                    break;
            }
        }

        return new PipelineServiceAccountProvisioningReport(provisioned, repaired, alreadyProvisioned, failures);
    }

    /// <inheritdoc />
    public async Task<PipelineServiceAccountProvisioningOutcome> EnsureAdapterProvisionedAsync(string tenantId,
        RtAdapter adapter)
    {
        try
        {
            return await ProvisionAsync(tenantId, adapter);
        }
        catch (Exception e)
        {
            // Never throws: the caller is a startup path, and the deploy guard already refuses the
            // pipelines of an unprovisioned adapter with an actionable message. Loud (Error log +
            // persistent tenant event) rather than fatal — an operator must be able to see WHY a
            // tenant's pipelines started refusing to deploy without reading pod logs.
            Logger.Error(e,
                "[{TenantId}] Failed to provision the pipeline service account of adapter '{AdapterName}' ({AdapterRtId})",
                tenantId, adapter.Name, adapter.RtId);

            try
            {
                await eventService.StoreErrorEventAsync(tenantId,
                    $"Could not provision the pipeline service account of adapter '{adapter.Name ?? adapter.RtId.ToString()}' (AB#5027): {e.Message}. " +
                    "Until it exists, deploying any pipeline of this adapter is refused. The next tenant load retries automatically; " +
                    "to retry now, re-run the tenant setup (PUT {systemTenant}/v1/tenants/clearCache?childTenantId=<tenant> on the asset repository).");
            }
            catch (Exception eventException)
            {
                Logger.Warn(eventException, "[{TenantId}] Could not store the service-account provisioning error event",
                    tenantId);
            }

            return PipelineServiceAccountProvisioningOutcome.Failed;
        }
    }

    private async Task<PipelineServiceAccountProvisioningOutcome> ProvisionAsync(string tenantId, RtAdapter adapter)
    {
        var wellKnownName = BuildWellKnownName(adapter.RtId);
        var clientId = BuildClientId(adapter.RtId);

        var linked = await serviceAccountResolver.GetAdapterDefaultAsync(tenantId, adapter.RtId);

        // The entity may exist while the edge does not — a half-applied earlier run, or an operator
        // who unlinked it. Look it up by its deterministic well-known name so we adopt our own
        // earlier work instead of creating a second credential entity next to it.
        var existing = linked ?? await communicationRepository
            .GetServiceAccountByWellKnownNameAsync(tenantId, wellKnownName);

        // Read through GetAttributeValueOrDefault, never through the generated properties: all four
        // attributes are mandatory on the CK type, so the generated getters throw
        // InvalidAttributeValueException on a value that was never written — which is exactly the
        // half-configured entity this method exists to repair.
        var existingSecret = ReadAttribute(existing, nameof(RtServiceAccountConfiguration.ClientSecret));
        var existingClientId = ReadAttribute(existing, nameof(RtServiceAccountConfiguration.ClientId));
        var existingIssuerUri = ReadAttribute(existing, nameof(RtServiceAccountConfiguration.IssuerUri));
        var existingTenantId = ReadAttribute(existing, nameof(RtServiceAccountConfiguration.TenantId));

        var isComplete = existing != null &&
                         !string.IsNullOrWhiteSpace(existingSecret) &&
                         !string.IsNullOrWhiteSpace(existingClientId) &&
                         !string.IsNullOrWhiteSpace(existingIssuerUri) &&
                         !string.IsNullOrWhiteSpace(existingTenantId);

        // 🔴 The secret never leaves this method except through the two sanctioned sinks (the bus
        // command to the identity service, and the tenant-scoped configuration entity). It is not
        // logged at any level, not truncated, and not carried in any exception message.
        var secret = isComplete
            // Re-sending the SAME plaintext is what makes a second run a no-op: the identity service
            // hashes it to the same value, so nothing rotates — while still healing a client that
            // was deleted underneath us. Generating a fresh one here would rotate the secret on
            // every service restart and break every adapter that had already cached the old one.
            ? existingSecret!
            : GenerateSecret();

        await SendIdentityClientAsync(tenantId, adapter, clientId, secret);

        if (isComplete && linked != null && existingClientId == clientId &&
            existingIssuerUri == options.Value.AuthorityUrl && existingTenantId == tenantId)
        {
            // Healthy and current: entity complete, linked to this adapter, and pointing at the
            // issuer/tenant this instance serves. Leave it completely untouched.
            return PipelineServiceAccountProvisioningOutcome.AlreadyProvisioned;
        }

        var isNewEntity = existing == null;
        var serviceAccount = new RtServiceAccountConfiguration
        {
            RtId = existing?.RtId ?? OctoObjectId.GenerateNewId(),
            CkTypeId = SystemCommunicationCkIds.RtCkServiceAccountConfigurationTypeId,
            RtWellKnownName = wellKnownName,
            ClientId = clientId,
            ClientSecret = secret,
            // IssuerUri is per-environment author configuration (see serviceAccountConfiguration.yaml):
            // taking it from this instance's configuration is what lets a tenant that moved clusters
            // converge onto the new identity host on the next sweep.
            IssuerUri = options.Value.AuthorityUrl,
            // The adapter needs this to send acr_values=tenant:{tenantId} — without it the
            // delegation grant (AB#5031) cannot resolve a tenant at all.
            TenantId = tenantId
        };

        await communicationRepository.SavePipelineServiceAccountAsync(tenantId, adapter.ToRtEntityId(),
            serviceAccount, isNewEntity);

        Logger.Info(
            "[{TenantId}] Pipeline service account '{WellKnownName}' (client '{ClientId}') is linked to adapter '{AdapterName}' ({AdapterRtId})",
            tenantId, wellKnownName, clientId, adapter.Name, adapter.RtId);

        return isNewEntity
            ? PipelineServiceAccountProvisioningOutcome.Provisioned
            : PipelineServiceAccountProvisioningOutcome.Repaired;
    }

    /// <summary>
    /// Creates or converges the identity client over the distribution event hub — the only channel
    /// this service has to the identity service.
    ///
    /// <para>
    /// Why not the identity REST API (<c>ClientsController</c> already accepts a plaintext secret and
    /// hashes it): calling it needs an <c>octo_api</c> bearer token, i.e. a client-credentials
    /// identity — which is exactly what this method is trying to create. No such bootstrap client is
    /// seeded anywhere, so the REST route is circular. It would also add a
    /// <c>Meshmakers.Octo.Sdk.ServiceClient</c> package reference and a second identity transport to
    /// a service that has one.
    /// </para>
    ///
    /// <para>
    /// The command is re-sent on every pass on purpose: it is the only way grants, scope and role
    /// assignment converge for a client that was created before this code (or edited by hand), and
    /// the consumer treats a re-send of the same secret as a no-op.
    /// </para>
    /// </summary>
    private async Task SendIdentityClientAsync(string tenantId, RtAdapter adapter, string clientId, string secret)
    {
        var request = new CreateIdentityDataCommandRequest(tenantId)
        {
            Clients = new List<DistClientDto>
            {
                new(clientId,
                    $"Pipeline service account for adapter '{adapter.Name ?? adapter.RtId.ToString()}'",
                    options.Value.PublicUrl)
                {
                    AllowedGrantTypes =
                    [
                        OidcConstants.GrantTypes.ClientCredentials,
                        // Precondition for AB#5031: Duende binds one extension-grant validator per
                        // grant type AND gates it on the client's own AllowedGrantTypes, so without
                        // this URN a delegation request is rejected before OnBehalfOfGrantValidator
                        // ever runs. Adding it later would mean touching every provisioned tenant.
                        Constants.OnBehalfOfGrantType
                    ],
                    RequireConsent = false,
                    RedirectUris = [],
                    PostLogoutRedirectUris = [],
                    AllowedCorsOrigins = [],
                    // Exactly the API scope, nothing else. The consumer writes AllowedScopes verbatim
                    // (unlike ClientsController, which unions in OctoDefaultScopes), and the delegation
                    // grant deliberately refuses offline_access — the role intersection is computed at
                    // issuance, so a refresh token would freeze it.
                    AllowedScopes = [CommonConstants.OctoApiFullAccess],
                    AllowOfflineAccess = false,
                    ClientSecret = secret,
                    RequireClientSecret = true,
                    // The controller's own endpoints authorize on the octo_api SCOPE, not on a role
                    // (Program.cs: every policy is a RequireClaim on the scope claim), so the deploy
                    // calls a pipeline makes back into this service already work with the scope alone.
                    // CommunicationManagement is granted anyway because it is the role the platform
                    // uses for communication management, and — far more important — because the
                    // AB#5031 delegated token carries the INTERSECTION of service-account and user
                    // roles. A role the service account lacks can never appear in a delegated token.
                    AssignedRoleNames = [CommonConstants.CommunicationManagementRole]
                }
            }
        };

        using var scope = serviceProvider.CreateScope();
        var commandClient = scope.ServiceProvider
            .GetRequiredService<ICommandClient<CreateIdentityDataCommandRequest>>();

        var response = await commandClient
            .GetResponse<EnumCommandResponse<CreateIdentityDataResult>>(request, CancellationToken.None,
                IdentityCommandTimeout);

        switch (response.Response)
        {
            case CreateIdentityDataResult.Success:
                return;
            case CreateIdentityDataResult.SuccessIdentityDataSeedPending:
                // The client exists; only the tenant's role seed has not landed yet, so the role
                // assignment was skipped identity-side. The entity is still written so pipelines can
                // deploy — the next sweep re-sends and the roles converge then.
                Logger.Warn(
                    "[{TenantId}] Pipeline service account client '{ClientId}' was created, but the tenant's identity role seed is not in place yet — roles will be assigned on a later pass",
                    tenantId, clientId);
                return;
            default:
                throw new InvalidOperationException(
                    $"[{tenantId}] The identity service refused to create the pipeline service account client " +
                    $"'{clientId}': {response.Response}.");
        }
    }

    /// <summary>
    /// Reads one attribute without triggering the generated mandatory-attribute guard. A
    /// configuration counts as usable only when all four are present — the adapter's
    /// <c>ServiceAccountTokenService</c> needs issuer and client id to discover and authenticate,
    /// both grants need the secret, and the delegation grant needs the tenant id for
    /// <c>acr_values</c>. An entity that fails this is repaired rather than preserved: the "leave a
    /// working configuration untouched" rule is about *working* ones.
    /// </summary>
    private static string? ReadAttribute(RtServiceAccountConfiguration? configuration, string attributeName)
    {
        return configuration?.GetAttributeValueOrDefault(attributeName) as string;
    }

    /// <summary>
    /// Deterministic per adapter, so a second provisioning run finds its own earlier entity instead
    /// of creating a duplicate — and so the name is stable across controller pods and restarts. The
    /// rtId (not the adapter name) is the key: names are editable, rtIds are not.
    /// </summary>
    internal static string BuildWellKnownName(OctoObjectId adapterRtId)
    {
        return $"pipeline-service-account-{adapterRtId}";
    }

    /// <summary>
    /// Deterministic client id, same reasoning as the well-known name. It is also what makes the
    /// identity-side upsert idempotent — <c>CreateIdentityDataCommandRequestConsumer</c> keys on
    /// <c>ClientId</c>.
    /// </summary>
    internal static string BuildClientId(OctoObjectId adapterRtId)
    {
        return $"octo-pipeline-sa-{adapterRtId}";
    }

    /// <summary>
    /// Cryptographically secure, URL-safe, 384 bits. URL-safe because the value travels as a form
    /// field in the OAuth token request the adapter sends.
    /// </summary>
    internal static string GenerateSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretByteLength);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
