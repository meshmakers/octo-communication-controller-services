using System.Net;
using System.Text;
using System.Text.Json;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Default <see cref="ISignalBridgeClient"/> — thin HTTP wrapper over the
///     <c>signal-cli-rest-api</c> bridge. Stateless; the base URL is a per-call argument because it
///     lives on the tenant's <c>SignalChannel</c> entity (with an instance-wide default from
///     <c>CommunicationControllerOptions.SignalBridgeApiUrl</c>).
/// </summary>
internal sealed class SignalBridgeClient(
    ILogger<SignalBridgeClient> logger,
    IHttpClientFactory httpClientFactory) : ISignalBridgeClient
{
    /// <summary>
    ///     Named client registered in <c>Program.cs</c> with a 60s timeout — a bridge register
    ///     can legitimately take 10-30s while signal-cli talks to the Signal servers.
    /// </summary>
    internal const string HttpClientName = "SignalBridge";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task RegisterAsync(string apiUrl, string number, string? captchaToken,
        CancellationToken cancellationToken = default)
    {
        // The bridge accepts an empty body too, but always sending use_voice keeps the request
        // shape deterministic; the captcha token is only present when the caller solved one.
        var body = captchaToken == null
            ? new Dictionary<string, object> { ["use_voice"] = false }
            : new Dictionary<string, object> { ["captcha"] = captchaToken, ["use_voice"] = false };

        await SendAsync(apiUrl, HttpMethod.Post, $"v1/register/{Uri.EscapeDataString(number)}", body,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task VerifyAsync(string apiUrl, string number, string verificationCode,
        CancellationToken cancellationToken = default)
    {
        await SendAsync(apiUrl, HttpMethod.Post,
            $"v1/register/{Uri.EscapeDataString(number)}/verify/{Uri.EscapeDataString(verificationCode)}",
            null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task UnregisterAsync(string apiUrl, string number, CancellationToken cancellationToken = default)
    {
        // delete_local_data removes the account from the bridge state entirely, so the number
        // becomes claimable again (register on another tenant, or on another bridge).
        var body = new Dictionary<string, object> { ["delete_local_data"] = true };

        await SendAsync(apiUrl, HttpMethod.Post, $"v1/unregister/{Uri.EscapeDataString(number)}", body,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetAccountsAsync(string apiUrl,
        CancellationToken cancellationToken = default)
    {
        var content = await SendAsync(apiUrl, HttpMethod.Get, "v1/accounts", null, cancellationToken);
        if (string.IsNullOrWhiteSpace(content))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(content, SerializerOptions) ?? [];
        }
        catch (JsonException e)
        {
            throw SignalBridgeException.Rejected(200, $"Unexpected accounts payload: {e.Message}");
        }
    }

    /// <inheritdoc />
    public async Task UpdateProfileAsync(string apiUrl, string number, string displayName,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object> { ["name"] = displayName };

        await SendAsync(apiUrl, HttpMethod.Put, $"v1/profiles/{Uri.EscapeDataString(number)}", body,
            cancellationToken);
    }

    private async Task<string> SendAsync(string apiUrl, HttpMethod method, string relativePath,
        IReadOnlyDictionary<string, object>? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, BuildUri(apiUrl, relativePath));
        if (body != null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, SerializerOptions),
                Encoding.UTF8, "application/json");
        }

        HttpResponseMessage response;
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            response = await client.SendAsync(request, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or UriFormatException
                                      or InvalidOperationException)
        {
            // TaskCanceledException without a caller cancellation is the HttpClient timeout;
            // Uri/InvalidOperation cover a malformed ApiUrl on the entity.
            logger.LogWarning(e, "Signal bridge call {Method} {Path} against '{ApiUrl}' failed",
                method, relativePath, apiUrl);
            throw SignalBridgeException.Unreachable(apiUrl, e);
        }

        using (response)
        {
            var content = response.Content == null!
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                return content;
            }

            var error = ExtractError(content);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                throw SignalBridgeException.RateLimited(error, response.Headers.RetryAfter?.Delta ??
                    (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow));
            }

            throw SignalBridgeException.Rejected((int)response.StatusCode, error);
        }
    }

    /// <summary>
    ///     The bridge reports failures as <c>{"error": "..."}</c>; anything else (proxy error
    ///     pages etc.) is surfaced verbatim, truncated so a misdirected ApiUrl cannot flood the
    ///     tenant's event log with an HTML dump.
    /// </summary>
    private static string ExtractError(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return "no error details provided";
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var errorProperty) &&
                errorProperty.ValueKind == JsonValueKind.String)
            {
                return errorProperty.GetString() ?? "no error details provided";
            }
        }
        catch (JsonException)
        {
            // Not JSON — fall through to the raw (truncated) content.
        }

        const int maxLength = 500;
        return content.Length <= maxLength ? content : content[..maxLength];
    }

    private static Uri BuildUri(string apiUrl, string relativePath)
    {
        var baseUri = new Uri(apiUrl.TrimEnd('/') + "/", UriKind.Absolute);
        return new Uri(baseUri, relativePath);
    }
}
