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
    // Statements (W5-Q1).

    /// <summary>What a <c>ViewDatabaseActivity</c> that is off says about the Statements page.</summary>
    public required string StatementsCapabilityDenial { get; init; }

    /// <summary>What <c>ReadOnly</c> says about the Statements page.</summary>
    public required string StatementsReadOnlyDenial { get; init; }

    /// <summary>What the scope policy's refusal of the database's statements says.</summary>
    public required string StatementsStorePolicyDenial { get; init; }

    /// <summary>What the write policy's refusal of <c>ViewDatabaseActivity</c> says about the Statements page.</summary>
    public required string StatementsWritePolicyDenial { get; init; }

    /// <summary>
    /// What the studio's cost meter counts and what it does not, inside the parentheses of the "studio cost" line - one meter
    /// for the Health page and the Statements page.
    /// </summary>
    public required string StudioCostCovers { get; init; }

    /// <summary>
    /// Which of the studio's reads run as <c>SqlConsoleRole</c> while it is set, and which as the source's own role - whose
    /// statements alone the Statements page reads - in a sentence after "SqlConsoleRole is x.".
    /// </summary>
    public required string StatementsStudioRoles { get; init; }

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
    /// T14: what a timeout the DBA sets as a backstop must also respect, starting with " - " to follow "is the DBA's
    /// backstop" - MartenStudio's HotCold leader's idle stretch (K08) - or empty for a studio with no daemon.
    /// </summary>
    public required string LeaderTimeoutClause { get; init; }

    /// <summary>S10: the writes a read-only transaction refuses, after "every": <c>SaveChanges, append and projection write</c>.</summary>
    public required string ReadOnlyWrites { get; init; }

    /// <summary>S10: that the application never asks for a read-write transaction, after "is read-only - ".</summary>
    public required string ReadOnlyNeverAsks { get; init; }

    // ---------------------------------------------------------------------------------------------------------------
    // Health: the words a shared check says of the application and its tables (wave 6). Each is MartenStudio's literal as
    // the check said it, and Postgres Studio's in its own words - a plain data source has no store, no documents, no
    // events and no daemon, and PostgresStudioWordsTests evaluates every shared check to hold it to that.

    /// <summary>
    /// The title a check's status row and its findings carry in this studio, by code, where it is not the check's own
    /// (<c>HealthCheckAttribute.Title</c>, which stays the docs heading): empty for MartenStudio, whose titles are the
    /// attributes'.
    /// </summary>
    public IReadOnlyDictionary<string, string> CheckTitles { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The title of the check <paramref name="code" /> in this studio: <see cref="CheckTitles" />' entry, or <paramref name="title" />.</summary>
    /// <param name="code">The check's code.</param>
    /// <param name="title">Its attribute's title.</param>
    public string CheckTitle(string code, string title) => CheckTitles.TryGetValue(code, out string? own) ? own : title;

    /// <summary>
    /// C01's status row when several Npgsql pools share the database's connection string, so which is the source's cannot be
    /// told.
    /// </summary>
    public required string AmbiguousPoolNotChecked { get; init; }

    /// <summary>C01: what borrows a connection from the pool, before " borrows its connection from this pool.".</summary>
    public required string PoolBorrowers { get; init; }

    /// <summary>C01's remedy: the line that raises the pool's ceiling.</summary>
    public required string PoolRemedyCode { get; init; }

    /// <summary>C01's remedy: when to raise it, and where.</summary>
    public required string PoolRemedyExplanation { get; init; }

    /// <summary>C03: who wants the next connection, in a list after "the next one is refused ... - ".</summary>
    public required string NextConnectionExamples { get; init; }

    /// <summary>C03: whose connections count, after "Connections to every database on the server count, ".</summary>
    public required string EveryDatabasesConnections { get; init; }

    /// <summary>C03's remedy: the line that lowers a process's pool ceiling.</summary>
    public required string FewerConnectionsRemedyCode { get; init; }

    /// <summary>
    /// C04: what a transaction id held open costs besides, after "once it has written it holds a transaction id" and before
    /// the full stop.
    /// </summary>
    public required string IdleTransactionIdCost { get; init; }

    /// <summary>C04: the commit an exception path skipped, after "an exception path that skipped ".</summary>
    public required string SkippedCommit { get; init; }

    /// <summary>
    /// C04's remedy: what a server-wide idle timeout also ends, starting with a space after "is the DBA's backstop." - or empty
    /// for a studio with no daemon.
    /// </summary>
    public required string IdleTimeoutLeaderClause { get; init; }

    /// <summary>C05: what queues behind a long statement's locks, after "anything that needs a stronger lock on the same tables - ".</summary>
    public required string StrongerLockExamples { get; init; }

    /// <summary>C05's remedy: a ceiling for the application's own statements.</summary>
    public required string LongStatementRemedyCode { get; init; }

    /// <summary>C05's remedy: what the ceiling reaches, and what a slow query needs instead.</summary>
    public required string LongStatementRemedyExplanation { get; init; }

    /// <summary>
    /// C07: what a forgotten prepared transaction holds, after "holds back vacuum for the whole database " and before the full
    /// stop.
    /// </summary>
    public required string PreparedTransactionHolds { get; init; }

    /// <summary>C08: the failures an application retries, between dashes after "most often failures the application retries".</summary>
    public required string RetriedFailures { get; init; }

    /// <summary>K07: the policy that decides which source a visitor reaches, before " or write policy": <c>store</c>.</summary>
    public required string ScopePolicyNoun { get; init; }

    /// <summary>R01: what reads a slot, in a list after "whatever consumed it - ".</summary>
    public required string ChangeDataCaptureConsumer { get; init; }

    /// <summary>R01: the writes an unread slot grows with, between dashes after "every write the server takes".</summary>
    public required string EveryWriteExamples { get; init; }

    /// <summary>R01: which databases count a server's slots, after "Slots are the server's, not this database's, so ".</summary>
    public required string EverySourceDatabase { get; init; }

    /// <summary>S02: what track_counts = off leaves behind, after "nothing is vacuumed except to stop transaction-id wraparound: ".</summary>
    public required string UnclearedDeadRows { get; init; }

    /// <summary>S02: what a lost commit means here, starting with a space - or empty.</summary>
    public required string LostCommitsMeaning { get; init; }

    /// <summary>S02: which rows a zeroed page takes with it, after "the rows on it are gone for good - ".</summary>
    public required string ZeroedPageRows { get; init; }

    /// <summary>
    /// S02's caveat: whose connection the settings were read on and whose sessions run with them, after "Read in pg_settings
    /// at …, ".
    /// </summary>
    public required string SettingsReadOn { get; init; }

    /// <summary>S03: what a corrupted page holds, between dashes after "the rows on that page".</summary>
    public required string PageRowsExamples { get; init; }

    /// <summary>S05: what an unencrypted connection carries, after "every statement, ".</summary>
    public required string ConnectionCarries { get; init; }

    /// <summary>
    /// T02's caveat: what a generic plan's placeholders cost an index, after "in which the statement's constants" - starting
    /// with a space or a dash.
    /// </summary>
    public required string GenericPlanPlaceholders { get; init; }

    /// <summary>T02, T23: whose statements on which tables are judged, after "No statement of ".</summary>
    public required string OwnRoleOnTables { get; init; }

    /// <summary>T02: the tables a statement names, after "most expensive statements name ".</summary>
    public required string OneOfTheTables { get; init; }

    /// <summary>T02: what a slow statement most often is, a whole sentence.</summary>
    public required string SlowStatementCause { get; init; }

    /// <summary>T02: what usually returns many rows a call, after "a read that returns this many rows each time is often ".</summary>
    public required string UnboundedRead { get; init; }

    /// <summary>T02: what a plan says when it reads none of the judged tables.</summary>
    public required string PlanReadsNoneOfTheTables { get; init; }

    /// <summary>T02: the scans a plan's summary does not name, after "and 2 scans".</summary>
    public required string UnnamedPlanScans { get; init; }

    /// <summary>T02's status row when the role has recorded no statement here yet.</summary>
    public string NothingRecorded =>
        "pg_stat_statements has recorded no statement of " + SourcePossessive + " own role in this database yet - since its last reset, "
        + "or since the extension was installed - so there is nothing to judge until the application has run.";

    /// <summary>
    /// T03: what an invalid index on a partitioned table needs instead of a rebuild of the parent's, starting with a space.
    /// </summary>
    public required string PartitionedIndexRepair { get; init; }

    /// <summary>
    /// T03: who rebuilds an invalid index no other branch of the check words - a declared one in MartenStudio, a primary key
    /// in a studio over plain data sources - starting with a space.
    /// </summary>
    public required string DeclaredIndexRebuild { get; init; }

    /// <summary>T07: what an append-only table is, between dashes after "An append-only table".</summary>
    public required string AppendOnlyExamples { get; init; }

    /// <summary>T14: the evidence label of the dead rows the judged tables hold.</summary>
    public required string HorizonDeadRowsLabel { get; init; }

    /// <summary>T14: what consumes a logical slot, between dashes after "when its consumer last confirmed".</summary>
    public required string LogicalSlotConsumers { get; init; }

    /// <summary>
    /// T15's caveat: a key column a table's own configuration adds, which need not lead - starting with ", and" - or empty.
    /// </summary>
    public required string ConfiguredKeyColumnClause { get; init; }

    /// <summary>T23: a judged statement, in a title: <c>store statement</c>.</summary>
    public required string JudgedStatementNoun { get; init; }

    /// <summary>T23: why the application's statements are hurt by JIT, a whole sentence.</summary>
    public required string ShortStatementsJit { get; init; }

    /// <summary>T23's remedy: the line that turns JIT off for the source's own connections.</summary>
    public required string JitRemedyCode { get; init; }

    /// <summary>T23's remedy: what it changes, and what a DBA can do instead.</summary>
    public required string JitRemedyExplanation { get; init; }

    /// <summary>
    /// The Health page's reason a finding behind the database gate is withheld from a visitor: what the scope policy must
    /// allow.
    /// </summary>
    public required string DatabaseGateReason { get; init; }

    /// <summary>The health export's word for one source it could not reach, before " could not be reached": <c>store</c>.</summary>
    public required string ExportSourceNoun { get; init; }

    /// <summary>What a page with nothing selected asks the visitor to pick in the header, after "Pick ": <c>a store and database</c>.</summary>
    public required string PickInTheHeader { get; init; }

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

        StatementsCapabilityDenial =
            "The statements this database runs, their counters and their text, need MartenStudioOptions.Capabilities.ViewDatabaseActivity.",
        StatementsReadOnlyDenial =
            "MartenStudioOptions.ReadOnly is true, which turns MartenStudioOptions.Capabilities.ViewDatabaseActivity off with every "
            + "other capability.",
        StatementsStorePolicyDenial =
            "Your account may not see this database's statements. The store policy (MartenStudioOptions.StoreAuthorizationPolicy) "
            + "refused this store and database as a whole, with no tenant selected: a statement belongs to no tenant.",
        StatementsWritePolicyDenial =
            "Your account may not see the statements this database runs. The write policy (MartenStudioOptions.WriteAuthorizationPolicy, "
            + "or StoreAuthorizationPolicy when no write policy is set) refused ViewDatabaseActivity for this store and database as "
            + "a whole, with no tenant selected.",
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
        LeaderTimeoutClause =
            " - but where a Marten HotCold daemon leads from a transaction, the first must be longer than its leader's idle stretch "
            + "and the second unset for the store's role, or they end that leadership too (K08)",
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
        ScopePolicyNoun = "store",
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
        PartitionedIndexRepair = InvalidIndexCheck.PartitionedInWords,
        DeclaredIndexRebuild = " Marten's configuration declares it, so a re-apply drops it and builds it again.",
        AppendOnlyExamples = "an event log such as mt_events, a history table",
        HorizonDeadRowsLabel = "Dead rows in the store's tables",
        LogicalSlotConsumers = "Debezium, a CDC pipeline on mt_events, a subscription",
        ConfiguredKeyColumnClause =
            ", and a column the table's own configuration adds to every key (a conjoined Marten table's tenant_id) need not lead",
        JudgedStatementNoun = "store statement",
        ShortStatementsJit =
            "Marten's statements are short, and a large table's cost estimates are what push them over the line.",
        JitRemedyCode = JitShareCheck.RemedyCode,
        JitRemedyExplanation =
            "Turns JIT off for the store's own connections only - every data source Marten builds, tenant databases included - and "
            + "leaves the server's setting alone for everybody else. A data source the host builds itself (UseNpgsqlDataSource) "
            + "takes the same Options on its own NpgsqlDataSourceBuilder. To keep JIT for the occasional long query, a DBA can "
            + "raise jit_above_cost for the store's role instead.",
        DatabaseGateReason = "needs the store policy to allow this database as a whole, with no tenant selected in the policy's resource",
        ExportSourceNoun = "store",
        PickInTheHeader = "a store and database",
    };
}
