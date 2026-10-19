using System.Globalization;

using JasperFx;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Schema;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Schema;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What a Marten table is, for the tables-and-indexes checks.</summary>
internal enum MartenTableKind
{
    /// <summary>A visible document type's table - the only kind a <c>Schema.For&lt;T&gt;()</c> remedy fits.</summary>
    Document,

    /// <summary>One of the event store's own tables.</summary>
    EventStore,

    /// <summary>A projection's table, or one of <c>StoreOptions.Storage.ExtendedSchemaObjects</c>.</summary>
    Managed,

    /// <summary>Marten's own bookkeeping (<c>mt_hilo</c>).</summary>
    Infrastructure,
}

/// <summary>Where an index on a Marten table comes from - which decides what a remedy can say about it.</summary>
internal enum IndexOrigin
{
    /// <summary>The primary key: never redundant, never unused, never a remedy's business.</summary>
    PrimaryKey,

    /// <summary>
    /// One Marten adds to a document table by itself - <c>mt_doc_type</c> for a hierarchy, <c>mt_deleted</c>
    /// for soft deletes, <c>tenant_id</c> for one tenancy ordering. No line removes it.
    /// </summary>
    Structural,

    /// <summary>One a document type declares: <c>Index</c>, <c>Duplicate</c>, <c>UniqueIndex</c>, a GIN index...</summary>
    Declared,

    /// <summary>One the event store's configuration declares.</summary>
    EventStore,

    /// <summary>One a projection's or an extended table's Weasel definition declares.</summary>
    Managed,

    /// <summary>One the configuration tells Marten's migration to leave alone (<c>IgnoreIndex</c>).</summary>
    Ignored,

    /// <summary>One nobody declares: the next apply drops it (<c>CreateOrUpdate</c> is not additive).</summary>
    Undeclared,
}

/// <summary>One table this store's configuration declares, as the checks know it.</summary>
/// <param name="Schema">The schema.</param>
/// <param name="Table">The table.</param>
/// <param name="Kind">What it is.</param>
/// <param name="OutsideStoreSchemas">
/// Whether <paramref name="Schema" /> is not one of the store's own schemas (<c>SchemaDeclarations.Schemas</c>): an
/// <c>ExtendedSchemaObjects</c> table in a schema of its own. The database browser withholds such a schema from a
/// visitor short of its gate (D27), so a finding naming the table carries it in <see cref="HealthFinding.GatedSchemas" />.
/// Required, and computed for every table <see cref="MartenTables.Read" /> lists: a default would be the fail-open answer.
/// </param>
/// <param name="DocumentType">The document type, for a document table; never a hidden one.</param>
internal sealed record MartenTable(
    string Schema,
    string Table,
    MartenTableKind Kind,
    bool OutsideStoreSchemas,
    IDocumentType? DocumentType = null)
{
    /// <summary>The key the history and the findings name it by.</summary>
    public string Key => SchemaKey.For(Schema, Table);

    /// <summary>What a finding naming this table puts in <see cref="HealthFinding.GatedSchemas" />.</summary>
    public IReadOnlyList<string> GatedSchemas => OutsideStoreSchemas ? [Schema] : [];

    /// <summary>
    /// The table's key for text shown at the check's gate with no per-visitor schema check - a status row's note, or a
    /// database-wide finding such as T12's that must not vanish because one table in it is gated: the key itself, or -
    /// for a table outside the store's own schemas - words that name neither it nor its schema.
    /// </summary>
    public string NoteName => OutsideStoreSchemas ? "a Marten-managed table outside the store's own schemas" : Key;

    /// <summary><see cref="Describe" />, or <see cref="NoteName" /> for a table outside the store's own schemas.</summary>
    public string DescribeUngated() => OutsideStoreSchemas ? NoteName : Describe();

    /// <summary>The .NET type a remedy's <c>Schema.For&lt;T&gt;()</c> names, for a document table.</summary>
    public string? TypeName => DocumentType is null ? null : SchemaTypeName.Of(DocumentType.DocumentType);

    /// <summary>The collection's alias, for a document table.</summary>
    public string? Alias => DocumentType?.Alias;

    /// <summary>The document types a finding about this table names - which the sweep drops a hidden one by.</summary>
    public IReadOnlyList<Type> DocumentTypes => DocumentType is null ? [] : [DocumentType.DocumentType];

    /// <summary>The mapping, when the document type is Marten's own <see cref="DocumentMapping" />.</summary>
    public DocumentMapping? Mapping => DocumentType as DocumentMapping;

    /// <summary>Whether the document type soft-deletes (AGENTS.md hard rule 10: read from the mapping's <c>DeleteStyle</c>).</summary>
    public bool SoftDeleted => Mapping?.DeleteStyle == DeleteStyle.SoftDelete;

    /// <summary>What the table is, for a sentence: "the Order collection's table", "the event store's mt_streams".</summary>
    public string Describe() => Kind switch
    {
        MartenTableKind.Document => "the " + TypeName + " collection's table " + Key,
        MartenTableKind.EventStore => "the event store's table " + Key,
        MartenTableKind.Managed => "the Marten-managed table " + Key,
        _ => "Marten's bookkeeping table " + Key,
    };

    /// <summary>The <c>HealthTableTarget</c> the statements bind.</summary>
    public HealthTableTarget Target => new(Schema, Table);
}

