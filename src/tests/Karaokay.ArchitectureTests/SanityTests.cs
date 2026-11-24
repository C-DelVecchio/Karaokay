namespace Karaokay.ArchitectureTests;

/// <summary>
/// Trivial sanity test proving the xUnit host discovers and runs tests.
/// The architecture dependency rules (Domain/Contracts isolation) live in
/// <c>ArchitectureRules.cs</c>, implemented with reflection and positive controls.
/// </summary>
public sealed class SanityTests
{
    [Fact]
    public void TestFrameworkIsWiredUp() => Assert.True(true);
}
