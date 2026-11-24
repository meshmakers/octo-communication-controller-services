namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     AB#9776 (decision "own issuer only", 2026-11-24): a pipeline's service account may only name this
///     installation's identity service and the pipeline's own tenant. The mesh adapter enforces the same rule at
///     run time (<c>OwnIssuerPolicy</c> in octo-mesh-adapter) and never sends a request to another issuer; this is
///     the deploy-time and health-time half, so such a configuration is refused with a message instead of failing
///     every execution.
/// </summary>
/// <remarks>
///     The own installation is spelled as: an empty value, the <c>{{service.authority}}</c> token in any case and
///     spacing (<see cref="PipelineServiceAccountProvisioningService.IsIssuerUriToken" />), or a concrete URL
///     equal to one of the own names — the controller's <c>AuthorityUrl</c> and the value <c>{{service.authority}}</c>
///     resolves to. URLs are compared after normalisation: absolute <c>http</c>/<c>https</c> without user info, query
///     or fragment; scheme and host without case, the default port made explicit, trailing slashes dropped, the path
///     compared ordinally. Anything that does not normalise is foreign (fails closed).
/// </remarks>
internal static class ServiceAccountOwnIssuer
{
    /// <summary>
    ///     Whether <paramref name="configuredIssuerUri" /> names this installation.
    /// </summary>
    internal static bool IsOwnIssuer(string? configuredIssuerUri, IEnumerable<string?> ownNames)
    {
        if (string.IsNullOrWhiteSpace(configuredIssuerUri))
        {
            return true;
        }

        if (PipelineServiceAccountProvisioningService.IsIssuerUriToken(configuredIssuerUri))
        {
            return true;
        }

        if (!TryNormalize(configuredIssuerUri, out var normalized))
        {
            return false;
        }

        foreach (var ownName in ownNames)
        {
            if (TryNormalize(ownName, out var own) && string.Equals(own, normalized, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    ///     Whether <paramref name="configuredTenantId" /> is empty or the pipeline's tenant (case and surrounding
    ///     blanks ignored, as tenant ids are compared in the adapter).
    /// </summary>
    internal static bool IsOwnTenant(string? configuredTenantId, string tenantId)
    {
        return string.IsNullOrWhiteSpace(configuredTenantId)
               || string.Equals(configuredTenantId.Trim(), tenantId.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool TryNormalize(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrEmpty(uri.IdnHost))
        {
            return false;
        }

        normalized = $"{uri.Scheme}://{uri.IdnHost.ToLowerInvariant()}:{uri.Port}{uri.AbsolutePath.TrimEnd('/')}";
        return true;
    }
}
