namespace Meshmakers.Octo.Backend.PlugControllerServices;

internal static class Statics
{
    public const string TenantId = "tenantId";

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
}