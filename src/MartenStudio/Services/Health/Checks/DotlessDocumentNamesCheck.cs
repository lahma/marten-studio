using System.Globalization;

using Marten;
using Marten.Schema;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Schema;

namespace MartenStudio.Services.Health.Checks;

/// <summary>One table or function whose name carries a dotless <c>ı</c>, as K09 judges it.</summary>
/// <param name="IsFunction">A function rather than a table.</param>
/// <param name="Schema">Its schema.</param>
/// <param name="Name">Its name, as the catalog holds it.</param>
/// <param name="HasInvariantTwin">Whether the same kind of object exists under the invariant spelling.</param>
/// <param name="DocumentTypes">
/// The document type this process knows that the name belongs to, or none: a finding about a hidden type is dropped by the
/// sweep, and one the process cannot attribute to a visible type is not named while the host hides any.
/// </param>
/// <param name="Listable">Whether the name may be put in a finding.</param>
internal sealed record DotlessName(
    bool IsFunction,
    string Schema,
    string Name,
    bool HasInvariantTwin,
    IReadOnlyList<Type> DocumentTypes,
    bool Listable)
{
    /// <summary>The name with every dotless letter replaced: what Marten 9.55 and later calls the object.</summary>
    public string Invariant => Name.Replace(DotlessNameQueries.Dotless, DotlessNameQueries.Plain, StringComparison.Ordinal);

    /// <summary>The key a finding about it carries: <c>schema.name</c>.</summary>
    public string Key => SchemaKey.For(Schema, Name);

    /// <summary>
    /// For a document type's function, the document table that shares its stem (<c>mt_upsert_ınvoice</c>, <c>mt_doc_ınvoice</c>),
    /// or <see langword="null" /> for any other function, and for a table.
    /// </summary>
    public string? DocumentTable
    {
        get
        {
            if (!IsFunction)
            {
                return null;
            }

            foreach (string prefix in DotlessNameQueries.FunctionPrefixes)
            {
                if (Name.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return "mt_doc_" + Name[prefix.Length..];
                }
            }

            return null;
        }
    }
}

/// <summary>What K09 reads.</summary>
/// <param name="Names">The tables and functions named with a dotless <c>ı</c> in the store's schemas, at most the query's limit of each kind.</param>
/// <param name="More">Whether the read had more than it kept.</param>
/// <param name="SchemasRead">How many of the store's schemas were read.</param>
internal sealed record DotlessDocumentNamesFacts(IReadOnlyList<DotlessName> Names, bool More, int SchemasRead);

/// <summary>
/// K09: document storage named under a Turkish or Azerbaijani culture, which Marten 9.55 no longer names that way.
/// </summary>
/// <remarks>
/// <para>
/// Marten 9.55 lowers document aliases, <c>DdlTemplate</c> lookups and flat-table upsert function names with the invariant
/// culture (marten#5778, #5783). Before it, a host under <c>tr-TR</c> or <c>az-AZ</c> got <c>mt_doc_ınvoice</c> (U+0131) for
/// <c>Invoice</c>; from 9.55 Marten expects <c>mt_doc_invoice</c> and creates it, empty. So the hazard is the upgrade, and the
/// check runs at every Marten version: it finds the objects <em>now</em>, whichever Marten wrote them.
/// </para>
/// <para>
/// Two variants: <see cref="DotlessOnlyVariant" /> (no invariant twin: the documents will appear missing, or already do on a
/// 9.55 that has not yet touched the type) and <see cref="BothVariant" /> (both exist: the documents are split between two
/// tables). A document type's <c>mt_upsert_</c>, <c>mt_insert_</c>, <c>mt_update_</c> and <c>mt_overwrite_</c> functions are
/// listed in its table's finding; any other function with a dotless letter - a flat-table projection's - is a finding of its
/// own.
/// </para>
/// <para>
/// <b>Hidden document types.</b> A table name is the type's name. A name attributed to a known hidden type is dropped with the
/// type's other findings (<see cref="HealthFinding.DocumentTypes" />); and when the host hides any type, a name the process
/// cannot attribute to a visible one - a type it has not loaded yet may be the hidden one - is counted and never named,
/// as T17 does.
/// </para>
/// <para>
/// One catalog read (<see cref="DotlessNameQueries.RowsSql" />) over the store's own schemas, locking nothing it lists. The
/// remedy is text: the studio renames nothing.
/// </para>
/// </remarks>
[HealthCheck("K09", HealthArea.Configuration, HealthCostTier.Catalog, "Document storage named under a Turkish or Azerbaijani culture",
    Gate = HealthGate.Database, Variants = [DotlessOnlyVariant, BothVariant])]
