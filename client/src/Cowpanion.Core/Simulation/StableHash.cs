using System.Text;

namespace Cowpanion.Core.Simulation;

/// <summary>
/// FNV-1a 64-bit. Stable across processes, machines and .NET versions, unlike <see cref="string.GetHashCode()"/>
/// which is randomised per process. Used for anything that must be "the same cow everywhere".
/// </summary>
public static class StableHash
{
    private const ulong OffsetBasis = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Fnv1a64(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        ulong hash = OffsetBasis;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= Prime;
        }
        return hash;
    }

    /// <summary>Returns bits [shift, shift+16) of the hash mapped to [0, 1).</summary>
    public static double Unit16(ulong hash, int shift)
    {
        ulong chunk = (hash >> shift) & 0xFFFFUL;
        return chunk / 65536.0;
    }
}