/// <summary>
/// The tables this store declares in this database, from its configuration alone - and what it declares
/// about their indexes.
/// </summary>
/// <param name="Tables">Every declared table but a hidden document type's, in schema and table order.</param>
/// <param name="Declarations">What <see cref="SchemaDeclarationReader" /> read.</param>
/// <param name="StructuralIndexes">The keys (<c>schema.table.index</c>) of the indexes Marten adds by itself.</param>
internal sealed record MartenTableSet(
    IReadOnlyList<MartenTable> Tables,
    SchemaDeclarations Declarations,
    IReadOnlySet<string> StructuralIndexes)
{
    private readonly Dictionary<string, MartenTable> byKey = Tables.ToDictionary(static x => x.Key, StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, DeclaredIndex> declared = Declarations.Indexes
        .GroupBy(static x => SchemaKey.For(x.Schema, x.Table, x.Name), StringComparer.OrdinalIgnoreCase)
        .ToDictionary(static x => x.Key, static x => x.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>What the statements bind.</summary>
    public IReadOnlyList<HealthTableTarget> Targets => [.. Tables.Select(static x => x.Target)];

    /// <summary>The declared table at <paramref name="schema" />.<paramref name="table" />, or <see langword="null" />.</summary>
    public MartenTable? Find(string schema, string table) => byKey.GetValueOrDefault(SchemaKey.For(schema, table));

    /// <summary>The declaration behind an index, or <see langword="null" />.</summary>
    public DeclaredIndex? DeclarationOf(HealthIndexRow index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return declared.GetValueOrDefault(index.Key);
    }

    /// <summary>Where <paramref name="index" /> comes from.</summary>
    public IndexOrigin OriginOf(HealthIndexRow index, MartenTable table)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(table);

        if (index.IsPrimaryKey)
        {
            return IndexOrigin.PrimaryKey;
        }

        if (StructuralIndexes.Contains(index.Key))
        {
            return IndexOrigin.Structural;
        }

        if (declared.ContainsKey(index.Key))
        {
            return table.Kind switch
            {
                MartenTableKind.EventStore => IndexOrigin.EventStore,
                MartenTableKind.Managed => IndexOrigin.Managed,
                _ => IndexOrigin.Declared,
            };
        }

        // The event store's ignore list is not per table, so it is keyed by schema and index alone.
        return Declarations.IgnoredIndexes.Contains(index.Key) || Declarations.IgnoredIndexes.Contains(SchemaKey.For(index.Schema, index.Name))
            ? IndexOrigin.Ignored
            : IndexOrigin.Undeclared;
    }
}

/// <summary>One declared table and what the catalog read about it.</summary>
/// <param name="Table">The declared table.</param>
/// <param name="Row">What <see cref="HealthTableQueries.Tables" /> read.</param>
internal sealed record TableFacts(MartenTable Table, HealthTableRow Row)
{
    /// <summary>The table's key.</summary>
    public string Key => Table.Key;

    /// <summary>The rows Postgres knows of: the collector's live count, else the planner's estimate.</summary>
    public long Rows => Row.LiveRows ?? Math.Max(Row.EstimatedRows, 0);

    /// <summary>The share of dead rows among live and dead, or <see langword="null" /> when the collector has nothing.</summary>
    public double? DeadShare => Row is { LiveRows: { } live, DeadRows: { } dead } && live + dead > 0
        ? (double) dead / (live + dead)
        : null;

    /// <summary>The share of updates that were HOT, since the counters began, or <see langword="null" /> before the first update.</summary>
    public double? HotShare => Row is { Updates: { } updates, HotUpdates: { } hot } && updates > 0 ? (double) hot / updates : null;

    /// <summary>The latest vacuum of either kind.</summary>
    public DateTimeOffset? LastVacuumOfAnyKind => Latest(Row.LastVacuum, Row.LastAutovacuum);

    /// <summary>The latest analyze of either kind.</summary>
    public DateTimeOffset? LastAnalyzeOfAnyKind => Latest(Row.LastAnalyze, Row.LastAutoanalyze);

    /// <summary>Whether the table's own <c>autovacuum_enabled</c> storage parameter is off.</summary>
    public bool AutovacuumOffForTable =>
        StorageParameters.Parse(Row.StorageOptions).TryGetValue("autovacuum_enabled", out string? value)
        && StorageParameters.IsFalse(value);

    /// <summary>
    /// Whether autovacuum leaves this table alone: the server's <c>autovacuum</c> is off, or the table's own
    /// <c>autovacuum_enabled</c> storage parameter is.
    /// </summary>
    public bool AutovacuumOff => !Row.AutovacuumEnabled || AutovacuumOffForTable;

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;
}

/// <summary>One index on a declared table, with where it comes from.</summary>
/// <param name="Table">The table it is on.</param>
/// <param name="Row">What <see cref="HealthTableQueries.Indexes" /> read.</param>
/// <param name="Origin">Where it comes from.</param>
/// <param name="Declaration">Its declaration, when it has one.</param>
internal sealed record IndexFacts(MartenTable Table, HealthIndexRow Row, IndexOrigin Origin, DeclaredIndex? Declaration)
{
    /// <summary>The index's key: schema, table, index - the <c>Subject</c> of a finding about it, and what the Schema tab marks by.</summary>
    public string Key => Row.Key;

    /// <summary>Whether anything in the configuration can be changed to remove it.</summary>
    public bool Removable => Origin is IndexOrigin.Declared or IndexOrigin.Managed or IndexOrigin.Undeclared or IndexOrigin.Ignored;
}

/// <summary>The database-wide facts every row of the table read carries.</summary>
/// <param name="StatsReset">When the statistics were last reset, or <see langword="null" /> for never.</param>
/// <param name="AutovacuumEnabled">The server's <c>autovacuum</c>.</param>
/// <param name="MaxParallelWorkersPerGather">How many workers one scan may add.</param>
internal sealed record DatabaseStatistics(DateTimeOffset? StatsReset, bool AutovacuumEnabled, int MaxParallelWorkersPerGather)
{
    /// <summary>Nothing read: no table to read about.</summary>
    public static DatabaseStatistics Unknown { get; } = new(null, true, 2);

    /// <summary>The facts the first row carries - every row carries the same ones.</summary>
    public static DatabaseStatistics Of(IReadOnlyList<TableFacts> tables) =>
        tables.Count == 0
            ? Unknown
            : new DatabaseStatistics(tables[0].Row.StatsReset, tables[0].Row.AutovacuumEnabled, tables[0].Row.MaxParallelWorkersPerGather);
}

/// <summary>
/// Reads the tables this store declares, and the catalog's facts about them, for the tables-and-indexes
/// checks.
/// </summary>
/// <remarks>
/// <para>
/// <b>The set comes from the configuration, never from the database</b>: <see cref="SchemaDeclarationReader" />,
/// which reads <c>StoreOptions</c> and <c>IDocumentType</c> and touches no connection - never
/// <c>AllObjects()</c> or <c>AllSchemaNames()</c>, which migrate (AGENTS.md hard rule 14). So a "Marten table"
/// here is one the store declares; a leftover <c>mt_doc_*</c> table no registered type names is not one, which
/// is also what keeps a hidden type nothing in the process has touched out of every finding.
/// </para>
/// <para>
/// <b>A hidden document type is never read about.</b> Its table is left out of the set, so its name is not
/// even a parameter of a statement, and a finding names the document type it is about besides, so the sweep
/// would drop it anyway.
/// </para>
/// </remarks>
internal static class MartenTables
{
    /// <summary>The tables the store declares, a hidden type's left out.</summary>
    public static MartenTableSet Read(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyStoreOptions options = context.StoreOptions;
        SchemaDeclarations declarations = SchemaDeclarationReader.Read(options, context.IsDocumentTypeVisible);

        List<MartenTable> tables = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> structural = new(StringComparer.OrdinalIgnoreCase);

        // Ordinal, as the database browser's gate compares schema names: a near miss is gated, never waved through.
        HashSet<string> storeSchemas = new(declarations.Schemas, StringComparer.Ordinal);
        bool OutsideStore(string schema) => !storeSchemas.Contains(schema);

        foreach (IDocumentType documentType in options.AllKnownDocumentTypes())
        {
            string schema = string.IsNullOrWhiteSpace(documentType.TableName.Schema)
                ? documentType.DatabaseSchemaName
                : documentType.TableName.Schema;
            string table = documentType.TableName.Name;

            // Hidden or not, its table is nobody else's: the declarations' own entry for it is skipped below.
            seen.Add(SchemaKey.For(schema, table));

            if (!context.IsDocumentTypeVisible(documentType.DocumentType))
            {
                continue;
            }

            tables.Add(new MartenTable(schema, table, MartenTableKind.Document, OutsideStore(schema), documentType));

            if (documentType is DocumentMapping mapping)
            {
                foreach (string name in StructuralIndexNames(mapping))
                {
                    structural.Add(SchemaKey.For(schema, table, name));
                }
            }
        }

        foreach ((string key, MartenDeclaredObject declared) in declarations.Objects)
        {
            MartenTableKind? kind = declared.Kind switch
            {
                MartenObjectKind.EventTable => MartenTableKind.EventStore,
                MartenObjectKind.ProjectionOrExtendedTable => MartenTableKind.Managed,
                MartenObjectKind.Infrastructure => MartenTableKind.Infrastructure,
                _ => null,
            };

            // The entry's own schema and name, never the key split back against SchemaDeclarations.Schemas: an
            // ExtendedSchemaObjects table may sit in a schema of its own, which that list deliberately leaves out
            // (D27), and a split that found no schema skipped the table without a word.
            if (kind is null || !seen.Add(key))
            {
                continue;
            }

            tables.Add(new MartenTable(declared.Schema, declared.Name, kind.Value, OutsideStore(declared.Schema)));
        }

        tables.Sort(static (a, b) =>
        {
            int bySchema = string.CompareOrdinal(a.Schema, b.Schema);
            return bySchema != 0 ? bySchema : string.CompareOrdinal(a.Table, b.Table);
        });

        return new MartenTableSet(tables, declarations, structural);
    }

    /// <summary>The memo key of the declared-table set.</summary>
    internal const string SetKey = "tables.set";

    /// <summary>The memo key of the table read.</summary>
    internal const string TablesKey = "tables.stats";

    /// <summary>The memo key of the index read.</summary>
    internal const string IndexesKey = "tables.indexes";

    /// <summary>The declared tables, built once per sweep for every check that asks.</summary>
    public static Task<MartenTableSet> SetAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(SetKey, _ => Task.FromResult(Read(context)));
    }

    /// <summary>
    /// The declared tables and the catalog's row for each - read once per sweep, whichever checks ask
    /// (<see cref="HealthCheckContext.Shared{T}" />), and shown under every asking check's "Show SQL".
    /// </summary>
    public static async Task<IReadOnlyList<TableFacts>> TablesAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        MartenTableSet set = await SetAsync(context).ConfigureAwait(false);
        return await context.Shared(TablesKey, token => ReadTablesAsync(context, set, token)).ConfigureAwait(false);
    }

    /// <summary>Every index on the declared tables, read once per sweep like <see cref="TablesAsync" />.</summary>
    public static async Task<IReadOnlyList<IndexFacts>> IndexesAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        MartenTableSet set = await SetAsync(context).ConfigureAwait(false);
        return await context.Shared(IndexesKey, token => ReadIndexesAsync(context, set, token)).ConfigureAwait(false);
    }

    /// <summary>The declared tables and the catalog's row for each of them; a table not created yet has none.</summary>
    private static async Task<IReadOnlyList<TableFacts>> ReadTablesAsync(
        HealthCheckContext context,
        MartenTableSet set,
        CancellationToken cancellationToken)
    {
        if (set.Tables.Count == 0)
        {
            // Nothing to ask about, so no connection is opened at all.
            return [];
        }

        IReadOnlyList<HealthTableRow> rows = await context.Sql
            .QueryAsync(HealthTableQueries.Tables(set.Targets), HealthTableQueries.ReadTable, cancellationToken)
            .ConfigureAwait(false);

        List<TableFacts> facts = new(rows.Count);
        foreach (HealthTableRow row in rows)
        {
            if (set.Find(row.Schema, row.Table) is { } table)
            {
                facts.Add(new TableFacts(table, row));
            }
        }

        return facts;
    }

    /// <summary>Every index on the declared tables, with where each comes from.</summary>
    private static async Task<IReadOnlyList<IndexFacts>> ReadIndexesAsync(
        HealthCheckContext context,
        MartenTableSet set,
        CancellationToken cancellationToken)
    {
        if (set.Tables.Count == 0)
        {
            return [];
        }

        IReadOnlyList<HealthIndexRow> rows = await context.Sql
            .QueryAsync(HealthTableQueries.Indexes(set.Targets), HealthTableQueries.ReadIndex, cancellationToken)
            .ConfigureAwait(false);

        List<IndexFacts> facts = new(rows.Count);
        foreach (HealthIndexRow row in rows)
        {
            if (set.Find(row.Schema, row.Table) is { } table)
            {
                facts.Add(new IndexFacts(table, row, set.OriginOf(row, table), set.DeclarationOf(row)));
            }
        }

        return facts;
    }

    /// <summary>
    /// The indexes <c>Marten.Storage.DocumentTable</c> adds to a document table by itself, named the way it
    /// names them: <c>mt_doc_type</c> for a hierarchy, <c>mt_deleted</c> for soft deletes, and <c>tenant_id</c>
    /// for a conjoined table whose primary key leads with the id (verified at V9.39.0 and V9.50.2).
    /// </summary>
    internal static IEnumerable<string> StructuralIndexNames(DocumentMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (mapping.IsHierarchy())
        {
            yield return new DocumentIndex(mapping, "mt_doc_type").Name;
        }

        if (mapping.DeleteStyle == DeleteStyle.SoftDelete)
        {
            yield return new DocumentIndex(mapping, "mt_deleted").Name;
        }

        if (mapping.TenancyStyle == TenancyStyle.Conjoined
            && mapping.PrimaryKeyTenancyOrdering == PrimaryKeyTenancyOrdering.Id_Then_TenantId)
        {
            yield return new DocumentIndex(mapping, "tenant_id").Name;
        }
    }
}