internal sealed class DotlessDocumentNamesCheck : HealthCheck<DotlessDocumentNamesFacts>
{
    /// <summary>The variant of a name with no invariant twin: the documents will appear missing.</summary>
    internal const string DotlessOnlyVariant = "dotless-only";

    /// <summary>The variant of a name whose invariant twin exists too: the documents are split.</summary>
    internal const string BothVariant = "both";

    /// <summary>The first Marten that names these objects with the invariant culture.</summary>
    internal static readonly Version InvariantFrom = new(9, 55, 0);

    /// <summary>The subject of the finding that counts what the host's document-type filter keeps from being named.</summary>
    internal const string Unlisted = "(not listed)";

    /// <inheritdoc />
    public override async Task<DotlessDocumentNamesFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        MartenTableSet declared = await MartenTables.SetAsync(context).ConfigureAwait(false);
        IReadOnlyList<string> schemas = declared.Declarations.Schemas;

        if (schemas.Count == 0)
        {
            return new DotlessDocumentNamesFacts([], false, 0);
        }

        IReadOnlyList<DotlessNameRow> rows = await context.Sql
            .QueryAsync(DotlessNameQueries.Rows(schemas), DotlessNameQueries.ReadRow, cancellationToken)
            .ConfigureAwait(false);

        bool more = rows.Count(static x => !x.IsFunction) > DotlessNameQueries.Limit || rows.Count(static x => x.IsFunction) > DotlessNameQueries.Limit;
        IEnumerable<DotlessNameRow> kept = rows.Where(static x => !x.IsFunction).Take(DotlessNameQueries.Limit)
            .Concat(rows.Where(static x => x.IsFunction).Take(DotlessNameQueries.Limit));

        // What the process knows: a document type by its table, under the name Marten declares now.
        bool hidesTypes = context.Settings.IsDocumentTypeVisible is not null;
        bool martenWritesInvariant = WritesInvariantNames(context.Runtime, CultureInfo.CurrentCulture);
        IReadOnlyList<DotlessName> names = NamesToJudge(kept, KnownTypes(context.StoreOptions), hidesTypes, martenWritesInvariant);

