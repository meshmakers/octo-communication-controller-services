using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Meshmakers.Octo.Services.Methods.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Methods;

/// <summary>
///     The real caller token validator of octo-common-services (<see cref="ICkMethodCallerTokenValidator" />) over a fake
///     identity service (discovery document and JWKS) and tokens signed with a throw-away RSA key (AB#7034). The pipeline
///     handler tests use it instead of a substitute: the result type of the validator cannot be built by a test, and a
///     substitute would not prove that a forged or refused token is really refused.
/// </summary>
internal sealed class CallerTokenKit : IDisposable
{
    public const string Authority = "https://identity.test/";
    public const string KeyId = "test-key-1";
    public const string TokenSubject = "token-subject-7";
    public const string TokenClient = "studio";
    public const string ServiceClientId = "mesh-bot-job";

    /// <summary>A marker in the payload of every token made here, so a log assertion can search for it.</summary>
    public const string PayloadMarker = "PAYLOAD-MARKER-4711";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly ServiceProvider _provider;

    public CallerTokenKit()
    {
        Signing = new SigningCredentials(new RsaSecurityKey(_rsa) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256);
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Production);
        Logs = new CapturingLoggerProvider();
        _provider = new ServiceCollection()
            .AddLogging(b => b.AddProvider(Logs))
            .AddSingleton(environment)
            .AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build())
            .AddCkMethodCallerTokenValidation(o => o.Authority = Authority)
            .AddSingleton<IHttpClientFactory>(new FakeIdentityFactory(this))
            .BuildServiceProvider();
        Validator = _provider.GetRequiredService<ICkMethodCallerTokenValidator>();
    }

    public SigningCredentials Signing { get; }

    public ICkMethodCallerTokenValidator Validator { get; }

    public CapturingLoggerProvider Logs { get; }

    /// <summary>A token like the identity service issues it; a user token unless <paramref name="user" /> is false.</summary>
    public string Create(Action<Dictionary<string, object>>? claims = null, string issuer = Authority,
        DateTime? expires = null, bool user = true, string? tenant = PipelineMethodTestSupport.TenantId,
        bool scope = true, SigningCredentials? signWith = null)
    {
        var payload = new Dictionary<string, object> { ["marker"] = PayloadMarker };
        if (user)
        {
            payload["sub"] = TokenSubject;
            payload["client_id"] = TokenClient;
            payload["role"] = new[] { "Reporting", "FirmwareOperator" };
        }
        else
        {
            payload["client_id"] = ServiceClientId;
        }

        if (tenant != null)
        {
            payload["tenant_id"] = tenant;
        }

        if (scope)
        {
            payload["scope"] = new[] { "octo_api", "openid" };
        }

        claims?.Invoke(payload);
        var validUntil = expires ?? DateTime.UtcNow.AddMinutes(10);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Claims = payload,
            NotBefore = validUntil.AddMinutes(-20),
            Expires = validUntil,
            SigningCredentials = signWith ?? Signing
        });
    }

    /// <summary>A token signed by a key the identity service does not publish.</summary>
    public string CreateWithForeignKey()
    {
        using var foreign = RSA.Create(2048);
        return Create(signWith: new SigningCredentials(new RsaSecurityKey(foreign) { KeyId = KeyId },
            SecurityAlgorithms.RsaSha256));
    }

    /// <summary>An unsigned token (<c>alg: none</c>) with the claims of a valid user token.</summary>
    public static string Unsigned()
    {
        var header = Base64UrlEncoder.Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        var payload = Base64UrlEncoder.Encode(
            $"{{\"iss\":\"{Authority}\",\"sub\":\"{TokenSubject}\",\"tenant_id\":\"{PipelineMethodTestSupport.TenantId}\"," +
            $"\"scope\":\"octo_api\",\"marker\":\"{PayloadMarker}\",\"exp\":{exp}}}");
        return header + "." + payload + ".";
    }

    public void Dispose()
    {
        _provider.Dispose();
        _rsa.Dispose();
    }

    private string KeysJson()
    {
        var p = _rsa.ExportParameters(false);
        return "{\"keys\":[{\"kty\":\"RSA\",\"use\":\"sig\",\"alg\":\"RS256\",\"kid\":\"" + KeyId + "\",\"n\":\"" +
               Base64UrlEncoder.Encode(p.Modulus) + "\",\"e\":\"" + Base64UrlEncoder.Encode(p.Exponent) + "\"}]}";
    }

    private sealed class FakeIdentityFactory(CallerTokenKit kit) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(kit), disposeHandler: true);
    }

    private sealed class Handler(CallerTokenKit kit) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.EndsWith("/.well-known/openid-configuration", StringComparison.Ordinal))
            {
                json = $"{{\"issuer\":\"{Authority}\",\"jwks_uri\":\"{Authority}.well-known/openid-configuration/jwks\"}}";
            }
            else if (path.EndsWith("/jwks", StringComparison.Ordinal))
            {
                json = kit.KeysJson();
            }
            else
            {
                return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}

/// <summary>One sink for the logs of every logger created through the provider (validator and invoker).</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _entries = new();

    public string All => string.Join("\n", _entries);

    public ILogger CreateLogger(string categoryName) => new Sink(_entries);

    public void Dispose()
    {
    }

    private sealed class Sink(ConcurrentQueue<string> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            entries.Enqueue($"[{logLevel}] {formatter(state, exception)}{(exception is null ? string.Empty : " | " + exception)}");
    }
}
