namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Sockets.Descriptions;

internal class SocketTenantDescription
{
    public IEnumerable<SocketDescription> Sockets { get; set; } = null!;
    public string TenantId { get; set; } = null!;
}