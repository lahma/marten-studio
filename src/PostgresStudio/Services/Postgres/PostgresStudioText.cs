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
/// <b>Never a Marten word but the two that name Marten on purpose.</b> <c>PostgresStudioWordsTests</c> holds every
/// sentence here to that: none says <c>MartenStudioOptions</c>, <c>StoreOptions</c>, "store" or a session API; only
/// the <c>mt_</c> notices name Marten and Marten Studio, and only the one about a Marten table's partitions
/// (<see cref="StudioText.MartenTablePartitionParameters" />) names the store whose <c>StoreOptions</c> declare it.
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

        StorageStorePolicyDenial =
            "Your account may not see this database's storage. The source policy "
            + "(PostgresStudioOptions.SourceAuthorizationPolicy) refused this data source and database.",

        StatementsCapabilityDenial =
            "The statements this database runs, their counters and their text, need PostgresStudioOptions.Capabilities.ViewDatabaseActivity.",
        StatementsReadOnlyDenial =
            "PostgresStudioOptions.ReadOnly is true, which turns PostgresStudioOptions.Capabilities.ViewDatabaseActivity off "
            + "with every other capability.",
        StatementsStorePolicyDenial =
            "Your account may not see this database's statements. The source policy "
            + "(PostgresStudioOptions.SourceAuthorizationPolicy) refused this data source and database.",
        StatementsWritePolicyDenial =
            "Your account may not see the statements this database runs. The write policy "
            + "(PostgresStudioOptions.WriteAuthorizationPolicy, or SourceAuthorizationPolicy when no write policy is set) refused "
            + "ViewDatabaseActivity for this data source and database.",
        StudioCostCovers =
            "health checks, the SQL console, the database browser, Sessions, Maintenance, Storage, Replication, Server and the "
            + "relationship reads",
        StatementsStudioRoles =
            "Every read the studio makes here but these reads of pg_stat_statements runs as it - the health checks, the SQL "
            + "console, the database browser, Sessions, the Maintenance, Storage, Replication and Server pages and the "
            + "Relationships graph - and this page lists none of their statements. These reads run as the data source's own "
            + "role, and are among the statements above when they rank among the ones kept.",

        ExamplesIntro = "Catalog reads that run against any database. Click one to load it.",
        ExamplesEmpty = "There is no example to offer.",
        RelationshipTableKey = "A foreign key - enforced by Postgres.",
        RelationshipTableCaption =
            "Every foreign key between the tables on the diagram, with the end that points, the end pointed at, the columns, "
            + "whether the database has the constraint, and what a delete does.",
        RelationshipIsolatedLabel = "Tables with no foreign key",
        RelationshipUnmatchedNote =
            "These foreign keys have an end in a table this studio does not draw, so there is nothing to draw them between.",
        RelationshipNeighbourhoodUnmatched = "an end in a table this studio does not draw, so there is nothing to draw them between.",
        RelationshipTablesUnavailable = "No table is drawn:",

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
        MartenTablePartitionParameters =
            "each partition's pg_class.reloptions says which; Marten Studio's Schema screen, over the store whose StoreOptions "
            + "declare this table, shows which parameters the partitions differ on",
        LeaderTimeoutClause = string.Empty,
        ReadOnlyWrites = "write the application makes",
        ReadOnlyNeverAsks = "and most data access never asks",

        // The two checks whose titles name a store: S02, whose docs heading says what it guards in Marten Studio's words, and
        // S10. The docs heading stays the attribute's, so the "Docs" link still finds its anchor.
        CheckTitles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["S02"] = "Settings that blind or endanger the database",
            ["S10"] = "Connections cannot write",
        },
        AmbiguousPoolNotChecked =
            "More than one Npgsql pool in this process uses this database's connection string - a second data source built from "
            + "the same connection string, or NpgsqlConnections opened with it - and Npgsql reports each under that same name. "
            + "Their numbers differ, so which pool is this data source's cannot be told, and the pool is not judged; naming the "
            + "other data source (NpgsqlDataSourceBuilder.Name), or giving its connections an Application Name of their own, "
            + "tells them apart.",
        PoolBorrowers = "Every command this process sends through the data source",
        PoolRemedyCode =
            """
            // The ceiling, in the connection string the data source is built from (Npgsql's default is 100):
            Maximum Pool Size=200
            """,
        PoolRemedyExplanation =
            "Raise it only when the server has the headroom (C03), and after ruling out connections held too long: dispose "
            + "every connection and transaction on every path (await using), and keep transactions short.",
        NextConnectionExamples = "a pool that wants to grow, a deployment's new instance, a scheduled job's next connection",
        EveryDatabasesConnections = "this application's and everybody else's",
        FewerConnectionsRemedyCode =
            """
            // Fewer connections per process, in the connection string each data source is built from:
            Maximum Pool Size=50
            """,
        IdleTransactionIdCost = ", which holds vacuum back as well, snapshot or not",
        SkippedCommit = "its commit",
        IdleTimeoutLeaderClause = string.Empty,
        StrongerLockExamples = "a migration's ALTER TABLE, a TRUNCATE",
        LongStatementRemedyCode =
            """
            // A ceiling for the application's own statements, in seconds, in the connection string the data source is built
            // from (Npgsql's default is 30):
            Command Timeout=60
            """,
        LongStatementRemedyExplanation =
            "Command Timeout bounds what the application sends through this data source; it does not reach another "
            + "application's statements. A query over a large table with no index for its filter needs that index, in the "
            + "application's own migrations.",
        PreparedTransactionHolds = "for good and blocks whatever needs its locks",
        RetriedFailures = "serialization failures, optimistic-concurrency conflicts, unique violations",
        ScopePolicyNoun = "source",
        ChangeDataCaptureConsumer = "a change data capture connector",
        EveryWriteExamples = "every insert, update and delete",
        EverySourceDatabase = "every database on this server",
        UnclearedDeadRows = "every row updated or deleted leaves a dead row version that is never cleared",
        LostCommitsMeaning = string.Empty,
        ZeroedPageRows = "including rows that other rows, or other systems, still point at",
        SettingsReadOn =
            "on a connection from the data source - so a value set for its role, its database or in its connection string is "
            + "the one its sessions run with",
        PageRowsExamples = "table rows, index entries",
        ConnectionCarries = "every row it reads or writes",
        GenericPlanPlaceholders =
            " are placeholders; a filter that needs its constant to match an expression index, or a partial index's WHERE clause, "
            + "cannot use that index in that plan, so a sequential scan filtering on one does not show that the table lacks an "
            + "index.",
        OwnRoleOnTables = "the data source's own role on the tables the studio judges",
        OneOfTheTables = "one of them",
        SlowStatementCause =
            "A statement this slow is most often a query over a large table with no index for its filter, or one that reads far "
            + "more rows than it returns.",
        UnboundedRead = "a query with no LIMIT or paging",
        PlanReadsNoneOfTheTables = "The plan reads none of the tables the studio judges directly",
        UnnamedPlanScans = " of a partition, or of a table the studio does not judge or does not show, not named here",
        PartitionedIndexRepair =
            " Whoever owns the schema rebuilds the leftover invalid index on the partition - REINDEX INDEX CONCURRENTLY, or drops "
            + "it and builds it again with CREATE INDEX CONCURRENTLY and ALTER INDEX ... ATTACH PARTITION - in the host's own "
            + "migrations or by hand. The partition is not named here; pg_index.indisvalid on the table's partitions says which.",
        DeclaredIndexRebuild =
            " Whoever owns the schema rebuilds it (REINDEX INDEX CONCURRENTLY), in the host's own migrations or by hand.",
        AppendOnlyExamples = "an event log, an audit or history table",
        HorizonDeadRowsLabel = "Dead rows in the tables the studio judges",
        LogicalSlotConsumers = "Debezium, a CDC pipeline, a subscription",
        ConfiguredKeyColumnClause = string.Empty,
        JudgedStatementNoun = "statement",
        ShortStatementsJit =
            "An application's statements are mostly short, and a large table's cost estimates are what push them over the line.",
        JitRemedyCode =
            """
            // In the connection string the data source is built from:
            Options=-c jit=off
            """,
        JitRemedyExplanation =
            "Turns JIT off for this data source's own connections only, and leaves the server's setting alone for everybody else. "
            + "To keep JIT for the occasional long query, a DBA can raise jit_above_cost for the data source's role instead.",
        DatabaseGateReason = "needs the source policy to allow this data source and database",
        ExportSourceNoun = "data source",
        PickInTheHeader = "a database",
        PlanIndexRemedy =
            "An index whose leading column the filter compares lets Postgres read only the rows it keeps: add it in your own "
            + "migrations, built CONCURRENTLY on a table in use.",
        PlanPatternRemedy =
            "A btree index can serve a pattern only by the fixed text it starts with, so one that starts with % or _ tests every "
            + "row. A trigram index (the pg_trgm extension's GIN index, gin_trgm_ops) serves it, added in your own migrations - "
            + "and so does searching for a prefix instead, or full-text search.",
    };
}
