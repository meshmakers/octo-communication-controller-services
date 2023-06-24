
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices;

internal static class Statics
{
    public const string TenantId = "tenantId";
    public const string PoolName = "pool-name";
    public const string PlugRtId = "plug-rtId";

    public const string CommunicationControllerConfigurationName = "CommunicationControllerServices";

    public const string CkIdPlug = "Meshmakers.Plug";
    public const string CkIdPlugGroup = "Meshmakers.Plug.Group";
    public const string CkIdPlugMapping = "Meshmakers.Plug.Mapping";
    public const string CkIdCommunicationPool = "Meshmakers.CommunicationPool";

    public const string RoleIdParentChild = "System.ParentChild";

    public static string? GetTenantId(this HttpContext httpContext)
    {
        return (string?)httpContext.GetRouteValue(TenantId);
    }
    
    public static string? GetPoolName(this HttpContext httpContext)
    {
        return httpContext.Request.Headers[PoolName];
    }
    
    public static OctoObjectId? GetPlugRtId(this HttpContext httpContext)
    {
        var v = (string?) httpContext.Request.Headers[PlugRtId];
        if (!string.IsNullOrWhiteSpace(v))
        {
            return new OctoObjectId(v);
        }

        return null;
    }
}