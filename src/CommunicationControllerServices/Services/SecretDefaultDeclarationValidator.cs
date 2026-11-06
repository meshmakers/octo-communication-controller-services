using System.Text.RegularExpressions;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Validates the SecretDefaults an Application declares. The declaration is written generically through the
///     asset repository, so the deploy and the projection are the enforcement net.
/// </summary>
internal static partial class SecretDefaultDeclarationValidator
{
    /// <summary>Longest key accepted; it becomes a well-known name in every served tenant.</summary>
    internal const int MaximumKeyLength = 128;

    /// <summary>
    ///     Throws a <see cref="DeploymentSiteServiceException" /> naming the Application and the key for the first
    ///     declaration that is empty, invalid, duplicated or has no value. A workload without SecretDefaults passes.
    /// </summary>
    public static void Validate(string tenantId, RtDeployableWorkload workload)
    {
        if (workload is not RtApplication { SecretDefaults: { Count: > 0 } declarations })
        {
            return;
        }

        var firstRejected = Check(declarations)
            .FirstOrDefault(checkedDeclaration => checkedDeclaration.Issue != SecretDefaultDeclarationIssue.None);
        if (firstRejected is null)
        {
            return;
        }

        throw firstRejected.Issue switch
        {
            SecretDefaultDeclarationIssue.KeyEmpty => DeploymentSiteServiceException.WorkloadSecretDefaultKeyEmpty(
                tenantId, workload.RtId, workload.Name),
            SecretDefaultDeclarationIssue.KeyInvalid => DeploymentSiteServiceException.WorkloadSecretDefaultKeyInvalid(
                tenantId, workload.RtId, workload.Name, firstRejected.Key, MaximumKeyLength),
            SecretDefaultDeclarationIssue.KeyDuplicate => DeploymentSiteServiceException
                .WorkloadSecretDefaultKeyDuplicate(tenantId, workload.RtId, workload.Name, firstRejected.Key),
            _ => DeploymentSiteServiceException.WorkloadSecretDefaultValueMissing(tenantId, workload.RtId,
                workload.Name, firstRejected.Key)
        };
    }

    /// <summary>
    ///     Checks every declaration on its own. Every entry of a key declared more than once is a duplicate, because
    ///     none of them can be told to be the intended one.
    /// </summary>
    public static IReadOnlyList<CheckedSecretDefaultDeclaration> Check(
        IEnumerable<RtSecretDefaultProjectionRecord?> declarations)
    {
        var declarationList = declarations.ToList();
        var keyCounts = declarationList
            .Select(ReadTrimmedKey)
            .Where(key => !string.IsNullOrEmpty(key))
            .GroupBy(key => key!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        return declarationList
            .Select(declaration => CheckOne(declaration, keyCounts))
            .ToList();
    }

    private static CheckedSecretDefaultDeclaration CheckOne(RtSecretDefaultProjectionRecord? declaration,
        IReadOnlyDictionary<string, int> keyCounts)
    {
        var key = ReadTrimmedKey(declaration) ?? string.Empty;
        var issue = key switch
        {
            "" => SecretDefaultDeclarationIssue.KeyEmpty,
            _ when key.Length > MaximumKeyLength || !KeyPattern().IsMatch(key) => SecretDefaultDeclarationIssue.KeyInvalid,
            _ when keyCounts.GetValueOrDefault(key) > 1 => SecretDefaultDeclarationIssue.KeyDuplicate,
            _ when declaration?.SecretValue is null => SecretDefaultDeclarationIssue.ValueMissing,
            _ => SecretDefaultDeclarationIssue.None
        };
        return new CheckedSecretDefaultDeclaration(declaration, key, issue);
    }

    private static string? ReadTrimmedKey(RtSecretDefaultProjectionRecord? declaration)
    {
        var storedKey = declaration?.GetAttributeValueOrDefault(nameof(RtSecretDefaultProjectionRecord.Key));
        return (storedKey as string)?.Trim();
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}

/// <summary>Why a SecretDefaults entry cannot be projected.</summary>
internal enum SecretDefaultDeclarationIssue
{
    /// <summary>The entry is valid.</summary>
    None,

    /// <summary>The key is missing or blank.</summary>
    KeyEmpty,

    /// <summary>The key has characters outside the allowed set or is too long.</summary>
    KeyInvalid,

    /// <summary>Another entry declares the same key.</summary>
    KeyDuplicate,

    /// <summary>The entry has no SecretValue.</summary>
    ValueMissing
}

/// <summary>One SecretDefaults entry with its trimmed key and the result of its check.</summary>
internal sealed class CheckedSecretDefaultDeclaration(
    RtSecretDefaultProjectionRecord? declaration,
    string key,
    SecretDefaultDeclarationIssue issue)
{
    /// <summary>The entry as declared.</summary>
    public RtSecretDefaultProjectionRecord? Declaration { get; } = declaration;

    /// <summary>The trimmed key; empty when the entry has none.</summary>
    public string Key { get; } = key;

    /// <summary>Why the entry cannot be projected, or <see cref="SecretDefaultDeclarationIssue.None" />.</summary>
    public SecretDefaultDeclarationIssue Issue { get; } = issue;
}
