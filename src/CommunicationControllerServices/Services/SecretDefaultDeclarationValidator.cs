using System.Text.RegularExpressions;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Validates the SecretDefaults an Application declares before it is deployed. The declaration is written
///     generically through the asset repository, so the deploy is the enforcement net.
/// </summary>
internal static partial class SecretDefaultDeclarationValidator
{
    /// <summary>Longest key accepted; it becomes a well-known name in every served tenant.</summary>
    internal const int MaximumKeyLength = 128;

    /// <summary>
    ///     Throws a <see cref="DeploymentSiteServiceException" /> naming the Application and the key when a
    ///     declaration is empty, invalid, duplicated or has no value. A workload without SecretDefaults passes.
    /// </summary>
    public static void Validate(string tenantId, RtDeployableWorkload workload)
    {
        if (workload is not RtApplication { SecretDefaults: { Count: > 0 } declarations })
        {
            return;
        }

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var declaration in declarations)
        {
            var key = declaration.Key?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                throw DeploymentSiteServiceException.WorkloadSecretDefaultKeyEmpty(tenantId, workload.RtId,
                    workload.Name);
            }

            if (key.Length > MaximumKeyLength || !KeyPattern().IsMatch(key))
            {
                throw DeploymentSiteServiceException.WorkloadSecretDefaultKeyInvalid(tenantId, workload.RtId,
                    workload.Name, key, MaximumKeyLength);
            }

            if (!seenKeys.Add(key))
            {
                throw DeploymentSiteServiceException.WorkloadSecretDefaultKeyDuplicate(tenantId, workload.RtId,
                    workload.Name, key);
            }

            if (declaration.SecretValue is null)
            {
                throw DeploymentSiteServiceException.WorkloadSecretDefaultValueMissing(tenantId, workload.RtId,
                    workload.Name, key);
            }
        }
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
