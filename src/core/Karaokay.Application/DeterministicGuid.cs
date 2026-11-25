using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;

namespace Karaokay.Application;

/// <summary>
/// RFC 4122 version-5 (SHA-1 name-based) deterministic GUIDs. Used for demo song ids so the same
/// media folder always maps to the same database row across machines, restarts, and CI runs
/// (docs/ARCHITECTURE.md §7, Slice 3 song seeding).
/// </summary>
public static class DeterministicGuid
{
    /// <summary>Standard RFC 4122 DNS namespace, used as the default parent namespace.</summary>
    public static readonly Guid DnsNamespace = new("6ba7b810-9dad-11d1-80b4-00c04fd430c8");

    /// <summary>Creates a version-5 GUID for <paramref name="name"/> inside <paramref name="namespaceId"/>.</summary>
    [SuppressMessage(
        "Security",
        "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 4122 version 5 name-based UUIDs are specified in terms of SHA-1; this is an identifier, not a security primitive.")]
    public static Guid Create(Guid namespaceId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var namespaceBytes = namespaceId.ToByteArray();
        SwapByteOrder(namespaceBytes);

        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(input, 0);
        nameBytes.CopyTo(input, namespaceBytes.Length);

        var hash = SHA1.HashData(input);
        var guidBytes = hash[..16];

        // Stamp version 5 and the RFC 4122 variant bits.
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        SwapByteOrder(guidBytes);
        return new Guid(guidBytes);
    }

    /// <summary>Creates a version-5 GUID in the standard DNS namespace.</summary>
    public static Guid Create(string name) => Create(DnsNamespace, name);

    private static void SwapByteOrder(Span<byte> guid)
    {
        // Guid.ToByteArray stores the first three fields little-endian; RFC 4122 hashes the
        // big-endian network form, so swap them in and back out.
        (guid[0], guid[3]) = (guid[3], guid[0]);
        (guid[1], guid[2]) = (guid[2], guid[1]);
        (guid[4], guid[5]) = (guid[5], guid[4]);
        (guid[6], guid[7]) = (guid[7], guid[6]);
    }
}
