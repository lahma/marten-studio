namespace MartenStudio.Services.Shapes;

/// <summary>
/// Every bound of the shape check, in one place (D-SHAPE S4). There is no option for any of them: a visitor cannot make a check
/// larger, and a host that wants a different bound has asked for a different feature.
/// </summary>
/// <remarks>
/// Worst case for one check: 201 index entries for the size probe, at most 200 index probes for the keys, at most 200 rows of
/// at most <see cref="MaxItemBytes" /> read once more by key, inside <see cref="Budget" />.
/// </remarks>
internal static class ShapeCheckLimits
{
    /// <summary>The most items one check reads: a collection (or event type) this small is read whole.</summary>
    public const int SampleSize = 200;

    /// <summary>How many of the lowest and of the highest keys a sampled document check reads.</summary>
    public const int Edge = 25;

    /// <summary>How many of the oldest and of the newest events of a type are read when no index leads with <c>type</c>.</summary>
    public const int EdgeWithoutTypeIndex = 100;

    /// <summary>How many probes spread across the collection a sampled check makes.</summary>
    public const int Spread = 150;

    /// <summary>The largest item (stored bytes for the keys, JSON text for the read) the check reads: 256 KiB.</summary>
    public const int MaxItemBytes = 262_144;

    /// <summary>The most members listed per type before "and N more".</summary>
    public const int MaxMemberFindings = 50;

    /// <summary>The most failure groups listed per type before "and N more".</summary>
    public const int MaxFailureGroups = 20;

    /// <summary>The longest path kept; a longer one is cut.</summary>
    public const int MaxPathLength = 256;

    /// <summary>The longest alias or event type name a check accepts; a longer one is answered as absent without a read.</summary>
    public const int MaxSubjectLength = 256;

    /// <summary>The server-side ceiling on one statement; the host's <c>QueryTimeout</c> applies when it is shorter.</summary>
    public static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The wall budget of one check; past it the result is partial, never a throw.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    /// <summary>How long a result is served: the <c>BoundedData</c> tier's five minutes.</summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromMinutes(5);

    /// <summary>How old a result has to be before "Check again" runs the check again.</summary>
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromSeconds(30);
}
