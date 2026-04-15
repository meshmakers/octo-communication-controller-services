using System.ComponentModel.DataAnnotations;
using Asp.Versioning;
using IdentityModel;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1.Controllers;

/// <summary>
/// Request body for parsing a node from a pipeline definition.
/// </summary>
/// <summary>
/// Request body for updating node properties in a pipeline definition.
/// </summary>
public class UpdateNodeRequest
{
    /// <summary>
    /// The current YAML pipeline definition string.
    /// </summary>
    [Required]
    public required string Definition { get; init; }

    /// <summary>
    /// The node type to update (e.g., "ForEach@1", "CreateUpdateInfoQ1").
    /// </summary>
    [Required]
    public required string NodeType { get; init; }

    /// <summary>
    /// Zero-based occurrence index of this node type in the definition.
    /// </summary>
    public int NodeIndex { get; init; }

    /// <summary>
    /// Property values to set on the node. Null values remove the property.
    /// </summary>
    [Required]
    public required Dictionary<string, object?> Properties { get; init; }
}

/// <summary>
/// Request body for parsing a node from a pipeline definition.
/// </summary>
public class ParseNodeRequest
{
    /// <summary>
    /// The YAML pipeline definition string.
    /// </summary>
    [Required]
    public required string Definition { get; init; }

    /// <summary>
    /// The node type to find (e.g., "For@1", "Simulation@1").
    /// </summary>
    [Required]
    public required string NodeType { get; init; }

    /// <summary>
    /// Zero-based occurrence index of this node type in the definition.
    /// Use this to distinguish between multiple nodes of the same type.
    /// </summary>
    public int NodeIndex { get; init; }
}

/// <summary>
/// Controller for pipeline definition analysis and introspection.
/// Provides endpoints to parse YAML pipeline definitions and extract node information
/// for editor integration (e.g., Node Properties panel).
/// </summary>
[Authorize(AuthenticationSchemes = OidcConstants.AuthenticationSchemes.AuthorizationHeaderBearer)]
[ApiController]
[Route("{tenantId:tenantId}/v{version:apiVersion}/[controller]")]
[ApiVersion("1.0")]
public class PipelineDefinitionController : ControllerBase
{
    private readonly ILogger<PipelineDefinitionController> _logger;
    private readonly IPipelineDefinitionService _pipelineDefinitionService;

    /// <summary>
    /// Constructor
    /// </summary>
    public PipelineDefinitionController(
        ILogger<PipelineDefinitionController> logger,
        IPipelineDefinitionService pipelineDefinitionService)
    {
        _logger = logger;
        _pipelineDefinitionService = pipelineDefinitionService;
    }

    /// <summary>
    /// Parses a pipeline definition and returns the properties of a specific node instance.
    /// </summary>
    /// <param name="request">The parse request containing the definition, node type, and index</param>
    /// <returns>The node properties, or 404 if the node was not found</returns>
    [HttpPost("parse-node")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(PipelineNodeProperties), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public IActionResult ParseNode([FromBody] [Required] ParseNodeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Definition))
        {
            return BadRequest(new ErrorResponse { ErrorMessage = "Pipeline definition is required" });
        }

        if (string.IsNullOrWhiteSpace(request.NodeType))
        {
            return BadRequest(new ErrorResponse { ErrorMessage = "Node type is required" });
        }

        try
        {
            var result = _pipelineDefinitionService.GetNodeProperties(
                request.Definition, request.NodeType, request.NodeIndex);

            if (result == null)
            {
                return NotFound(new ErrorResponse
                {
                    ErrorMessage =
                        $"Node '{request.NodeType}' at index {request.NodeIndex} not found in the definition"
                });
            }

            return Ok(result);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error parsing pipeline definition for node {NodeType}", request.NodeType);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new ErrorResponse { ErrorMessage = $"Failed to parse pipeline definition: {e.Message}" });
        }
    }

    /// <summary>
    /// Updates the properties of a specific node in a pipeline definition.
    /// Finds the node by type and occurrence index, merges the provided properties,
    /// and returns the updated YAML definition.
    /// </summary>
    /// <param name="request">The update request containing the definition, node identifier, and new property values</param>
    /// <returns>The updated YAML definition string, or 404 if the node was not found</returns>
    [HttpPut("update-node")]
    [Authorize(Constants.TenantCommunicationApiReadWritePolicy)]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public IActionResult UpdateNode([FromBody] [Required] UpdateNodeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Definition))
        {
            return BadRequest(new ErrorResponse { ErrorMessage = "Pipeline definition is required" });
        }

        if (string.IsNullOrWhiteSpace(request.NodeType))
        {
            return BadRequest(new ErrorResponse { ErrorMessage = "Node type is required" });
        }

        try
        {
            var result = _pipelineDefinitionService.UpdateNodeProperties(
                request.Definition, request.NodeType, request.NodeIndex, request.Properties);

            if (result == null)
            {
                return NotFound(new ErrorResponse
                {
                    ErrorMessage =
                        $"Node '{request.NodeType}' at index {request.NodeIndex} not found in the definition"
                });
            }

            return Ok(result);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error updating node {NodeType} in pipeline definition", request.NodeType);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new ErrorResponse { ErrorMessage = $"Failed to update node: {e.Message}" });
        }
    }

    /// <summary>
    /// Parses a pipeline definition and returns all nodes with their types and property values.
    /// </summary>
    /// <param name="definition">The YAML pipeline definition string</param>
    /// <returns>List of all nodes found in the definition</returns>
    [HttpPost("parse-all-nodes")]
    [Authorize(Constants.TenantCommunicationApiReadOnlyPolicy)]
    [ProducesResponseType(typeof(IReadOnlyList<PipelineNodeProperties>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status500InternalServerError)]
    public IActionResult ParseAllNodes([FromBody] [Required] string definition)
    {
        if (string.IsNullOrWhiteSpace(definition))
        {
            return BadRequest(new ErrorResponse { ErrorMessage = "Pipeline definition is required" });
        }

        try
        {
            var result = _pipelineDefinitionService.GetAllNodes(definition);
            return Ok(result);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error parsing pipeline definition");
            return StatusCode(StatusCodes.Status500InternalServerError,
                new ErrorResponse { ErrorMessage = $"Failed to parse pipeline definition: {e.Message}" });
        }
    }
}
