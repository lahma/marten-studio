using System.Globalization;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Server;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What T26 reads: the integer keys a sequence feeds, and who reads their statistics.</summary>
/// <param name="Keys">One row per column and sequence, the most specific way it is fed kept.</param>
/// <param name="Judged">Every table of the judged set.</param>
internal sealed record IntegerKeyFacts(IReadOnlyList<HealthIntegerKeyRow> Keys, IReadOnlyList<StudioTable> Judged)
{
    /// <summary>The role the reads run as, when a column's statistics were not readable - for the "not checked" sentence.</summary>
    public string? ReadsAs { get; init; }

    /// <summary>Who runs the server, for who can grant the column's <c>SELECT</c>.</summary>
    public PostgresPlatform Platform { get; init; } = PostgresPlatform.SelfHosted;
}

/// <summary>How far through its range one integer key is, by a lock-free estimate (T26).</summary>
/// <param name="Estimate">The largest value stored, at least.</param>
/// <param name="FromStatistics">Whether <paramref name="Estimate" /> is the last <c>ANALYZE</c>'s sample rather than the row-count floor.</param>
/// <param name="Limit">The narrower of the column's range and the sequence's maximum.</param>
/// <param name="LimitIsColumn">Whether the column's range is the limit (<c>column</c>) rather than the sequence's (<c>sequence</c>).</param>
internal sealed record IntegerKeyHeadroom(double Estimate, bool FromStatistics, long Limit, bool LimitIsColumn)
{
    /// <summary>The share of the range used.</summary>
    public double Used => Limit <= 0 ? 0 : Estimate / Limit;
}

/// <summary>
/// T26: a <c>smallint</c> or <c>integer</c> key - or a <c>bigint</c> one on a <c>smallint</c> or <c>integer</c> sequence -
/// whose largest stored value is past half its range, estimated without reading the sequence (W5-B1, the health-gap analysis).
/// </summary>
/// <remarks>
/// <para>
/// <b>Which columns.</b> Every one of the judged set's tables - a library's included: running out of ids is not index design -
/// whose column a sequence feeds: an identity column or a <c>serial</c> (an owned sequence) or a default that calls one, as
/// <c>pg_depend</c> records it, never as text (<see cref="IndexLintQueries.IntegerKeysSql" />). A cycling sequence, a
/// descending one and one that starts below zero are not judged.
/// </para>
/// <para>
/// <b>The estimate</b> is the greater of two floors on the largest value stored: the table's <c>reltuples</c> past the
/// sequence's start - for a column a unique index holds, whose values are distinct - which needs no privilege, and the last
/// <c>ANALYZE</c>'s largest sampled value, which <c>pg_stats</c> shows only to a role with <c>SELECT</c> on the column (B0
/// §8c). It is judged against the narrower of the column's range and the sequence's <c>seqmax</c>: Warning at half, Critical
/// at four fifths - T11's lines.
/// </para>
/// <para>
/// <b>Never the sequence's value</b>, which is read only under a lock (B0 §3): a rolled-back insert and an
/// <c>INSERT ... ON CONFLICT DO NOTHING</c> take a value and store nothing, so the sequence may be further on than any stored
/// value - the caveat says so, and the database browser's Sequences list reads one sequence's value when asked.
/// </para>
/// </remarks>
[HealthCheck("T26", HealthArea.Tables, HealthCostTier.Catalog, "Integer key running out", Variants = [ColumnVariant, SequenceVariant], Gate = HealthGate.Database)]
internal sealed class IntegerKeyHeadroomCheck : HealthCheck<IntegerKeyFacts>
{
    /// <summary>The variant where the column's own range is the limit.</summary>
    public const string ColumnVariant = "column";

    /// <summary>The variant where the sequence's maximum is the limit: a <c>bigint</c> column on a narrower sequence.</summary>
    public const string SequenceVariant = "sequence";

    /// <summary>The share of the range at and above which the finding is a Warning (T11's line).</summary>
    public const double WarningShare = 0.5;

    /// <summary>The share at and above which it is Critical (T11's line).</summary>
    public const double CriticalShare = 0.8;

    /// <inheritdoc />
    public override async Task<IntegerKeyFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IStudioTableSet set = await HealthTables.SetAsync(context).ConfigureAwait(false);

        if (set.Tables.Count == 0)
        {
            return new IntegerKeyFacts([], []);
        }

