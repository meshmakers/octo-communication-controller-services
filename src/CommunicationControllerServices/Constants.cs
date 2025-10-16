
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices;

internal static class Constants
{
    internal static readonly DateTime StartTime = DateTime.UtcNow;

    private const string TenantId = "tenantId";
    private const string PoolName = "pool-name";
    private const string AdapterRtId = "adapter-rtId";
    private const string AdapterCkTypeId = "adapter-ckTypeId";

    public const string CommunicationControllerServiceEnabledKey = "CommunicationControllerServicesEnabled";

    public const string CommunicationControllerServiceIdentityDataVersionKey = "CommunicationControllerServicesIdentityData";
    public const int CommunicationControllerServiceIdentityDataVersionValue = 2;
    
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