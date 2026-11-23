using System.Text;
using Karaokay.Application;

namespace Karaokay.UnitTests;

/// <summary>Locks the 24-letter room-code alphabet and the collision-retry contract (§4.2).</summary>
public sealed class RoomCodeGeneratorTests
{
    private static readonly char[] ForbiddenLetters = ['I', 'O', 'Q'];

    [Fact]
    public void AlphabetIsTheDocumentedUnambiguousLetters()
    {
        // Docs/ARCHITECTURE.md §4.2 prints this exact 23-letter string (26 letters minus I, O, Q).
        // The prose also says "24 letters"/"331,776 codes", which is an arithmetic slip: no
        // fourth excluded letter exists. The alphabet literal is authoritative.
        Assert.Equal("ABCDEFGHJKLMNPRSTUVWXYZ", RoomCodeGenerator.Alphabet);
        Assert.Equal(26 - 3, RoomCodeGenerator.Alphabet.Length);
        Assert.Equal(RoomCodeGenerator.Alphabet.Length, RoomCodeGenerator.Alphabet.Distinct().Count());
        Assert.DoesNotContain(RoomCodeGenerator.Alphabet, character => char.IsDigit(character));
        foreach (var forbidden in ForbiddenLetters)
        {
            Assert.DoesNotContain(forbidden, RoomCodeGenerator.Alphabet);
        }

        Assert.All(RoomCodeGenerator.Alphabet, character => Assert.InRange(character, 'A', 'Z'));
        Assert.Equal(
            RoomCodeGenerator.Alphabet,
            new string([.. RoomCodeGenerator.Alphabet.OrderBy(character => character)]));
    }

    [Fact]
    public void GeneratedCodesAreFourCharactersFromTheAlphabet()
    {
        var generator = new RoomCodeGenerator(new Random(7));

        var codes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 2000; i++)
        {
            var code = generator.GenerateNewCode();
            Assert.Equal(RoomCodeGenerator.CodeLength, code.Length);
            Assert.All(code, character => Assert.Contains(character, RoomCodeGenerator.Alphabet));
            codes.Add(code);
        }

        Assert.Equal(2000, codes.Count);
    }

    [Fact]
    public void SeededCollisionRetriesWithAFreshCode()
    {
        const int seed = 12345;

        // Replay the exact sequence the generator will draw: four Alphabet[random.Next(24)] picks.
        var replay = new Random(seed);
        var firstCode = BuildCode(replay);
        var secondCode = BuildCode(replay);
        Assert.NotEqual(firstCode, secondCode);

        var generator = new RoomCodeGenerator(new Random(seed), [firstCode]);

        var generated = generator.GenerateNewCode();

        Assert.NotEqual(firstCode, generated);
        Assert.Equal(secondCode, generated);
    }

    [Fact]
    public void ExhaustedActiveSetThrowsAfterBoundedRetries()
    {
        var allCodes = BuildAllCodes();
        Assert.Equal(279_841, allCodes.Count); // 23^4 codes in the documented alphabet.

        var generator = new RoomCodeGenerator(new Random(1), allCodes);

        var exception = Assert.Throws<InvalidOperationException>(() => generator.GenerateNewCode());
        Assert.Contains("attempts", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildCode(Random random)
    {
        var builder = new StringBuilder(RoomCodeGenerator.CodeLength);
        for (var i = 0; i < RoomCodeGenerator.CodeLength; i++)
        {
            builder.Append(RoomCodeGenerator.Alphabet[random.Next(RoomCodeGenerator.Alphabet.Length)]);
        }

        return builder.ToString();
    }

    private static HashSet<string> BuildAllCodes()
    {
        var alphabet = RoomCodeGenerator.Alphabet;
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var first in alphabet)
        {
            foreach (var second in alphabet)
            {
                foreach (var third in alphabet)
                {
                    foreach (var fourth in alphabet)
                    {
                        codes.Add(new string([first, second, third, fourth]));
                    }
                }
            }
        }

        return codes;
    }
}
