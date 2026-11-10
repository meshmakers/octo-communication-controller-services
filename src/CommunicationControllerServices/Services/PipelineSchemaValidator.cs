using Meshmakers.Octo.Communication.Contracts.Serialization;
using NJsonSchema;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Validates pipeline definitions against a JSON Schema
/// </summary>
internal interface IPipelineSchemaValidator
{
    /// <summary>
    /// Validates a pipeline definition (YAML or JSON) against the given schema
    /// </summary>
    /// <param name="pipelineDefinition">The pipeline definition string (YAML or JSON)</param>
    /// <param name="pipelineSchemaJson">The JSON Schema to validate against</param>
    /// <returns>List of validation error messages; empty if valid</returns>
    IReadOnlyList<string> Validate(string pipelineDefinition, string pipelineSchemaJson);
}

internal class PipelineSchemaValidator : IPipelineSchemaValidator
{
    /// <inheritdoc />
    public IReadOnlyList<string> Validate(string pipelineDefinition, string pipelineSchemaJson)
    {
        string jsonString;
        try
        {
            // Shared with the MCP server's validate_pipeline_definition tool, so a definition gets
            // the same verdict here and there. The previous local conversion deserialized YAML
            // untyped, which turned every scalar into a string and failed the schema's number /
            // integer / boolean types on any definition carrying one (AB#5240).
            jsonString = YamlToJsonConverter.ToJsonNodeAutoDetect(pipelineDefinition)?.ToJsonString() ?? "null";
        }
        catch (Exception e)
        {
            // A definition that does not parse cannot be schema-validated; report it as a finding
            // rather than letting the exception escape into the deploy path. AB#8596 (CodeRabbit on #105):
            // never the parser's text - a duplicate-key or scalar error can quote a value of the definition,
            // which may be a credential, and the finding reaches logs, REST answers and CK method errors. The
            // position is enough to find the error.
            return [ParseFailureMessage(e)];
        }

        var schema = JsonSchema.FromJsonAsync(pipelineSchemaJson).GetAwaiter().GetResult();
        var validationErrors = schema.Validate(jsonString);

        return validationErrors.Select(e => e.ToString()).ToList();
    }

    /// <summary>The finding of a definition that does not parse: a fixed text plus the position, never the parser's text.</summary>
    internal static string ParseFailureMessage(Exception exception)
    {
        const string text = "The pipeline definition could not be parsed as YAML or JSON";
        return exception switch
        {
            YamlDotNet.Core.YamlException yaml => $"{text} (line {yaml.Start.Line}, column {yaml.Start.Column}).",
            System.Text.Json.JsonException { LineNumber: { } line } json =>
                $"{text} (line {line + 1}, position {json.BytePositionInLine + 1}).",
            _ => $"{text}."
        };
    }
}
