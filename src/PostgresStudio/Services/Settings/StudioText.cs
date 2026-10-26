using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health.Checks;

namespace PostgresStudio.Services.Settings;

/// <summary>Which studio is speaking - for the shared log events, whose templates are each studio's own.</summary>
internal enum StudioProduct
{
    /// <summary>Marten Studio: every template, sentence and remedy exactly as 0.4 wrote it.</summary>
    MartenStudio,

    /// <summary>Postgres Studio: the same events, ids and properties, in its own words.</summary>
    PostgresStudio,
}

/// <summary>
/// Every sentence the shared studio says differently in each studio: the refusals that name an option or a policy, the
/// prose that names a store's configuration, the remedies that are code, and the wording of the judged-table checks.
/// </summary>
/// <remarks>
/// <para>
/// <b>One per studio, read through <see cref="IStudioSettings.Text" />.</b> Shared code never spells a studio's product,
/// options type or configuration itself: it reads the sentence here. MartenStudio's instance is <see cref="Marten" />,
/// built from the very constants the shared code said before it was shared (<c>DatabaseGate.CapabilityDenial</c> and the
/// rest), so a MartenStudio host reads every sentence byte for byte as it did; <c>StudioTextPinTests</c> holds each one
/// to its literal. Postgres Studio's is <c>PostgresStudioText.Instance</c>.
/// </para>
/// <para>
/// <b>What is not here.</b> A sentence that differs between the studios only by the options type it names is composed
/// with <see cref="OptionName" /> (or <see cref="IStudioSettings.OptionName" />) where it is written. A sentence that can
/// only ever be said in MartenStudio - one about a hidden document type, or another registered store - stays where it is:
/// a plain data source has neither.
/// </para>
/// </remarks>
internal sealed class StudioText
{
    /// <summary>Which studio this is, for the shared log events and the prose a component draws as markup.</summary>
    public required StudioProduct Product { get; init; }

    /// <summary>The product's name in a sentence: <c>Marten Studio</c>.</summary>
    public required string ProductName { get; init; }

    /// <summary>The public options type's name: <c>MartenStudioOptions</c>.</summary>
    public required string OptionsTypeName { get; init; }

    /// <summary>The registration method a host calls: <c>AddMartenStudio</c>.</summary>
    public required string RegistrationMethod { get; init; }

    /// <summary>A source in a sentence: <c>Marten store</c>.</summary>
    public required string SourceNoun { get; init; }

    /// <summary><paramref name="member" /> as a host's registration spells it: <c>MartenStudioOptions.SqlConsoleRole</c>.</summary>
    /// <param name="member">The member path below the options type.</param>
    public string OptionName(string member) => OptionsTypeName + "." + member;

    /// <summary>
    /// The words of the studio <paramref name="settings" /> belong to - for a service whose settings are an optional
    /// constructor argument only so that a test can build it by hand, the way its <c>StudioLogThrottle</c> is:
    /// MartenStudio's, as every such test is, when there are none. The container always supplies them.
    /// </summary>
    /// <param name="settings">The studio's settings, or <see langword="null" /> for a hand-built owner.</param>
    public static StudioText Of(IStudioSettings? settings) => settings?.Text ?? Marten;

    /// <summary>What the Activity ring says of a scope a policy refused.</summary>
    public required string ScopeDeniedEntry { get; init; }

    /// <summary>
    /// The source, possessive, before "own role", "own schemas" or "most expensive statements": <c>the store's</c>.
    /// </summary>
    public required string SourcePossessive { get; init; }

    // ---------------------------------------------------------------------------------------------------------------
    // The database browser.

    /// <summary>What <c>ReadOnly</c> says about the database browser.</summary>
    public required string BrowseReadOnlyDenial { get; init; }

    /// <summary>What a <c>BrowseDatabase</c> that is off says.</summary>
    public required string BrowseCapabilityDenial { get; init; }

    /// <summary>What the write policy's refusal of <c>BrowseDatabase</c> says.</summary>
    public required string BrowseWritePolicyDenial { get; init; }

    /// <summary>What the scope policy's refusal of the database as a whole says.</summary>
    public required string BrowseStorePolicyDenial { get; init; }

    /// <summary>What an empty <c>BrowsableSchemas</c> says.</summary>
    public required string BrowseEmptyListDenial { get; init; }

    /// <summary>What a view reading one of Marten's own tables says about its rows.</summary>
    public required string MartenDependencyDenial { get; init; }

    /// <summary>What a relation the connection's own role may not read says, when no <c>SqlConsoleRole</c> is set.</summary>
    public required string NoSelectPrivilege { get; init; }

