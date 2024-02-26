using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repository;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Consumers;

/// <summary>
/// ETL context for the Retriever
/// </summary>
public class RetrieverEtlContext : IRetrieverEtlContext
{
    /// <summary>
    /// Create a new instance of <see cref="RetrieverEtlContext"/>
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="message"></param>
    /// <param name="tenantRepository"></param>
    /// <param name="session"></param>
    /// <param name="properties"></param>
    public RetrieverEtlContext(string tenantId, string message, ITenantRepository tenantRepository, IOctoSession session, IDictionary<string, object?> properties)
    {
        TenantId = tenantId;
        Message = message;
        TenantRepository = tenantRepository;
        Session = session;
        Properties = properties;
    }

    /// <inheritdoc />
    public string TenantId { get; }

    /// <inheritdoc />
    public IDictionary<string, object?> Properties { get; }


    /// <inheritdoc />
    public string Message { get; }

    /// <inheritdoc />
    public ITenantRepository TenantRepository { get; }

    /// <inheritdoc />
    public IOctoSession Session { get; }
}