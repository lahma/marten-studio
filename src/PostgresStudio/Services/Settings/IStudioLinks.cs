namespace PostgresStudio.Services.Settings;

/// <summary>
/// The links shared screens make into a studio's own screens - a document collection, the event streams, the schema
/// screen - each one <see langword="null" /> where the studio has no such screen.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a seam.</b> The database browser, the Relationships screen, Sessions and Health all point somewhere when an
/// object is really read elsewhere: a Marten document table's rows are read in Documents, where tenancy, soft deletes and
/// the store's serializer apply (D27), and the event store's in Streams. Those screens exist only in MartenStudio. A
/// shared screen asks here and draws plain text where the answer is <see langword="null" />, which is the honest answer
/// in a studio that has no such screen.
/// </para>
/// <para>
/// <b>What an implementation must and must not do.</b> Every member is pure: it builds a relative URL - relative to the
/// studio-rooted <c>&lt;base href&gt;</c>, like every studio link - and never resolves the scope, asks a policy or does
/// IO. Authorization is the target page's: it resolves its own scope and authorizes the visitor for it again, so a link
/// is never a grant. The scope, when there is one, travels in the query string (D9) under the studio's own source
/// parameter, and an identifier the URL carries is escaped, never concatenated raw.
/// </para>
/// </remarks>
internal interface IStudioLinks
{
    /// <summary>The document collections' index, or <see langword="null" /> without a documents screen.</summary>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    string? ToDocuments(StudioScope? scope);

    /// <summary>One document collection, by its alias.</summary>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    /// <param name="alias">The collection's alias.</param>
    string? ToCollection(StudioScope? scope, string alias);

    /// <summary>One document, by its collection's alias and its id as the URL carries it.</summary>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    /// <param name="alias">The collection's alias.</param>
    /// <param name="id">The document id.</param>
    string? ToDocument(StudioScope? scope, string alias, string id);

    /// <summary>The event streams list, or <see langword="null" /> without an event store screen.</summary>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    string? ToStreams(StudioScope? scope);

    /// <summary>The event feed.</summary>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    string? ToFeed(StudioScope? scope);

    /// <summary>The projections screen.</summary>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    string? ToProjections(StudioScope? scope);

    /// <summary>One tab of the schema screen - by default its tables, where a studio's own bookkeeping is described.</summary>
    /// <param name="scope">The scope to carry, or <see langword="null" />.</param>
    /// <param name="tab">The tab.</param>
    string? ToSchema(StudioScope? scope, string tab = "tables");
}