/// <summary>How a cumulative counter changed over the history: from the last reset, to now.</summary>
/// <param name="Change">The change.</param>
/// <param name="From">The first sample of the run.</param>
/// <param name="To">The latest sample.</param>
internal sealed record CounterDelta(double Change, DateTimeOffset From, DateTimeOffset To)
{
    /// <summary>How long the change was measured over.</summary>
    public TimeSpan Span => To - From;
}

/// <summary>Rates and persistence over <see cref="HealthHistory" />, for the evaluators. Pure.</summary>
internal static class TableHistory
{
    /// <summary>
    /// The change of a cumulative counter over its latest unbroken run, or <see langword="null" /> before two
    /// samples of it exist. A value lower than the one before it is a reset - <c>pg_stat_reset()</c>, a crash,
    /// a table recreated - and the run starts again there rather than reporting a negative rate.
    /// </summary>
    public static CounterDelta? Delta(HealthTrend trend)
    {
        ArgumentNullException.ThrowIfNull(trend);

        IReadOnlyList<(DateTimeOffset At, double Value)> points = trend.Points;
        if (points.Count < 2)
        {
            return null;
        }

        int start = points.Count - 1;
        while (start > 0 && points[start - 1].Value <= points[start].Value)
        {
            start--;
        }

        (DateTimeOffset At, double Value) first = points[start];
        (DateTimeOffset At, double Value) last = points[^1];

        return start == points.Count - 1 || last.At <= first.At
            ? null
            : new CounterDelta(last.Value - first.Value, first.At, last.At);
    }

