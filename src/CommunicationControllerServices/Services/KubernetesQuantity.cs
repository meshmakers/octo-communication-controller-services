using System.Globalization;
using System.Text.RegularExpressions;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     AB#7427: a small parser for Kubernetes resource quantities (<c>100m</c>, <c>0.5</c>, <c>512Mi</c>,
///     <c>1Gi</c>, <c>2G</c>, <c>1e3</c>). Used to validate the typed workload sizing attributes before a
///     deploy, and to compare a request with its limit on parsed values (1024Mi equals 1Gi).
///     The controller does not reference the Kubernetes client, and one parser is not worth that dependency.
/// </summary>
/// <remarks>
///     Accepts the suffixes of the Kubernetes <c>resource.Quantity</c> grammar: decimal SI (<c>n u m</c>,
///     none, <c>k M G T P E</c>), binary SI (<c>Ki Mi Gi Ti Pi Ei</c>) and a decimal exponent (<c>e3</c>,
///     <c>E-2</c>). Negative values are rejected: a request or a limit cannot be negative.
/// </remarks>
internal static partial class KubernetesQuantity
{
    private const int MaxExponent = 27;

    [GeneratedRegex(
        @"^(?<number>\d+(\.\d*)?|\.\d+)(?:(?<suffix>Ki|Mi|Gi|Ti|Pi|Ei|n|u|m|k|M|G|T|P|E)|[eE](?<exp>[+-]?\d+))?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex QuantityRegex();

    /// <summary>Parses <paramref name="text"/>; returns <c>false</c> when it is not a valid, non-negative quantity.</summary>
    public static bool TryParse(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var match = QuantityRegex().Match(text.Trim());
        if (!match.Success)
        {
            return false;
        }

        try
        {
            var number = decimal.Parse(match.Groups["number"].Value, NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture);

            if (match.Groups["exp"].Success)
            {
                if (!int.TryParse(match.Groups["exp"].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                        out var exponent) || Math.Abs(exponent) > MaxExponent)
                {
                    return false;
                }

                number *= PowerOfTen(exponent);
            }
            else if (match.Groups["suffix"].Success)
            {
                number *= Multiplier(match.Groups["suffix"].Value);
            }

            value = number;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static decimal PowerOfTen(int exponent)
    {
        var result = 1m;
        for (var i = 0; i < Math.Abs(exponent); i++)
        {
            result = exponent > 0 ? result * 10m : result / 10m;
        }

        return result;
    }

    private static decimal Multiplier(string suffix)
    {
        return suffix switch
        {
            "n" => 0.000000001m,
            "u" => 0.000001m,
            "m" => 0.001m,
            "k" => 1_000m,
            "M" => 1_000_000m,
            "G" => 1_000_000_000m,
            "T" => 1_000_000_000_000m,
            "P" => 1_000_000_000_000_000m,
            "E" => 1_000_000_000_000_000_000m,
            "Ki" => 1024m,
            "Mi" => 1024m * 1024m,
            "Gi" => 1024m * 1024m * 1024m,
            "Ti" => 1024m * 1024m * 1024m * 1024m,
            "Pi" => 1024m * 1024m * 1024m * 1024m * 1024m,
            "Ei" => 1024m * 1024m * 1024m * 1024m * 1024m * 1024m,
            _ => throw new ArgumentOutOfRangeException(nameof(suffix), suffix, "Unknown quantity suffix"),
        };
    }
}
