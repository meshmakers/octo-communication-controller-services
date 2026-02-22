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
}