    /// <summary>The rail's flag on a source's own schema - <c>M</c> - or <see langword="null" /> to draw none.</summary>
    public required string? OwnSchemaFlag { get; init; }

    /// <summary>The flag's tooltip.</summary>
    public required string? OwnSchemaFlagTitle { get; init; }

    /// <summary>The tooltip of an object the <c>mt_</c> rule calls Marten's own.</summary>
    public required string MartenInternalTitle { get; init; }

    /// <summary>
    /// The database page's notice when the database holds objects named like Marten's, or <see langword="null" /> for a
    /// studio that is the place to browse them.
    /// </summary>
    public required string? MartenStoreNotice { get; init; }

    /// <summary>The owner filter's tooltip on its "Marten" side.</summary>
    public required string OwnerFilterMartenTitle { get; init; }

    /// <summary>The owner filter's tooltip on its "Other" side.</summary>
    public required string OwnerFilterOtherTitle { get; init; }

    // ---------------------------------------------------------------------------------------------------------------
    // Database › Sessions.

    /// <summary>What a <c>ViewDatabaseActivity</c> that is off says.</summary>
    public required string SessionsCapabilityDenial { get; init; }

    /// <summary>What <c>ReadOnly</c> says about session details.</summary>
    public required string SessionsReadOnlyDenial { get; init; }

    /// <summary>What the scope policy's refusal of the database's sessions says.</summary>
    public required string SessionsStorePolicyDenial { get; init; }

    /// <summary>What the write policy's refusal of <c>ViewDatabaseActivity</c> says.</summary>
    public required string SessionsWritePolicyDenial { get; init; }

    // ---------------------------------------------------------------------------------------------------------------
    // Storage (W5-CAP).

    /// <summary>What the scope policy's refusal of the database's storage says: its sizes and growth belong to no tenant.</summary>
    public required string StorageStorePolicyDenial { get; init; }

    // ---------------------------------------------------------------------------------------------------------------
    // Query and Relationships.

    /// <summary>The examples panel's lead.</summary>
    public required string ExamplesIntro { get; init; }

    /// <summary>What the examples panel says with nothing to offer.</summary>
    public required string ExamplesEmpty { get; init; }

    /// <summary>The Relationships legend's line for a key between plain tables.</summary>
    public required string RelationshipTableKey { get; init; }

    // ---------------------------------------------------------------------------------------------------------------
    // Health.

    /// <summary>The sentence pointing at Database › Sessions for the statements a finding counts.</summary>
    public required string StatementsOnSessionsPage { get; init; }

    /// <summary>The comment above a server setting in a remedy: <c>A server setting, not a StoreOptions one</c>.</summary>
    public required string ServerSettingComment { get; init; }

    /// <summary>C08: where deadlocks usually come from.</summary>
    public required string DeadlockCause { get; init; }

    /// <summary>C08: what usually spills a sort to disk, ending without a full stop.</summary>
    public required string TempSpillCause { get; init; }

    /// <summary>C05: what a long statement usually is.</summary>
    public required string LongStatementCause { get; init; }

    /// <summary>T14: the code that keeps a transaction short.</summary>
    public required string ShortTransactionCode { get; init; }

    /// <summary>C04: the code that ends what a session began.</summary>
    public required string EndWhatYouBeginCode { get; init; }

    /// <summary>T12, T13: the judged tables, plural and capitalised for an evidence label: <c>Marten tables</c>.</summary>
    public required string JudgedTables { get; init; }

    /// <summary>T12, T13: one judged table, after "The oldest": <c>Marten table</c>.</summary>
    public required string JudgedTable { get; init; }

    /// <summary>T12, T13: what a table not judged is: <c>not the store's</c>.</summary>
    public required string NotJudged { get; init; }

    /// <summary>T12, T13: where the ages are read from: <c>the store's own tables</c>.</summary>
    public required string JudgedTablesRead { get; init; }

    /// <summary>T12: the quiet note when no judged table exists.</summary>
    public required string NoJudgedTable { get; init; }

    /// <summary>T12: why a table that is not judged still matters, starting with a space.</summary>
    public required string UnjudgedOldest { get; init; }

    /// <summary>
    /// T07, T08: where a person sees which storage parameters the partitions of a Marten table differ on, said inside the
    /// parentheses after "autovacuum may be switched off for some of them": MartenStudio's own Schema screen, or - for a
    /// studio with no store's configuration to show - Marten Studio's, over the store that manages the table.
    /// </summary>
    public required string MartenTablePartitionParameters { get; init; }

