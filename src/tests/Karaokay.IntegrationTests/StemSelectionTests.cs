using Karaokay.Infrastructure.MediaIngest;

namespace Karaokay.IntegrationTests;

/// <summary>
/// Pure, fast checks on the stem-selection and FFmpeg filter-graph builders. These are the pieces
/// the importer uses to decide what enters <c>instrumental.opus</c>; crowd noise and guide vocals
/// must never leak into the instrumental mix.
/// </summary>
public sealed class StemSelectionTests
{
    private static readonly string[] ExpectedInstrumentalStems = ["bass.ogg", "drums.ogg", "guitar.ogg", "keys.ogg", "rhythm.ogg"];
    private static readonly string[] ExpectedVocalStems = ["vocals.ogg", "vocals_1.ogg"];
    private static readonly string[] Listing =
    [
        "guitar.ogg",
        "drums.ogg",
        "bass.ogg",
        "rhythm.ogg",
        "keys.ogg",
        "crowd.ogg",
        "CROWD_1.ogg",
        "vocals.ogg",
        "vocals_1.ogg",
        "cover.png",
        "notes.mid",
    ];

    [Fact]
    public void InstrumentalSelectionKeepsOnlyNonCrowdNonVocalAudioStems()
    {
        var selected = StemMixer.SelectInstrumentalStems(Listing);

        Assert.Equal(
            ExpectedInstrumentalStems,
            selected.Select(path => Path.GetFileName(path)!));
        Assert.DoesNotContain(selected, path => Path.GetFileName(path)!.StartsWith("crowd", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(selected, path => Path.GetFileName(path)!.StartsWith("vocals", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(selected, path => Path.GetExtension(path) is not (".ogg" or ".oga" or ".opus"));
    }

    [Fact]
    public void VocalSelectionKeepsOnlyVocalsAudioStems()
    {
        var selected = StemMixer.SelectVocalStems(Listing);

        Assert.Equal(
            ExpectedVocalStems,
            selected.Select(path => Path.GetFileName(path)!));
    }

    [Fact]
    public void InstrumentalFilterGraphMixesExactlyTheSelectedStemCount()
    {
        var stems = StemMixer.SelectInstrumentalStems(Listing);
        var graph = StemMixer.BuildInstrumentalFilterGraph(stems.Count);

        Assert.Contains($"amix=inputs={stems.Count}", graph, StringComparison.Ordinal);
        Assert.Contains("duration=longest", graph, StringComparison.Ordinal);
        Assert.DoesNotContain("crowd", graph, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("vocals", graph, StringComparison.OrdinalIgnoreCase);

        // The argument builder wires every selected stem as an input and nothing else.
        var arguments = StemMixer.BuildMeasureArguments(stems, graph);
        Assert.Equal(stems.Count, arguments.Count(argument => argument == "-i"));
        Assert.DoesNotContain(
            arguments,
            argument => argument.Contains("crowd", StringComparison.OrdinalIgnoreCase)
                || argument.Contains("vocals", StringComparison.OrdinalIgnoreCase));
        foreach (var stem in stems)
        {
            Assert.Contains(stem, arguments);
        }
    }
}
