using System.Globalization;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using YamlDotNet.RepresentationModel;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     AB#7428 (Feature AB#7417): projects the typed sizing attributes of a workload into the Helm values
///     with the precedence <b>chart default &lt; typed attributes &lt; explicit <c>ValuesYaml</c> / <c>Values[]</c></b>.
///     The merge is built in the controller on purpose: no <c>WorkloadDeployedDto</c> contract change, so every
///     operator version (edge clusters run older ones) renders the same release.
/// </summary>
/// <remarks>
///     A typed value becomes one more <c>Values[]</c> entry, which the operator layers above
///     <c>ValuesYaml</c>. It is therefore only added when neither <c>Values[]</c> nor <c>ValuesYaml</c> already
///     sets that path — an explicit value wins, and the caller is told so with a warning
///     (<c>LastDeploymentWarning</c>). Same "pinned wins" rule as <c>AppendAdapterPoolMemberOverrides</c>; a
///     <c>Values[]</c> path counts as set when present (path match is case-insensitive), a
///     <c>ValuesYaml</c> key when it carries a non-blank scalar.
/// </remarks>
internal static class WorkloadResourceOverrides
{
    private sealed record Setting(string Attribute, string Value, string[] YamlPath)
    {
        public string HelmPath => string.Join('.', YamlPath);
    }

    /// <summary>The overrides to send plus the warning to show (null when nothing is overridden).</summary>
    public readonly record struct Result(ValueOverrideDto[] Overrides, string? Warning);

    public static Result Apply(RtDeployableWorkload workload, ValueOverrideDto[] overrides, string? valuesYaml)
    {
        // A pool is sized per member by PoolMember* (AppendAdapterPoolMemberOverrides); the typed
        // attributes are rejected on a pool before we get here.
        if (workload is RtAdapterPool)
        {
            return new Result(overrides, null);
        }

        var settings = new[]
        {
            Create(nameof(RtDeployableWorkload.CpuRequest), workload.CpuRequest, "requests", "cpu"),
            Create(nameof(RtDeployableWorkload.CpuLimit), workload.CpuLimit, "limits", "cpu"),
            Create(nameof(RtDeployableWorkload.MemoryRequest), workload.MemoryRequest, "requests", "memory"),
            Create(nameof(RtDeployableWorkload.MemoryLimit), workload.MemoryLimit, "limits", "memory"),
        }.OfType<Setting>().ToList();

        if (settings.Count == 0)
        {
            return new Result(overrides, null);
        }

        var yaml = ParseYaml(valuesYaml);
        var result = new List<ValueOverrideDto>(overrides);
        var warnings = new List<string>();

        foreach (var setting in settings)
        {
            var explicitValues = overrides.FirstOrDefault(o =>
                string.Equals(o.Path, setting.HelmPath, StringComparison.OrdinalIgnoreCase));
            if (explicitValues != null)
            {
                AddWarningUnlessEqual(warnings, setting, explicitValues.Value, "Values[]");
                continue;
            }

            var yamlValue = ReadYamlScalar(yaml, setting.YamlPath);
            if (yamlValue != null)
            {
                AddWarningUnlessEqual(warnings, setting, yamlValue, "ValuesYaml");
                continue;
            }

            result.Add(new ValueOverrideDto { Path = setting.HelmPath, Value = setting.Value, IsSecret = false });
        }

        return new Result(result.ToArray(), warnings.Count == 0 ? null : string.Join("; ", warnings));
    }

    private static Setting? Create(string attribute, string? value, string section, string resource)
    {
        // Blank means "the chart default"; rendering an empty string would make the pod spec invalid.
        return string.IsNullOrWhiteSpace(value) ? null : new Setting(attribute, value.Trim(), ["resources", section, resource]);
    }

    private static void AddWarningUnlessEqual(List<string> warnings, Setting setting, string explicitValue,
        string source)
    {
        // The same quantity written differently (1024Mi vs 1Gi) overrides nothing worth a warning.
        if (KubernetesQuantity.TryParse(setting.Value, out var typed) &&
            KubernetesQuantity.TryParse(explicitValue, out var explicitQuantity) && typed == explicitQuantity)
        {
            return;
        }

        warnings.Add(string.Create(CultureInfo.InvariantCulture,
            $"{setting.Attribute} {setting.Value} is overridden by an explicit Helm value ({source} {setting.HelmPath})"));
    }

    private static YamlMappingNode? ParseYaml(string? valuesYaml)
    {
        if (string.IsNullOrWhiteSpace(valuesYaml))
        {
            return null;
        }

        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(valuesYaml);
            stream.Load(reader);
            return stream.Documents.Count == 0 ? null : stream.Documents[0].RootNode as YamlMappingNode;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // Invalid YAML fails in the operator's helm run with its own message; for the precedence it
            // simply sets nothing.
            return null;
        }
    }

    private static string? ReadYamlScalar(YamlMappingNode? root, string[] path)
    {
        YamlNode? node = root;
        foreach (var key in path)
        {
            if (node is not YamlMappingNode map ||
                !map.Children.TryGetValue(new YamlScalarNode(key), out var child))
            {
                return null;
            }

            node = child;
        }

        return node is YamlScalarNode { Value: { } value } && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
    }
}
