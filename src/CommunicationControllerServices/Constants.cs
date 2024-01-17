
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices;

internal static class Constants
{
    public const string TenantId = "tenantId";
    public const string PoolName = "pool-name";
    public const string PlugRtId = "plug-rtId";
    public const string SocketRtId = "socket-rtId";

    public const string CommunicationControllerServiceSchemaVersionKey = "CommunicationControllerServices";
    public const int CommunicationControllerServiceSchemaVersionValue = 1;

    public const string CkIdPlug = "Meshmakers.Plug";
    public const string CkIdSocket = "Meshmakers.Socket";
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
    
    public static OctoObjectId? GetSocketRtId(this HttpContext httpContext)
    {
        var v = (string?) httpContext.Request.Headers[SocketRtId];
        if (!string.IsNullOrWhiteSpace(v))
        {
            return new OctoObjectId(v);
        }

        return null;
    }
}