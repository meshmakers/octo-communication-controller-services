using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

/// <summary>
/// Interface for the Retriever ETL context
/// </summary>
public interface IRetrieverEtlContext : IEtlContext
{
    /// <summary>
    /// Returns the message
    /// </summary>
    public object? Message { get; }
    
    /// <summary>
    /// Returns the associated tenant repository
    /// </summary>
    public ITenantRepository TenantRepository { get; }
}