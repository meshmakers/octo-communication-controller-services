using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     Maps a <c>DeployableWorkload</c> onto the discriminator the operator receives on the wire.
/// </summary>
/// <remarks>
///     One place, four call sites (deploy, undeploy, scale, operator reverse-sync). Before AB#4924
///     each of them carried its own <c>workload is RtApplication ? … : …</c> ternary, which meant a
///     new subtype was silently reported as an Adapter at every one of them — and
///     <see cref="RtAdapterPool" /> is exactly such a subtype. The operator routes a pool to a
///     different namespace, gives it an owner reference and withholds the cluster's data-store
///     credentials, so "silently an Adapter" is not a cosmetic default here.
/// </remarks>
internal static class WorkloadWireMapping
{
    public static WorkloadTypeDto ResolveWorkloadType(RtDeployableWorkload workload) => workload switch
    {
        RtApplication => WorkloadTypeDto.Application,
        RtAdapterPool => WorkloadTypeDto.AdapterPool,
        // Adapter, and the historical default for anything unknown: an unrecognised subtype that
        // reaches the operator as an Adapter behaves as workloads did before this type existed.
        _ => WorkloadTypeDto.Adapter,
    };
}
