namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Result of validating a mapping expression.
/// </summary>
/// <param name="Valid">Whether the expression is syntactically valid and evaluates successfully.</param>
/// <param name="Error">Error message if the expression is invalid.</param>
/// <param name="Result">Evaluated result for the test value.</param>
/// <param name="NormalizedExpression">The expression after ternary-to-if conversion (for debugging).</param>
public record ExpressionValidationResult(
    bool Valid,
    string? Error = null,
    double? Result = null,
    string? NormalizedExpression = null);

/// <summary>
/// Validates mapping expressions using the mXparser engine (OctoExpression).
/// Uses the same expression evaluation path as ApplyDataPointMappingsNode in the Mesh Adapter.
/// </summary>
public interface IExpressionValidationService
{
    /// <summary>
    /// Validates the given mXparser expression with a test value.
    /// Supports C-style ternary operators (cond ? a : b) which are converted to mXparser if() syntax.
    /// </summary>
    /// <param name="expression">The expression to validate (e.g., "value &gt; 0 ? value : 0").</param>
    /// <param name="testValue">The test value to use for the 'value' variable (default: 42.0).</param>
    /// <returns>Validation result with success/error state and evaluated result.</returns>
    ExpressionValidationResult Validate(string expression, double testValue = 42.0);
}
