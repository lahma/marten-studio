using Marten.Schema;

using MartenStudio.Internal;
using MartenStudio.Internal.Sql;

using Microsoft.Extensions.Options;

using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Query;

namespace MartenStudio.Services.Query;

/// <summary>
/// 1.2-INDEX-A: MartenStudio's <see cref="IIndexCandidateContributor" /> - the Statements page's index advisor, in Marten's terms. On
/// a Marten document table an index read off a plan is replaced by the index Marten would create for a <c>StoreOptions</c> line
/// (and carries that line as its remedy), or dropped. It adds nothing of its own: a recorded statement names no JSON member
/// (<c>pg_stat_statements</c> replaces every constant, the key included, with <c>$n</c>), so P26's member-by-member advice cannot be
/// made from one.
/// </summary>
/// <remarks>
/// <para>
/// <b>An index Marten's next apply would drop is never offered as SQL.</b> <c>AutoCreate.CreateOrUpdate</c> is not additive: an index
/// the configuration does not declare is dropped by the next schema apply. So a plan-origin candidate on a visible document table
/// survives only as one of the two forms a line declares - a duplicated field's column (<c>Index(x =&gt; x.Member)</c>, Marten's own
/// DDL through <see cref="MartenIndexDdl.Computed" />) or <c>mt_last_modified</c> (<c>IndexLastModified()</c>) - and every other one is
/// dropped, as is every candidate on any other table named like Marten's (its event, stream and progression tables).
/// </para>
/// <para>
/// <b>Read from the configuration, never the database</b> (hard rule 14): <see cref="MartenPlanTables" /> reads <c>StoreOptions</c>
/// and the mappings, <c>AllKnownDocumentTypes()</c> touches no connection, and nothing here calls a storage-ensuring API. A hidden
/// document type has no <see cref="MartenPlanTable" />, so its candidates are dropped and its table is never named. The store is
/// learned the way any scope is (authorization first, the tenant dropped); when it cannot be, every candidate on a table named like
/// Marten's is dropped and the rest are returned - MartenStudio never offers an unlined index on a Marten table.
/// </para>
/// <para>
/// <b>A line the configuration already declares is no remedy.</b> A <c>Duplicate()</c> always declares the index on its column, so
/// when that index is missing from the database the candidate carries no line and the note says applying the schema creates it.
/// </para>
/// </remarks>
internal sealed class MartenIndexCandidateContributor : IIndexCandidateContributor
{
    private readonly StudioScopeResolver resolver;
    private readonly IOptions<MartenStudioOptions> options;

    public MartenIndexCandidateContributor(StudioScopeResolver resolver, IOptions<MartenStudioOptions> options)
    {
        this.resolver = resolver;
        this.options = options;
    }

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<HypotheticalIndex>> ContributeAsync(
        QueryPlan plan,
        IReadOnlyList<HypotheticalIndex> fromPlan,
        StudioScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(fromPlan);
        ArgumentNullException.ThrowIfNull(scope);

        if (!MartenPlanInsights.ReadsDocumentTables(plan) && !fromPlan.Any(static x => DatabaseObjectClassifier.IsMartenName(x.Table)))
        {
            return fromPlan;
        }

        IReadOnlyList<MartenPlanTable>? tables = null;

        try
        {
            ResolvedScope resolved = await resolver.ResolveAsync(scope with { TenantId = null }, capability: null, cancellationToken).ConfigureAwait(false);
            tables = MartenPlanTables.Read(resolved.Store, options.Value.IsDocumentTypeVisible);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Refused, not found, will not build, or a mapping that will not read: no Marten table's index is offered.
            tables = null;
        }

        return Offer(fromPlan, tables);
    }

