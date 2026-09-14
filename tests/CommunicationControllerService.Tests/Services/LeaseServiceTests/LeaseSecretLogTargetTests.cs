using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;

namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Services.LeaseServiceTests;

/// <summary>
///     AB#4924 increment 6 — the borrower's client secret must not reach any log target on the
///     <b>lease</b> path.
/// </summary>
/// <remarks>
///     <para>
///         🔴 <b>Why this exists.</b> AB#5027 put a client secret on the deploy path and guarded it
///         with <c>DeployWorkloadAsync_NeverWritesTheClientSecretToAnyLogTarget</c>. Concept §8 Q6 has
///         now put the same class of secret on a <i>second</i> route — the hub — so the same guard has
///         to exist there. This suite is deliberately the same shape as that one (reconfigure NLog to
///         a <c>MemoryTarget</c>, drive the real path, assert on the <b>rendered</b> output) rather
///         than a weaker approximation: a test that inspected a specific log statement would pass
///         while an unrelated <c>Logger.Debug(..., lease)</c> elsewhere leaked the value.
///     </para>
///     <para>
///         The probe asserts three things, and all three matter. That the secret really travelled —
///         otherwise it proves nothing. That neither the value nor a prefix of it appears — a
///         truncated secret is still secret material. And that the run logged <i>something</i> —
///         an empty target would make the assertion vacuous.
///     </para>
/// </remarks>
internal class LeaseSecretLogTargetTests : LeaseServiceTestsBase
{
    [Test]
    [NotInParallel(nameof(LeaseSecretLogTargetTests))]
    public async Task GrantLeaseAsync_NeverWritesTheClientSecretToAnyLogTarget()
    {
        ArrangeGrantableLease();

        var memoryTarget = new NLog.Targets.MemoryTarget("lease-secret-probe")
        {
            Layout = "${level}|${message}|${exception:format=ToString}"
        };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest("exec-1"));

