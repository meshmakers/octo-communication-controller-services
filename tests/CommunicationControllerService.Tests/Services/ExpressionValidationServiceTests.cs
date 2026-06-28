using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

public class ExpressionValidationServiceTests
{
    private static readonly IFormulaEngine FormulaEngine =
        new ServiceCollection().AddFormulaEngine().BuildServiceProvider().GetRequiredService<IFormulaEngine>();

    private readonly ExpressionValidationService _service = new(FormulaEngine);

    [Test]
    public async Task Validate_SimpleArithmetic_ReturnsValid()
    {
        var result = _service.Validate("value / 100", 42.0);

        await Assert.That(result.Valid).IsTrue();
        await Assert.That(result.Result).IsEqualTo(0.42);
        await Assert.That(result.Error).IsNull();
    }

    [Test]
    public async Task Validate_TernaryExpression_ConvertsToIfAndEvaluates()
    {
        var result = _service.Validate("value > 0 ? value : 0", 42.0);

        await Assert.That(result.Valid).IsTrue();
        await Assert.That(result.Result).IsEqualTo(42.0);
        await Assert.That(result.NormalizedExpression).IsEqualTo("if(value > 0, value, 0)");
    }

    [Test]
    public async Task Validate_TernaryExpressionFalseBranch_ReturnsZero()
    {
        var result = _service.Validate("value > 0 ? value : 0", -5.0);

        await Assert.That(result.Valid).IsTrue();
        await Assert.That(result.Result).IsEqualTo(0.0);
    }

    [Test]
    public async Task Validate_AbsFunction_ReturnsValid()
    {
        var result = _service.Validate("abs(value)", -42.0);

        await Assert.That(result.Valid).IsTrue();
        await Assert.That(result.Result).IsEqualTo(42.0);
    }

    [Test]
    public async Task Validate_MinMaxClamp_ReturnsValid()
    {
        var result = _service.Validate("min(max(value, 0), 100)", 150.0);

        await Assert.That(result.Valid).IsTrue();
        await Assert.That(result.Result).IsEqualTo(100.0);
    }

    [Test]
    public async Task Validate_MultiplicationExpression_ReturnsValid()
    {
        var result = _service.Validate("value * 100", 0.42);

        await Assert.That(result.Valid).IsTrue();
        await Assert.That(result.Result).IsEqualTo(42.0);
    }

    [Test]
    public async Task Validate_SubtractionCalibration_ReturnsValid()
    {
        var result = _service.Validate("value - 2.5", 25.0);

        await Assert.That(result.Valid).IsTrue();
        await Assert.That(result.Result).IsEqualTo(22.5);
    }

    [Test]
    public async Task Validate_InversionExpression_ReturnsValid()
    {
        var result = _service.Validate("100 - value", 75.0);

        await Assert.That(result.Valid).IsTrue();
        await Assert.That(result.Result).IsEqualTo(25.0);
    }

    [Test]
    public async Task Validate_InvalidSyntax_ReturnsError()
    {
        var result = _service.Validate("value ///");

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Error).IsNotNull();
    }

    [Test]
    public async Task Validate_EmptyExpression_ReturnsError()
    {
        var result = _service.Validate("");

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Error).IsEqualTo("Expression must not be empty.");
    }

    [Test]
    public async Task Validate_WhitespaceOnlyExpression_ReturnsError()
    {
        var result = _service.Validate("   ");

        await Assert.That(result.Valid).IsFalse();
        await Assert.That(result.Error).IsEqualTo("Expression must not be empty.");
    }

    [Test]
    public async Task Validate_UnknownVariable_ReturnsError()
    {
        var result = _service.Validate("unknownVar + 1");

        await Assert.That(result.Valid).IsFalse();
    }

    [Test]
    public async Task Validate_DefaultTestValue_Uses42()
    {
        var result = _service.Validate("value");

        await Assert.That(result.Valid).IsTrue();
        await Assert.That(result.Result).IsEqualTo(42.0);
    }

    [Test]
    public async Task Validate_CustomTestValue_UsesProvidedValue()
    {
        var result = _service.Validate("value", 99.0);

        await Assert.That(result.Valid).IsTrue();
        await Assert.That(result.Result).IsEqualTo(99.0);
    }

    [Test]
    public async Task Validate_NestedTernaryWithParens_ConvertsCorrectly()
    {
        // Nested ternaries should use parentheses for unambiguous parsing
        var result = _service.Validate("value > 100 ? 100 : (value < 0 ? 0 : value)", 50.0);

        await Assert.That(result.Valid).IsTrue();
        await Assert.That(result.Result).IsEqualTo(50.0);
    }

    [Test]
    public async Task Validate_BatteryChargingPattern_ReturnsValid()
    {
        // Positive value → charging
        var result = _service.Validate("value > 0 ? value : 0", 3500.0);
        await Assert.That(result.Valid).IsTrue();
        await Assert.That(result.Result).IsEqualTo(3500.0);
    }

    [Test]
    public async Task Validate_BatteryDischargingPattern_ReturnsValid()
    {
        // Negative value → discharging (absolute)
        var result = _service.Validate("value < 0 ? abs(value) : 0", -3500.0);
        await Assert.That(result.Valid).IsTrue();
        await Assert.That(result.Result).IsEqualTo(3500.0);
    }

    [Test]
    public async Task Validate_NoTernary_NormalizedExpressionUnchanged()
    {
        var result = _service.Validate("value + 1");
        await Assert.That(result.NormalizedExpression).IsEqualTo("value + 1");
    }

    [Test]
    public async Task Validate_SimpleTernary_NormalizedToIf()
    {
        var result = _service.Validate("value > 0 ? value : 0");
        await Assert.That(result.NormalizedExpression).IsEqualTo("if(value > 0, value, 0)");
    }
}
