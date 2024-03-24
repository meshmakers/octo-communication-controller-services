
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices;

internal static class Constants
{
    public const string TenantId = "tenantId";
    public const string PoolName = "pool-name";
    public const string AdapterRtId = "adapter-rtId";
    public const string AdapterCkTypeId = "adapter-ckTypeId";

    public const string CommunicationControllerServiceSchemaVersionKey = "CommunicationControllerServices";
    public const int CommunicationControllerServiceSchemaVersionValue = 1;
    public const string CacheFileName ="CommunicationControllerServicesCache.json";

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
            return new RtEntityId(new CkId<CkTypeId>(ckTypeId), new OctoObjectId(rtId));
        }

        return null;
    }
}