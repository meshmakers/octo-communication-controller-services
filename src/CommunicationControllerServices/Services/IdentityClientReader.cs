using System.Text.Json;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Options;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.Extensions.Options;
using NLog;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc cref="IIdentityClientReader" />
/// <remarks>
/// Talks to the identity service's tenant REST API at the installation's <c>AuthorityUrl</c> — the
/// same base address the controller's own JWT bearer setup discovers metadata from, so any
/// deployment where tokens validate can also answer this read. The caller's bearer token is
/// forwarded verbatim from the ambient HTTP request (<see cref="IHttpContextAccessor" />); see the
/// interface remarks for why that is the transport of choice.
/// </remarks>
internal class IdentityClientReader(
    IHttpClientFactory httpClientFactory,
    IHttpContextAccessor httpContextAccessor,
    IOptions<CommunicationControllerOptions> options)
    : IIdentityClientReader
{
    /// <summary>Named client; timeout configured at registration (Program.cs).</summary>
    internal const string HttpClientName = "IdentityClientReader";

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Identity serializes with the ASP.NET default (camelCase); the DTO properties are
    /// PascalCase, so the read must be case-insensitive.
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <inheritdoc />
    public async Task<IdentityClientLookup> GetClientAsync(string tenantId, string clientId, bool includeRoles)
    {
        var bearer = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(bearer))
        {
            // Background paths (no ambient HTTP request) and anonymous transports end here. By
            // contract every consumer treats Unavailable as "unknown", never as "missing".
            return IdentityClientLookup.Unavailable(
                "no caller bearer token is available to query the identity service with");
        }

        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        var baseUrl = options.Value.AuthorityUrl.TrimEnd('/');
        var clientUrl =
            $"{baseUrl}/{Uri.EscapeDataString(tenantId)}/v1/Clients/{Uri.EscapeDataString(clientId)}";

        ClientDto? client;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, clientUrl);
            request.Headers.TryAddWithoutValidation("Authorization", bearer);
            using var response = await httpClient.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return IdentityClientLookup.NotFound;
            }

            if (!response.IsSuccessStatusCode)
            {
                // 401/403/5xx: not an authoritative "does not exist" — the caller's privileges or
                // the identity service's state got in the way of the question.
                return IdentityClientLookup.Unavailable(
                    $"the identity service answered {(int)response.StatusCode} for client '{clientId}'");
            }

            client = JsonSerializer.Deserialize<ClientDto>(
                await response.Content.ReadAsStringAsync(), JsonOptions);
        }
        catch (Exception e)
        {
            // Connection refused, DNS, timeout, malformed payload — identity is effectively down
            // for this read. Non-blocking by contract; the exception message carries no secret.
            return IdentityClientLookup.Unavailable(
                $"the identity service could not be queried: {e.Message}");
        }

        if (client == null)
        {
            return IdentityClientLookup.Unavailable(
                $"the identity service returned an empty client payload for '{clientId}'");
        }

        IReadOnlyList<string>? roleNames = null;
        if (includeRoles)
        {
            roleNames = await TryGetAssignedRoleNamesAsync(httpClient, bearer, baseUrl, tenantId, clientId);
        }

        return new IdentityClientLookup(IdentityClientLookupStatus.Found, client, roleNames, null);
    }

    /// <inheritdoc />
    public async Task<IdentityClientActorsLookup> GetActorClientIdsAsync(string tenantId, string clientId)
    {
        var bearer = httpContextAccessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(bearer))
        {
            return IdentityClientActorsLookup.Unavailable(
                "no caller bearer token is available to query the identity service with");
        }

        var httpClient = httpClientFactory.CreateClient(HttpClientName);
        var baseUrl = options.Value.AuthorityUrl.TrimEnd('/');
        var actorsUrl =
            $"{baseUrl}/{Uri.EscapeDataString(tenantId)}/v1/Clients/{Uri.EscapeDataString(clientId)}/actors";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, actorsUrl);
            request.Headers.TryAddWithoutValidation("Authorization", bearer);
            using var response = await httpClient.SendAsync(request);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // Authoritative: the TARGET client does not exist — no edge can exist onto it.
                return IdentityClientActorsLookup.NotFound;
            }

            if (!response.IsSuccessStatusCode)
            {
                return IdentityClientActorsLookup.Unavailable(
                    $"the identity service answered {(int)response.StatusCode} for the actors of client '{clientId}'");
            }

            var actorClientIds = JsonSerializer.Deserialize<List<string>>(
                await response.Content.ReadAsStringAsync(), JsonOptions);
            if (actorClientIds == null)
            {
                return IdentityClientActorsLookup.Unavailable(
                    $"the identity service returned an empty actors payload for client '{clientId}'");
            }

            return IdentityClientActorsLookup.Found(actorClientIds);
        }
        catch (Exception e)
        {
            return IdentityClientActorsLookup.Unavailable(
                $"the identity service could not be queried: {e.Message}");
        }
    }

    /// <summary>
    /// Resolves the names of the client's directly assigned roles:
    /// <c>GET Clients/{id}/roles</c> answers role <b>rtIds</b>, so the tenant's role list is
    /// fetched once to map them to the names the declaration (<c>AssignedRoleNames</c>) speaks in.
    /// Best-effort: any failure degrades to <c>null</c> ("roles unknown") with a debug log — the
    /// client-existence answer stands on its own.
    /// </summary>
    private static async Task<IReadOnlyList<string>?> TryGetAssignedRoleNamesAsync(HttpClient httpClient,
        string bearer, string baseUrl, string tenantId, string clientId)
    {
        try
        {
            var roleIds = await GetAsync<List<string>>(httpClient, bearer,
                $"{baseUrl}/{Uri.EscapeDataString(tenantId)}/v1/Clients/{Uri.EscapeDataString(clientId)}/roles");
            if (roleIds == null)
            {
                return null;
            }

            if (roleIds.Count == 0)
            {
                return [];
            }

            var roles = await GetAsync<List<RoleDto>>(httpClient, bearer,
                $"{baseUrl}/{Uri.EscapeDataString(tenantId)}/v1/Roles");
            if (roles == null)
            {
                return null;
            }

            var namesById = roles
                .Where(r => r.Id != null && r.Name != null)
                .ToDictionary(r => r.Id!, r => r.Name!, StringComparer.Ordinal);

            // An id without a name (role deleted between the two reads) maps to the raw id — a
            // drift report naming an unknown edge beats silently dropping it.
            return roleIds.Select(id => namesById.GetValueOrDefault(id, id)).ToList();
        }
        catch (Exception e)
        {
            Logger.Debug(e, "[{TenantId}] Could not resolve the assigned roles of identity client '{ClientId}'",
                tenantId, clientId);
            return null;
        }
    }

    private static async Task<T?> GetAsync<T>(HttpClient httpClient, string bearer, string url) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Authorization", bearer);
        using var response = await httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return JsonSerializer.Deserialize<T>(await response.Content.ReadAsStringAsync(), JsonOptions);
    }
}
