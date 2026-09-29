using MartenStudio.Internal.Sql;
using MartenStudio.Services.Database;

namespace MartenStudio.Services.Relationships;

/// <summary>
/// One document type, as the relationships graph draws it.
/// </summary>
/// <param name="Alias">Marten's document alias — the segment the collection's URL carries, and the node's key.</param>
/// <param name="DisplayName">The .NET type's short name, drawn under the alias.</param>
/// <param name="Hue">The collection's stable colour, from <see cref="CollectionColorizer"/>.</param>
/// <param name="Count">
/// How many documents there are. A <c>pg_class.reltuples</c> estimate (D8) and never a <c>count(*)</c>:
/// this screen draws every collection of the store at once, so an exact count per node would be one
/// sequential scan per document type per navigation.
/// </param>
/// <param name="IsHierarchyRoot">Whether this table holds more than one .NET type.</param>
/// <param name="SubclassAliases">The subclasses sharing this table, for a hierarchy root.</param>
internal sealed record RelationshipNode(
    string Alias,
    string DisplayName,
    int Hue,
    DocumentCount Count,
    bool IsHierarchyRoot,
    IReadOnlyList<string> SubclassAliases)
{
    /// <summary>The schema of the document type's table, or <see langword="null" /> when not known.</summary>
    public string? Schema { get; init; }

    /// <summary>The document type's table, or <see langword="null" /> when not known.</summary>
    /// <remarks>What the neighbourhood graph finds a document node by when it is handed a table name.</remarks>
    public string? Table { get; init; }
}

/// <summary>
/// One table the studio does not map as a document type - somebody else's, or a relational table Marten
/// manages for a projection - as the relationships graph draws it.
/// </summary>
/// <remarks>
/// Only ever a table in a schema the visitor may see the structure of (the database browser's gate,
/// D27), and never a document table, an event table or Marten's own bookkeeping: those are either
/// drawn as documents or not drawn at all.
/// </remarks>
/// <param name="Key">
/// The node's key: the schema and the name joined by a NUL, which no identifier and no alias can contain -
/// so two quoted names that differ only by case, or a schema and a name that contain dots, never collide.
/// </param>
/// <param name="Schema">The schema, as the catalog has it.</param>
/// <param name="Name">The table, as the catalog has it.</param>
/// <param name="Hue">A stable colour for the <em>schema</em>, so the tables of one schema read as one group.</param>
/// <param name="Count">The <c>reltuples</c> estimate, or unavailable when the catalog read did not reach it.</param>
/// <param name="Owner">Whose it is: <c>Other</c>, or <c>MartenProjectionOrExtended</c>.</param>
/// <param name="RecognisedAs">A neutral "recognised by name" hint - Quartz.NET, Wolverine - or <see langword="null" />.</param>
/// <param name="InStoreSchema">Whether it sits in one of the store's own schemas.</param>
internal sealed record RelationshipTableNode(
    string Key,
    string Schema,
    string Name,
    int Hue,
    DocumentCount Count,
    DatabaseObjectOwner Owner,
    string? RecognisedAs,
    bool InStoreSchema)
{
    /// <summary>The qualified name a person reads.</summary>
    public string QualifiedName => Schema + "." + Name;

    /// <summary>The key a table is drawn under.</summary>
    public static string KeyFor(string schema, string name) => schema + "\u0000" + name;

    /// <summary>Whether a node key is a table's rather than a document alias.</summary>
    public static bool IsTableKey(string key) => key.Contains('\u0000', StringComparison.Ordinal);

    /// <summary>
    /// A schema's colour: the collection ring, hashed from the schema's name in a namespace of its own, so a
    /// schema and a collection alias that happen to share a name are not given one colour by construction -
    /// and so the sample's <c>quartz</c> and <c>legacy</c>, which share a slot as bare names, do not.
    /// </summary>
    public static int SchemaHue(string schema) => CollectionColorizer.HueFor("schema:" + schema);

    /// <summary>What the node's third line says about its owner.</summary>
    public string? OwnerHint => Owner == DatabaseObjectOwner.MartenProjectionOrExtended ? "Marten-managed" : RecognisedAs;
}

