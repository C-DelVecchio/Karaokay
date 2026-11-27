using Karaokay.Importer.Dlc;

namespace Karaokay.IntegrationTests;

/// <summary>
/// Pins the Onyx pre-stage's content sniffing: 4-byte STFS magics, the extension-hinted PS3 rows,
/// the bare-MOGG vendor marker, ARK directory markers, the plain-folder-wins rule and the
/// sidecar ignore list. Fixtures are synthetic magic + zeros under <c>fixtures/dlc-headers</c>.
/// </summary>
public sealed class DlcContainerDetectorTests
{
    [Theory]
    [InlineData("stfs-con.bin", "STFS CON", false)]
    [InlineData("stfs-live.bin", "STFS LIVE", false)]
    [InlineData("stfs-pirs.bin", "STFS PIRS", false)]
    [InlineData("bare-mogg.bin", "MOGG", false)]
    public void SniffFileRecognizesContentMagicWithoutAnExtension(
        string fixtureName,
        string expectedFormat,
        bool expectedIsPs3)
    {
        using var temp = new TempDirectory("karaokay-dlc-sniff-");
        var extensionless = temp.Resolve("a-ha - Take on Me");
        DlcTestSupport.CopyFixture(fixtureName, extensionless);

        var container = DlcContainerDetector.SniffFile(extensionless);

        Assert.NotNull(container);
        Assert.Equal(expectedFormat, DlcContainerDetector.DisplayName(container.Format));
        Assert.Equal(expectedIsPs3, container.IsPs3);
        Assert.Equal(extensionless, container.Path);
    }

    [Theory]
    [InlineData("ps3-pkg.bin", ".pkg", "PS3 package")]
    [InlineData("ps3-edat.bin", ".mid.edat", "PS3 EDAT")]
    public void Ps3ContainersRequireTheirDocumentedExtension(
        string fixtureName,
        string extension,
        string expectedFormat)
    {
        using var temp = new TempDirectory("karaokay-dlc-ps3-");

        var withoutHint = temp.Resolve("payload");
        DlcTestSupport.CopyFixture(fixtureName, withoutHint);
        Assert.Null(DlcContainerDetector.SniffFile(withoutHint));

        var hinted = temp.Resolve("payload" + extension);
        DlcTestSupport.CopyFixture(fixtureName, hinted);
        var container = DlcContainerDetector.SniffFile(hinted);
        Assert.NotNull(container);
        Assert.Equal(expectedFormat, DlcContainerDetector.DisplayName(container.Format));
        Assert.True(container.IsPs3);
    }

    [Theory]
    [InlineData("plain-ogg.bin")]
    [InlineData("short.bin")]
    public void OrdinaryContentIsNotAContainer(string fixtureName)
    {
        Assert.Null(DlcContainerDetector.SniffFile(DlcTestSupport.FixturePath(fixtureName)));
    }

    [Fact]
    public void TwoExtensionlessContainersAreClassifiedInOrdinalOrder()
    {
        using var temp = new TempDirectory("karaokay-dlc-two-");
        DlcTestSupport.CopyFixture("stfs-con.bin", temp.Resolve("b - Second"));
        DlcTestSupport.CopyFixture("stfs-con.bin", temp.Resolve("a - First"));

        var detection = DlcContainerDetector.ClassifyDirectory(temp.Root);

        Assert.Equal(DlcInputKind.EncryptedContainers, detection.Kind);
        Assert.Equal(2, detection.Containers.Count);
        Assert.EndsWith("a - First", detection.Containers[0].Path, StringComparison.Ordinal);
        Assert.EndsWith("b - Second", detection.Containers[1].Path, StringComparison.Ordinal);
        Assert.All(detection.Containers, container => Assert.Equal(DlcContainerFormat.StfsCon, container.Format));
        Assert.Equal("2 encrypted containers", DlcContainerDetector.DescribeCount(detection.Containers));
        Assert.Equal("STFS CON x2", DlcContainerDetector.FormatKinds(detection.Containers));
    }

