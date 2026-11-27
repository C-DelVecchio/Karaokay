using Karaokay.Importer.Dlc;

namespace Karaokay.IntegrationTests;

/// <summary>
/// Pins the locator precedence (flag &gt; env &gt; PATH &gt; repo-local <c>.tools</c>), the
/// exists+executable validation, near-miss reporting and the actionable §6.1 message. No real Onyx
/// is needed.
/// </summary>
public sealed class OnyxLocatorTests
{
    [Fact]
    public void CliFlagWinsOverEnvironmentAndPath()
    {
        using var temp = new TempDirectory("karaokay-onyx-locator-");
        var cli = FakeOnyx.WriteExecutable(temp.Resolve("cli/onyx"));
        var environment = FakeOnyx.WriteExecutable(temp.Resolve("env/onyx"));
        var pathDirectory = temp.Resolve("pathdir");
        FakeOnyx.WriteExecutable(Path.Combine(pathDirectory, "onyx"));

        var resolved = OnyxLocator.TryResolve(
            new OnyxLocatorEnvironment(cli, environment, [pathDirectory], RepositoryRoot: null, IsWindows: false),
            out var location,
            out _);

        Assert.True(resolved);
        Assert.Equal("--onyx", location.Source);
        Assert.Equal(cli, location.ExecutablePath);
    }

    [Fact]
    public void EnvironmentVariableWinsOverPath()
    {
        using var temp = new TempDirectory("karaokay-onyx-locator-");
        var environment = FakeOnyx.WriteExecutable(temp.Resolve("env/onyx"));
        var pathDirectory = temp.Resolve("pathdir");
        FakeOnyx.WriteExecutable(Path.Combine(pathDirectory, "onyx"));

        var resolved = OnyxLocator.TryResolve(
            new OnyxLocatorEnvironment(null, environment, [pathDirectory], null, IsWindows: false),
            out var location,
            out _);

        Assert.True(resolved);
        Assert.Equal(OnyxLocator.EnvironmentVariableName, location.Source);
        Assert.Equal(environment, location.ExecutablePath);
    }

    [Fact]
    public void PathEntryResolvesThePlatformName()
    {
        using var temp = new TempDirectory("karaokay-onyx-locator-");
        var pathDirectory = temp.Resolve("bin");
        var executable = FakeOnyx.WriteExecutable(Path.Combine(pathDirectory, "onyx"));

        var resolved = OnyxLocator.TryResolve(
            new OnyxLocatorEnvironment(null, null, [pathDirectory], null, IsWindows: false),
            out var location,
            out _);

        Assert.True(resolved);
        Assert.Equal("PATH", location.Source);
        Assert.Equal(executable, location.ExecutablePath);
    }

    [Fact]
    public void DirectoryFormAppendsThePlatformName()
    {
        using var temp = new TempDirectory("karaokay-onyx-locator-");
        var directory = temp.Resolve("install");
        var executable = FakeOnyx.WriteExecutable(Path.Combine(directory, "onyx"));

        var resolved = OnyxLocator.TryResolve(
            new OnyxLocatorEnvironment(directory, null, [], null, IsWindows: false),
            out var location,
            out _);

        Assert.True(resolved);
        Assert.Equal(executable, location.ExecutablePath);
    }

    [Fact]
    public void ToolsExtractedBinaryIsTheLastResort()
    {
        using var temp = new TempDirectory("karaokay-onyx-locator-");
        var executable = FakeOnyx.WriteExecutable(
            Path.Combine(temp.Root, ".tools", "squashfs-root", "usr", "bin", "onyx"));

        var resolved = OnyxLocator.TryResolve(
            new OnyxLocatorEnvironment(null, null, [], temp.Root, IsWindows: false),
            out var location,
            out _);

        Assert.True(resolved);
        Assert.Equal(".tools", location.Source);
        Assert.Equal(executable, location.ExecutablePath);
    }

    [Fact]
    public void ExtractedToolsBinaryWinsOverTheAppImage()
    {
        using var temp = new TempDirectory("karaokay-onyx-locator-");
        var extracted = FakeOnyx.WriteExecutable(
            Path.Combine(temp.Root, ".tools", "squashfs-root", "usr", "bin", "onyx"));
        FakeOnyx.WriteExecutable(Path.Combine(temp.Root, ".tools", "onyx.AppImage"));

        var resolved = OnyxLocator.TryResolve(
            new OnyxLocatorEnvironment(null, null, [], temp.Root, IsWindows: false),
            out var location,
            out _);

        Assert.True(resolved);
        Assert.Equal(extracted, location.ExecutablePath);
    }

