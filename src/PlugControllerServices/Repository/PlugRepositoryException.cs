using Meshmakers.Octo.Common.Shared;

namespace Meshmakers.Octo.Backend.PlugControllerServices.Repository;

public class PlugRepositoryException : Exception
{
    public PlugRepositoryException()
    {
    }

    public PlugRepositoryException(string message) : base(message)
    {
    }

    public PlugRepositoryException(string message, Exception inner) : base(message, inner)
    {
    }

    internal static Exception PlugNotFound(string tenantId, OctoObjectId plugRtId)
    {
        return new PlugRepositoryException($"[{tenantId}] Plug '{plugRtId}' does not exist");
    }

    internal static Exception PlugNotAssociatedToPlugPool(string tenantId, OctoObjectId plugRtId)
    {
        return new PlugRepositoryException($"[{tenantId}] Plug '{plugRtId}' is not associated with a plug pool");
    }

    public static Exception CommonGettingPlugPoolOfPlug(string tenantId, OctoObjectId plugRtId, Exception exception)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get associated plug pool for plug '{plugRtId}'", exception);
    }

    public static Exception PlugPoolNotFound(string tenantId, OctoObjectId plugPoolId)
    {
        return new PlugRepositoryException($"[{tenantId}] Failed to get associated plug pool for plug '{plugPoolId}'");
    }
}