    /// <summary>
    /// Since when a condition has held without a break, over the samples the check ran in - or
    /// <see langword="null" /> when it does not hold in the latest one.
    /// </summary>
    /// <param name="history">The database's samples, this sweep's included.</param>
    /// <param name="sweptSeries">The series the check writes in every sweep it runs in.</param>
    /// <param name="conditionSeries">The series it writes, as 1, while the condition holds.</param>
    public static DateTimeOffset? HeldSince(HealthHistoryView history, string sweptSeries, string conditionSeries)
    {
        ArgumentNullException.ThrowIfNull(history);

        DateTimeOffset? since = null;

        foreach (HealthSample sample in history.Samples)
        {
            if (!sample.Values.ContainsKey(sweptSeries))
            {
                // A sweep that did not run the check says nothing either way.
                continue;
            }

            since = sample.Values.TryGetValue(conditionSeries, out double value) && value > 0
                ? since ?? sample.At
                : null;
        }

        return since;
    }
}

/// <summary>
/// What T07 and T08 say about autovacuum for a table, and the remedy that fits its state: the server's
/// switch, the table's own switch, or a scale factor.
/// </summary>
internal static class Autovacuum
{
    /// <summary>
    /// The remedy for a table autovacuum is not keeping up with: switch it back on for the table, or lower the
    /// scale factor - a <c>Schema.For&lt;T&gt;().StorageParameter</c> line (Marten 9.47+) for a document table,
    /// and nothing for another table or for a server whose <c>autovacuum</c> is off, which no StoreOptions line
    /// changes (<see cref="State" /> says so in words).
    /// </summary>
    public static HealthRemedy? Remedy(TableFacts table, string parameter, string value, string explanation)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (table.Table.TypeName is not { } type || !table.Row.AutovacuumEnabled)
        {
            return null;
        }

