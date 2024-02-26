using Meshmakers.Octo.Runtime.Contracts;
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
    public string Message { get; }
    
    /// <summary>
    /// Returns the associated tenant repository
    /// </summary>
    public ITenantRepository TenantRepository { get; }

    /// <summary>
    /// Returns the current session
    /// </summary>
    public IOctoSession Session { get; }

}