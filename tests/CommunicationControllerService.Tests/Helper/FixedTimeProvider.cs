namespace Meshmakers.Octo.Backend.CommunicationControllerService.Tests.Helper;

/// <summary>
/// A clock that stands still until a test moves it (AB#5583: clock-hour aligned statistics
/// windows are only testable against a fixed "now").
/// </summary>
internal sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
{
    public DateTime UtcNow { get; set; } = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

    public override DateTimeOffset GetUtcNow() => new(UtcNow, TimeSpan.Zero);
}
