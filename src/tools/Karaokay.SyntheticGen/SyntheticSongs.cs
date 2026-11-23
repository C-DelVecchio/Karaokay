namespace Karaokay.SyntheticGen;

/// <summary>
/// Hardcoded score definitions for the three demo songs. Timing and layout never depend on the
/// seed; the seed only feeds a per-song <see cref="Random"/> used for cosmetic vocal pitch and
/// detune choices.
/// </summary>
internal static class SyntheticSongs
{
    private static readonly int[] TwinkleScale = [60, 62, 64, 65, 67, 69, 71, 72];
    private static readonly int[] TelegramScale = [57, 59, 60, 62, 64, 65, 67, 69];
    private static readonly int[] CanyonScale = [50, 53, 55, 57, 58, 60, 62, 65];

    private static readonly int[][] MajorProgression =
    [
        [60, 64, 67],
        [57, 60, 64],
        [53, 57, 60],
        [55, 59, 62],
    ];

    private static readonly int[][] MinorProgression =
    [
        [57, 60, 64],
        [53, 57, 60],
        [48, 52, 55],
        [55, 59, 62],
    ];

    private static readonly int[][] CanyonProgression =
    [
        [50, 53, 57],
        [46, 50, 53],
        [53, 57, 60],
        [48, 52, 55],
    ];

    /// <summary>Builds all three scores for <paramref name="seed"/>.</summary>
    public static IReadOnlyList<Score> Build(int seed) =>
        [BuildTwinkleVariations(seed), BuildSpokenTelegram(seed), BuildCountdownCanyon(seed)];

    /// <summary>
    /// Per-song RNG derived as <c>new Random(unchecked(seed * 397 + songIndex))</c>. The same
    /// expression is used for every run and platform, so cosmetic choices are reproducible.
    /// </summary>
    private static Random CreateRandom(int seed, int songIndex) =>
        new(unchecked((seed * 397) + songIndex));

    private static Score BuildTwinkleVariations(int seed)
    {
        var builder = new ScoreBuilder(
            slug: "synth-twinkle-variations",
            title: "Twinkle Variations",
            artist: "Public Domain Arr. K. Okay",
            tempoChanges:
            [
                new TempoChange(0, 120),
                new TempoChange(32 * 480L, 100),
                new TempoChange(60 * 480L, 75),
            ],
            scale: TwinkleScale,
            progression: MajorProgression,
            random: CreateRandom(seed, 0));

        // Explicit 105/106 markers; several adjacent phrases are separated by only 500 ms, so the
        // 1500 ms gap fallback would merge them. Markers must win.
        builder.AddPhrase(
            4,
            marker: true,
            new SyllableSpec("Twin", Connector: "-"),
            new SyllableSpec("kle"),
            new SyllableSpec("twin", Connector: "-"),
            new SyllableSpec("kle"),
            new SyllableSpec("lit", Connector: "-"),
            new SyllableSpec("tle"),
            new SyllableSpec("star", LineBreak: true));

        builder.AddPhrase(
            12,
            marker: true,
            new SyllableSpec("How"),
            new SyllableSpec("I"),
            new SyllableSpec("won", Connector: "+"),
            new SyllableSpec("der", LineBreak: true),
            new SyllableSpec("what", Beats: 2),
            new SyllableSpec("you"),
            new SyllableSpec("are", LineBreak: true));

        builder.AddPhrase(
            20,
            marker: true,
            new SyllableSpec("Up"),
            new SyllableSpec("a", Connector: "-"),
            new SyllableSpec("bove"),
            new SyllableSpec("the"),
            new SyllableSpec("world"),
            new SyllableSpec("so"),
            new SyllableSpec("high", LineBreak: true));

        builder.AddPhrase(
            28,
            marker: true,
            new SyllableSpec("Like"),
            new SyllableSpec("a"),
            new SyllableSpec("dia", Connector: "-"),
            new SyllableSpec("mond"),
            new SyllableSpec("in"),
            new SyllableSpec("the"),
            new SyllableSpec("sky", LineBreak: true));

        builder.AddPhrase(
            36,
            marker: true,
            new SyllableSpec("When"),
            new SyllableSpec("the"),
            new SyllableSpec("blaz", Connector: "-"),
            new SyllableSpec("ing"),
            new SyllableSpec("sun"),
            new SyllableSpec("is"),
            new SyllableSpec("gone", LineBreak: true));

        builder.AddPhrase(
            44,
            marker: true,
            new SyllableSpec("When"),
            new SyllableSpec("he"),
            new SyllableSpec("noth", Connector: "-"),
            new SyllableSpec("ing"),
            new SyllableSpec("shines"),
            new SyllableSpec("up", Connector: "-"),
            new SyllableSpec("on", LineBreak: true));

        builder.AddPhrase(
            52,
            marker: true,
            new SyllableSpec("Then"),
            new SyllableSpec("you"),
            new SyllableSpec("show"),
            new SyllableSpec("your"),
            new SyllableSpec("lit", Connector: "-"),
            new SyllableSpec("tle"),
            new SyllableSpec("light", LineBreak: true));

        builder.AddPhrase(
            60,
            marker: true,
            new SyllableSpec("Twin", Connector: "-"),
            new SyllableSpec("kle"),
            new SyllableSpec("twin", Connector: "-"),
            new SyllableSpec("kle"),
            new SyllableSpec("through"),
            new SyllableSpec("the"),
            new SyllableSpec("night", LineBreak: true));

        return builder.Build();
    }

