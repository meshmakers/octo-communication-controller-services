using System.Text.Json;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
/// Represents a debug point with the before and after data and the node configuration
/// </summary>
/// <param name="nodeId">Node identifier</param>
/// <param name="nodePath">Node path</param>
/// <param name="description">Description of the node</param>
/// <param name="sequenceNumber">Sequence number of the node within a transformation list</param>
public class DebugPointDataDto(string nodeId, NodePath nodePath, string? description, uint sequenceNumber)
{
    /// <summary>
    /// Gets the node path
    /// </summary>
    public NodePath NodeId { get; } = nodeId;

    /// <summary>
    /// Gets the node path
    /// </summary>
    public NodePath NodePath { get; } = nodePath;

    /// <summary>
    /// Gets the description of the node
    /// </summary>
    public string? Description { get; } = description;

    /// <summary>
    /// Gets the sequence number of the node within a transformation list
    /// </summary>
    public uint SequenceNumber { get; } = sequenceNumber;

    /// <summary>
    /// Gets or sets the debug messages
    /// </summary>
    public IEnumerable<DebugMessage>? Messages { get; init; }

    /// <summary>
    /// Gets the input data
    /// </summary>
    public JsonElement? Input { get; init; }

    /// <summary>
    /// Gets the output data
    /// </summary>
    public JsonElement? Output { get; init; }

}