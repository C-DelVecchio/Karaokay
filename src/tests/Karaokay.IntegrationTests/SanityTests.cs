namespace Karaokay.IntegrationTests;

/// <summary>
/// Trivial sanity test proving the xUnit host discovers and runs tests.
/// WebApplicationFactory, SignalR hub, and yt-dlp integration tests arrive
/// in later slices.
/// </summary>
public sealed class SanityTests
{
    [Fact]
    public void TestFrameworkIsWiredUp() => Assert.True(true);
}
