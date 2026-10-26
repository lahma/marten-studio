using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// Postgres Studio's words for every sentence the shared studio says in a studio's own (<see cref="StudioText" />): its
/// options type, its policies, a data source rather than a store, and remedies that are SQL or a server setting rather
/// than a <c>StoreOptions</c> line.
/// </summary>
/// <remarks>
/// <para>
/// <b>What a plain data source is.</b> One database, registered by the application; its own schemas are the ones
/// <c>PostgresStudioOptions.BrowsableSchemas</c> names exactly, whose structure is shown to anyone the source policy
/// admits, and every row and definition needs <c>BrowseDatabase</c> and the write policy (D27, design PS10). Nothing in
/// it is anybody's store, except what the <c>mt_</c> rule calls Marten's - whose rows are never read here, in either
/// studio, and which <see cref="StudioText.MartenStoreNotice" /> points at Marten Studio for.
/// </para>
/// <para>
/// <b>Never a Marten word but the two that name Marten on purpose.</b> <c>PostgresStudioTextTests</c> holds every
/// sentence here to that: none says <c>MartenStudioOptions</c>, <c>StoreOptions</c>, "store" or a session API; only
/// the <c>mt_</c> notices name Marten and Marten Studio.
/// </para>
/// </remarks>
internal static class PostgresStudioText
{
    /// <summary>The options type's name, as a host's registration spells it.</summary>
    public const string OptionsTypeName = "PostgresStudioOptions";

    /// <summary>Postgres Studio's words.</summary>
    public static StudioText Instance { get; } = new()
    {
        Product = StudioProduct.PostgresStudio,
        ProductName = "Postgres Studio",
        OptionsTypeName = OptionsTypeName,
        RegistrationMethod = "AddPostgresStudio",
        SourceNoun = "data source",
        ScopeDeniedEntry = "Not authorized for this data source or database.",
        SourcePossessive = "the data source's",

        BrowseReadOnlyDenial =
            "PostgresStudioOptions.ReadOnly is true, which turns PostgresStudioOptions.Capabilities.BrowseDatabase off with every "
            + "other capability.",
        BrowseCapabilityDenial =
            "The rows and definitions of every object here, and the schemas PostgresStudioOptions.BrowsableSchemas admits only "
            + "through \"*\", need PostgresStudioOptions.Capabilities.BrowseDatabase.",
        BrowseWritePolicyDenial =
            "Your account may not browse the database here. The write policy (PostgresStudioOptions.WriteAuthorizationPolicy, "
            + "or SourceAuthorizationPolicy when no write policy is set) refused BrowseDatabase for this data source and database.",
        BrowseStorePolicyDenial =
            "Your account may not browse the database here. The source policy (PostgresStudioOptions.SourceAuthorizationPolicy) "
            + "refused this data source and database.",
        BrowseEmptyListDenial =
            "PostgresStudioOptions.BrowsableSchemas is empty, so no schema is browsable. List the schema names, or \"*\".",
        MartenDependencyDenial =
            "This view reads one of Marten's own tables - a document or event table, or Marten's bookkeeping - whose rows are "
            + "never read here: browse them with Marten Studio, where the store's tenancy and visibility rules apply.",
        NoSelectPrivilege = "The data source's Postgres role has no SELECT privilege on it.",
        OwnSchemaFlag = null,
        OwnSchemaFlagTitle = null,
        MartenInternalTitle =
            "Named like Marten's own (mt_): a Marten store's document, event or bookkeeping object. Its rows are never read "
            + "here; browse them with Marten Studio.",
        MartenStoreNotice =
            "This database holds a Marten store; browse its documents with Marten Studio, where its tenancy and visibility "
            + "rules apply.",
        OwnerFilterMartenTitle =
            "Only what is named like Marten's own (mt_): a Marten store's objects, whose rows are read in Marten Studio",
        OwnerFilterOtherTitle = "Everything not named like Marten's own: this application's objects, and other libraries'",

        SessionsCapabilityDenial =
            "Process ids, roles, client addresses, application names, connection times and statements need "
            + "PostgresStudioOptions.Capabilities.ViewDatabaseActivity.",
        SessionsReadOnlyDenial =
            "PostgresStudioOptions.ReadOnly is true, which turns PostgresStudioOptions.Capabilities.ViewDatabaseActivity off "
            + "with every other capability.",
        SessionsStorePolicyDenial =
            "Your account may not see this database's sessions. The source policy "
            + "(PostgresStudioOptions.SourceAuthorizationPolicy) refused this data source and database.",
        SessionsWritePolicyDenial =
            "Your account may not see who is using this database. The write policy (PostgresStudioOptions.WriteAuthorizationPolicy, "
            + "or SourceAuthorizationPolicy when no write policy is set) refused ViewDatabaseActivity for this data source and "
            + "database.",

        ExamplesIntro = "Catalog reads that run against any database. Click one to load it.",
        ExamplesEmpty = "There is no example to offer.",
        RelationshipTableKey = "A foreign key - enforced by Postgres.",

        StatementsOnSessionsPage =
            "The statements themselves are on Database › Sessions, behind PostgresStudioOptions.Capabilities.ViewDatabaseActivity, "
            + "masked for the schemas you may not see.",
        ServerSettingComment = "A server setting",
        DeadlockCause =
            "They come from two transactions taking the same rows in different orders: two code paths updating the same rows "
            + "in a different order, or a batch job and a request writing the same rows.",
        TempSpillCause = "It is usually an ORDER BY no index covers, or a large aggregation",
        LongStatementCause =
            "It is most often a query over a large table with no index for its filter, a batch job, or a report somebody "
            + "started by hand.",
        ShortTransactionCode =
            """
            // Keep transactions short, and end what you begin:
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            // ... work ...
            await transaction.CommitAsync(cancellationToken);
            """,
        EndWhatYouBeginCode =
            """
            // Dispose every connection and transaction on every path, and end what it began:
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            // ... work ...
            await transaction.CommitAsync(cancellationToken);
            """,
        JudgedTables = "Tables in the browsable schemas",
        JudgedTable = "table in the browsable schemas",
        NotJudged = "not among the tables the studio judges",
        JudgedTablesRead = "the tables in the browsable schemas",
        NoJudgedTable = "No table exists in the browsable schemas yet.",
        UnjudgedOldest =
            " The oldest table in this database is not one the studio judges - in a schema PostgresStudioOptions.BrowsableSchemas "
            + "does not admit, or beyond the largest tables a sweep reads - and wraparound protection counts every table in the "
            + "database, so it holds the whole database back as surely as one of these would.",
    };
}