    private static Score BuildSpokenTelegram(int seed)
    {
        var builder = new ScoreBuilder(
            slug: "synth-spoken-telegram",
            title: "The Spoken Telegram",
            artist: "Public Domain Arr. K. Okay",
            tempoChanges: [new TempoChange(0, 120)],
            scale: TelegramScale,
            progression: MinorProgression,
            random: CreateRandom(seed, 1));

        // No 105/106 markers: phrases are separated by ≥2000 ms silences so the parser's
        // >1500 ms gap fallback groups them.
        builder.AddPhrase(
            4,
            marker: false,
            new SyllableSpec("at", Connector: "-", Talky: true),
            new SyllableSpec("ten", Connector: "-", Talky: true),
            new SyllableSpec("tion", Talky: true, LineBreak: true));

        builder.AddPhrase(
            11,
            marker: false,
            new SyllableSpec("ev", Connector: "-", Talky: true),
            new SyllableSpec("ery", Connector: "-", Talky: true),
            new SyllableSpec("one", Talky: true, LineBreak: true),
            new SyllableSpec("this", Talky: true),
            new SyllableSpec("tel", Connector: "-", Talky: true),
            new SyllableSpec("e", Connector: "-", Talky: true),
            new SyllableSpec("gram", Talky: true),
            new SyllableSpec("is", Talky: true),
            new SyllableSpec("for", Talky: true),
            new SyllableSpec("you", Talky: true, LineBreak: true));

        builder.AddPhrase(
            25,
            marker: false,
            new SyllableSpec("Please"),
            new SyllableSpec("lis", Connector: "-"),
            new SyllableSpec("ten"),
            new SyllableSpec("close", Connector: "-"),
            new SyllableSpec("ly"),
            new SyllableSpec("now", LineBreak: true));

        builder.AddPhrase(
            35,
            marker: false,
            new SyllableSpec("mes", Connector: "-", Talky: true),
            new SyllableSpec("sage", Talky: true),
            new SyllableSpec("from", Talky: true, LineBreak: true),
            new SyllableSpec("a", Talky: true),
            new SyllableSpec("far", Connector: "+", Talky: true),
            new SyllableSpec("a", Connector: "-", Talky: true),
            new SyllableSpec("way", Talky: true),
            new SyllableSpec("land", Talky: true, LineBreak: true));

        builder.AddPhrase(
            47,
            marker: false,
            new SyllableSpec("send", Talky: true),
            new SyllableSpec("your", Talky: true),
            new SyllableSpec("an", Connector: "-", Talky: true),
            new SyllableSpec("swers", Talky: true),
            new SyllableSpec("by", Talky: true, LineBreak: true),
            new SyllableSpec("tel", Connector: "-", Talky: true),
            new SyllableSpec("e", Connector: "-", Talky: true),
            new SyllableSpec("graph", Talky: true),
            new SyllableSpec("to", Connector: "-", Talky: true),
            new SyllableSpec("night", Talky: true, LineBreak: true));

        builder.AddPhrase(
            61,
            marker: false,
            new SyllableSpec("that", Talky: true),
            new SyllableSpec("is", Talky: true),
            new SyllableSpec("all", Talky: true),
            new SyllableSpec("i", Talky: true),
            new SyllableSpec("have", Talky: true),
            new SyllableSpec("to", Talky: true),
            new SyllableSpec("say", Talky: true, LineBreak: true),
            new SyllableSpec("so", Talky: true),
            new SyllableSpec("good", Connector: "-", Talky: true),
            new SyllableSpec("night", Talky: true, LineBreak: true));

        return builder.Build();
    }