            using var _ = Assert.Multiple();
            // The value really did travel — otherwise the probe proves nothing.
            await Assert.That(result.Granted).IsTrue();
            await Assert.That(CapturePushedLease().ClientSecret).IsEqualTo(ClientSecret);
            // Not verbatim, and not truncated either — a prefix is still secret material.
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(ClientSecret, StringComparison.Ordinal)))
                .IsFalse();
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(ClientSecret[..8], StringComparison.Ordinal)))
                .IsFalse();
            // The probe is only meaningful if the run actually logged something.
            await Assert.That(memoryTarget.Logs).IsNotEmpty();
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }

    /// <summary>
    ///     🔴 The most likely future leak is not a deliberate log of the secret — it is somebody
    ///     logging the <see cref="LeaseDto" /> itself, whose generated record <c>ToString</c> would
    ///     print every property. This drives the same path with a logger that renders the whole
    ///     object, which is what a structured-logging call would do.
    /// </summary>
    [Test]
    [NotInParallel(nameof(LeaseSecretLogTargetTests))]
    public async Task RenderingTheLeaseObjectItselfDoesNotRevealTheSecret()
    {
        ArrangeGrantableLease();
        await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, ARequest());
        var lease = CapturePushedLease();

        var memoryTarget = new NLog.Targets.MemoryTarget("lease-tostring-probe")
        {
            Layout = "${message}"
        };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            NLog.LogManager.GetLogger("probe").Info("A lease travelled: {Lease}", lease);

            using var _ = Assert.Multiple();
            await Assert.That(memoryTarget.Logs).IsNotEmpty();
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(ClientSecret, StringComparison.Ordinal)))
                .IsFalse();
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(ClientSecret[..8], StringComparison.Ordinal)))
                .IsFalse();
            // The line still identifies the lease — otherwise the obvious "fix" for a useless log
            // line is to print the object some other way.
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(lease.LeaseId, StringComparison.Ordinal)))
                .IsTrue();
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }

    /// <summary>
    ///     🔴 AB#4924 §9.9 / D4 — <b>the same assertion, over the enlarged DTO.</b> The lease now also
    ///     carries the borrowing tenant's pipeline input and that pipeline's resolved configuration
    ///     entries, and AB#5027 puts a service-account credential into exactly those entries. Adding
    ///     work to the lease must not have opened a second log or error path that renders the whole
    ///     object — which is what would happen the first time somebody logged the <c>LeaseDto</c>
    ///     itself, or interpolated its <c>Pipeline</c> into a diagnostic.
    /// </summary>
    [Test]
    [NotInParallel(nameof(LeaseSecretLogTargetTests))]
    public async Task GrantLeaseAsync_NeverWritesTheWorkTheLeaseCarriesToAnyLogTarget()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();

        var memoryTarget = new NLog.Targets.MemoryTarget("lease-work-probe")
        {
            Layout = "${level}|${message}|${exception:format=ToString}"
        };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            var result = await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, AWorkRequest());
            var lease = CapturePushedLease();

            using var _ = Assert.Multiple();
            // The work really did travel — otherwise the probe proves nothing.
            await Assert.That(result.Granted).IsTrue();
            await Assert.That(lease.PipelineInput).IsEqualTo(PipelineInput);
            await Assert.That(lease.Pipeline).IsNotNull();
            await Assert.That(memoryTarget.Logs).IsNotEmpty();

            // The lease's own credential, unchanged from increment 6.
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(ClientSecret, StringComparison.Ordinal)))
                .IsFalse();
            // The configuration's credential — the one the enlarged DTO added.
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(ConfigurationSecret, StringComparison.Ordinal)))
                .IsFalse();
            await Assert.That(memoryTarget.Logs
                    .Any(l => l.Contains(ConfigurationSecret[..8], StringComparison.Ordinal)))
                .IsFalse();
            // The borrower's payload. Not a credential, but it is another tenant's data in a process
            // that serves a third one a second later.
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(PipelineInput, StringComparison.Ordinal)))
                .IsFalse();
            await Assert.That(memoryTarget.Logs
                    .Any(l => l.Contains("BORROWER-PRIVATE-4711", StringComparison.Ordinal)))
                .IsFalse();
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }

    /// <summary>
    ///     🔴 The same probe as <c>RenderingTheLeaseObjectItselfDoesNotRevealTheSecret</c>, extended to
    ///     the work. A record's generated <c>ToString</c> prints every property, so a structured-logging
    ///     call on the enlarged object would print the input and the whole pipeline configuration too.
    /// </summary>
    [Test]
    [NotInParallel(nameof(LeaseSecretLogTargetTests))]
    public async Task RenderingTheEnlargedLeaseObjectRevealsNeitherTheInputNorTheConfiguration()
    {
        ArrangeGrantableLease();
        ArrangeProjectablePipeline();
        await LeaseService.GrantLeaseAsync(LenderTenantId, PoolRtId, AWorkRequest());
        var lease = CapturePushedLease();

        var memoryTarget = new NLog.Targets.MemoryTarget("lease-work-tostring-probe") { Layout = "${message}" };
        var previousConfiguration = NLog.LogManager.Configuration;
        var probeConfiguration = new NLog.Config.LoggingConfiguration();
        probeConfiguration.AddRule(NLog.LogLevel.Trace, NLog.LogLevel.Fatal, memoryTarget);
        NLog.LogManager.Configuration = probeConfiguration;
        try
        {
            NLog.LogManager.GetLogger("probe").Info("A lease travelled: {Lease}", lease);

            using var _ = Assert.Multiple();
            await Assert.That(memoryTarget.Logs).IsNotEmpty();
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(ClientSecret, StringComparison.Ordinal)))
                .IsFalse();
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(ConfigurationSecret, StringComparison.Ordinal)))
                .IsFalse();
            await Assert.That(memoryTarget.Logs.Any(l => l.Contains(PipelineInput, StringComparison.Ordinal)))
                .IsFalse();
            // It still identifies the work, or the obvious "fix" for a useless line is to print the
            // object some other way.
            await Assert.That(memoryTarget.Logs
                    .Any(l => l.Contains(PipelineRtId.ToString(), StringComparison.Ordinal)))
                .IsTrue();
        }
        finally
        {
            NLog.LogManager.Configuration = previousConfiguration;
        }
    }
}
