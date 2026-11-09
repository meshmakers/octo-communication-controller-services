namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Marks a deploy the controller refused before anything reached Helm. The refusal is stored as
///     <c>DeploymentState</c> Error with <c>LastDeploymentError</c> starting with <see cref="MessagePrefix" />; an
///     Error reported by the operator after a rollout never carries it. That is how a refused Application is told
///     apart from one whose rollout failed.
/// </summary>
internal static class DeploymentRejection
{
    internal const string MessagePrefix = "Deploy refused: ";

    /// <summary>The <c>LastDeploymentError</c> text recorded for a refusal.</summary>
    public static string Format(string reason)
    {
        return MessagePrefix + reason;
    }

    /// <summary>True when the recorded deployment error is a refusal of the controller.</summary>
    public static bool IsRejection(string? lastDeploymentError)
    {
        return lastDeploymentError is not null &&
               lastDeploymentError.StartsWith(MessagePrefix, StringComparison.Ordinal);
    }
}
