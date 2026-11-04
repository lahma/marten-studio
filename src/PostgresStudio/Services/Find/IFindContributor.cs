namespace PostgresStudio.Services.Find;

/// <summary>
/// One kind of thing Find can look for, and the page its hits open: PostgresStudio's tables and statements, MartenStudio's documents,
/// streams and events (1.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>What an implementation must and must not do</b>, as <see cref="Sources.INavIndicatorContributor" /> says of the sidebar's:
/// </para>
/// <list type="bullet">
/// <item>It reads only through the data service of the page its hit opens, which resolves the scope and asks the policies itself, so
/// a probe is subject to exactly the gates, and writes exactly the audit, of the read that page makes. It opens no connection of its
/// own and calls nothing storage-ensuring (hard rule 14).</item>
/// <item>It never throws but for cancellation. A failure is a note, and a note names nothing the visitor could not already see.</item>
/// <item>It never returns, or counts, anything about a hidden type, a withheld schema or a tenant the visitor's policy refuses: such
/// a match reads exactly as no match at all.</item>
/// <item>It answers <see cref="FindContribution.NotApplicable" /> for a value that is not the shape of its kind, so a phrase is
/// listed only for a kind that was really searched.</item>
/// </list>
/// </remarks>
internal interface IFindContributor
{
    /// <summary>Where this kind's hits sit in the list, lowest first.</summary>
    int Order { get; }

    /// <summary>What this contributor searches, for a note that it did not: "Documents", "Tables and views".</summary>
    string Noun { get; }

    /// <summary>
    /// Whether this kind may be searched in a scope other than the one on screen. False for a kind whose page does not apply its own
    /// address's scope (Statements): its hit would open the page in the scope on screen, not the one searched.
    /// </summary>
    bool SearchesOtherScopes => true;

    /// <summary>Looks for the value.</summary>
    /// <param name="scope">The store, database and tenant to look in.</param>
    /// <param name="query">What was typed.</param>
    /// <param name="cancellationToken">Cancels the read - a newer Find, the budget, or the visitor going away.</param>
    Task<FindContribution> FindAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken);
}