/// <summary>
/// One foreign key between two of the nodes the graph draws, with what the configuration says and what
/// the database has held up against each other.
/// </summary>
/// <remarks>
/// <para>
/// The two booleans are the whole point of the screen for a key <em>on a document table</em>.
/// <c>Declared &amp;&amp; Physical</c> is a key that is both configured and there. <c>Declared &amp;&amp;
/// !Physical</c> is drift — the host wrote <c>ForeignKey&lt;T&gt;()</c> and nobody applied the migration,
/// so nothing is enforcing it; <c>!Declared &amp;&amp; Physical</c> is a constraint somebody added by hand,
/// which Marten does not know about and which <c>CreateOrUpdate</c> would drop on the next apply.
/// </para>
/// <para>
/// A key <em>on a table the studio does not map</em> has no declared half - nothing in
/// <c>StoreOptions</c> describes somebody else's table - so its state is whether Postgres has validated
/// it: <see cref="Validated" /> is <see langword="false" /> for a key added <c>NOT VALID</c>.
/// </para>
/// </remarks>
/// <param name="FromAlias">The node that points: a document alias, or a <see cref="RelationshipTableNode.Key" />.</param>
/// <param name="ToAlias">The node pointed at, likewise.</param>
/// <param name="Column">
/// The edge's label: for a key between two document types, the column the key is really about (the
/// tenant half of a conjoined key left out); for a key with a table at either end, its columns with the
/// leading ones both primary keys share stripped (<see cref="RelationshipGraphBuilder.LabelFor" />).
/// </param>
/// <param name="Member">The .NET member behind the column, when Marten duplicated one into it.</param>
/// <param name="Declared">Whether <c>StoreOptions</c> declares this key.</param>
/// <param name="Physical">Whether Postgres has the constraint.</param>
/// <param name="OnDelete">The delete action, as <c>CascadeAction</c> spells it.</param>
/// <param name="ConstraintName">The constraint's name, when one end or the other named it.</param>
internal sealed record RelationshipEdge(
    string FromAlias,
    string ToAlias,
    string Column,
    string? Member,
    bool Declared,
    bool Physical,
    string OnDelete,
    string? ConstraintName = null)
{
    /// <summary>Every pointing column, in constraint order, comma separated; empty when only the label is known.</summary>
    public string Columns { get; init; } = string.Empty;

    /// <summary>Every referenced column, in constraint order, comma separated.</summary>
    public string LinkedColumns { get; init; } = string.Empty;

    /// <summary>Whether Postgres has validated the key's existing rows (<see langword="false" /> for <c>NOT VALID</c>).</summary>
    public bool Validated { get; init; } = true;

    /// <summary>Whether the pointing end is a table rather than a document type.</summary>
    public bool FromIsTable { get; init; }

    /// <summary>Whether the referenced end is a table rather than a document type.</summary>
    public bool ToIsTable { get; init; }

    /// <summary>Whether either end is a table - the edges the Tables view draws.</summary>
    public bool IsTableEdge => FromIsTable || ToIsTable;

    /// <summary>
    /// Whether the constraint lives on a document table, which Marten manages and a migration can change -
    /// what makes the declared/physical drift styles meaningful for it.
    /// </summary>
    public bool OnDocumentTable => !FromIsTable;

    /// <summary>Every pointing column, falling back to the label when the list is not known.</summary>
    public string AllColumns => Columns.Length > 0 ? Columns : Column;

    /// <summary>Whether this key points at the node it is declared on. Ordinal: two tables whose names differ by case are two tables.</summary>
    public bool IsSelfReference => string.Equals(FromAlias, ToAlias, StringComparison.Ordinal);

    /// <summary>How the state reads on the screen and to a screen reader.</summary>
    public string State => OnDocumentTable
        ? (Declared, Physical) switch
        {
            (true, true) => "declared and physical",
            (true, false) => "declared only",
            (false, true) => "physical only",
            _ => "unknown",
        }
        : Validated ? "enforced" : "not valid";
}

