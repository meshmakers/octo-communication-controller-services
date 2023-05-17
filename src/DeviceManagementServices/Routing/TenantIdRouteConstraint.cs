namespace Meshmakers.Octo.Backend.DeviceManagementServices.Routing;

/// <summary>
///     Checks if the tenant id is a valid string.
/// </summary>
internal class TenantIdRouteConstraint : IRouteConstraint
{
    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values,
        RouteDirection routeDirection)
    {
        // check nulls
        var isMatch = values.TryGetValue(routeKey, out var value) && value != null;
        if (isMatch)
        {
            httpContext?.Items.Add("d", value);
        }

        return isMatch;
    }
}
