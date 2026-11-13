using System.Globalization;

namespace KStudio.Core.Index;

/// <summary>What the host's record index did for one search plan (AGENTS.md D48).</summary>
internal enum KafkaIndexOutcome
{
    /// <summary>The index named ranges, and the plan reads only those (inside the window).</summary>
    Narrowed,

    /// <summary>The index answered, but no hint named anything the plan reads: the whole window is read.</summary>
    PointedNowhere,

    /// <summary>The index threw, timed out or could not be created: the whole window is read.</summary>
    DidNotAnswer,
}

/// <summary>One partition's part of a narrowed plan.</summary>
/// <param name="Topic">The topic.</param>
/// <param name="Partition">The partition.</param>
/// <param name="From">The first offset read.</param>
/// <param name="To">One past the last offset read.</param>
/// <param name="WindowFrom">The first offset the plan would have read without the index.</param>
/// <param name="WindowTo">One past the last offset the plan would have read without the index.</param>
internal sealed record KafkaIndexRange(string Topic, int Partition, long From, long To, long WindowFrom, long WindowTo)
{
    /// <summary>How many offsets are read.</summary>
    public long Offsets => To - From;
}

/// <summary>
/// What the host's record index did to a search plan, with the engine's own sentences about it (the pages show them; they never build one):
/// the ranges the plan was narrowed to, how much of the window that is, and what a negative may then say. A hint only ever narrows, so a match
/// is always a match the scan found, never a hint the index gave.
/// </summary>
internal sealed class KafkaIndexNarrowing
{
    /// <summary>The note beside a plan whose hints named nothing the plan reads.</summary>
    public const string PointedNowhereNote = "The host's record index pointed nowhere in this window; the whole window is read.";

    /// <summary>The note beside a plan whose index did not answer.</summary>
    public const string DidNotAnswerNote = "The host's record index did not answer; the whole window is read.";

    private KafkaIndexNarrowing(KafkaIndexOutcome outcome, IReadOnlyList<KafkaIndexRange> ranges, int hintsUsed, int hintsNotUsed, long offsetsInWindow)
    {
        Outcome = outcome;
        Ranges = ranges;
        HintsUsed = hintsUsed;
        HintsNotUsed = hintsNotUsed;
        OffsetsInWindow = offsetsInWindow;
    }

    /// <summary>What the index did.</summary>
    public KafkaIndexOutcome Outcome { get; }

    /// <summary>Whether the plan reads less than its window because of the index.</summary>
    public bool IsNarrowed => Outcome == KafkaIndexOutcome.Narrowed;

    /// <summary>The ranges read, per partition, ascending as the plan lists its partitions; empty unless <see cref="IsNarrowed" />.</summary>
    public IReadOnlyList<KafkaIndexRange> Ranges { get; }

    /// <summary>How many hints made the ranges.</summary>
    public int HintsUsed { get; }

    /// <summary>How many hints that were about the search's topics and partitions were left unused because the index named more than the engine uses.</summary>
    public int HintsNotUsed { get; }

    /// <summary>The offsets the plan would have read without the index.</summary>
    public long OffsetsInWindow { get; }

    /// <summary>The offsets the narrowed plan reads.</summary>
    public long OffsetsRead => Ranges.Sum(static x => x.Offsets);

    /// <summary>The sentence for the plan's notes, before the search runs: what the index did to the plan.</summary>
    public string Note => Outcome switch
    {
        KafkaIndexOutcome.PointedNowhere => PointedNowhereNote,
        KafkaIndexOutcome.DidNotAnswer => DidNotAnswerNote,
        _ => string.Create(
            CultureInfo.InvariantCulture,
            $"The host's record index narrowed this search to {Ranges.Count:N0} {(Ranges.Count == 1 ? "range" : "ranges")} ({OffsetsRead:N0} of {OffsetsInWindow:N0} offsets of the window)."),
    };

    /// <summary>The sentence beside a narrowed result's matches: what the search did not look for.</summary>
    public string? Caveat => !IsNarrowed
        ? null
        : string.Create(
            CultureInfo.InvariantCulture,
            $"Read only where the host's record index pointed ({OffsetsRead:N0} of {OffsetsInWindow:N0} offsets); other matches in the window were not looked for.{NotUsed}");

    /// <summary>The sentence of a narrowed result with no match: a negative is said only of the ranges read.</summary>
    public string? NoMatch => !IsNarrowed
        ? null
        : Ranges.Count == 1
            ? string.Create(CultureInfo.InvariantCulture, $"No match in the range the host's record index named ({Offsets(OffsetsRead)}). The rest of the window was not read.")
            : string.Create(CultureInfo.InvariantCulture, $"No match in the {Ranges.Count:N0} ranges the host's record index named ({Offsets(OffsetsRead)}). The rest of the window was not read.");

    /// <summary>What the audit message adds: <c>; narrowed by the host's record index to 2 ranges of orders (60 offsets)</c>; empty when the index narrowed nothing.</summary>
    public string AuditSuffix
    {
        get
        {
            if (!IsNarrowed)
            {
                return string.Empty;
            }

            string[] topics = [.. Ranges.Select(static x => x.Topic).Distinct(StringComparer.Ordinal)];
            string named = topics.Length switch
            {
                1 => topics[0],
                _ => string.Join(", ", topics[..^1]) + " and " + topics[^1],
            };

            return string.Create(
                CultureInfo.InvariantCulture,
                $"; narrowed by the host's record index to {Ranges.Count:N0} {(Ranges.Count == 1 ? "range" : "ranges")} of {named} ({Offsets(OffsetsRead)})");
        }
    }

    private string NotUsed => HintsNotUsed == 0
        ? string.Empty
        : string.Create(CultureInfo.InvariantCulture, $" {HintsNotUsed:N0} further {(HintsNotUsed == 1 ? "hint was" : "hints were")} not used.");

    /// <summary>An index that narrowed a plan.</summary>
    /// <param name="ranges">The partitions read.</param>
    /// <param name="hintsUsed">How many hints made them.</param>
    /// <param name="hintsNotUsed">How many hints were left unused.</param>
    /// <param name="offsetsInWindow">What the plan would have read without the index.</param>
    public static KafkaIndexNarrowing Narrowed(IReadOnlyList<KafkaIndexRange> ranges, int hintsUsed, int hintsNotUsed, long offsetsInWindow) =>
        new(KafkaIndexOutcome.Narrowed, ranges, hintsUsed, hintsNotUsed, offsetsInWindow);

    /// <summary>An index whose hints named nothing the plan reads.</summary>
    /// <param name="offsetsInWindow">What the plan reads.</param>
    public static KafkaIndexNarrowing PointedNowhere(long offsetsInWindow) => new(KafkaIndexOutcome.PointedNowhere, [], 0, 0, offsetsInWindow);

    /// <summary>An index that did not answer.</summary>
    /// <param name="offsetsInWindow">What the plan reads.</param>
    public static KafkaIndexNarrowing DidNotAnswer(long offsetsInWindow) => new(KafkaIndexOutcome.DidNotAnswer, [], 0, 0, offsetsInWindow);

    /// <summary>A count of offsets as a sentence writes it: <c>1 offset</c>, <c>60 offsets</c>.</summary>
    /// <param name="count">The count.</param>
    public static string Offsets(long count) => string.Create(CultureInfo.InvariantCulture, $"{count:N0} {(count == 1 ? "offset" : "offsets")}");
}