/// <summary>
/// A foreign key the graph cannot draw, because one of its ends is not a node - and both of its ends are
/// in schemas the visitor may see, so naming them tells nobody anything they could not already read.
/// </summary>
/// <remarks>
/// Listed rather than dropped: a key into a table the studio cannot place is exactly the kind of thing
/// somebody needs to know about. A key whose end belongs to a type the host hid with
/// <c>IsDocumentTypeVisible</c> is <em>not</em> here — hidden is hidden everywhere, including in the list of
/// what could not be drawn — and neither is one with an end in a schema the visitor is not shown: that one
/// is a <see cref="WithheldForeignKey" />, counted and never named.
/// </remarks>
/// <param name="Name">The constraint name.</param>
/// <param name="From">The pointing table, qualified, or its alias when it is a known collection.</param>
/// <param name="Columns">The key's columns, comma separated.</param>
/// <param name="To">The referenced table, qualified, or the admission that it could not be resolved.</param>
/// <param name="Declared">Whether <c>StoreOptions</c> declares it.</param>
/// <param name="Physical">Whether Postgres has it.</param>
/// <param name="Reason">Why it is not on the picture.</param>
internal sealed record UnmatchedForeignKey(
    string Name,
    string From,
    string Columns,
    string To,
    bool Declared,
    bool Physical,
    string Reason)
{
    /// <summary>The pointing table's schema, as the catalog or the configuration spells it.</summary>
    public string? FromSchema { get; init; }

    /// <summary>The pointing table, likewise.</summary>
    public string? FromTable { get; init; }

    /// <summary>The referenced table's schema, or <see langword="null" /> when the key names no table.</summary>
    public string? ToSchema { get; init; }

    /// <summary>The referenced table, or <see langword="null" /> when the key names no table.</summary>
    public string? ToTable { get; init; }

    /// <summary>
    /// Whether this key is on, or points at, one table - compared ordinally, because two tables whose quoted
    /// names differ only by case are two tables. What the object page's neighbourhood keeps its rows by.
    /// </summary>
    /// <param name="schema">The table's schema, as the catalog has it.</param>
    /// <param name="table">The table, as the catalog has it.</param>
    public bool Touches(string schema, string table) =>
        (string.Equals(FromSchema, schema, StringComparison.Ordinal) && string.Equals(FromTable, table, StringComparison.Ordinal))
        || (string.Equals(ToSchema, schema, StringComparison.Ordinal) && string.Equals(ToTable, table, StringComparison.Ordinal));
}

/// <summary>
/// A foreign key with one end in a schema the visitor is not shown: counted, and deliberately anonymous.
/// </summary>
/// <remarks>
/// Neither the constraint nor the far end is named, not even its schema: "a key from
/// <c>billing.accounts</c> points at this customer" is the withheld schema's structure said out loud to
/// somebody the database browser's gate refused it to (D27). What is kept is only the visible end, so a
/// panel about that end can say "and N more, in schemas you are not shown".
/// </remarks>
/// <param name="VisibleEnd">The key of the visible end's node, or <see langword="null" /> when that end is not a node.</param>
/// <param name="VisibleEndIsTarget">Whether the visible end is the referenced one.</param>
internal sealed record WithheldForeignKey(string? VisibleEnd, bool VisibleEndIsTarget);

