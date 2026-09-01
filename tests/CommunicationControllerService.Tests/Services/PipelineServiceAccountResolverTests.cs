using Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;
using Meshmakers.Octo.Backend.CommunicationControllerServices.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using NSubstitute;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services;

/// <summary>
/// AB#5027 (Epic AB#4979): pins the resolution order for the identity a pipeline executes as —
/// per-pipeline override beats adapter default, adapter default applies when there is no
/// override, and "nothing linked" resolves to <see cref="PipelineServiceAccountSource.None" />.
/// </summary>
internal class PipelineServiceAccountResolverTests
{
    private const string TenantId = "tenantId";

    private readonly ICommunicationRepository _communicationRepository =
        Substitute.For<ICommunicationRepository>();

    private readonly PipelineServiceAccountResolver _resolver;

    public PipelineServiceAccountResolverTests()
    {
        _resolver = new PipelineServiceAccountResolver(_communicationRepository);
    }

    private void ArrangePipelineConfigurations(OctoObjectId pipelineRtId, params RtConfiguration[] configurations)
    {
        _communicationRepository.GetConfigurationsByPipelineAsync(TenantId, pipelineRtId)
            .Returns(Task.FromResult<IEnumerable<RtConfiguration>>(configurations));
    }

    [Test]
    public async Task Resolve_PipelineOverrideAndAdapterDefault_OverrideWins()
    {
        var pipeline = RtEntityCreator.CreatePipeline();
        var adapter = RtEntityCreator.CreateAdapter();
        var overrideAccount = RtEntityCreator.CreateServiceAccountConfiguration("pipeline-override");
        var adapterDefault = RtEntityCreator.CreateServiceAccountConfiguration("adapter-default");

        ArrangePipelineConfigurations(pipeline.RtId, overrideAccount);
        _communicationRepository.GetServiceAccountForAdapterAsync(TenantId, adapter.RtId).Returns(adapterDefault);

        var result = await _resolver.ResolveAsync(TenantId, pipeline.RtId, adapter.RtId);

        using var _ = Assert.Multiple();
        await Assert.That(result.IsResolved).IsTrue();
        await Assert.That(result.Source).IsEqualTo(PipelineServiceAccountSource.PipelineOverride);
        await Assert.That(result.ServiceAccount!.RtId).IsEqualTo(overrideAccount.RtId);
    }

    [Test]
    public async Task Resolve_NoOverride_FallsBackToAdapterDefault()
    {
        var pipeline = RtEntityCreator.CreatePipeline();
        var adapter = RtEntityCreator.CreateAdapter();
        var adapterDefault = RtEntityCreator.CreateServiceAccountConfiguration("adapter-default");

        // The pipeline links a configuration, just not a service account.
        ArrangePipelineConfigurations(pipeline.RtId, new RtSftpConfiguration { RtWellKnownName = "sftp" });
        _communicationRepository.GetServiceAccountForAdapterAsync(TenantId, adapter.RtId).Returns(adapterDefault);

        var result = await _resolver.ResolveAsync(TenantId, pipeline.RtId, adapter.RtId);

        using var _ = Assert.Multiple();
        await Assert.That(result.IsResolved).IsTrue();
        await Assert.That(result.Source).IsEqualTo(PipelineServiceAccountSource.AdapterDefault);
        await Assert.That(result.ServiceAccount!.RtId).IsEqualTo(adapterDefault.RtId);
    }

    [Test]
    public async Task Resolve_NothingLinked_IsUnresolved()
    {
        var pipeline = RtEntityCreator.CreatePipeline();
        var adapter = RtEntityCreator.CreateAdapter();

        ArrangePipelineConfigurations(pipeline.RtId);
        _communicationRepository.GetServiceAccountForAdapterAsync(TenantId, adapter.RtId)
            .Returns((RtServiceAccountConfiguration?)null);

        var result = await _resolver.ResolveAsync(TenantId, pipeline.RtId, adapter.RtId);

        using var _ = Assert.Multiple();
        await Assert.That(result.IsResolved).IsFalse();
        await Assert.That(result.Source).IsEqualTo(PipelineServiceAccountSource.None);
        await Assert.That(result.ServiceAccount).IsNull();
    }

    [Test]
    public async Task Resolve_OverridePresent_DoesNotQueryAdapterDefault()
    {
        var pipeline = RtEntityCreator.CreatePipeline();
        var adapter = RtEntityCreator.CreateAdapter();
        ArrangePipelineConfigurations(pipeline.RtId, RtEntityCreator.CreateServiceAccountConfiguration("override"));

        await _resolver.ResolveAsync(TenantId, pipeline.RtId, adapter.RtId);

        await _communicationRepository.DidNotReceiveWithAnyArgs()
            .GetServiceAccountForAdapterAsync(Arg.Any<string>(), Arg.Any<OctoObjectId>());
    }

    [Test]
    public async Task Resolve_MultipleOverrides_PicksDeterministically()
    {
        var pipeline = RtEntityCreator.CreatePipeline();
        var adapter = RtEntityCreator.CreateAdapter();
        var first = RtEntityCreator.CreateServiceAccountConfiguration("a", "670000000000000000000001");
        var second = RtEntityCreator.CreateServiceAccountConfiguration("b", "670000000000000000000002");

        // Same set, opposite enumeration order — both must resolve to the same account.
        ArrangePipelineConfigurations(pipeline.RtId, second, first);
        var forward = await _resolver.ResolveAsync(TenantId, pipeline.RtId, adapter.RtId);

        ArrangePipelineConfigurations(pipeline.RtId, first, second);
        var reverse = await _resolver.ResolveAsync(TenantId, pipeline.RtId, adapter.RtId);

        using var _ = Assert.Multiple();
        await Assert.That(forward.ServiceAccount!.RtId).IsEqualTo(first.RtId);
        await Assert.That(reverse.ServiceAccount!.RtId).IsEqualTo(first.RtId);
    }

    [Test]
    public async Task ResolveForPipeline_NoOverride_UsesExecutingAdapterDefault()
    {
        var pipeline = RtEntityCreator.CreatePipeline();
        var adapter = RtEntityCreator.CreateAdapter();
        var adapterDefault = RtEntityCreator.CreateServiceAccountConfiguration("adapter-default");

        ArrangePipelineConfigurations(pipeline.RtId);
        _communicationRepository.GetAdapterByPipelineAsync(TenantId, pipeline.ToRtEntityId()).Returns(adapter);
        _communicationRepository.GetServiceAccountForAdapterAsync(TenantId, adapter.RtId).Returns(adapterDefault);

        var result = await _resolver.ResolveForPipelineAsync(TenantId, pipeline.ToRtEntityId());

        using var _ = Assert.Multiple();
        await Assert.That(result.Source).IsEqualTo(PipelineServiceAccountSource.AdapterDefault);
        await Assert.That(result.ServiceAccount!.RtId).IsEqualTo(adapterDefault.RtId);
    }

    [Test]
    public async Task ResolveForPipeline_PipelineWithoutAdapter_IsUnresolved()
    {
        var pipeline = RtEntityCreator.CreatePipeline();

        ArrangePipelineConfigurations(pipeline.RtId);
        _communicationRepository.GetAdapterByPipelineAsync(TenantId, pipeline.ToRtEntityId())
            .Returns<RtAdapter?>(_ => throw CommunicationRepositoryException.PipelineHasNoAdapter(TenantId,
                pipeline.RtId));

        var result = await _resolver.ResolveForPipelineAsync(TenantId, pipeline.ToRtEntityId());

        await Assert.That(result.IsResolved).IsFalse();
    }
}
