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
            string member = string.Join('.', duplicated.Members.Select(static x => x.Name));

            return Make(
                table,
                column,
                computed,
                Line(table, "Index(x => x." + member + ")"),
                name => (MartenIndexDdl.Computed(mapping, duplicated.Members, name), Line(table, "Index(x => x." + member + ", i => i.Name = \"" + name + "\")")));
        }

        if (string.Equals(table.Info.MetadataColumnName(DocumentMetadataColumn.LastModified), column, StringComparison.Ordinal)
            && MartenIndexDdl.OnColumn(mapping, column) is { } onColumn)
        {
            return Make(
                table,
                column,
                onColumn,
                Line(table, "IndexLastModified()"),
                name => (MartenIndexDdl.OnColumn(mapping, column, name: name), Line(table, "IndexLastModified(i => i.Name = \"" + name + "\")")));
        }

        return null;
    }

    /// <summary>
    /// The index for <paramref name="line" /> - unless the store's configuration <em>already declares</em> an index on that very
    /// column (a <c>Duplicate()</c> or <c>[DuplicateField]</c> always declares its own, under whatever name, and
    /// <c>IndexLastModified()</c> may be declared): then the line is no remedy, because it is already written. The index is missing
    /// from the database, and applying the schema creates it - so the candidate has <b>no line</b> and says so
    /// (<see cref="MartenIndexDdl.AlreadyDeclaredNote" />), and what is imagined is the declared index's own DDL, so the what-if tests
    /// what an apply would create.
    /// </summary>
    private static HypotheticalIndex Make(
        MartenPlanTable table,
        string column,
        string definition,
        string line,
        Func<string, (string? Definition, string Line)> named)
    {
        HypotheticalIndex index = MartenIndexDdl.Index("advisor:" + table.Schema + "." + table.Table + ":" + line, definition, line, table.Info, duplicate: false);

        if (DeclaredOn(table, column) is { } declared)
        {
            return index with
            {
                Definition = declared.Definition,
                Line = null,
                Caption = "The index " + declared.Name + ", which the store already declares",
                Note = MartenIndexDdl.AlreadyDeclaredNote,
            };
        }

        // An index of the line's own derived name that is not the plain full b-tree (partial, hash, another method) would collide with the
        // line: the apply would find the name taken. The line then names its index explicitly, and the what-if imagines that one.
        if (DeclaredName(definition) is { } derived
            && table.Indexes.FirstOrDefault(x => string.Equals(x.Name, derived, StringComparison.Ordinal)) is { } taken)
        {
            string name = derived + "_full";
            (string? namedDefinition, string namedLine) = named(name);

            if (namedDefinition is not null)
            {
                string kind = !string.IsNullOrWhiteSpace(taken.Predicate) ? "partial" : !taken.IsBtree ? "non-b-tree" : "differently shaped";

                return MartenIndexDdl.Index("advisor:" + table.Schema + "." + table.Table + ":" + namedLine, namedDefinition, namedLine, table.Info, duplicate: false) with
                {
                    Note = "The store declares a " + kind + " index named " + derived + " on this column; a full b-tree needs a name of its own. "
                        + MartenIndexDdl.DeclareNote,
                };
            }
        }

        return index;
    }

    /// <summary>
    /// The index the mapping declares on exactly <paramref name="column" />, whatever it is named: a plain b-tree over that one
    /// column, not partial. A partial or multi-column index does not serve the same lookups, so it is not "the" declared index.
    /// </summary>
    private static MartenDeclaredIndex? DeclaredOn(MartenPlanTable table, string column) =>
        table.Indexes.FirstOrDefault(x => x.IsBtree && string.IsNullOrWhiteSpace(x.Predicate) && x.Columns.Count == 1
            && string.Equals(Bare(x.Columns[0]), column, StringComparison.OrdinalIgnoreCase)
            && x.Definition.StartsWith("CREATE ", StringComparison.Ordinal));

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

    /// <summary>A column as an index holds it: without the parentheses and quotes an expression index and a quoted name add.</summary>
    private static string Bare(string column) => column.Trim().Trim('(', ')').Trim().Trim('"').Trim();

    private static string Line(MartenPlanTable table, string call) => "options.Schema.For<" + table.TypeName + ">()." + call + ";";
}
