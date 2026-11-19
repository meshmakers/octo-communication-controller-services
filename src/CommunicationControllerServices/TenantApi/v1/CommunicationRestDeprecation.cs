namespace Meshmakers.Octo.Backend.CommunicationControllerServices.TenantApi.v1;

/// <summary>
///     REST deprecation of the communication controller endpoints that are facades of System.Communication CK methods (AB#9051,
///     Feature AB#5701 F5.3; <c>octo-common-services/docs/rest-deprecation.md</c>): the 15 adapter, adapter pool, pipeline,
///     data-flow and workload endpoints of AB#9048 / AB#9049 and the 7 deployment endpoints of AB#9050.
/// </summary>
/// <remarks>
///     <para>
///         The dates come from configuration (<c>RestDeprecation:Waves:CommunicationWave3:Since|Sunset</c>, environment
///         <c>OCTO_RestDeprecation__Waves__CommunicationWave3__Since</c>), not from code. Until <c>Since</c> is set (the delivery
///         day of the release that contains the methods), the endpoints answer the <c>Link</c> header, are <c>deprecated</c> in
///         OpenAPI and are counted in <c>octo.rest.deprecated.calls</c>, but carry no <c>Deprecation</c> header.
///     </para>
///     <para>
///         No <c>Sunset</c>: the sunset date is to be announced; Gerald decides it after the F5.x Features. The endpoints that
///         stay REST (reads, the stateless tools, the tenant-level <c>CommunicationController</c> endpoints) carry no attribute;
///         the list with the reasons is in <c>docs/rest-method-facades.md</c>.
///     </para>
/// </remarks>
public static class CommunicationRestDeprecation
{
    /// <summary>The wave of the endpoints that are facades of System.Communication methods.</summary>
    public const string Wave3 = "CommunicationWave3";

    /// <summary>Documentation of the replacing methods (Communication methods page, REST to method table).</summary>
    public const string MethodsLink = "https://docs.meshmakers.cloud/docs/technologyGuide/communication/methods";
}
