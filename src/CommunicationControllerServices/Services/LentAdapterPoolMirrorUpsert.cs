using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Services;

/// <summary>
///     What one mirror upsert did (AB#5271).
/// </summary>
/// <remarks>
///     The distinction exists so a reconcile can report what it actually changed. Without it every
///     pass counts every mirror as work — and since the reconcile runs on every tenant load and
///     every pool deploy, "Refresh" would report three pools provisioned on a run that touched
///     nothing, which is how a no-op becomes indistinguishable from a repair.
/// </remarks>
/// <param name="RtId">The mirror's RtId in the borrowing tenant.</param>
/// <param name="Wrote">False when the stored mirror already said the right thing.</param>
public readonly record struct LentAdapterPoolMirrorUpsert(OctoObjectId RtId, bool Wrote)
{
    /// <summary>The mirror did not exist and was created.</summary>
    public static LentAdapterPoolMirrorUpsert Created(OctoObjectId rtId) => new(rtId, true);

    /// <summary>The mirror existed and carried something else.</summary>
    public static LentAdapterPoolMirrorUpsert Updated(OctoObjectId rtId) => new(rtId, true);

    /// <summary>The mirror existed and already matched; nothing was written.</summary>
    public static LentAdapterPoolMirrorUpsert Unchanged(OctoObjectId rtId) => new(rtId, false);
}