    [Fact]
    public void ToolsAppImageIsAcceptedWhenTheExtractedTreeIsAbsent()
    {
        using var temp = new TempDirectory("karaokay-onyx-locator-");
        var appImage = FakeOnyx.WriteExecutable(Path.Combine(temp.Root, ".tools", "onyx.AppImage"));

        var resolved = OnyxLocator.TryResolve(
            new OnyxLocatorEnvironment(null, null, [], temp.Root, IsWindows: false),
            out var location,
            out _);

        Assert.True(resolved);
        Assert.Equal(".tools", location.Source);
        Assert.Equal(appImage, location.ExecutablePath);
    }

    [Fact]
    public void ExplicitButMissingFlagDoesNotFallBack()
    {
        using var temp = new TempDirectory("karaokay-onyx-locator-");
        var environment = FakeOnyx.WriteExecutable(temp.Resolve("env/onyx"));
        var missing = temp.Resolve("missing/onyx");

        var resolved = OnyxLocator.TryResolve(
            new OnyxLocatorEnvironment(missing, environment, [], null, IsWindows: false),
            out _,
            out var summary);

        Assert.False(resolved);
        Assert.Contains($"--onyx '{missing}' (not found)", summary, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void NonExecutableCandidateIsReportedAsANearMiss()
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix execute bits are not available on Windows.");

        using var temp = new TempDirectory("karaokay-onyx-locator-");
        var pathDirectory = temp.Resolve("bin");
        Directory.CreateDirectory(pathDirectory);
        var notExecutable = Path.Combine(pathDirectory, "onyx");
        File.WriteAllText(notExecutable, "#!/bin/sh\n");

        var resolved = OnyxLocator.TryResolve(
            new OnyxLocatorEnvironment(null, null, [pathDirectory], null, IsWindows: false),
            out _,
            out var summary);

        Assert.False(resolved);
        Assert.Contains("exists but is not executable", summary, StringComparison.Ordinal);
        Assert.Contains(notExecutable, summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingSummaryNamesEveryPrecedenceLevel()
    {
        using var temp = new TempDirectory("karaokay-onyx-locator-");

        var resolved = OnyxLocator.TryResolve(
            new OnyxLocatorEnvironment(null, null, [], temp.Root, IsWindows: false),
            out _,
            out var summary);

        Assert.False(resolved);
        Assert.Contains("--onyx (not given)", summary, StringComparison.Ordinal);
        Assert.Contains("KARAOKAY_ONYX_PATH (unset)", summary, StringComparison.Ordinal);
        Assert.Contains("PATH (", summary, StringComparison.Ordinal);
        Assert.Contains(".tools (no onyx)", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingMessageIsActionableAndMentionsPs3RapWhenNeeded()
    {
        var containers = new List<DlcContainer>
        {
            new("/dlc/a CON", DlcContainerFormat.StfsCon, IsPs3: false),
            new("/dlc/b.pkg", DlcContainerFormat.Ps3Package, IsPs3: true),
        };

        var message = OnyxLocator.BuildMissingOnyxMessage(
            "media/rb-ingest",
            containers,
            "--onyx (not given), KARAOKAY_ONYX_PATH (unset), PATH (4 entries), .tools (no onyx)");

        Assert.Contains("encrypted Rock Band DLC detected in 'media/rb-ingest'", message, StringComparison.Ordinal);
        Assert.Contains("Detected: 2 containers (STFS CON, PS3 package)", message, StringComparison.Ordinal);
        Assert.Contains("https://github.com/mtolly/onyx/releases", message, StringComparison.Ordinal);
        Assert.Contains("--onyx /path/to/onyx", message, StringComparison.Ordinal);
        Assert.Contains("KARAOKAY_ONYX_PATH", message, StringComparison.Ordinal);
        Assert.Contains("libfuse2", message, StringComparison.Ordinal);
        Assert.Contains("libpangoxft-1.0.so.0", message, StringComparison.Ordinal);
        Assert.Contains("onyx-resources/raps/<CONTENTID>.RAP", message, StringComparison.Ordinal);
        Assert.Contains("20261111", message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingMessageOmitsTheRapParagraphForNonPs3Inputs()
    {
        var message = OnyxLocator.BuildMissingOnyxMessage(
            "media/rb-ingest",
            [new DlcContainer("/dlc/a CON", DlcContainerFormat.StfsCon, IsPs3: false)],
            "--onyx (not given)");

        Assert.DoesNotContain("onyx-resources/raps", message, StringComparison.Ordinal);
    }
}
