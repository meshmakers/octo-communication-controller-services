namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Models;

/// <summary>
///     The authenticated principal acting on the Signal channel (AB#5143 registration audit
///     trail). Built by the controller from the request principal — the same claim mapping the
///     pipeline execute endpoint uses for its caller projection (subject falling back to
///     <c>client_id</c>, name falling back to <c>preferred_username</c>) — and passed into the
///     service layer, which records it on every register/verify/delete attempt.
/// </summary>
/// <param name="SubjectId">
///     The stable subject identifier (<c>sub</c> claim, or <c>client_id</c> for a pure service
///     credential). Null when the principal carries neither.
/// </param>
/// <param name="Name">
///     Human-readable name (<c>name</c> claim, falling back to <c>preferred_username</c>).
/// </param>
public sealed record SignalChannelActor(string? SubjectId, string? Name)
{
    /// <summary>Actor for an unauthenticated or claim-less principal (defensive; the routes require a bearer).</summary>
    public static readonly SignalChannelActor Unknown = new(null, null);

    /// <summary>
    ///     The single audit string persisted in a history entry: "Name (subject)" when both claims
    ///     are present, the one that exists otherwise, and "unknown" without either.
    /// </summary>
    public string ToAuditString()
    {
        return (Name, SubjectId) switch
        {
            ({ } name, { } subjectId) when !string.Equals(name, subjectId, StringComparison.Ordinal) =>
                $"{name} ({subjectId})",
            ({ } name, _) => name,
            (_, { } subjectId) => subjectId,
            _ => "unknown"
        };
    }
}
