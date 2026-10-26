namespace PostgresStudio.Services.Sources;

/// <summary>
/// Which group a statement belongs to on the Statements page (W5-Q1, issue #8 G6): what the statement does, to what - a
/// verb and a table in a plain database, a Marten operation and a document type in a store.
/// </summary>
/// <param name="Key">The group's identity, stable for one statement shape; summed by on the page, never shown.</param>
/// <param name="Label">What the group is called on the page: <c>select · public.orders</c>, <c>upsert · Order</c>, <c>event append</c>.</param>
internal sealed record StatementGroup(string Key, string Label)
{
    /// <summary>
    /// The table <see cref="Label" /> names, when it names one - so that the page can withhold a table in a schema the
    /// visitor may not see (D27, <see cref="StudioTable.GatedSchemas" />): a structured field is blanked, never half-masked.
    /// </summary>
    public StudioTable? Table { get; init; }

    /// <summary>What the statement does - <c>select</c>, <c>upsert</c> - for a label that cannot name its table to a visitor.</summary>
    public string? Verb { get; init; }
}

/// <summary>
/// Groups a statement <c>pg_stat_statements</c> recorded by what it does: each studio's own reading of its own SQL (W5-Q1).
/// </summary>
/// <remarks>
/// <para>
/// <b>Gate-free, and never a name it should not say.</b> A classifier is handed the statement's normalised text, its comments
/// masked, and the source's judged tables (<see cref="IStudioTableSet" />), and answers from those alone. It names a table
/// only from the set - so a hidden document type, which the set never holds, is never named - and the page withholds a
/// named table whose schema the visitor may not see. Postgres Studio's names the verb and the first table of the set the
/// statement names; MartenStudio's names the Marten operation and the document type.
/// </para>
/// <para>
/// Registered by each studio's <c>Add*Studio</c>. A studio that registers none groups nothing: the page shows the statements
/// alone.
/// </para>
/// </remarks>
internal interface IStatementClassifier
{
    /// <summary>The group of <paramref name="text" />, or <see langword="null" /> when it cannot be read as a statement.</summary>
    /// <param name="text">The statement's normalised text, its comments masked.</param>
    /// <param name="tables">The source's judged tables.</param>
    StatementGroup? Classify(string text, IStudioTableSet tables);
}