        return new DotlessDocumentNamesFacts(names, more, schemas.Count);
    }

    /// <summary>
    /// Whether this process's Marten lowers a document's name with a culture that leaves a literal <c>ı</c> as it is and makes
    /// an <c>I</c> an <c>i</c>: from Marten 9.55 always (invariant), before it only when the process's culture lowers
    /// <c>I</c> to <c>i</c>. Then a table whose name a known type declares today is a name Marten writes itself.
    /// </summary>
    /// <param name="runtime">The Marten the host runs.</param>
    /// <param name="culture">The process's culture, which Marten before 9.55 lowered with.</param>
    internal static bool WritesInvariantNames(MartenRuntime runtime, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(culture);

        return runtime.VersionOf(RuntimePackage.Marten) is { } marten && marten >= InvariantFrom
            || culture.TextInfo.ToLower('I') == 'i';
    }

    /// <summary>The document types this process knows, by <c>schema.table</c>, under the table name Marten declares now.</summary>
    /// <param name="options">The store's options.</param>
    internal static IReadOnlyDictionary<string, IDocumentType> KnownTypes(IReadOnlyStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Dictionary<string, IDocumentType> known = new(StringComparer.Ordinal);

        foreach (IDocumentType documentType in options.AllKnownDocumentTypes())
        {
            string schema = string.IsNullOrWhiteSpace(documentType.TableName.Schema) ? documentType.DatabaseSchemaName : documentType.TableName.Schema;
            known.TryAdd(SchemaKey.For(schema, documentType.TableName.Name), documentType);
        }

        return known;
    }

    /// <summary>
    /// Turns the catalog's rows into the names K09 judges: drops what Marten writes itself, and attributes the rest to a known
    /// document type when there is one.
    /// </summary>
    /// <remarks>
    /// A document type may carry a literal <c>ı</c> in its own name (<c>Kayıt</c>, <c>Satış</c>), and Marten keeps it at every
    /// version: <c>ToLowerInvariant()</c> leaves it alone. Such a table, and the functions that share its stem, are healthy,
    /// and renaming them to <c>mt_doc_kayit</c> would lose their documents. So a row whose schema and name are exactly what a
    /// known type's table is called today is skipped while <paramref name="martenWritesInvariant" /> says this process's Marten
    /// writes that exact name. A name no known type claims is judged by its letters alone.
    /// </remarks>
    /// <param name="rows">What the catalog read returned.</param>
    /// <param name="known">The document types this process knows (<see cref="KnownTypes" />).</param>
    /// <param name="hidesTypes">Whether the host hides any document type.</param>
    /// <param name="martenWritesInvariant"><see cref="WritesInvariantNames" />.</param>
    internal static IReadOnlyList<DotlessName> NamesToJudge(
        IEnumerable<DotlessNameRow> rows,
        IReadOnlyDictionary<string, IDocumentType> known,
        bool hidesTypes,
        bool martenWritesInvariant)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(known);

        List<DotlessName> names = [];

        foreach (DotlessNameRow row in rows)
        {
            DotlessName probe = new(row.IsFunction, row.Schema, row.Name, row.HasInvariantTwin, [], true);
            string? table = row.IsFunction ? probe.DocumentTable : row.Name;
            IDocumentType? owner = null;
            Type? type = null;

            if (table is not null)
            {
                string invariant = table.Replace(DotlessNameQueries.Dotless, DotlessNameQueries.Plain, StringComparison.Ordinal);
                IDocumentType? exact = known.GetValueOrDefault(SchemaKey.For(row.Schema, table));

                if (exact is not null && martenWritesInvariant && string.Equals(exact.TableName.Name, table, StringComparison.Ordinal))
                {
                    // Marten names this table itself (a type called Kayıt): nothing to rename.
                    continue;
                }

                owner = exact ?? known.GetValueOrDefault(SchemaKey.For(row.Schema, invariant));
                type = owner?.DocumentType;
            }

            // A name attributed to a known type goes into a finding that carries the type, which the sweep drops while the host
            // hides it; one attributed to none is named only when the host hides no type at all.
            bool listable = type is not null || !hidesTypes;
            names.Add(probe with { DocumentTypes = type is null ? [] : [type], Listable = listable });
        }

        return names;
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(DotlessDocumentNamesFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (facts.Names.Count == 0)
        {
            return HealthVerdict.Quiet(facts.SchemasRead == 0 ? "The store declares no schema to read." : null);
        }

        bool invariantMarten = evaluation.Runtime.VersionOf(RuntimePackage.Marten) is { } marten && marten >= InvariantFrom;
        List<HealthFinding> findings = [];

        IReadOnlyList<DotlessName> tables = [.. facts.Names.Where(static x => !x.IsFunction && x.Listable)];
        HashSet<string> attached = new(StringComparer.Ordinal);

        foreach (DotlessName table in tables)
        {
            DotlessName[] functions =
            [
                .. facts.Names.Where(x => x.IsFunction && x.Listable && x.Schema == table.Schema && x.DocumentTable == table.Name),
            ];

            foreach (DotlessName function in functions)
            {
                attached.Add(function.Key);
            }

            findings.Add(Finding(table, functions, invariantMarten, facts.More));
        }

        foreach (DotlessName function in facts.Names.Where(x => x.IsFunction && x.Listable && !attached.Contains(x.Key)))
        {
            findings.Add(Finding(function, [], invariantMarten, facts.More));
        }

        // Counted, never named: the host hides document types, and these may be one's.
        foreach (IGrouping<bool, DotlessName> group in facts.Names.Where(static x => !x.Listable).GroupBy(static x => x.HasInvariantTwin))
        {
            findings.Add(UnlistedFinding(group.Key, group.Count(), group.Count(static x => !x.IsFunction), invariantMarten));
        }

        return findings.Count == 0 ? HealthVerdict.Quiet() : HealthVerdict.Of([.. findings]);
    }

    private static HealthFinding Finding(DotlessName name, DotlessName[] functions, bool invariantMarten, bool more)
    {
        string kind = name.IsFunction ? "function" : "table";
        string subject = name.Key;
        bool both = name.HasInvariantTwin;

        string title = both
            ? "The " + kind + " " + subject + " exists under a dotless ı and under its invariant name"
                + (name.IsFunction ? ": Marten 9.55 and later calls the invariant one" : ": the documents are split between two tables")
            : invariantMarten
                ? "The " + kind + " " + subject + " is named with a dotless ı, and this Marten looks for the invariant name"
                    + (name.IsFunction ? "" : ": its documents appear missing")
                : "The " + kind + " " + subject + " is named with a dotless ı: after upgrading to Marten 9.55 "
                    + (name.IsFunction ? "Marten will not find it" : "these documents will appear missing");

        List<HealthEvidence> evidence =
        [
            new("Name", name.Name),
            new("Invariant name", name.Invariant + (both ? " (exists too)" : " (does not exist)")),
        ];

        if (functions.Length > 0)
        {
            evidence.Add(new HealthEvidence(
                "Functions",
                string.Join(", ", functions.OrderBy(static x => x.Name, StringComparer.Ordinal).Select(static x => x.Name + (x.HasInvariantTwin ? " (invariant twin exists)" : "")))));
        }

        if (more)
        {
            evidence.Add(new HealthEvidence("More", "The read stopped at " + DotlessNameQueries.Limit + " of a kind; more objects are named this way."));
        }

        return new HealthFinding
        {
            Code = "K09",
            Severity = HealthSeverity.Warning,
            Variant = both ? BothVariant : DotlessOnlyVariant,
            Subject = subject,
            DocumentTypes = name.DocumentTypes,
            Title = title,
            WhyItMatters = Why(both, invariantMarten),
            Evidence = evidence,
            Caveat =
                "Read from the catalog: tables and functions of the store's own schemas named mt_, with a dotless ı (U+0131) in the name. "
                + "A name that a document type this process knows declares today, in a Marten that writes that name itself, is left out "
                + "(a type called Kayıt keeps its ı at every version). For a name no known type claims, the studio judges the letters "
                + "alone: rename only where the ı came from a capital I. "
                + "A function the studio cannot attribute to a document type is listed on its own. Only document tables and the "
                + "mt_upsert_, mt_insert_, mt_update_ and mt_overwrite_ functions are read; an index, a constraint or a sequence that "
                + "carries the letter follows its table's name and is not looked for.",
            Remedy = Remedy(),
        };
    }

    private static HealthFinding UnlistedFinding(bool both, int count, int tables, bool invariantMarten) => new()
    {
        Code = "K09",
        Severity = HealthSeverity.Warning,
        Variant = both ? BothVariant : DotlessOnlyVariant,
        Subject = Unlisted,
        Title = count == 1
            ? "1 table or function is named with a dotless ı" + (both ? " and its invariant twin exists" : "") + ", and is not named here"
            : count + " tables or functions are named with a dotless ı" + (both ? " and their invariant twins exist" : "") + ", and are not named here",
        WhyItMatters = Why(both, invariantMarten)
            + " The host hides some document types from the studio (IsDocumentTypeVisible), and these may belong to one, so their names are not shown.",
        Evidence =
        [
            new HealthEvidence("Objects", count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new HealthEvidence("Tables", tables.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new HealthEvidence("Functions", (count - tables).ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ],
        Caveat = "Counted from the catalog and never named, because a document type this process has not loaded yet may be one the host hides.",
        Remedy = Remedy(),
    };

    private static string Why(bool both, bool invariantMarten) =>
        both
            ? "Marten 9.55 (marten#5778, #5783) lowers a document's alias and its upsert function names with the invariant culture, where a "
                + "host under tr-TR or az-AZ used to get a dotless ı for each capital I. Both spellings exist here, so writes go to one table "
                + "and reads to the other, depending on which Marten wrote the row: the documents are split between two tables, and each "
                + "half looks like the whole to the code that reads it."
            : invariantMarten
                ? "Marten 9.55 and later (marten#5778, #5783) lowers a document's alias and its upsert function names with the invariant "
                    + "culture, where a host under tr-TR or az-AZ used to get a dotless ı for each capital I. This Marten therefore looks for the "
                    + "invariant name, finds nothing, and creates it empty when the type is first used: the existing documents are still in "
                    + "the old table, and the application does not see them."
                : "Marten 9.55 (marten#5778, #5783) lowers a document's alias and its upsert function names with the invariant culture, where "
                    + "a host under tr-TR or az-AZ used to get a dotless ı for each capital I. After the upgrade Marten looks for the invariant "
                    + "name, finds nothing, and creates it empty: the existing documents stay in the old table, and the application does not "
                    + "see them. Nothing is lost, and nothing is migrated.";

    private static HealthRemedy Remedy() => new(
        """
        // Before upgrading Marten, with the application stopped, in your own migration:
        // 1. rename each table to the invariant name (mt_doc_ınvoice -> mt_doc_invoice);
        // 2. drop the dotless functions (mt_upsert_, mt_insert_, mt_update_ and mt_overwrite_ + the same stem);
        // 3. apply the schema, so Marten recreates the functions under the invariant name.
        """,
        "Marten owns these names, so the studio renames and drops nothing. Do not rename the functions: a renamed function "
        + "still writes to the old dotless table inside its body, so it has to be dropped and recreated. Where the invariant table "
        + "exists already (variant both), its rows have to be merged by whoever knows which half is current before the dotless table "
        + "goes. Rename only a name whose ı came from a capital I.");
}
