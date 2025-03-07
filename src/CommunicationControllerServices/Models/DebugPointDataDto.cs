using System.Text.Json;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
/// Represents a debug point with the before and after data and the node configuration
/// </summary>
/// <param name="nodePath"></param>
/// <param name="sequenceNumber"></param>
public class DebugPointDataDto(NodePath nodePath, uint sequenceNumber)
{
    /// <summary>
    /// Gets the node path
    /// </summary>
    public NodePath NodePath { get; } = nodePath;

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