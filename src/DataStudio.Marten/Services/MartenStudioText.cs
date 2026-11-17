using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Marten.Services;

/// <summary>
/// The Marten module's words for every sentence the shared studio says in a module's own (<see cref="StudioText" />): its options type,
/// its store policy, the prose that names a store's configuration, and remedies that are <c>StoreOptions</c> lines.
/// </summary>
/// <remarks>
/// A sentence that is the same in both modules but for the nouns is not here: <see cref="StudioText" /> composes it over
/// <see cref="StudioText.OptionsTypeName" />, <see cref="StudioText.SourceNoun" /> and the other nouns this instance sets.
/// </remarks>
internal static class MartenStudioText
{
    /// <summary>The Marten module's words.</summary>
    public static StudioText Instance { get; } = new()
    {
        OptionsTypeName = "MartenOptions",
        SourceNoun = "Marten store",
        SourcePossessive = "the store's",
        ScopePolicyName = "StoreAuthorizationPolicy",
        ScopePolicyNoun = "store",
        TenantlessScopeClause = " as a whole, with no tenant selected",
        OptionsRegistration = "services.AddDataStudio().AddMarten(options => ...)",
        HealthCheckRegistration = "services.AddDataStudio().AddMarten() as well as AddHealthChecks().AddMartenDiagnostics()",
        ScopeDeniedEntry = "Not authorized for this store, database or tenant.",
        FindHint = "A document or stream id, a natural key, #sequence, an exact table or view name or a queryid",
        NamedSchemasAreOwn = false,
        ConfigurationDeclaresTables = true,
        BrowseCapabilityDenial =
            "Other schemas, and the rows and definitions of objects Marten does not own, need "
            + "MartenOptions.Capabilities.BrowseDatabase.",
        BrowseEmptyListDenial =
            "MartenOptions.BrowsableSchemas is empty, so no schema's non-Marten rows or definitions are browsable. List the "
            + "schema names, or \"*\".",
        DefinitionCapabilityDenial =
            "The definitions of objects Marten does not own need MartenOptions.Capabilities.ViewDefinitions, for the schemas "
            + "MartenOptions.BrowsableSchemas names exactly, or MartenOptions.Capabilities.BrowseDatabase.",
        MartenDependencyDenial =
            "This view reads one of Marten's own tables - a document or event table, or Marten's bookkeeping - whose rows "
            + "are never read raw: open them in Documents or Events, where tenancy, soft delete and the store's serializer "
            + "apply.",
        OwnSchemaFlag = "M",
        OwnSchemaFlagTitle = "Declared by this Marten store. Its structure is shown whether or not Capabilities.BrowseDatabase is on.",
        MartenInternalTitle = "Marten's own bookkeeping. It is described on the Schema screen and never browsed raw.",
        MartenStoreNotice = null,
        OwnerFilterMartenTitle = "Only what Marten owns: document tables, the event store, projections and its own bookkeeping",
        OwnerFilterOtherTitle = "Only what Marten does not own: this application's own objects, and other libraries'",
        StudioCostCovers =
            "health checks, the SQL console, the database browser, the Query page, and the schema, relationship, document, event and "
            + "projection reads; a Marten session the studio opens to write or to steer the daemon is not counted",
        StatementsStudioRoles =
            "It is the role of the health checks (all but these reads of pg_stat_statements), the SQL console, the Query page, "
            + "the database browser, Sessions, the Maintenance, Storage, Replication and Server pages, the Schema page's catalog "
            + "reads, the Relationships graph and the Projections page's high-water explanation, and this page lists none of "
            + "their statements. The document, event and projection reads, tenant discovery, the Relationships page's document "
            + "counts, these reads of pg_stat_statements and Marten's own sessions - the studio's writes, and the Schema page's "
            + "Check, Preview and DDL - run as the store's own role, and are among the statements above when they rank among "
            + "the ones kept.",
        ExamplesIntro = "Built from this store's own aliases and tables. Click one to load it.",
        ExamplesEmpty = "This store has no document type to build an example from.",
        RelationshipTableKey = "A key on a table Marten does not map - enforced by Postgres.",
        RelationshipTableCaption =
            "Every foreign key between the document types and tables on the diagram, with the end that points, the end pointed "
            + "at, the columns, whether the database has the constraint, and what a delete does.",
        RelationshipIsolatedLabel = "Document types and tables with no foreign key",
        RelationshipUnmatchedNote =
            "These foreign keys have an end that is not a document type of this store, so there is nothing to draw them between.",
        RelationshipUnplacedTarget = "It points at a table no document type of this store maps.",
        RelationshipUnplacedSource = "It is on a table no document type of this store maps.",
        RelationshipNeighbourhoodUnmatched =
            "an end that is not a document type of this store or a table the studio can place, so there is nothing to draw them "
            + "between.",
        RelationshipTablesUnavailable = "Only document types are drawn:",
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
        JudgedTablesRead = "the tables the studio judges",
        NoJudgedTable = "No table the studio judges exists yet.",
        UnjudgedOldest =
            " The oldest table in this database is not one the studio judges - in a schema neither the store nor "
            + "MartenOptions.BrowsableSchemas admits, another registered store's, a document type the studio hides, or "
            + "beyond the largest tables a sweep reads - and wraparound protection counts every table in the database, so it "
            + "holds the whole database back as surely as one of these would.",
        MartenTablePartitionParameters = "the Schema screen's Tables tab shows which parameters the partitions differ on",
        LeaderTimeoutClause =
            " - but where a Marten HotCold daemon leads from a transaction, the first must be longer than its leader's idle stretch "
            + "and the second unset for the store's role, or they end that leadership too (K08)",
        MigrationBehindVacuum = "a migration queued behind it (ApplyAllDatabaseChangesOnStartup, db-apply)",
        ConnectionSource = "the store's data source",
        ReadOnlyWrites = "SaveChanges, append and projection write",
        ReadOnlyNeverAsks = "and Marten, like most data access, never asks",
        AmbiguousPoolNotChecked =
            "More than one Npgsql pool in this process uses this database's connection string - the host's own "
            + "AddNpgsqlDataSource beside Marten's, or NpgsqlConnections opened with Marten's connection string - and Npgsql "
            + "reports each under that same name. Their numbers differ, so which pool is Marten's cannot be told, and the "
            + "pool is not judged; naming the host's data source (NpgsqlDataSourceBuilder.Name), or giving its connections an "
            + "Application Name of their own, tells them apart.",
        PoolBorrowers = "Every Marten session, query and daemon batch in this process",
        PoolRemedyCode =
            """
            // The ceiling is the connection string's "Maximum Pool Size" (Npgsql's default is 100).
            // For every data source Marten builds from a connection string:
            opts.ConfigureNpgsqlDataSourceBuilder(b => b.ConnectionStringBuilder.MaxPoolSize = 200);
            """,
        PoolRemedyExplanation =
            "Raise it only when the server has the headroom (C03, and C02 for a store with a database per tenant), and after "
            + "ruling out sessions that hold their connection too long: dispose every IDocumentSession (await using), and keep "
            + "transactions short. A data source the host builds itself (UseNpgsqlDataSource) is configured where it is built.",
        NextConnectionExamples = "a pool that wants to grow, a deployment's new instance, the async daemon's next connection",
        EveryDatabasesConnections = "this store's and everybody else's",
        FewerConnectionsRemedyCode =
            """
            // Fewer connections per process - every data source Marten builds from a connection string:
            opts.ConfigureNpgsqlDataSourceBuilder(b => b.ConnectionStringBuilder.MaxPoolSize = 50);
            """,
        IdleTransactionIdCost = " that Marten's high-water detection can wait behind, so async projections stop moving (D01)",
        SkippedCommit = "SaveChangesAsync",
        IdleTimeoutLeaderClause =
            " With the HotCold leadership lock transaction-scoped (Events.UseAdvisoryLockTransaction, the default at Marten 9.39 "
            + "and 9.40) it can also end the daemon's leadership session: one shorter than the minute or so the leader sits idle "
            + "in its transaction - after taking the lead at 9.39, between DistributedLock's keepalives at 9.50.2 - or any "
            + "timeout with Events.UseMonitoredAdvisoryLock off.",
        StrongerLockExamples = "a schema apply, a rebuild's truncate",
        LongStatementRemedyCode =
            """
            // A ceiling for Marten's own statements, in seconds (Npgsql's default is 30):
            opts.CommandTimeout = 60;

            // ...and an index for the filter a slow document query uses:
            opts.Schema.For<Order>().Index(x => x.CustomerId);
            """,
        LongStatementRemedyExplanation =
            "CommandTimeout bounds what Marten sends; it does not reach another application's statements. The Query page's index "
            + "advisor says which index a document query needs.",
        PreparedTransactionHolds =
            "for good, blocks whatever needs its locks, and, having a transaction id, can hold Marten's high-water mark still so "
            + "that async projections stop",
        RetriedFailures = "optimistic concurrency on documents, an expected version on an event stream, unique violations",
        ChangeDataCaptureConsumer = "a change data capture connector reading Marten's events",
        EveryWriteExamples = "every Marten append, every document upsert",
        EverySourceDatabase = "every store database on this server",
        UnclearedDeadRows = "every document Marten upserts leaves a dead row version that is never cleared",
        LostCommitsMeaning = " For an event store that is events an async projection or a subscriber may already have acted on.",
        ZeroedPageRows = "including a document or an event the store still points at",
        SettingsReadOn =
            "on a connection from the store's own data source - so a value set for its role, its database or in its connection "
            + "string is the one the store's sessions run with",
        PageRowsExamples = "documents, events, index entries",
        ConnectionCarries = "every document and event it reads or writes",
        GenericPlanPlaceholders =
            " - the JSON member names Marten writes into its SQL included - are placeholders; a filter on data ->> $n can never "
            + "use an expression index in that plan, so a sequential scan filtering on one does not show that the table lacks an "
            + "index.",
        OwnRoleOnTables = "the store's own role on its tables",
        OneOfTheTables = "one of its tables",
        SlowStatementCause =
            "A statement this slow is most often a query over a large document table with no index for its filter, or one that "
            + "reads far more rows than it returns.",
        UnboundedRead = "a ToListAsync() with no Take() or paging",
        PlanReadsNoneOfTheTables = "The plan reads none of the store's tables directly",
        UnnamedPlanScans = " of a partition, or of a table this store does not declare or the studio does not show, not named here",
        PartitionedIndexRepair =
            " On a partitioned table a re-apply cannot resume the build: Weasel reads only the partitioned table's own "
            + "indexes, and rebuilding under the same child index name fails while the leftover index on the partition is "
            + "still there. A DBA drops that leftover partition index first; the next apply then builds the index again. The "
            + "partition is not named here, because Marten names a tenant's partition after the tenant.",
        DeclaredIndexRebuild = " Marten's configuration declares it, so a re-apply drops it and builds it again.",
        AppendOnlyExamples = "an event log such as mt_events, a history table",
        HorizonDeadRowsLabel = "Dead rows in the store's tables",
        LogicalSlotConsumers = "Debezium, a CDC pipeline on mt_events, a subscription",
        ConfiguredKeyColumnClause =
            ", and a column the table's own configuration adds to every key (a conjoined Marten table's tenant_id) need not lead",
        JudgedStatementNoun = "store statement",
        ShortStatementsJit =
            "Marten's statements are short, and a large table's cost estimates are what push them over the line.",
        JitRemedyCode =
            "opts.ConfigureNpgsqlDataSourceBuilder(b =>\n"
            + "    b.ConnectionStringBuilder.Options = ((b.ConnectionStringBuilder.Options ?? string.Empty) + \" -c jit=off\").Trim());",
        JitRemedyExplanation =
            "Turns JIT off for the store's own connections only - every data source Marten builds, tenant databases included - and "
            + "leaves the server's setting alone for everybody else. A data source the host builds itself (UseNpgsqlDataSource) "
            + "takes the same Options on its own NpgsqlDataSourceBuilder. To keep JIT for the occasional long query, a DBA can "
            + "raise jit_above_cost for the store's role instead.",
        DataSourceBuilderRemedy =
            """
            // For every data source Marten builds from a connection string:
            opts.ConfigureNpgsqlDataSourceBuilder(b =>
            {
                {statements}
            });
            """,
        LogParametersLoggerClause =
            " Marten 9 gives the data sources it builds no logger factory while StoreOptions.DisableNpgsqlLogging is on, which is its "
            + "default, so there the keyword logs nothing unless the host turned that off or built its own data source with a logger factory.",
        PoolIdleTimeoutLeaderClause =
            " K08 judges the same idle_session_timeout against the HotCold daemon's leadership connection, which this finding does not cover.",
        SharedDataSourceRemedyCode =
            """
            // One NpgsqlDataSource for the process: register it once ...
            services.AddNpgsqlDataSource(connectionString);

            // ... and hand it to Marten instead of a connection string of its own:
            services.AddMarten(opts => { /* ... */ })
                .UseNpgsqlDataSource();
            """,
        SharedDataSourceRemedyExplanation =
            "Every part of the process that opens connections - Marten, Wolverine, a repository of the host's own - then shares "
            + "one pool and one Maximum Pool Size, so the server sees one ceiling for the process instead of one per data source. "
            + "A pool per tenant database is not a duplicate and is not counted.",
        DatabaseGateReason = "needs the store policy to allow this database as a whole, with no tenant selected in the policy's resource",
        ExportSourceNoun = "store",
        PickInTheHeader = "a store and database",
        PlanIndexRemedy =
            "An index whose leading column the filter compares lets Postgres read only the rows it keeps. For a document table, "
            + "declare it in the store's options - a computed index, or a duplicated field - because an index made by hand on a "
            + "Marten table is dropped by the next CreateOrUpdate; the Query page's index advisor writes the line for a where "
            + "clause. For any other table, add it in your own migrations, built CONCURRENTLY on a table in use.",
        PlanPatternRemedy =
            "A btree index can serve a pattern only by the fixed text it starts with, so one that starts with % or _ tests every "
            + "row. A trigram index (the pg_trgm extension's GIN index, gin_trgm_ops) serves it - for a document table declared "
            + "in the store's options, because an index made by hand on a Marten table is dropped by the next CreateOrUpdate - "
            + "and so does searching for a prefix instead, or full-text search.",
    };
}