    private static Score BuildCountdownCanyon(int seed)
    {
        var builder = new ScoreBuilder(
            slug: "synth-countdown-canyon",
            title: "Countdown Canyon",
            artist: "Public Domain Arr. K. Okay",
            tempoChanges: [new TempoChange(0, 120)],
            scale: CanyonScale,
            progression: CanyonProgression,
            random: CreateRandom(seed, 2));

        // No markers. The 8-beat (4000 ms) break after beat 28 and the 6-beat (3000 ms) break
        // after beat 62 both exercise the gap fallback plus a 4-dot countdown.
        builder.AddPhrase(
            4,
            marker: false,
            new SyllableSpec("down"),
            new SyllableSpec("the"),
            new SyllableSpec("can", Connector: "-"),
            new SyllableSpec("yon"),
            new SyllableSpec("we"),
            new SyllableSpec("go"),
            new SyllableSpec("now", LineBreak: true));

        builder.AddPhrase(
            12,
            marker: false,
            new SyllableSpec("four"),
            new SyllableSpec("beats"),
            new SyllableSpec("un", Connector: "-"),
            new SyllableSpec("til"),
            new SyllableSpec("the"),
            new SyllableSpec("next"),
            new SyllableSpec("line", LineBreak: true));

        builder.AddPhrase(
            20,
            marker: false,
            new SyllableSpec("count"),
            new SyllableSpec("the"),
            new SyllableSpec("ech", Connector: "-"),
            new SyllableSpec("oes"),
            new SyllableSpec("off"),
            new SyllableSpec("the"),
            new SyllableSpec("can", Connector: "-"),
            new SyllableSpec("yon", LineBreak: true));

        builder.AddPhrase(
            36,
            marker: false,
            new SyllableSpec("ev", Connector: "-"),
            new SyllableSpec("ery"),
            new SyllableSpec("beat"),
            new SyllableSpec("is"),
            new SyllableSpec("a"),
            new SyllableSpec("step"),
            new SyllableSpec("to", Connector: "-"),
            new SyllableSpec("night", LineBreak: true));

        builder.AddPhrase(
            45,
            marker: false,
            new SyllableSpec("hear"),
            new SyllableSpec("the"),
            new SyllableSpec("riv", Connector: "-"),
            new SyllableSpec("er"),
            new SyllableSpec("an", Connector: "-"),
            new SyllableSpec("swer"),
            new SyllableSpec("back"),
            new SyllableSpec("now", LineBreak: true));

        builder.AddPhrase(
            54,
            marker: false,
            new SyllableSpec("one"),
            new SyllableSpec("two"),
            new SyllableSpec("three"),
            new SyllableSpec("four"),
            new SyllableSpec("count"),
            new SyllableSpec("a", Connector: "-"),
            new SyllableSpec("gain"),
            new SyllableSpec("slow", LineBreak: true));

        builder.AddPhrase(
            68,
            marker: false,
            new SyllableSpec("down"),
            new SyllableSpec("the"),
            new SyllableSpec("can", Connector: "-"),
            new SyllableSpec("yon"),
            new SyllableSpec("we"),
            new SyllableSpec("go"),
            new SyllableSpec("home", LineBreak: true));

        return builder.Build();
    }
}
