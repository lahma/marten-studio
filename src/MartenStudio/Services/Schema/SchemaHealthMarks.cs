using MartenStudio.Services.Health;

namespace MartenStudio.Services.Schema;

/// <summary>One mark on an Indexes tab row, from a health finding about that index.</summary>
/// <param name="Code">The finding's code.</param>
/// <param name="Severity">How much it matters.</param>
/// <param name="Label">What the flag says.</param>
/// <param name="Title">What hovering it says: the finding's own title.</param>
/// <param name="Link">The finding's card on the Health page.</param>
internal sealed record IndexHealthMark(string Code, HealthSeverity Severity, string Label, string Title, string Link);

/// <summary>
/// The Indexes tab's T03, T04 and T05 marks: the health advisor's findings about indexes, drawn on the rows
/// they are about.
/// </summary>
/// <remarks>
/// <para>
/// <b>From the advisor's facts, never from a second judgement.</b> The marks are the findings of the last
/// regular sweep, read through the cache-only <c>IHealthService.CachedFindingsAsync</c>, which never starts a
/// sweep and answers through the visitor's own gates - so the tab says exactly what the Health page says, and
/// costs nothing when nobody has the Health page or the Overview open. With nothing cached it draws no mark at
/// all: an absent mark is never a claim that the index is fine.
/// </para>
/// <para>
/// A finding about an index has the index's <c>schema.table.index</c> key as its <c>Subject</c>, which is
/// how a row finds its marks. T03's finding about a partitioned table's partitions is about no row on this
/// tab and is left out; it is on the Health page.
/// </para>
/// </remarks>
internal static class SchemaHealthMarks
{
    /// <summary>The codes the Indexes tab marks rows with.</summary>
    public static readonly string[] IndexCodes = ["T03", "T04", "T05"];

    /// <summary>Every mark, by the index key it belongs on.</summary>
    /// <param name="findings">The cached findings this visitor may see.</param>
    /// <param name="link">The Health page link of a finding.</param>
    public static IReadOnlyDictionary<string, IReadOnlyList<IndexHealthMark>> ForIndexes(
        IReadOnlyList<HealthReportFinding> findings,
        Func<HealthReportFinding, string> link)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(link);

        Dictionary<string, List<IndexHealthMark>> marks = new(StringComparer.OrdinalIgnoreCase);

        foreach (HealthReportFinding finding in findings)
        {
            if (finding.Finding.Subject is not { Length: > 0 } subject
                || Label(finding.Code) is not { } label)
            {
                continue;
            }

            if (!marks.TryGetValue(subject, out List<IndexHealthMark>? list))
            {
                list = [];
                marks[subject] = list;
            }

            list.Add(new IndexHealthMark(finding.Code, finding.Severity, label, finding.Finding.Title, link(finding)));
        }

        return marks.ToDictionary(
            static x => x.Key,
            static x => (IReadOnlyList<IndexHealthMark>) [.. x.Value.OrderBy(static m => m.Code, StringComparer.Ordinal)],
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The flag a code draws, or <see langword="null" /> for a code that marks no index row.</summary>
    internal static string? Label(string code) => code switch
    {
        "T03" => "invalid",
        "T04" => "redundant",
        "T05" => "unused, write-heavy",
        _ => null,
    };
}
