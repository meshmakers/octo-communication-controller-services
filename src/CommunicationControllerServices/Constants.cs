
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Services.Infrastructure.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices;

internal static class Constants
{
    internal static readonly DateTime StartTime = DateTime.UtcNow;

    private const string TenantId = "tenantId";
    private const string PoolName = "pool-name";
    private const string AdapterRtId = "adapter-rtId";
    private const string AdapterCkTypeId = "adapter-ckTypeId";

    /// <summary>
    /// Tenant configuration key of the Communication enabled flag. The literal is owned by
    /// octo-common-services so the asset repository's delete/detach guard reads the same key (AB#4255).
    /// </summary>
    public const string CommunicationControllerServiceEnabledKey = TenantCapabilityConfigurationKeys.Communication;

    /// <summary>
    /// Tenant configuration key of the on-demand lifecycle configuration (AB#4914). Same
    /// key-value store as the enabled flag; carries <c>CommunicationLifecycleConfiguration</c>
    /// (currently just the per-tenant ScaleToZeroEnabled gate, default off).
    /// </summary>
    public const string CommunicationLifecycleConfigurationKey = "communicationLifecycle";

    public const string CommunicationControllerServiceIdentityDataVersionKey = "CommunicationControllerServicesIdentityData";
    public const int CommunicationControllerServiceIdentityDataVersionValue = 3;

    /// <summary>
    /// The OctoMesh delegation ("on-behalf-of") grant type (AB#5026), put on every provisioned
    /// pipeline service account by <see cref="Services.PipelineServiceAccountProvisioningService"/>.
    /// <para>
    /// Duende gates its extension-grant validators on the client's own <c>AllowedGrantTypes</c>, so a
    /// service account without this URN has its delegation request rejected before
    /// <c>OnBehalfOfGrantValidator</c> runs — which is why it is seeded now (AB#5027) rather than
    /// when AB#5031 lands, when adding it would mean touching every already-provisioned tenant.
    /// </para>
    /// <para>
    /// The literal is duplicated on purpose: the canonical definition is
    /// <c>DelegationConstants.OnBehalfOfGrantType</c> in the <c>IdentityServices</c> web assembly,
    /// which is not referenceable from here (nor from <c>octo-sdk</c>); the mesh adapter's
    /// <c>ServiceAccountTokenService.OnBehalfOfGrantType</c> carries the same copy. Promoting it into
    /// a shared package is worth doing when one of these repos next needs a contract change anyway.
    /// </para>
    /// </summary>
    public const string OnBehalfOfGrantType = "urn:meshmakers:params:oauth:grant-type:on-behalf-of";
    
    /// <summary>
    ///     Policy for system api authorization
    /// </summary>
    public const string SystemCommunicationApiPolicy = "SystemCommunicationApiPolicy";
    
    /// <summary>
    ///     Policy for tenant api read only authorization
    /// </summary>
    public const string TenantCommunicationApiReadOnlyPolicy = "TenantCommunicationApiReadOnlyPolicy";
    
    /// <summary>
    ///     Policy for tenant api read write authorization
    /// </summary>
    public const string TenantCommunicationApiReadWritePolicy = "SystemCommunicationApiReadWritePolicy";

    public static string? GetTenantId(this HttpContext httpContext)
    {
        return (string?)httpContext.GetRouteValue(TenantId);
    }
    
    public static string? GetPoolName(this HttpContext httpContext)
    {
        return httpContext.Request.Headers[PoolName];
    }
    
    public static RtEntityId? GetAdapterRtEntityId(this HttpContext httpContext)
    {
        var rtId = (string?) httpContext.Request.Headers[AdapterRtId];
        var ckTypeId = (string?) httpContext.Request.Headers[AdapterCkTypeId];
        if (!string.IsNullOrWhiteSpace(rtId) && !string.IsNullOrWhiteSpace(ckTypeId))
        {
            return new RtEntityId(new RtCkId<CkTypeId>(ckTypeId), new OctoObjectId(rtId));
        }

        return null;
    }
}