    [Fact]
    public void PlainSongFolderWinsOverAStrayContainer()
    {
        using var temp = new TempDirectory("karaokay-dlc-plain-wins-");
        var songDirectory = DlcTestSupport.WriteSongFolder(temp.Root, "song");
        DlcTestSupport.CopyFixture("stfs-con.bin", Path.Combine(songDirectory, "leftover-dlc"));

        var detection = DlcContainerDetector.ClassifyDirectory(songDirectory);

        Assert.Equal(DlcInputKind.PlainSongFolder, detection.Kind);
        Assert.Empty(detection.Containers);
    }

    [Fact]
    public void SidecarFilesAreIgnoredAndNeverCountAsContent()
    {
        using var temp = new TempDirectory("karaokay-dlc-sidecars-");

        var containerDirectory = temp.Resolve("with-container");
        Directory.CreateDirectory(containerDirectory);
        DlcTestSupport.CopyFixture("stfs-con.bin", Path.Combine(containerDirectory, "dlc"));
        File.WriteAllText(Path.Combine(containerDirectory, "dlc:Zone.Identifier"), "[ZoneTransfer]");
        File.WriteAllText(Path.Combine(containerDirectory, "dlc.Zone.Identifier"), "[ZoneTransfer]");
        File.WriteAllText(Path.Combine(containerDirectory, "._dlc"), "appledouble");
        File.WriteAllText(Path.Combine(containerDirectory, ".DS_Store"), "finder");
        File.WriteAllText(Path.Combine(containerDirectory, "Thumbs.db"), "windows");
        File.WriteAllText(Path.Combine(containerDirectory, "desktop.ini"), "shell");

        var detection = DlcContainerDetector.ClassifyDirectory(containerDirectory);

        Assert.Equal(DlcInputKind.EncryptedContainers, detection.Kind);
        Assert.Single(detection.Containers);

        var sidecarsOnly = temp.Resolve("sidecars-only");
        Directory.CreateDirectory(sidecarsOnly);
        File.WriteAllText(Path.Combine(sidecarsOnly, "whatever:Zone.Identifier"), "[ZoneTransfer]");
        File.WriteAllText(Path.Combine(sidecarsOnly, "whatever.Zone.Identifier"), "[ZoneTransfer]");
        File.WriteAllText(Path.Combine(sidecarsOnly, "._whatever"), "appledouble");
        File.WriteAllText(Path.Combine(sidecarsOnly, ".DS_Store"), "finder");

        Assert.Equal(DlcInputKind.Unknown, DlcContainerDetector.ClassifyDirectory(sidecarsOnly).Kind);
    }

    [Theory]
    [InlineData("MAIN.HDR")]
    [InlineData("main_xbox.hdr")]
    [InlineData("main_ps3.hdr")]
    public void ArkMarkersMakeADirectoryAContainer(string markerName)
    {
        using var temp = new TempDirectory("karaokay-dlc-ark-");
        File.WriteAllBytes(Path.Combine(temp.Root, markerName), []);

        var detection = DlcContainerDetector.ClassifyDirectory(temp.Root);

        Assert.Equal(DlcInputKind.EncryptedContainers, detection.Kind);
        var container = Assert.Single(detection.Containers);
        Assert.Equal(DlcContainerFormat.Ark, container.Format);
        Assert.False(container.IsPs3);
        Assert.Equal(temp.Root, container.Path);
    }

    [Fact]
    public void EmptyDirectoryAndUnknownFileAreUnknown()
    {
        using var temp = new TempDirectory("karaokay-dlc-unknown-");

        Assert.Equal(DlcInputKind.Unknown, DlcContainerDetector.ClassifyDirectory(temp.Root).Kind);
        Assert.Equal(
            DlcInputKind.Unknown,
            DlcContainerDetector.ClassifyFile(DlcTestSupport.FixturePath("plain-ogg.bin")).Kind);
    }

    [Fact]
    public void UnreadableOrMissingPathsDoNotThrow()
    {
        using var temp = new TempDirectory("karaokay-dlc-unreadable-");

        Assert.Null(DlcContainerDetector.SniffFile(temp.Root));
        Assert.Equal(DlcInputKind.Unknown, DlcContainerDetector.ClassifyDirectory(temp.Resolve("missing")).Kind);
        Assert.Equal(DlcInputKind.Unknown, DlcContainerDetector.ClassifyFile(temp.Resolve("missing.bin")).Kind);
        Assert.Equal(DlcInputKind.Unknown, DlcContainerDetector.Classify(temp.Resolve("missing")).Kind);
    }
}
