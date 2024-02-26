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
    /// <param name="properties"></param>
    public RetrieverEtlContext(string tenantId, object? message, ITenantRepository tenantRepository, IDictionary<string, object?> properties)
    {
        TenantId = tenantId;
        Message = message;
        TenantRepository = tenantRepository;
        Properties = properties;
    }

    /// <inheritdoc />
    public string TenantId { get; }

    /// <inheritdoc />
    public IDictionary<string, object?> Properties { get; }


    /// <inheritdoc />
    public object? Message { get; }

    /// <inheritdoc />
    public ITenantRepository TenantRepository { get; }
}