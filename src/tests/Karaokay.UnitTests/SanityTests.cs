namespace Karaokay.UnitTests;

/// <summary>
/// Trivial sanity test proving the xUnit host discovers and runs tests.
/// Real domain, MIDI golden-file, and room-code tests arrive in later slices.
/// </summary>
public sealed class SanityTests
{
    [Fact]
    public void TestFrameworkIsWiredUp() => Assert.True(true);
}
