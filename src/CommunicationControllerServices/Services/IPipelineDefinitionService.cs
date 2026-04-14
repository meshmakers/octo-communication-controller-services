namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// Represents a parsed node from a pipeline definition with its property values.
/// </summary>
public class PipelineNodeProperties
{
    /// <summary>
    /// The node type identifier (e.g., "For@1", "Simulation@1").
    /// </summary>
    public required string NodeType { get; init; }

    /// <summary>
    /// Zero-based occurrence index of this node type in the definition.
    /// </summary>
    public int NodeIndex { get; init; }

    /// <summary>
    /// Property values of the node, keyed by property name (camelCase).
    /// Complex values (nested objects, arrays) are included as-is.
    /// </summary>
    public required IDictionary<string, object?> Properties { get; init; }
}

/// <summary>
/// Service for parsing pipeline definitions and extracting node information.
/// </summary>
public interface IPipelineDefinitionService
{
    /// <summary>
    /// Parses a YAML pipeline definition and returns the properties of a specific node instance.
    /// </summary>
    /// <param name="pipelineDefinition">The YAML pipeline definition string</param>
    /// <param name="nodeType">The node type to find (e.g., "For@1", "Simulation@1")</param>
    /// <param name="nodeIndex">Zero-based occurrence index of the node type</param>
    /// <returns>The parsed node properties, or null if not found</returns>
    PipelineNodeProperties? GetNodeProperties(string pipelineDefinition, string nodeType, int nodeIndex);

    /// <summary>
    /// Parses a YAML pipeline definition and returns all nodes with their types and indices.
    /// </summary>
    /// <param name="pipelineDefinition">The YAML pipeline definition string</param>
    /// <returns>List of all nodes found in the definition</returns>
    IReadOnlyList<PipelineNodeProperties> GetAllNodes(string pipelineDefinition);
}
