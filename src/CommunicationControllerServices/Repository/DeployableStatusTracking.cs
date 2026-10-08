using Meshmakers.Octo.ConstructionKit.Models.System.Communication.Generated.System.Communication.v3;

namespace Meshmakers.Octo.Backend.CommunicationControllerServices.Repository;

/// <summary>
/// Keeps the outcome history of a deployable's live status line (AB#5618). <c>StatusMessage</c>
/// is overwritten by every report (AB#5385), so an error line used to erase WHEN the deployable
/// last succeeded. Two runtime-state attributes survive a failing streak:
/// <c>LastSuccessfulStatusAt</c> (only a success moves it) and <c>ConsecutiveStatusFailures</c>
/// (every failure counts up, a success resets it to 0).
/// </summary>
internal static class DeployableStatusTracking
{
    /// <summary>
    /// What a failed poll's line starts with by contract (the mesh adapter's
    /// <c>MailPollStatusLine.ErrorPrefix</c>). Used to classify a report whose sender did not set
    /// the error flag.
    /// </summary>
    public const string ErrorLinePrefix = "ERROR ";

    /// <summary>
    /// Whether a status report is a failure. The explicit flag wins; a report without it
    /// (deserialised as <c>false</c>) whose line nevertheless starts with <see cref="ErrorLinePrefix"/>
    /// is still a failure, so a sender that only follows the line convention does not reset the
    /// failure streak.
    /// </summary>
    public static bool IsFailure(bool isError, string? message)
    {
        return isError || (message?.StartsWith(ErrorLinePrefix, StringComparison.Ordinal) ?? false);
    }

    /// <summary>
    /// Writes the outcome of one status report onto <paramref name="update"/> — an entity used as
    /// a partial update, so only the attributes assigned here are written.
    /// </summary>
    /// <param name="update">The partial update to fill</param>
    /// <param name="current">The persisted entity, or <c>null</c> if it could not be read</param>
    /// <param name="isFailure">Outcome of the report (see <see cref="IsFailure"/>)</param>
    /// <param name="reportedAtUtc">When the controller received the report (UTC)</param>
    public static void Apply(RtDeployableEntity update, RtDeployableEntity? current, bool isFailure,
        DateTime reportedAtUtc)
    {
        if (isFailure)
        {
            // LastSuccessfulStatusAt is deliberately not touched: it is the whole point.
            update.ConsecutiveStatusFailures = (current?.ConsecutiveStatusFailures ?? 0) + 1;
        }
        else
        {
            update.LastSuccessfulStatusAt = reportedAtUtc;
            update.ConsecutiveStatusFailures = 0;
        }
    }
}
