using System.Collections.Immutable;
using System.Text;

namespace MartenStudio.Services;

/// <summary>
/// Gives every collection, and every event type, a stable colour that is never mistaken for a status.
/// </summary>
/// <remarks>
/// <para>
/// A colour per collection is what makes a mixed list - a search result, an event feed, a dead-letter
/// queue - readable at a glance, and it has to be the <em>same</em> colour on every page and in every
/// session, or it is worse than no colour at all. So it is a pure function of the alias: FNV-1a over its
/// UTF-8 bytes, into a fixed ring. Nothing is stored, nothing is assigned at startup, and two processes
/// agree without talking.
/// </para>
/// <para>
/// The ring avoids two bands entirely: within 25 degrees of red (0) and of green (140). Those are the
/// studio's danger and success hues, and a collection that happens to hash to red is a collection that
/// looks like a failure on every screen it appears on.
/// </para>
/// </remarks>
internal static class CollectionColorizer
{
    /// <summary>The hue of the danger colour, which the ring stays away from.</summary>
    public const int DangerHue = 0;

    /// <summary>The hue of the success colour, which the ring stays away from.</summary>
    public const int SuccessHue = 140;

    /// <summary>How close to a status hue a collection hue is allowed to get.</summary>
    public const int StatusExclusionDegrees = 25;

    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>The twelve collection hues, in ring order.</summary>
    public static ImmutableArray<int> Hues { get; } =
        [35, 50, 65, 85, 100, 175, 195, 215, 235, 260, 285, 310];

    /// <summary>
    /// The twelve event-type hues: the same ring, rotated, so an event type and a collection that hash
    /// to the same slot still read as different things.
    /// </summary>
    public static ImmutableArray<int> EventTypeHues { get; } =
        [42, 57, 72, 92, 107, 182, 202, 222, 242, 267, 292, 317];

    /// <summary>The hue for a document collection's alias.</summary>
    public static int HueFor(string? alias) => Hues[SlotFor(alias)];

    /// <summary>The hue for an event type's name.</summary>
    public static int EventTypeHueFor(string? eventTypeName) => EventTypeHues[SlotFor(eventTypeName)];

    /// <summary>Which slot of the ring a name lands in.</summary>
    public static int SlotFor(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return 0;
        }

        return (int)(Fnv1a(name) % (uint)Hues.Length);
    }

    /// <summary>Whether a hue is far enough from both status hues to be used for a collection.</summary>
    public static bool IsSafeHue(int hue) =>
        CircularDistance(hue, DangerHue) > StatusExclusionDegrees &&
        CircularDistance(hue, SuccessHue) > StatusExclusionDegrees;

    /// <summary>The shortest distance between two hues on the 360-degree wheel.</summary>
    public static int CircularDistance(int left, int right)
    {
        var difference = Math.Abs(((left % 360) + 360) % 360 - (((right % 360) + 360) % 360));
        return Math.Min(difference, 360 - difference);
    }

    /// <summary>FNV-1a, 32-bit, over the UTF-8 bytes of <paramref name="value"/>.</summary>
    internal static uint Fnv1a(string value)
    {
        var hash = FnvOffsetBasis;
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Span<byte> buffer = byteCount <= 256 ? stackalloc byte[256] : new byte[byteCount];
        var written = Encoding.UTF8.GetBytes(value, buffer);

        for (var i = 0; i < written; i++)
        {
            hash ^= buffer[i];
            hash *= FnvPrime;
        }

        return hash;
    }
}
