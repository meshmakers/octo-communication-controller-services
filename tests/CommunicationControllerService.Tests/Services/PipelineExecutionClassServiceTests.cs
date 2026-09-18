using System.Diagnostics.CodeAnalysis;
using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Caches.Adapters;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Hubs;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v4;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
///     Pins the AB#4924 execution-class resolution: a pipeline is Interactive iff its TRIGGER
///     declares it, sourced from the descriptor flag (self-description) or the known-name fallback
///     (adapters whose descriptors are not available), and Batch in every other case.
/// </summary>
/// <remarks>
///     Every test here fails on a specific realistic mistake, not on a tautology:
///     <list type="bullet">
///         <item>reading the class off a transform node instead of a trigger</item>
///         <item>treating malformed YAML as "no trigger, therefore Batch" without noticing</item>
///         <item>letting a descriptor that is not a trigger vote on the class</item>
///         <item>dropping the fallback and classifying by whether the adapter happens to be online</item>
///     </list>
/// </remarks>
internal class PipelineExecutionClassServiceTests
{
    private const string TenantId = "tenantId";
    private const string ConnectionId = "connectionId";
    private const int Interactive = 0;
    private const int Batch = 1;

    private readonly IAdapterCache _adapterCache = Substitute.For<IAdapterCache>();
    // AB#4924: the real pool registry, so a leased resolution runs the real "which member answers
    // for this pool" pick rather than a stubbed answer.
    private readonly AdapterPoolConnectionManager _poolConnectionManager = new();
    // AB#5271: the capability service resolves a leased adapter's lending pool through its
    // LentFrom mirror, which is a repository read. Substituted here — the tests that exercise the
    // leased path arrange the mirror themselves.
    private readonly ICommunicationRepository _communicationRepository = Substitute.For<ICommunicationRepository>();
    private readonly PipelineExecutionClassService _service;
    private readonly RtAdapter _rtAdapter;

    public PipelineExecutionClassServiceTests()
    {
        // Real parser, deliberately: the classification has to run against real YAML, and the
        // triggers-vs-transformations distinction it depends on lives in that parser. Real
        // capability service for the same reason — it is the seam that decides WHOSE descriptors
        // answer, and a substitute would hide exactly that.
        _service = new PipelineExecutionClassService(
            new AdapterNodeCapabilityService(_adapterCache, _poolConnectionManager, _communicationRepository),
            new PipelineDefinitionService());
        _rtAdapter = RtEntityCreator.CreateAdapter();
    }

    private const string HttpTriggerPipeline = """
                                               triggers:
                                                 - type: FromHttpRequest@2
                                                   route: /invoices
                                               transformations:
                                                 - type: SetJson@1
                                               """;

    private const string PollingTriggerPipeline = """
                                                  triggers:
                                                    - type: FromPolling@1
                                                      interval: 60
                                                  transformations:
                                                    - type: SetJson@1
                                                  """;

    [Test]
    public async Task AKnownInteractiveTriggerIsInteractiveWithoutAnyDescriptor()
    {
        // The fallback path. Descriptors are in-memory only and null whenever the adapter has not
        // registered in this controller process — offline, hibernated, or a controller restart.
        // Without the fallback the class would depend on whether the adapter happened to be
        // connected when somebody saved, which is not an answer a queue view can explain.
        var result = _service.Resolve(HttpTriggerPipeline, null);

        await Assert.That(result).IsEqualTo(Interactive);
    }

    [Test]
    public async Task ADescriptorDeclaringInteractiveWins()
    {
        var result = _service.Resolve(
            """
            triggers:
              - type: FromCustomThing@3
            """,
            [Descriptor("FromCustomThing", 3, isTrigger: true, executionClass: Interactive)]);

        await Assert.That(result).IsEqualTo(Interactive);
    }

    [Test]
    public async Task ATriggerThatDeclaresNothingIsBatch()
    {
        // The conservative default, and the whole reason Batch is key 1 rather than key 0: a
        // trigger nobody classified must never jump a queue.
        var result = _service.Resolve(PollingTriggerPipeline, null);

        await Assert.That(result).IsEqualTo(Batch);
    }

    [Test]
    public async Task ANonTriggerDescriptorDeclaringInteractiveIsIgnored()
    {
        // A transform node saying "Interactive" means nothing — the class describes how the work
        // ARRIVED. Without the IsTrigger filter this returns Interactive.
        var result = _service.Resolve(
            """
            triggers:
              - type: FromPolling@1
            transformations:
              - type: SomeTransform@1
            """,
            [Descriptor("SomeTransform", 1, isTrigger: false, executionClass: Interactive)]);

        await Assert.That(result).IsEqualTo(Batch);
    }