/// <summary>
/// Everything the Relationships screen draws: the document types of the scoped store, the tables beside
/// them the visitor may see, the foreign keys between all of those, and the keys that could not be
/// placed on the picture.
/// </summary>
/// <param name="Nodes">The document types, ordered by alias.</param>
/// <param name="Edges">The foreign keys, ordered by source then target then column.</param>
/// <param name="Unmatched">The keys with an end that is not a node, both ends visible.</param>
/// <param name="ReadAt">When the read happened, so the page can say how old the picture is.</param>
/// <param name="Error">
/// What went wrong, or <see langword="null" />. "Cannot report" is a value here as everywhere else
/// (plan §4.8): a graph that could not be read has to say so rather than render as a store with no
/// relationships in it.
/// </param>
internal sealed record RelationshipGraph(
    IReadOnlyList<RelationshipNode> Nodes,
    IReadOnlyList<RelationshipEdge> Edges,
    IReadOnlyList<UnmatchedForeignKey> Unmatched,
    DateTimeOffset ReadAt,
    string? Error = null)
{
    /// <summary>Nothing at all — what a page holds before its first read.</summary>
    public static RelationshipGraph None { get; } = new([], [], [], DateTimeOffset.MinValue);

    /// <summary>
    /// The tables beside the document types, in schemas the visitor may see: every one of them, with
    /// or without a key, ordered by schema then name. Empty when the gate could not be read.
    /// </summary>
    public IReadOnlyList<RelationshipTableNode> Tables { get; init; } = [];

    /// <summary>The keys with one end in a schema the visitor is not shown. Counted, never named.</summary>
    public IReadOnlyList<WithheldForeignKey> Withheld { get; init; } = [];

    /// <summary>
    /// The schemas whose structure this visitor may see - the store's own, then those the database
    /// browser's gate admits. <see langword="null" /> for a graph that says nothing about it.
    /// </summary>
    public IReadOnlyList<string>? VisibleSchemas { get; init; }

    /// <summary>
    /// Why the graph has no tables in it although it might have had: the database browser's gate or the
    /// catalog could not be read, and the graph fell back to the document types alone rather than guess
    /// whose each table is.
    /// </summary>
    public string? TablesUnavailable { get; init; }

    /// <summary>Whether a catalog cap stopped the table list or the key read, so the picture is partial.</summary>
    public bool Truncated { get; init; }

    /// <summary>A graph that could not be read, carrying the reason.</summary>
    public static RelationshipGraph Failed(string error, DateTimeOffset readAt) =>
        new([], [], [], readAt, error);

    /// <summary>Whether there is no foreign key at all to show or to list.</summary>
    public bool IsEmpty => Edges.Count == 0 && Unmatched.Count == 0;

    /// <summary>Whether any table takes part in a drawn key - what shows the Documents · Tables · Both switch.</summary>
    public bool HasTableEdges => Edges.Any(static x => x.IsTableEdge);

    /// <summary>A one-sentence description of the picture, which is the SVG's accessible name.</summary>
    public string Summary
    {
        get
        {
            if (Error is { Length: > 0 })
            {
                return "The relationships of this store could not be read.";
            }

            int tables = CountDrawnTables();

            if (tables > 0)
            {
                return $"A diagram of {Nodes.Count} document types and {tables} tables, and {Edges.Count} foreign " +
                       "keys between them. The table below lists the same relationships.";
            }

            return Edges.Count == 0
                ? $"A diagram of {Nodes.Count} document types with no foreign keys between them."
                : $"A diagram of {Nodes.Count} document types and {Edges.Count} foreign keys between them. " +
                  "The table below lists the same relationships.";
        }
    }

    /// <summary>The table node with this key, or <see langword="null" />.</summary>
    public RelationshipTableNode? FindTable(string key)
    {
        foreach (RelationshipTableNode table in Tables)
        {
            if (string.Equals(table.Key, key, StringComparison.Ordinal))
            {
                return table;
            }
        }

        return null;
    }

    /// <summary>The document node with this alias, or <see langword="null" />.</summary>
    public RelationshipNode? FindDocument(string alias)
    {
        foreach (RelationshipNode node in Nodes)
        {
            if (string.Equals(node.Alias, alias, StringComparison.Ordinal))
            {
                return node;
            }
        }

        return null;
    }

    /// <summary>What a person reads for a node key: an alias as it is, a table as <c>schema.name</c>.</summary>
    public string DisplayName(string key) =>
        FindTable(key)?.QualifiedName ?? key;

    private int CountDrawnTables()
    {
        HashSet<string> drawn = new(StringComparer.Ordinal);

        foreach (RelationshipEdge edge in Edges)
        {
            if (edge.FromIsTable)
            {
                drawn.Add(edge.FromAlias);
            }

            if (edge.ToIsTable)
            {
                drawn.Add(edge.ToAlias);
            }
        }

        return drawn.Count;
    }
}

