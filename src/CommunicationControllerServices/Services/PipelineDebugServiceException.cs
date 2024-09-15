using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

internal class PipelineDebugServiceException : Exception
{
    public PipelineDebugServiceException()
    {
    }

    public PipelineDebugServiceException(string message) : base(message)
    {
    }

    public PipelineDebugServiceException(string message, Exception inner) : base(message, inner)
    {
    }
}

internal class PipelineDebugInformationNotFoundException : PipelineDebugServiceException
{
    public PipelineDebugInformationNotFoundException()
    {
    }

    public PipelineDebugInformationNotFoundException(string message) : base(message)
    {
    }

    public PipelineDebugInformationNotFoundException(string message, Exception inner) : base(message, inner)
    {
    }

    public static Exception NotFound(string tenantId, RtEntityId pipelineRtEntityId)
    {
        return new PipelineDebugInformationNotFoundException($"Pipeline debug information not found for tenant {tenantId} and pipeline {pipelineRtEntityId}");
    }

    public static Exception ExecutionNotFound(string tenantId, RtEntityId pipelineRtEntityId, Guid pipelineExecutionId)
    {
        return new PipelineDebugInformationNotFoundException($"Pipeline debug information not found for tenant {tenantId}, pipeline {pipelineRtEntityId} and execution {pipelineExecutionId}");
    }
}