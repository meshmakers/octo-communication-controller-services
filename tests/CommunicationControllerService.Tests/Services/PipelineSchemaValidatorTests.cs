using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

internal class PipelineSchemaValidatorTests
{
    private readonly PipelineSchemaValidator _validator = new();

    private const string SimpleSchema = """
    {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "type": "object",
        "properties": {
            "triggers": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "type": { "type": "string" }
                    },
                    "required": ["type"]
                }
            },
            "transformations": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "type": { "type": "string" }
                    },
                    "required": ["type"]
                }
            }
        }
    }
    """;

    [Test]
    public async Task Validate_ValidJson_ReturnsNoErrors()
    {
        var json = """{"triggers":[{"type":"Polling@1"}],"transformations":[{"type":"Select@1"}]}""";

        var errors = _validator.Validate(json, SimpleSchema);

        await Assert.That(errors).Count().IsEqualTo(0);
    }

    [Test]
    public async Task Validate_ValidYaml_ReturnsNoErrors()
    {
        var yaml = """
            triggers:
              - type: Polling@1
            transformations:
              - type: Select@1
            """;

        var errors = _validator.Validate(yaml, SimpleSchema);

        await Assert.That(errors).Count().IsEqualTo(0);
    }

    [Test]
    public async Task Validate_InvalidJson_MissingRequired_ReturnsErrors()
    {
        // "type" is required but missing from the trigger
        var json = """{"triggers":[{"notType":"value"}]}""";

        var errors = _validator.Validate(json, SimpleSchema);

        await Assert.That(errors).Count().IsGreaterThan(0);
    }

    [Test]
    public async Task Validate_InvalidYaml_WrongPropertyType_ReturnsErrors()
    {
        // triggers should be an array, not a string
        var yaml = """
            triggers: "not an array"
            """;

        var errors = _validator.Validate(yaml, SimpleSchema);

        await Assert.That(errors).Count().IsGreaterThan(0);
    }

    // ===== AB#5240: YAML/JSON parity ============================================
    // The validator used to deserialize YAML untyped and re-serialize it, which turned every
    // scalar into a string — a definition carrying a number or a boolean was reported invalid
    // while the equivalent JSON passed. The conversion now comes from the shared
    // Communication.Contracts YamlToJsonConverter, the same one the MCP server's
    // validate_pipeline_definition tool uses, so both surfaces reach the same verdict.

    private const string TypedSchema = """
    {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "type": "object",
        "properties": {
            "enabled": { "type": "boolean" },
            "transformations": {
                "type": "array",
                "items": {
                    "type": "object",
                    "properties": {
                        "type":       { "type": "string" },
                        "adapterId":  { "type": "string" },
                        "maxAttempts":{ "type": "integer" },
                        "timeout":    { "type": "number" },
                        "continueOnError": { "type": "boolean" }
                    },
                    "required": ["type"]
                }
            }
        }
    }
    """;

    private const string TypedYaml = """
        enabled: true
        transformations:
          - type: MakeHttpRequest@1
            maxAttempts: 3
            timeout: 1.5
            continueOnError: false
          - type: ApplyChanges@1
            adapterId: 670000000000000000000002
        """;

    private const string TypedJson = """
        {"enabled":true,
         "transformations":[
           {"type":"MakeHttpRequest@1","maxAttempts":3,"timeout":1.5,"continueOnError":false},
           {"type":"ApplyChanges@1","adapterId":"670000000000000000000002"}]}
        """;

    [Test]
    public async Task Validate_YamlWithNumbersAndBooleans_ReturnsNoErrors()
    {
        var errors = _validator.Validate(TypedYaml, TypedSchema);

        await Assert.That(errors).Count().IsEqualTo(0);
    }

    [Test]
    public async Task Validate_SameDefinitionAsYamlAndJson_ReturnsTheSameVerdict()
    {
        var fromYaml = _validator.Validate(TypedYaml, TypedSchema);
        var fromJson = _validator.Validate(TypedJson, TypedSchema);

        await Assert.That(fromYaml.Count).IsEqualTo(fromJson.Count);
        await Assert.That(fromYaml).Count().IsEqualTo(0);
    }

    [Test]
    public async Task Validate_YamlWithWrongScalarType_StillReportsErrors()
    {
        // The fix must not blunt the validator: a genuine type error still has to surface.
        var yaml = """
            transformations:
              - type: MakeHttpRequest@1
                maxAttempts: "three"
            """;

        var errors = _validator.Validate(yaml, TypedSchema);

        await Assert.That(errors).Count().IsGreaterThan(0);
    }

    [Test]
    public async Task Validate_UnparseableDefinition_IsReportedAsAnErrorNotAnException()
    {
        var broken = "transformations: [\n  - type: X@1\n bad indentation here\n";

        var errors = _validator.Validate(broken, TypedSchema);

        await Assert.That(errors).Count().IsGreaterThan(0);
    }
}