/// <summary>
/// One collection or table that points at the document on screen, and how many of its rows do.
/// </summary>
/// <param name="FromAlias">The pointing collection's alias, or a pointing table's <c>schema.name</c>.</param>
/// <param name="Column">The column the foreign key is on - the label, for a composite key.</param>
/// <param name="Member">The .NET member behind it, when there is one — this is what the filter uses.</param>
/// <param name="Hue">The pointing collection's colour, or the pointing table's schema's.</param>
/// <param name="Count">How many of its rows point here, up to the cap.</param>
/// <param name="IsCapped">Whether the count stopped at the cap, so the screen says "1000+".</param>
/// <param name="Declared">Whether <c>StoreOptions</c> declares the key.</param>
/// <param name="Physical">Whether Postgres has the constraint.</param>
/// <param name="Error">Why this one could not be counted, or <see langword="null" />.</param>
internal sealed record ReferencedByEntry(
    string FromAlias,
    string Column,
    string? Member,
    int Hue,
    long Count,
    bool IsCapped,
    bool Declared,
    bool Physical,
    string? Error = null)
{
    /// <summary>The pointing table's schema, for a table the studio does not map.</summary>
    public string? Schema { get; init; }

    /// <summary>The pointing table, for a table the studio does not map.</summary>
    public string? Table { get; init; }

    /// <summary>The delete action, as <c>CascadeAction</c> spells it: what deleting this document does to these rows.</summary>
    public string OnDelete { get; init; } = "NoAction";

    /// <summary>Every pointing column, comma separated, when it is more than <see cref="Column" /> says.</summary>
    public string? AllColumns { get; init; }

    /// <summary>
    /// Why the rows were deliberately not counted - the database browser's gate, which a count of a
    /// non-Marten table's rows needs - or <see langword="null" />. Not an error: the key is real and
    /// listed, the number is simply not this visitor's to read.
    /// </summary>
    public string? NotCounted { get; init; }

    /// <summary>Whether the pointing end is a table rather than a collection.</summary>
    public bool IsTable => Table is not null;

    /// <summary>Whether <see cref="Count" /> is a number that may be shown.</summary>
    public bool IsCounted => Error is null && NotCounted is null;
}

