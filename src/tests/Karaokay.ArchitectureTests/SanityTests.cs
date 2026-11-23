namespace Karaokay.ArchitectureTests;

/// <summary>
/// Trivial sanity test proving the xUnit host discovers and runs tests.
/// NetArchTest dependency rules (Domain/Contracts isolation) arrive in a later
/// Slice 1 work package.
/// </summary>
public sealed class SanityTests
{
    [Fact]
    public void TestFrameworkIsWiredUp() => Assert.True(true);
}
