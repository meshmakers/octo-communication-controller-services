using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
/// AB#7246 (Feature AB#7219): the entity types whose values are platform-only and therefore never part of the
/// configuration list the controller ships to an adapter. That list reaches the pipeline of a tenant with its
/// Secret values revealed (<see cref="ConfigurationSecretSerializer" />), so a type listed here must never be in it.
/// </summary>
/// <remarks>
/// <c>System.Communication/SecretDefault</c> (System.Communication 4.15.0, AB#7243) derives from <c>System/Entity</c>
/// and is not a Configuration type, so no association can bring it into the list by itself; the exclusion in
/// <c>AdapterService.CreatePipelineConfigurationAsync</c> makes that an enforced rule instead of a property of the model.
/// The id is spelled out here and not taken from the generated model so the rule also holds on a build that has not
/// picked the model up yet.
/// </remarks>
internal static class PlatformOnlyConfigurationTypes
{
    public static readonly RtCkId<CkTypeId> SecretDefaultTypeId = new("System.Communication/SecretDefault");

    public static bool IsPlatformOnly(RtCkId<CkTypeId>? ckTypeId)
    {
        return ckTypeId != null && SecretDefaultTypeId.Equals(ckTypeId);
    }
}