/// <summary>
/// What points at one document — the inbound half of the detail page's relationships.
/// </summary>
/// <remarks>
/// The outbound half (<c>RelatedDocuments</c>) is one indexed read per key and is always shown. This half
/// is one bounded count per pointing collection or table, which is why the counts stop at a cap and why it
/// is rendered as its own panel rather than folded into the other.
/// </remarks>
/// <param name="Entries">One per pointing collection or table: collections by alias, then tables by name.</param>
/// <param name="Cap">The count cap, so the page can render "1000+" with the right number in it.</param>
/// <param name="Error">What went wrong, or <see langword="null" />.</param>
internal sealed record ReferencedBy(
    IReadOnlyList<ReferencedByEntry> Entries,
    int Cap = RelationshipQueries.DefaultInboundCap,
    string? Error = null)
{
    /// <summary>Nothing points here — the ordinary answer, and not an error.</summary>
    public static ReferencedBy None { get; } = new([]);

    /// <summary>
    /// How many keys point here from tables in schemas the visitor is not shown. A count and nothing
    /// else: not the constraint, not the table, not the schema (D27).
    /// </summary>
    public int Withheld { get; init; }

    /// <summary>
    /// Whether the foreign-key read stopped at <see cref="RelationshipQueries.DefaultForeignKeyCap" />, so a
    /// key that points here may be missing from <see cref="Entries" /> and from <see cref="Withheld" />.
    /// </summary>
    public bool Truncated { get; init; }

    /// <summary>Whether there is nothing at all to say.</summary>
    public bool IsEmpty => Entries.Count == 0 && Withheld == 0 && Error is null;

    /// <summary>The inbound list could not be read.</summary>
    public static ReferencedBy Failed(string error) => new([], RelationshipQueries.DefaultInboundCap, error);

    /// <summary>
    /// What a hard delete's confirmation adds, or <see langword="null" /> when there is nothing to add: which
    /// rows elsewhere a delete of this document deletes or changes through an <c>ON DELETE</c> action - and,
    /// just as plainly, what it may delete or change that the studio did not count or may not name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A number only where one was made and means that.</b> A non-Marten table's count is a raw count of
    /// exactly the rows the constraint covers, bounded at the cap, so it is said as a number. A document
    /// table's inbound count leaves out soft-deleted rows and other tenants' rows on purpose - it describes
    /// the list it links to - so it is not a count of the rows Postgres would cascade into; the cascade is
    /// still real, so it is said without one, as something the delete <em>may</em> do.
    /// </para>
    /// <para>
    /// <b>Silence is not an answer.</b> A table whose rows this visitor may not count, or whose count failed,
    /// still has its <c>ON DELETE</c> action fired by the delete, so it is named with "not counted" rather
    /// than left out - a visitor who may delete documents but not browse the database would otherwise delete
    /// a customer and cascade into <c>legacy.customer_credit</c> without a word. A key from a schema the
    /// visitor is not shown is counted and never named, as everywhere else (D27). A key read that stopped at
    /// its cap, or failed, says so, because either means the list above is not the whole story.
    /// </para>
    /// <para>
    /// A soft delete is an update and fires no <c>ON DELETE</c> action at all, which is why the caller asks
    /// for this only for a hard delete. <c>NO ACTION</c> and <c>RESTRICT</c> change no rows - Postgres
    /// refuses the delete instead, and says so - so they are not part of the sentence.
    /// </para>
    /// </remarks>
    public string? DeleteConsequence()
    {
        List<string> certain = [];
        List<string> possible = [];

        foreach (ReferencedByEntry entry in Entries)
        {
            // What the action does to a pointing row: removes it, or sets its key columns to something.
            (bool Deletes, string SetTo, string Clause)? action = entry.OnDelete switch
            {
                "Cascade" => (true, string.Empty, "ON DELETE CASCADE"),
                "SetNull" => (false, "null", "ON DELETE SET NULL"),
                "SetDefault" => (false, "its default", "ON DELETE SET DEFAULT"),
                _ => null,
            };

            if (action is not { } known)
            {
                continue;
            }

            (bool deletes, string setTo, string clause) = known;
            string columns = entry.AllColumns ?? entry.Column;

            if (!entry.IsTable)
            {
                possible.Add(deletes
                    ? $"delete the {entry.FromAlias} documents that point at it ({clause})"
                    : $"set {columns} to {setTo} in the {entry.FromAlias} documents that point at it ({clause})");

                continue;
            }

            string table = entry.Schema + "." + entry.Table;

            if (!entry.IsCounted)
            {
                possible.Add(deletes
                    ? $"delete rows in {table} ({clause}, not counted)"
                    : $"set {columns} to {setTo} in rows of {table} ({clause}, not counted)");

                continue;
            }

            if (entry.Count <= 0)
            {
                continue;
            }

            string rows = RowsText(entry);

            certain.Add(deletes
                ? $"deletes {rows} in {table} ({clause})"
                : $"sets {columns} to {setTo} in {rows} of {table} ({clause})");
        }

        List<string> sentences = [];

        if (certain.Count > 0)
        {
            sentences.Add("Deleting this document also " + string.Join(", and ", certain) + ".");
        }

        if (possible.Count > 0)
        {
            sentences.Add((certain.Count > 0 ? "It may also " : "Deleting this document may also ") + string.Join(", and ", possible) + ".");
        }

        if (Withheld > 0)
        {
            sentences.Add(Withheld == 1
                ? "1 foreign key from a schema you are not shown points at this document; deleting it may delete or change rows there."
                : $"{Withheld.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} foreign keys from schemas you are not shown " +
                  "point at this document; deleting it may delete or change rows there.");
        }

        if (Error is not null)
        {
            sentences.Add("What points at this document could not be read, so this cannot say what else deleting it would delete or change.");
        }
        else if (Truncated)
        {
            sentences.Add(
                $"The studio stopped reading foreign keys at {RelationshipQueries.DefaultForeignKeyCap.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}, " +
                "so a key that points at this document may be missing from what is said here.");
        }

        return sentences.Count == 0 ? null : string.Join(" ", sentences);
    }

    private static string RowsText(ReferencedByEntry entry)
    {
        string number = entry.Count.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + (entry.IsCapped ? "+" : string.Empty);

        return number + (entry.Count == 1 && !entry.IsCapped ? " row" : " rows");
    }
}
