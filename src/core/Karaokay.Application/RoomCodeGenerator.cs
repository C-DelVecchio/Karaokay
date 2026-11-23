namespace Karaokay.Application;

/// <summary>
/// Generates the 4-letter party room codes used by the stage/guest join flow
/// (docs/ARCHITECTURE.md §4.2). The alphabet deliberately omits <c>I</c>, <c>O</c> and <c>Q</c>
/// so codes can never be confused with digits when read aloud or typed on a phone.
/// </summary>
/// <remarks>
/// The generator is the in-memory half of the collision guard; the database unique index remains
/// the backstop described by the architecture. Generated codes are reserved in the generator's
/// own copy of the active set so two calls on one instance can never return the same code.
/// </remarks>
public sealed class RoomCodeGenerator
{
    /// <summary>23 unambiguous uppercase letters (26 minus <c>I</c>/<c>O</c>/<c>Q</c>).</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPRSTUVWXYZ";

    /// <summary>Room codes are always four characters long.</summary>
    public const int CodeLength = 4;

    /// <summary>Bounded retry count before the generator gives up on a saturated active set.</summary>
    public const int MaxAttempts = 100;

    private readonly Random _random;
    private readonly HashSet<string> _activeCodes;

    /// <param name="random">
    /// Source of randomness; supply a seeded instance in tests. Defaults to <see cref="Random.Shared"/>.
    /// </param>
    /// <param name="activeCodes">Codes already in use; the generator never returns one of them.</param>
    public RoomCodeGenerator(Random? random = null, IReadOnlyCollection<string>? activeCodes = null)
    {
        _random = random ?? Random.Shared;
        _activeCodes = activeCodes is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(activeCodes, StringComparer.Ordinal);
    }

    /// <summary>
    /// Returns a four-character code that is not present in the active set. Retries with a fresh
    /// code for at most <see cref="MaxAttempts"/> attempts, then throws when the space is saturated.
    /// </summary>
    /// <exception cref="InvalidOperationException">No free code was found within the retry budget.</exception>
    public string GenerateNewCode()
    {
        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            var code = GenerateCode();
            if (_activeCodes.Add(code))
            {
                return code;
            }
        }

        throw new InvalidOperationException(
            $"Could not generate a free {CodeLength}-character room code after {MaxAttempts} attempts; " +
            "the active-code set appears to be exhausted.");
    }

    private string GenerateCode()
    {
        Span<char> characters = stackalloc char[CodeLength];
        for (var i = 0; i < characters.Length; i++)
        {
            characters[i] = Alphabet[_random.Next(Alphabet.Length)];
        }

        return new string(characters);
    }
}