    /// <summary>
    /// MartenStudio's wording, exactly as the shared code wrote it before it was shared. Every member is either one of
    /// the constants it used, or that literal moved here - but the six judged-table members of T12 and T13
    /// (<see cref="JudgedTables" /> to <see cref="UnjudgedOldest" />), reworded in 0.5 when MartenStudio's table checks
    /// began to judge every table of the store's schemas and the ones the database browser admits, not only the tables
    /// the store declares (issue #8, G1).
    /// </summary>
    public static StudioText Marten { get; } = new()
    {
        Product = StudioProduct.MartenStudio,
        ProductName = "Marten Studio",
        OptionsTypeName = "MartenStudioOptions",
        RegistrationMethod = "AddMartenStudio",
        SourceNoun = "Marten store",
        ScopeDeniedEntry = "Not authorized for this store, database or tenant.",
        SourcePossessive = "the store's",

        BrowseReadOnlyDenial = DatabaseGate.ReadOnlyDenial,
        BrowseCapabilityDenial = DatabaseGate.CapabilityDenial,
        BrowseWritePolicyDenial = DatabaseGate.WritePolicyDenial,
        BrowseStorePolicyDenial = DatabaseGate.StorePolicyDenial,
        BrowseEmptyListDenial = DatabaseGate.EmptyListDenial,
        MartenDependencyDenial = DatabaseGate.MartenDependencyDenial,
        NoSelectPrivilege = "The store's Postgres role has no SELECT privilege on it.",
        OwnSchemaFlag = "M",
        OwnSchemaFlagTitle = "Declared by this Marten store. Its structure is shown whether or not Capabilities.BrowseDatabase is on.",
        MartenInternalTitle = "Marten's own bookkeeping. It is described on the Schema screen and never browsed raw.",
        MartenStoreNotice = null,
        OwnerFilterMartenTitle = "Only what Marten owns: document tables, the event store, projections and its own bookkeeping",
        OwnerFilterOtherTitle = "Only what Marten does not own: this application's own objects, and other libraries'",

        SessionsCapabilityDenial = SessionActivityService.CapabilityDenial,
        SessionsReadOnlyDenial = SessionActivityService.ReadOnlyDenial,
        SessionsStorePolicyDenial = SessionActivityService.StorePolicyDenial,
        SessionsWritePolicyDenial = SessionActivityService.WritePolicyDenial,

        StorageStorePolicyDenial =
            "Your account may not see this database's storage. The store policy (MartenStudioOptions.StoreAuthorizationPolicy) "
            + "refused this store and database as a whole, with no tenant selected: a table's size belongs to no tenant.",

        ExamplesIntro = "Built from this store's own aliases and tables. Click one to load it.",
        ExamplesEmpty = "This store has no document type to build an example from.",
        RelationshipTableKey = "A key on a table Marten does not map - enforced by Postgres.",

        StatementsOnSessionsPage = SessionEvidence.StatementsOnSessionsPage,
        ServerSettingComment = "A server setting, not a StoreOptions one",
        DeadlockCause =
            "In a Marten store they come from two transactions taking the same rows in different orders: "
            + "documents saved in a different order by two code paths, a projection and a request writing the same documents, "
            + "an inline projection updating rows another session holds.",
        TempSpillCause =
            "In a Marten store it is usually an ORDER BY on a document field no index covers, or a large "
            + "aggregation",
        LongStatementCause =
            "In a Marten store it is most often a query over a large document table "
            + "with no index for its filter, a projection rebuild's batch, or a report somebody started by hand.",
        ShortTransactionCode =
            """
            // Keep transactions short, and end what you begin:
            await using var session = store.LightweightSession();
            // ... work ...
            await session.SaveChangesAsync(cancellationToken);
            """,
        EndWhatYouBeginCode =
            """
            // Dispose every session on every path, and end what it began:
            await using var session = store.LightweightSession();
            // ... work ...
            await session.SaveChangesAsync(cancellationToken);
            """,
        JudgedTables = "Tables the studio judges",
        JudgedTable = "table the studio judges",
        NotJudged = "not among the tables the studio judges",
        JudgedTablesRead = "the tables the studio judges",
        NoJudgedTable = "No table the studio judges exists yet.",
        UnjudgedOldest =
            " The oldest table in this database is not one the studio judges - in a schema neither the store nor "
            + "MartenStudioOptions.BrowsableSchemas admits, another registered store's, a document type the studio hides, or "
            + "beyond the largest tables a sweep reads - and wraparound protection counts every table in the database, so it "
            + "holds the whole database back as surely as one of these would.",
        MartenTablePartitionParameters = "the Schema screen's Tables tab shows which parameters the partitions differ on",
    };
}
