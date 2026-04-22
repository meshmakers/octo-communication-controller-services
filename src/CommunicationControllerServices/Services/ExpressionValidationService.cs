using Meshmakers.Octo.Runtime.Engine.MongoDb.Formulas;
using org.mariuszgromada.math.mxparser;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <inheritdoc />
internal class ExpressionValidationService : IExpressionValidationService
{
    /// <inheritdoc />
    public ExpressionValidationResult Validate(string expression, double testValue = 42.0)
    {
        if (string.IsNullOrWhiteSpace(expression))
        {
            return new ExpressionValidationResult(false, "Expression must not be empty.");
        }

        try
        {
            var normalized = ConvertTernaryToIf(expression);

            var expr = new OctoExpression(normalized);
            expr.addArguments(new Argument("value", testValue));

            if (!expr.checkSyntax())
            {
                return new ExpressionValidationResult(false, expr.getErrorMessage(),
                    NormalizedExpression: normalized);
            }

            var result = expr.calculate();
            if (double.IsNaN(result))
            {
                return new ExpressionValidationResult(false, "Expression evaluates to NaN.",
                    NormalizedExpression: normalized);
            }

            return new ExpressionValidationResult(true, Result: result,
                NormalizedExpression: normalized);
        }
        catch (Exception ex)
        {
            return new ExpressionValidationResult(false, ex.Message);
        }
    }

    /// <summary>
    /// Converts C-style ternary operators (cond ? a : b) to mXparser's if(cond, a, b) syntax.
    /// This is the same logic used by ApplyDataPointMappingsNode in the Mesh Adapter SDK.
    /// </summary>
    internal static string ConvertTernaryToIf(string expression)
    {
        if (!expression.Contains('?')) return expression;

        while (true)
        {
            var qIdx = expression.IndexOf('?');
            if (qIdx < 0) break;

            var depth = 0;
            var colonIdx = -1;
            var nestedQCount = 0;
            for (var i = qIdx + 1; i < expression.Length; i++)
            {
                var ch = expression[i];
                if (ch == '(') depth++;
                else if (ch == ')') depth--;
                else if (ch == '?' && depth == 0) nestedQCount++;
                else if (ch == ':' && depth == 0)
                {
                    if (nestedQCount == 0) { colonIdx = i; break; }
                    nestedQCount--;
                }
            }

            if (colonIdx < 0) break;

            var condStart = FindConditionStart(expression, qIdx);
            var falseEnd = FindFalseEnd(expression, colonIdx);

            var condition = expression[condStart..qIdx].Trim();
            var trueBranch = expression[(qIdx + 1)..colonIdx].Trim();
            var falseBranch = expression[(colonIdx + 1)..falseEnd].Trim();

            var replacement = $"if({condition}, {trueBranch}, {falseBranch})";
            expression = expression[..condStart] + replacement + expression[falseEnd..];
        }

        return expression;
    }

    private static int FindConditionStart(string s, int qIdx)
    {
        var depth = 0;
        for (var i = qIdx - 1; i >= 0; i--)
        {
            var ch = s[i];
            if (ch == ')') depth++;
            else if (ch == '(')
            {
                if (depth == 0) return i + 1;
                depth--;
            }
        }
        return 0;
    }

    private static int FindFalseEnd(string s, int colonIdx)
    {
        var depth = 0;
        for (var i = colonIdx + 1; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '(') depth++;
            else if (ch == ')')
            {
                if (depth == 0) return i;
                depth--;
            }
        }
        return s.Length;
    }
}