        return table.AutovacuumOffForTable
            ? new HealthRemedy(
                "opts.Schema.For<" + type + ">().StorageParameter(\"autovacuum_enabled\", true);",
                "Autovacuum is switched off for this table (its autovacuum_enabled storage parameter is false). Declaring it true "
                + "lets Marten's next apply switch it back on; Marten manages only the storage parameters it declares.",
                RuntimeFeature.DocumentStorageParameter)
            : new HealthRemedy(
                "opts.Schema.For<" + type + ">().StorageParameter(\"" + parameter + "\", " + value + ");",
                explanation + " Below Marten 9.47 the same setting is a DBA's ALTER TABLE ... SET (" + parameter + " = " + value
                + ") on the table, which no Marten apply resets unless the apply rebuilds the table (a changed partition scheme "
                + "copies it out and back): Marten manages only the storage parameters it declares.",
                RuntimeFeature.DocumentStorageParameter);
    }

    /// <summary>The sentence about the table's autovacuum state that a finding's "why" ends with, or nothing.</summary>
    public static string State(TableFacts table, string parameter, string value)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (!table.Row.AutovacuumEnabled)
        {
            return " Autovacuum is switched off for the whole server (autovacuum = off), so nothing will look after this table "
                + "until a DBA does.";
        }

        if (table.AutovacuumOffForTable)
        {
            string off = " Autovacuum is switched off for this table (autovacuum_enabled = false"
                + (table.Row.IsPartitioned ? " on every partition" : string.Empty) + ").";

            // A document table's remedy is a Schema.For<T>() line (Remedy). Any other table's storage parameters are not
            // something a StoreOptions line the studio can name sets, so the way back is said in words, never as DDL.
            return table.Table.Kind == MartenTableKind.Document
                ? off
                : off + " Switching it back on is the host's to do: re-enable autovacuum on this table where its storage parameters "
                  + "are set - the host's own DDL, or (Marten 9.47+) the table's declaration where the host hands it to Marten (a "
                  + "projection's table, or StoreOptions.Storage.ExtendedSchemaObjects).";
        }

        string partly = table.Row.DifferingStorageOptions.Contains("autovacuum_enabled", StringComparer.OrdinalIgnoreCase)
            ? " Its partitions do not all have the same autovacuum_enabled setting, so autovacuum may be switched off for some "
              + "of them (the Schema screen's Tables tab shows which parameters the partitions differ on)."
            : string.Empty;

        // From Marten 9.47 a projection's or an ExtendedSchemaObjects table's Weasel declaration can carry storage
        // parameters, and an apply then sets a declared one back; Marten's own event-store and bookkeeping tables still
        // declare none a host can set.
        string apply = table.Table.Kind == MartenTableKind.Managed
            ? "no Marten apply resets it unless the apply rebuilds the table, as a changed partition scheme does - or, from "
              + "Marten 9.47, unless the table's own declaration (a projection's table, or an ExtendedSchemaObjects table) "
              + "declares " + parameter + ", which an apply then sets back to the declared value."
            : "no Marten apply resets it unless the apply rebuilds the table, as a changed partition scheme does.";

        return table.Table.Kind == MartenTableKind.Document
            ? partly
            : partly + " No StoreOptions line the studio can suggest sets this table's autovacuum settings; a DBA's ALTER TABLE ... SET ("
              + parameter + " = " + value + ") does, and " + apply;
    }
}

