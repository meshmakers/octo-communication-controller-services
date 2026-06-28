using Meshmakers.Octo.Runtime.Contracts.Formulas;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc />
internal class ExpressionValidationService(IFormulaEngine formulaEngine) : IExpressionValidationService
{
    /// <inheritdoc />
    public ExpressionValidationResult Validate(string expression, double testValue = 42.0)
    {
        var result = formulaEngine.Validate(expression,
            new Dictionary<string, double> { ["value"] = testValue });

        return new ExpressionValidationResult(result.IsValid, result.Error, result.Result,
            result.NormalizedExpression);
    }
}
