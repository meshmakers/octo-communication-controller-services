
using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices;

internal static class Statics
{
    public const string TenantId = "tenantId";
    public const string PoolName = "plug-pool-name";
    public const string PlugRtId = "plug-rtId";

    public const string PlugControllerConfigurationName = "PlugControllerServices";

    public const string CkIdPlug = "Meshmakers.Plug";
    public const string CkIdPlugGroup = "Meshmakers.Plug.Group";
    public const string CkIdPlugMapping = "Meshmakers.Plug.Mapping";
    public const string CkIdPlugPool = "Meshmakers.PlugPool";

    public const string RoleIdParentChild = "System.ParentChild";

    public static string? GetTenantId(this HttpContext httpContext)
    {
        return (string?)httpContext.GetRouteValue(TenantId);
    }
    
    public static string? GetPlugPoolName(this HttpContext httpContext)
    {
        return httpContext.Request.Headers[PlugRtId];
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