    /// <summary>
    /// What the studio offers for the plan given the tables the store declares (<see langword="null" /> when they could
    /// not be learned: every candidate on a table named like Marten's is then dropped). Pure: reads no database.
    /// </summary>
    /// <param name="fromPlan">The indexes the plan suggests, or the previous contributor's.</param>
    /// <param name="tables">The store's visible document tables, or <see langword="null" />.</param>
    internal static List<HypotheticalIndex> Offer(IReadOnlyList<HypotheticalIndex> fromPlan, IReadOnlyList<MartenPlanTable>? tables)
    {
        if (tables is null)
        {
            return [.. fromPlan.Where(static x => !DatabaseObjectClassifier.IsMartenName(x.Table))];
        }

        List<HypotheticalIndex> offered = [];
        HashSet<string> definitions = new(StringComparer.Ordinal);

        foreach (HypotheticalIndex candidate in fromPlan)
        {
            if (!DatabaseObjectClassifier.IsMartenName(candidate.Table) || candidate.Origin != HypotheticalIndexOrigin.Plan)
            {
                // Not a Marten table, or already one of a studio's own: untouched.
                Add(candidate);
                continue;
            }

            if (TableOf(tables, candidate.Schema, candidate.Table) is { Mapping: not null } table && candidate.Columns.Count == 1
                && Replace(table, candidate.Columns[0]) is { } replacement)
            {
                Add(replacement);
            }

            // Anything else on a Marten table is dropped: no line declares it, and one made by hand is dropped by the next apply.
        }

        return offered;

        void Add(HypotheticalIndex index)
        {
            if (definitions.Add(index.Definition))
            {
                offered.Add(index);
            }
        }
    }

    /// <summary>The visible, declared document table at <paramref name="schema" />.<paramref name="name" />, or <see langword="null" />.</summary>
    private static MartenPlanTable? TableOf(IReadOnlyList<MartenPlanTable> tables, string? schema, string name) =>
        schema is null
            ? null
            : tables.FirstOrDefault(x => string.Equals(x.Schema, schema, StringComparison.Ordinal) && string.Equals(x.Table, name, StringComparison.Ordinal));

    /// <summary>
    /// The index Marten would create for the line that declares an index on <paramref name="column" />: <c>Index(x =&gt; x.Member)</c>
    /// on a physical duplicated field's column, or <c>IndexLastModified()</c> on the last-modified column - or
    /// <see langword="null" /> when no line does.
    /// </summary>
    private static HypotheticalIndex? Replace(MartenPlanTable table, string column)
    {
        IDocumentType mapping = table.Mapping!;

        if (MartenDuplicatedFields.Physical(mapping).FirstOrDefault(x => string.Equals(x.ColumnName, column, StringComparison.Ordinal)) is { Members.Length: > 0 } duplicated
            && MartenIndexDdl.Computed(mapping, duplicated.Members) is { } computed)
        {
            string line = Line(table, "Index(x => x." + string.Join('.', duplicated.Members.Select(static x => x.Name)) + ")");
            return Make(table, computed, line);
        }

        if (string.Equals(table.Info.MetadataColumnName(DocumentMetadataColumn.LastModified), column, StringComparison.Ordinal)
            && MartenIndexDdl.OnColumn(mapping, column) is { } onColumn)
        {
            return Make(table, onColumn, Line(table, "IndexLastModified()"));
        }

        return null;
    }

    /// <summary>
    /// The index for <paramref name="line" /> - unless the store's configuration <em>already declares</em> an index of that name (a
    /// <c>Duplicate()</c> or <c>[DuplicateField]</c> always declares its own, and <c>IndexLastModified()</c> may be declared): then
    /// the line is no remedy, because it is already written. The index is missing from the database, and applying the schema creates
    /// it - so the candidate has <b>no line</b> and says so (<see cref="MartenIndexDdl.AlreadyDeclaredNote" />).
    /// </summary>
    private static HypotheticalIndex Make(MartenPlanTable table, string definition, string line)
    {
        HypotheticalIndex index = MartenIndexDdl.Index("advisor:" + table.Schema + "." + table.Table + ":" + line, definition, line, table.Info, duplicate: false);

        return DeclaredName(definition) is { } name && table.Indexes.Any(x => string.Equals(x.Name, name, StringComparison.Ordinal))
            ? index with { Line = null, Caption = "The index " + name + ", which the store already declares", Note = MartenIndexDdl.AlreadyDeclaredNote }
            : index;
    }

    /// <summary>The name in Marten's <c>CREATE INDEX name ON …</c>, or <see langword="null" />.</summary>
    private static string? DeclaredName(string definition)
    {
        const string Prefix = "CREATE INDEX ";
        const string UniquePrefix = "CREATE UNIQUE INDEX ";

        string rest = definition.StartsWith(Prefix, StringComparison.Ordinal) ? definition[Prefix.Length..]
            : definition.StartsWith(UniquePrefix, StringComparison.Ordinal) ? definition[UniquePrefix.Length..]
            : string.Empty;

        int end = rest.IndexOf(' ', StringComparison.Ordinal);
        return end > 0 ? rest[..end] : null;
    }

    private static string Line(MartenPlanTable table, string call) => "options.Schema.For<" + table.TypeName + ">()." + call + ";";
}
