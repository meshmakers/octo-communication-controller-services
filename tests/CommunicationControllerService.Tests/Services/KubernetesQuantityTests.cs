using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>AB#7427: the Kubernetes quantity grammar the typed workload sizing is validated with.</summary>
internal class KubernetesQuantityTests
{
    [Test]
    [Arguments("100m", "0.1")]
    [Arguments("500m", "0.5")]
    [Arguments("0.5", "0.5")]
    [Arguments("2", "2")]
    [Arguments("1.5", "1.5")]
    [Arguments("1", "1")]
    [Arguments("1Ki", "1024")]
    [Arguments("512Mi", "536870912")]
    [Arguments("1Gi", "1073741824")]
    [Arguments("1024Mi", "1073741824")]
    [Arguments("1k", "1000")]
    [Arguments("2M", "2000000")]
    [Arguments("1G", "1000000000")]
    [Arguments("1e3", "1000")]
    [Arguments("1E-1", "0.1")]
    [Arguments("128974848", "128974848")]
    [Arguments(" 1Gi ", "1073741824")]
    public async Task TryParse_ValidQuantity_ReturnsParsedValue(string text, string expected)
    {
        var ok = KubernetesQuantity.TryParse(text, out var value);

        using var _ = Assert.Multiple();
        await Assert.That(ok).IsTrue();
        await Assert.That(value).IsEqualTo(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Test]
    [Arguments("512 MB")]
    [Arguments("512MB")]
    [Arguments("1gi")]
    [Arguments("Gi")]
    [Arguments("-1")]
    [Arguments("1..5")]
    [Arguments("1e")]
    [Arguments("1e400")]
    [Arguments("abc")]
    [Arguments("1 Gi")]
    [Arguments("")]
    [Arguments("   ")]
    public async Task TryParse_InvalidQuantity_ReturnsFalse(string text)
    {
        await Assert.That(KubernetesQuantity.TryParse(text, out _)).IsFalse();
    }

    [Test]
    public async Task TryParse_Null_ReturnsFalse()
    {
        await Assert.That(KubernetesQuantity.TryParse(null, out _)).IsFalse();
    }

    [Test]
    public async Task TryParse_BinaryAndDecimalSuffixesCompareOnParsedValues()
    {
        KubernetesQuantity.TryParse("1Gi", out var binary);
        KubernetesQuantity.TryParse("1G", out var decimalSi);

        await Assert.That(binary).IsGreaterThan(decimalSi);
    }
}
