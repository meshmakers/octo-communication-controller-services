using System.Collections.Immutable;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// The entity types whose values are platform-only and therefore never part of the
/// configuration list the controller ships to an adapter.
/// </summary>
internal static class PlatformOnlyConfigurationTypes
{
    public static readonly ImmutableArray<RtCkId<CkTypeId>> PlatformOnlyTypeIds =
    [
        new("System.Communication/SecretDefault")
    ];

    public static bool IsPlatformOnly(RtCkId<CkTypeId>? ckTypeId)
    {
        return ckTypeId != null && PlatformOnlyTypeIds.Contains(ckTypeId);
    }
}