        IReadOnlyList<HealthIntegerKeyRow> rows = await context.Sql
            .QueryAsync(IndexLintQueries.IntegerKeys(HealthTables.Targets(set)), IndexLintQueries.ReadIntegerKey, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<HealthIntegerKeyRow> keys = Distinct(rows);

        if (keys.All(static x => x.StatisticsReadable))
        {
            return new IntegerKeyFacts(keys, set.Tables);
        }

        // Only now who reads, for the sentence that names the grant (A7): both are the sweep's shared reads.
        PostgresGrants grants = await PostgresGrants.SharedAsync(context).ConfigureAwait(false);
        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        return new IntegerKeyFacts(keys, set.Tables) { ReadsAs = grants.ReadsAs, Platform = server.Platform };
    }

    /// <summary>
    /// One row per column and sequence: a <c>serial</c> is both an owned sequence and a default that calls it, and is kept as
    /// the owned one; an identity column is kept as identity.
    /// </summary>
    internal static IReadOnlyList<HealthIntegerKeyRow> Distinct(IReadOnlyList<HealthIntegerKeyRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);

        return
        [
            .. rows
                .GroupBy(static x => (x.Schema, x.Table, x.Column, x.SequenceSchema, x.Sequence))
                .Select(static g => g.OrderBy(static x => x.FedBy switch { "identity" => 0, "serial" => 1, _ => 2 }).First()),
        ];
    }

    /// <summary>The largest value a column of <paramref name="type" /> holds, or <see langword="null" /> for a type not judged.</summary>
    internal static long? ColumnMaximum(string type) => type switch
    {
        "smallint" => short.MaxValue,
        "integer" => int.MaxValue,
        "bigint" => long.MaxValue,
        _ => null,
    };

    /// <summary>
    /// Whether the sequence's <c>MAXVALUE</c> was set below its type's own - which <c>ALTER SEQUENCE … AS bigint</c> and an
    /// identity column's <c>ALTER COLUMN … TYPE bigint</c> both keep, so widening needs <c>NO MAXVALUE</c> too (W5-B1 review F8).
    /// </summary>
    internal static bool ExplicitMaximum(HealthIntegerKeyRow key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return ColumnMaximum(key.SequenceType) is { } typeMaximum && key.Maximum < typeMaximum;
    }

    /// <summary>
    /// The estimate for one key, or <see langword="null" /> when it is not judged - a cycling, descending or negative-start
    /// sequence, a type not judged - or nothing bounds it: no row count to count from and no statistics to read.
    /// </summary>
    internal static IntegerKeyHeadroom? Headroom(HealthIntegerKeyRow key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (key.Cycles || key.Increment < 0 || key.Start < 0 || ColumnMaximum(key.ColumnType) is not { } columnMaximum)
        {
            return null;
        }

        long limit = Math.Min(columnMaximum, key.Maximum);

        // A unique column's values are distinct and, fed from a start at or above zero, no smaller than it: its largest is at
        // least the row count past the start. A column that is not unique proves nothing by its count.
        double floor = key.Unique && key.Rows is { } rows ? Math.Max(0, key.Start - 1) + rows : 0;
        double sampled = key.LargestSampled ?? 0;

        if (floor <= 0 && key.LargestSampled is null)
        {
            return null;
        }

        return new IntegerKeyHeadroom(Math.Max(floor, sampled), key.LargestSampled is not null && sampled >= floor, limit, columnMaximum <= key.Maximum);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(IntegerKeyFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        Dictionary<string, StudioTable> judged = new(StringComparer.Ordinal);
        foreach (StudioTable table in facts.Judged)
        {
            judged.TryAdd(table.Key, table);
        }

        HashSet<string> ownSchemas = new(facts.Judged.Where(static x => x.GatedSchemas.Count == 0).Select(static x => x.Schema), StringComparer.Ordinal);
        List<HealthFinding> findings = [];
        int unread = 0;

        foreach (HealthIntegerKeyRow key in facts.Keys)
        {
            if (!judged.TryGetValue(key.TableKey, out StudioTable? table))
            {
                continue;
            }

            if (Headroom(key) is not { } headroom)
            {
                unread += key.StatisticsReadable || key.Cycles || key.Increment < 0 || key.Start < 0 ? 0 : 1;
                continue;
            }

            if (headroom.Used >= WarningShare)
            {
                findings.Add(Finding(table, key, headroom, ownSchemas, facts, evaluation));
            }
        }

        string? note = unread == 0
            ? null
            : (unread == 1 ? "1 column" : SessionText.Number(unread) + " columns")
              + " fed by a sequence could not be estimated - no unique index to count from, and "
              + (facts.ReadsAs is { } role
                  ? "not checked: its statistics need SELECT on the column for role " + role + ", which neither pg_read_all_stats nor "
                    + "pg_monitor gives; the table's owner can grant it."
                  : "no statistics to read.");

        return findings.Count == 0 ? HealthVerdict.Quiet(note) : new HealthVerdict(findings, note);
    }

    private static string Number(double value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private static HealthFinding Finding(
        StudioTable table,
        HealthIntegerKeyRow key,
        IntegerKeyHeadroom headroom,
        HashSet<string> ownSchemas,
        IntegerKeyFacts facts,
        HealthEvaluation evaluation)
    {
        bool sequenceElsewhere = !string.Equals(key.SequenceSchema, table.Schema, StringComparison.Ordinal) && !ownSchemas.Contains(key.SequenceSchema);
        string sequence = key.SequenceSchema + "." + key.Sequence;
        string limit = headroom.LimitIsColumn
            ? Number(headroom.Limit) + ", the largest " + key.ColumnType + " value"
            : ExplicitMaximum(key)
                ? Number(headroom.Limit) + ", the sequence's own MAXVALUE (it is " + key.SequenceType + ")"
                : Number(headroom.Limit) + ", the sequence's maximum (AS " + key.SequenceType + ")";

        return new HealthFinding
        {
            Code = "T26",
            Severity = headroom.Used >= CriticalShare ? HealthSeverity.Critical : HealthSeverity.Warning,
            Variant = headroom.LimitIsColumn ? ColumnVariant : SequenceVariant,
            Subject = table.Key + "." + key.Column,
            DocumentTypes = table.DocumentTypes,
            GatedSchemas = sequenceElsewhere ? [.. table.GatedSchemas, key.SequenceSchema] : table.GatedSchemas,
            Title = "Column " + key.Column + " of " + table.Key + " has used at least " + TableText.Percent(headroom.Used) + " of its "
                + (headroom.LimitIsColumn ? key.ColumnType + " range" : "sequence's range"),
            WhyItMatters =
                "Its values come from " + sequence + " (" + key.FedBy + "), and once that passes " + Number(headroom.Limit)
                + " every insert into the table fails - nextval raises \"reached maximum value of sequence\", or the column "
                + "\"value out of range\". Widening the column rewrites the table under a lock, which is a maintenance window "
                + "to plan, not one to find when inserts stop.",
            Evidence =
            [
                new HealthEvidence("Column", key.Column + " (" + key.ColumnType + ")"),
                new HealthEvidence("Table", table.Describe()),
                new HealthEvidence("Sequence", sequence + " (" + key.SequenceType + ", " + key.FedBy + ")"),
                new HealthEvidence("Limit", limit),
                new HealthEvidence(
                    "Largest value stored, at least",
                    Number(headroom.Estimate) + (headroom.FromStatistics ? " - as of the last ANALYZE" : " - a row-count floor")),
                new HealthEvidence("Used", TableText.Percent(headroom.Used)),
            ],
            Caveat =
                "An estimate that never reads the sequence, whose value is read only under a lock: the greater of the largest value "
                + "the last ANALYZE sampled and, for a column a unique index holds, the row count past the sequence's start. A "
                + "rolled-back insert and INSERT ... ON CONFLICT DO NOTHING take a value and store nothing, so the sequence can be "
                + "further on than any stored value: the database browser's Sequences list reads its value, one sequence at a time, "
                + "when asked."
                + (!key.StatisticsReadable && facts.ReadsAs is { } role
                    ? " " + PostgresGrants.NotChecked(PostgresGrant.ColumnSelect(table.Key, key.Column), facts.Platform, role)
                      + " So this is the row-count floor alone."
                    : string.Empty),
            Remedy = evaluation.TableRemedies?.WidenKey(table, key),
            Link = new HealthLink(
                "Read the sequence's value in the database browser",
                DatabaseLinks.BrowserRoute,
                [
                    new KeyValuePair<string, string?>(DatabaseLinks.SchemaParameter, key.SequenceSchema),
                    new KeyValuePair<string, string?>(DatabaseLinks.KindParameter, DatabaseLinks.KindToken(DatabaseObjectCategory.Sequences)),
                    new KeyValuePair<string, string?>(DatabaseLinks.FilterParameter, key.Sequence),
                ]),
        };
    }
}
