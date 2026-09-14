using MartenStudio.Internal.Sql;

namespace MartenStudio.Services.Relationships;

/// <summary>
/// One document type, as the relationships graph draws it.
/// </summary>
/// <param name="Alias">Marten's document alias — the segment the collection's URL carries.</param>
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
    IReadOnlyList<string> SubclassAliases);

/// <summary>
/// One foreign key between two of the store's document types, with what the configuration says and what
/// the database has held up against each other.
/// </summary>
/// <remarks>
/// The two booleans are the whole point of the screen. <c>Declared &amp;&amp; Physical</c> is a key that
/// is both configured and there. <c>Declared &amp;&amp; !Physical</c> is drift — the host wrote
/// <c>ForeignKey&lt;T&gt;()</c> and nobody applied the migration, so nothing is enforcing it;
/// <c>!Declared &amp;&amp; Physical</c> is a constraint somebody added by hand, which Marten does not know
/// about and which <c>CreateOrUpdate</c> would drop on the next apply.
/// </remarks>
/// <param name="FromAlias">The collection that points.</param>
/// <param name="ToAlias">The collection pointed at.</param>
/// <param name="Column">The column the key is on.</param>
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
    /// <summary>Whether this key points at the collection it is declared on.</summary>
    public bool IsSelfReference =>
        string.Equals(FromAlias, ToAlias, StringComparison.OrdinalIgnoreCase);

    /// <summary>How the state reads on the screen and to a screen reader.</summary>
    public string State => (Declared, Physical) switch
    {
        (true, true) => "declared and physical",
        (true, false) => "declared only",
        (false, true) => "physical only",
        _ => "unknown",
    };
}

/// <summary>
/// A foreign key the graph cannot draw, because one of its ends is not a document type of this store.
/// </summary>
/// <remarks>
/// Listed rather than dropped: a key into a table Marten does not map is exactly the kind of thing
/// somebody needs to know about, and drawing an anonymous node for it would put a table on a diagram of
/// document types that is not one. A key whose end belongs to a type the host hid with
/// <c>IsDocumentTypeVisible</c> is <em>not</em> here either — hidden is hidden everywhere, including in
/// the list of what could not be drawn.
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
    string Reason);

/// <summary>
/// Everything the Relationships screen draws: the document types of the scoped store, the foreign keys
/// between them, and the keys that could not be placed on the picture.
/// </summary>
/// <param name="Nodes">The document types, ordered by alias.</param>
/// <param name="Edges">The foreign keys, ordered by source then target then column.</param>
/// <param name="Unmatched">The keys with an end outside the store's document types.</param>
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

    /// <summary>A graph that could not be read, carrying the reason.</summary>
    public static RelationshipGraph Failed(string error, DateTimeOffset readAt) =>
        new([], [], [], readAt, error);

    /// <summary>Whether the store declares and has no foreign keys at all between document types.</summary>
    public bool IsEmpty => Edges.Count == 0 && Unmatched.Count == 0;

    /// <summary>A one-sentence description of the picture, which is the SVG's accessible name.</summary>
    public string Summary
    {
        get
        {
            if (Error is { Length: > 0 })
            {
                return "The relationships of this store could not be read.";
            }

            return Edges.Count == 0
                ? $"A diagram of {Nodes.Count} document types with no foreign keys between them."
                : $"A diagram of {Nodes.Count} document types and {Edges.Count} foreign keys between them. " +
                  "The table below lists the same relationships.";
        }
    }
}

/// <summary>
/// One collection that points at the document on screen, and how many of its documents do.
/// </summary>
/// <param name="FromAlias">The pointing collection.</param>
/// <param name="Column">The column the foreign key is on.</param>
/// <param name="Member">The .NET member behind it, when there is one — this is what the filter uses.</param>
/// <param name="Hue">The pointing collection's colour.</param>
/// <param name="Count">How many of its documents point here, up to the cap.</param>
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
    string? Error = null);

/// <summary>
/// What points at one document — the inbound half of the detail page's relationships.
/// </summary>
/// <remarks>
/// The outbound half (<c>RelatedDocuments</c>) is one indexed read per key and is always shown. This half
/// is one bounded count per pointing collection, which is why the counts stop at a cap and why it is
/// rendered as its own panel rather than folded into the other.
/// </remarks>
/// <param name="Entries">One per pointing collection, ordered by alias.</param>
/// <param name="Cap">The count cap, so the page can render "1000+" with the right number in it.</param>
/// <param name="Error">What went wrong, or <see langword="null" />.</param>
internal sealed record ReferencedBy(
    IReadOnlyList<ReferencedByEntry> Entries,
    int Cap = RelationshipQueries.DefaultInboundCap,
    string? Error = null)
{
    /// <summary>Nothing points here — the ordinary answer, and not an error.</summary>
    public static ReferencedBy None { get; } = new([]);

    /// <summary>The inbound list could not be read.</summary>
    public static ReferencedBy Failed(string error) => new([], RelationshipQueries.DefaultInboundCap, error);
}