/// <summary>The wording the tables-and-indexes findings share.</summary>
internal static class TableText
{
    /// <summary>A count of rows, prefixed <c>~</c> when it is an estimate (D8).</summary>
    public static string Rows(long rows, bool estimate = true) =>
        (estimate ? "~" : string.Empty) + rows.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A count.</summary>
    public static string Count(double value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>A share, as a whole percentage - or one decimal under ten percent.</summary>
    public static string Percent(double share) =>
        (share * 100).ToString(share < 0.1 ? "0.0" : "0", CultureInfo.InvariantCulture) + "%";

    /// <summary>A size.</summary>
    public static string Bytes(long bytes) => SchemaFormat.Bytes(bytes);

    /// <summary>A moment, in UTC: evidence is formatted once, for every visitor of the sweep.</summary>
    public static string When(DateTimeOffset? at) =>
        at is { } value ? value.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "never";

    /// <summary>A span, as a person says it.</summary>
    public static string Span(TimeSpan span) => span switch
    {
        _ when span < TimeSpan.FromMinutes(1) => "under a minute",
        _ when span < TimeSpan.FromHours(2) => ((int) span.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min",
        _ when span < TimeSpan.FromDays(2) => ((int) span.TotalHours).ToString(CultureInfo.InvariantCulture) + " h",
        _ => ((int) span.TotalDays).ToString(CultureInfo.InvariantCulture) + " days",
    };

    /// <summary>How long ago, or "never".</summary>
    public static string Ago(DateTimeOffset? at, DateTimeOffset now) =>
        at is { } value ? Span(now - value) + " ago (" + When(value) + ")" : "never, as far as the statistics collector knows";

    /// <summary>
    /// The caveat every counter-based finding carries: how old the counters are, and what zeroes them.
    /// </summary>
    public static string CountersSince(DateTimeOffset? statsReset, DateTimeOffset now) =>
        statsReset is { } reset
            ? "Counters since the statistics were last reset, " + Ago(reset, now) + ". pg_stat_reset() and a crash zero them."
            : "Counters since the statistics were last reset - never, as pg_stat_database reports it, so they may span the "
              + "whole life of the database; a crash zeroes them without saying so.";
}