    [Test]
    public async Task AnInteractiveNodeInTheTransformationsSectionIsIgnored()
    {
        // The reason TryGetTriggerNodes exists instead of GetAllNodes, which flattens both
        // sections into one list. With GetAllNodes this returns Interactive.
        var result = _service.Resolve(
            """
            triggers:
              - type: FromPolling@1
            transformations:
              - type: FromHttpRequest@2
            """,
            null);

        await Assert.That(result).IsEqualTo(Batch);
    }

    [Test]
    public async Task MalformedYamlIsBatchRatherThanAnException()
    {
        // A save must not fail because the class could not be resolved — but it must not quietly
        // become Interactive either.
        var result = _service.Resolve("triggers: [ this is not: valid: yaml", null);

        await Assert.That(result).IsEqualTo(Batch);
    }

    [Test]
    public async Task APipelineWithNoTriggerSectionIsBatch()
    {
        var result = _service.Resolve("transformations:\n  - type: SetJson@1", null);

        await Assert.That(result).IsEqualTo(Batch);
    }

    [Test]
    public async Task AnEmptyDefinitionIsBatch()
    {
        using var _ = Assert.Multiple();
        await Assert.That(_service.Resolve(null, null)).IsEqualTo(Batch);
        await Assert.That(_service.Resolve("", null)).IsEqualTo(Batch);
    }

    [Test]
    public async Task TheMostUrgentTriggerWins()
    {
        // A pipeline reachable both by HTTP and by schedule is interactive when it arrives by
        // HTTP, and the class is resolved once at save time — so the urgent answer is the safe one.
        // Bounded, because the class only orders work inside one tenant's own turn.
        var result = _service.Resolve(
            """
            triggers:
              - type: FromPolling@1
              - type: FromHttpRequest@2
            """,
            null);

        await Assert.That(result).IsEqualTo(Interactive);
    }

    [Test]
    public async Task TheVersionStrippedFallbackMatchesAnyVersion()
    {
        // FromHttpRequest@1 is deprecated but still deployed. The fallback is version-agnostic on
        // purpose, matching the AB#4984 precedent.
        var result = _service.Resolve("triggers:\n  - type: FromHttpRequest@1", null);

        await Assert.That(result).IsEqualTo(Interactive);
    }

    [Test]
    public async Task ResolveForAdapterUsesTheAdaptersLiveDescriptors()
    {
        GivenRegisteredAdapterWithDescriptors(
            Descriptor("FromCustomThing", 1, isTrigger: true, executionClass: Interactive));

        var result = await _service.ResolveForAdapterAsync(TenantId, _rtAdapter.ToRtEntityId(),
            "triggers:\n  - type: FromCustomThing@1");

        await Assert.That(result).IsEqualTo(Interactive);
    }

    [Test]
    public async Task ResolveForAdapterFallsBackWhenTheAdapterIsNotConnected()
    {
        // No GivenRegisteredAdapterWithDescriptors — the cache misses, as it does for every
        // hibernated adapter and after every controller restart.
        var result = await _service.ResolveForAdapterAsync(TenantId, _rtAdapter.ToRtEntityId(), HttpTriggerPipeline);

        await Assert.That(result).IsEqualTo(Interactive);
    }

    private static NodeDescriptorDto Descriptor(string name, int version, bool isTrigger, int executionClass) =>
        new(name, version, isTrigger ? "Trigger" : "Transform", isTrigger, false, "{}",
            false, null, false, executionClass);

    [SuppressMessage("Non-substitutable member", "NS1004:Argument matcher used with a non-virtual member of a class.")]
    private void GivenRegisteredAdapterWithDescriptors(params NodeDescriptorDto[] nodeDescriptors)
    {
        var adapterTenant = new AdapterTenant(Substitute.For<IAdapterCachePublish>(), TenantId);
        var adapter = adapterTenant.AddAdapter(_rtAdapter.ToRtEntityId(), ConnectionId,
            new AdapterConfigurationDto(_rtAdapter.ToRtEntityId(), null, []));
        adapter.SetNodeDescriptors(nodeDescriptors);

        _adapterCache.TryGetTenant(TenantId, out Arg.Any<AdapterTenant?>())
            .Returns(x =>
            {
                x[1] = adapterTenant;
                return true;
            });
    }
}
