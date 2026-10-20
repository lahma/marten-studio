using System.Globalization;

using JasperFx.Core.Reflection;

using Marten.Schema;

using Weasel.Core.Sequences;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Schema;

namespace MartenStudio.Services.Health.Checks;

/// <summary>One HiLo sequence that hands out <c>int</c> ids, and the document types that draw from it.</summary>
/// <param name="EntityName">Its row in <c>mt_hilo</c>: <c>HiloSettings.SequenceName</c>, or the document type's name.</param>
/// <param name="MaxLo">How many ids one "hi" covers - the largest of the types that share the row.</param>
/// <param name="DocumentTypes">The visible document types with an <c>int</c> id (or a strong-typed one wrapping an <c>int</c>) that draw from it.</param>
internal sealed record HiloSequenceFacts(string EntityName, int MaxLo, IReadOnlyList<IDocumentType> DocumentTypes);

/// <summary>What T11 read.</summary>
/// <param name="Schema">The store's document schema, where <c>mt_hilo</c> lives.</param>
/// <param name="Sequences">The int-id sequences, from the mappings alone.</param>
/// <param name="HiloTableExists">Whether <c>mt_hilo</c> exists - read from the catalog, never created.</param>
/// <param name="Rows">Its rows for those sequences.</param>
internal sealed record HiloHeadroomFacts(
    string Schema,
    IReadOnlyList<HiloSequenceFacts> Sequences,
    bool HiloTableExists,
    IReadOnlyList<HiloRow> Rows);

/// <summary>
/// T11: a HiLo sequence handing out <c>int</c> ids is far through <c>int.MaxValue</c>.
/// </summary>
/// <remarks>
/// <para>
/// Marten gives an <c>int</c> or <c>long</c> id (and a strong-typed id wrapping one) from a HiLo sequence: one
/// row per sequence in <c>mt_hilo</c>, whose <c>hi_value</c> is the last block handed out, each block
/// <c>MaxLo</c> ids wide - so ids up to <c>(hi_value + 1) × MaxLo</c> may already be issued
/// (<c>mt_get_next_hi</c> and Weasel's <c>HiloSequenceBase</c>, verified at V9.39.0). An <c>int</c> id is
/// <c>(int)NextLong()</c>: past <c>int.MaxValue</c> it wraps to a negative number silently, so the headroom is
/// worth knowing about long before then. A <c>long</c> id, and an identity key (which draws a <c>long</c>), is not
/// judged.
/// </para>
/// <para>
/// <b>Hard rule 14's own trap, kept shut.</b> Which types use HiLo comes from the mappings
/// (<c>IDocumentType.IdType</c>, <c>IdStrategy</c>, <c>DocumentMapping.HiloSettings</c> and
/// <c>StoreOptions.Advanced.HiloSequenceDefaults</c>, the same rule <c>SequenceFactory.SequenceFor</c> applies),
/// never from <c>database.Sequences</c>, whose first touch creates <c>mt_hilo</c>. The table is read only after
/// the catalog says it exists; an absent one means no HiLo id has been issued yet, which is a value, not an
/// error.
/// </para>
/// </remarks>
[HealthCheck("T11", HealthArea.Tables, HealthCostTier.Catalog, "Numeric id headroom")]
internal sealed class HiloHeadroomCheck : HealthCheck<HiloHeadroomFacts>
{
    /// <summary>The share of <c>int.MaxValue</c> past which the check warns.</summary>
    public const double WarningShare = 0.5;

    /// <summary>The share of <c>int.MaxValue</c> past which the finding is Critical.</summary>
    public const double CriticalShare = 0.8;

    /// <inheritdoc />
    public override async Task<HiloHeadroomFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        string schema = context.StoreOptions.DatabaseSchemaName;
        IReadOnlyList<HiloSequenceFacts> sequences = IntSequences(context);

        if (sequences.Count == 0)
        {
            // No statement at all: nothing this store shows draws an int id from HiLo.
            return new HiloHeadroomFacts(schema, [], false, []);
        }

        bool exists = await context.Sql.ScalarAsync<bool>(HealthTableQueries.HiloExists(schema), cancellationToken).ConfigureAwait(false);

        if (!exists)
        {
            return new HiloHeadroomFacts(schema, sequences, false, []);
        }

        IReadOnlyList<HiloRow> rows = await context.Sql
            .QueryAsync(
                HealthTableQueries.HiloValues(schema, [.. sequences.Select(static x => x.EntityName)]),
                HealthTableQueries.ReadHilo,
                cancellationToken)
            .ConfigureAwait(false);

