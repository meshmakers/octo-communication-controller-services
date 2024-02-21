
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices;

internal static class Constants
{
    public const string TenantId = "tenantId";
    public const string PoolName = "pool-name";
    public const string AdapterRtId = "adapter-rtId";

    public const string CommunicationControllerServiceSchemaVersionKey = "CommunicationControllerServices";
    public const int CommunicationControllerServiceSchemaVersionValue = 1;
    
    public static string? GetTenantId(this HttpContext httpContext)
    {
        return (string?)httpContext.GetRouteValue(TenantId);
    }
    
    public static string? GetPoolName(this HttpContext httpContext)
    {
        return httpContext.Request.Headers[PoolName];
    }
    
    public static OctoObjectId? GetAdapterRtId(this HttpContext httpContext)
    {
        var v = (string?) httpContext.Request.Headers[AdapterRtId];
        if (!string.IsNullOrWhiteSpace(v))
        {
            return new OctoObjectId(v);
        }

        return null;
    }
}