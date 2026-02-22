using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NJsonSchema;
using YamlDotNet.Serialization;

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
        var jsonString = ConvertToJson(pipelineDefinition);

        var schema = JsonSchema.FromJsonAsync(pipelineSchemaJson).GetAwaiter().GetResult();
        var validationErrors = schema.Validate(jsonString);

        return validationErrors.Select(e => e.ToString()).ToList();
    }

    private static string ConvertToJson(string input)
    {
        // Try JSON first
        try
        {
            JToken.Parse(input);
            return input;
        }
        catch (JsonReaderException)
        {
            // Not valid JSON, try YAML
        }

        var deserializer = new DeserializerBuilder().Build();
        var yamlObject = deserializer.Deserialize<object>(input);
        return JsonConvert.SerializeObject(yamlObject);
    }
}