        return new HiloHeadroomFacts(schema, sequences, true, rows);
    }

    /// <summary>
    /// The HiLo sequences that hand out <c>int</c> ids to a visible document type, keyed as <c>mt_hilo</c>
    /// keys them.
    /// </summary>
    internal static IReadOnlyList<HiloSequenceFacts> IntSequences(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyHiloSettings defaults = context.StoreOptions.Advanced.HiloSequenceDefaults;
        Dictionary<string, (int MaxLo, List<IDocumentType> Types)> byName = new(StringComparer.Ordinal);

        foreach (IDocumentType documentType in context.StoreOptions.AllKnownDocumentTypes())
        {
            if (!context.IsDocumentTypeVisible(documentType.DocumentType) || !DrawsIntIds(documentType))
            {
                continue;
            }

            IReadOnlyHiloSettings settings = (documentType as DocumentMapping)?.HiloSettings ?? defaults;
            string name = string.IsNullOrEmpty(settings.SequenceName) ? documentType.DocumentType.Name : settings.SequenceName;

            if (!byName.TryGetValue(name, out (int MaxLo, List<IDocumentType> Types) entry))
            {
                entry = (settings.MaxLo, []);
            }

            entry.Types.Add(documentType);
            byName[name] = (Math.Max(entry.MaxLo, settings.MaxLo), entry.Types);
        }

        return [.. byName
            .OrderBy(static x => x.Key, StringComparer.Ordinal)
            .Select(static x => new HiloSequenceFacts(x.Key, x.Value.MaxLo, x.Value.Types))];
    }

    /// <summary>
    /// Whether Marten assigns this type's ids as <c>int</c>s from HiLo: an <c>int</c> id, or a strong-typed id
    /// whose inner value is an <c>int</c> - the two cases Weasel's identification calls <c>NextInt()</c> for.
    /// </summary>
    internal static bool DrawsIntIds(IDocumentType documentType)
    {
        ArgumentNullException.ThrowIfNull(documentType);

        return documentType.IdType == typeof(int)
            || (documentType.IdStrategy is ValueTypeInfo valueType && valueType.SimpleType == typeof(int));
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(HiloHeadroomFacts facts, HealthEvaluation evaluation)
    {
        if (facts.Sequences.Count == 0)
        {
            return HealthVerdict.Quiet("No document type this store shows has an int id drawn from HiLo.");
        }

        if (!facts.HiloTableExists)
        {
            return HealthVerdict.Quiet(
                "mt_hilo does not exist in " + facts.Schema + " yet, so no HiLo id has been issued. The advisor reads it only once it is there.");
        }

        Dictionary<string, HiloRow> rows = facts.Rows.ToDictionary(static x => x.EntityName, StringComparer.Ordinal);
        List<HealthFinding> findings = [];

        foreach (HiloSequenceFacts sequence in facts.Sequences)
        {
            if (!rows.TryGetValue(sequence.EntityName, out HiloRow? row))
            {
                continue;
            }

            double issued = (row.HiValue + 1.0) * sequence.MaxLo;
            double share = issued / int.MaxValue;

            if (share >= WarningShare)
            {
                findings.Add(Finding(sequence, row, issued, share));
            }
        }

        return findings.Count == 0 ? HealthVerdict.Quiet() : new HealthVerdict(findings);
    }

    private static HealthFinding Finding(HiloSequenceFacts sequence, HiloRow row, double issued, double share)
    {
        string types = string.Join(", ", sequence.DocumentTypes.Select(static x => SchemaTypeName.Of(x.DocumentType)));
        bool critical = share >= CriticalShare;

        return new HealthFinding
        {
            Code = "T11",
            Severity = critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Subject = sequence.EntityName,
            DocumentTypes = [.. sequence.DocumentTypes.Select(static x => x.DocumentType)],
            Title = "The int ids of " + types + " are " + TableText.Percent(Math.Min(share, 1)) + " of the way to int.MaxValue",
            WhyItMatters =
                "Marten hands out these ids from a HiLo sequence, a block of " + sequence.MaxLo.ToString("N0", CultureInfo.InvariantCulture)
                + " at a time, and an int id past int.MaxValue (" + int.MaxValue.ToString("N0", CultureInfo.InvariantCulture)
                + ") does not fail: it wraps to a negative number and the next insert collides or lands somewhere nobody expects. "
                + "Blocks are drawn per process, and every restart abandons the rest of a block, so the sequence runs out faster "
                + "than the row count suggests.",
            Evidence =
            [
                new HealthEvidence("Sequence (mt_hilo.entity_name)", sequence.EntityName),
                new HealthEvidence("Document types", types),
                new HealthEvidence("hi_value", row.HiValue.ToString("N0", CultureInfo.InvariantCulture)),
                new HealthEvidence("MaxLo", sequence.MaxLo.ToString("N0", CultureInfo.InvariantCulture)),
                new HealthEvidence("Ids that may be issued", TableText.Count(issued)),
                new HealthEvidence("Of int.MaxValue", TableText.Percent(share)),
            ],
            Caveat =
                "Computed as (hi_value + 1) × MaxLo, the highest id the blocks handed out so far can reach: an upper bound, since a "
                + "block may be abandoned half used. MaxLo is this process's configuration.",
            Remedy = new HealthRemedy(
                "// In the document type: a long id, whose HiLo range is 2^63.\n"
                + "public long Id { get; set; }",
                "Changing the id's type changes its column's: Marten's next apply alters the id column to bigint (AutoCreate."
                + "CreateOrUpdate is not additive - review db-patch first), and every foreign key, every duplicated reference and "
                + "every stored copy of these ids elsewhere must follow. The sooner, the smaller the table it rewrites."),
        };
    }
}
