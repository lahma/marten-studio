using DataStudio.Postgres.Services.Postgres;

namespace DataStudio.Postgres.Services.Settings;

/// <summary>
/// Every sentence the shared studio says in a module's own words: the refusals that name an option or a policy, the prose that names
/// a store's configuration, the remedies that are code, and the wording of the judged-table checks.
/// </summary>
/// <remarks>
/// <para>
/// <b>One per module, read through <see cref="IDiagnosticsSettings.Text" />.</b> Shared code never spells a module's options type,
/// policy or configuration itself: it reads the sentence here. The Postgres module's instance is <c>PostgresStudioText.Instance</c> and
/// the Marten module's is built in <c>DataStudio.Marten</c>; <see cref="Of" /> falls back to the Postgres words.
/// </para>
/// <para>
/// <b>It holds only what differs.</b> A sentence that is the same in both modules but for the nouns below
/// (<see cref="OptionsTypeName" />, <see cref="SourceNoun" />, <see cref="SourcePossessive" />, <see cref="ScopePolicyName" />,
/// <see cref="TenantlessScopeClause" />) is a computed member written once. A sentence that truly differs - a remedy that is a
/// <c>StoreOptions</c> line in one module and SQL in the other - is a <see langword="required" /> member each module says in its own
/// words. The actor of a sentence is Data Studio, never a module.
/// </para>
/// </remarks>
internal sealed class StudioText
{
    // ---------------------------------------------------------------------------------------------------------------
    // The nouns every computed sentence is written over.

    /// <summary>The public options type's name: <c>MartenOptions</c>.</summary>
    public required string OptionsTypeName { get; init; }

    /// <summary>A source in a sentence: <c>Marten store</c>.</summary>
    public required string SourceNoun { get; init; }

    /// <summary>
    /// The source, possessive, before "own role", "own schemas" or "most expensive statements": <c>the store's</c>.
    /// </summary>
    public required string SourcePossessive { get; init; }

    /// <summary>The policy asked, with the scope as the resource, before anything of a source is listed: <c>StoreAuthorizationPolicy</c>.</summary>
    public required string ScopePolicyName { get; init; }

    /// <summary>K07: the policy that decides which source a visitor reaches, before " or write policy": <c>store</c>.</summary>
    public required string ScopePolicyNoun { get; init; }

    /// <summary>
    /// What a refusal of a source says when the module has tenants, after "this store and database": <c> as a whole, with no tenant
    /// selected</c> - or empty for a module with none.
    /// </summary>
    public required string TenantlessScopeClause { get; init; }

    /// <summary>
    /// The registration a host writes to configure the module, in a remedy's comment: <c>services.AddDataStudio().AddMarten(options =&gt; ...)</c>.
    /// </summary>
    public required string OptionsRegistration { get; init; }

    /// <summary>
    /// What a host that added the health check without the module calls, whole, after "Data Studio's services are not registered: call ".
    /// </summary>
    public required string HealthCheckRegistration { get; init; }

    /// <summary><paramref name="member" /> as a host's registration spells it: <c>MartenOptions.SqlConsoleRole</c>.</summary>
    /// <param name="member">The member path below the options type.</param>
    public string OptionName(string member) => OptionsTypeName + "." + member;

    /// <summary>The option that switches <paramref name="capability" /> on: <c>MartenOptions.Capabilities.BrowseDatabase</c>.</summary>
    /// <param name="capability">The capability's name.</param>
    public string CapabilityOption(string capability) => OptionName("Capabilities." + capability);

    /// <summary>The scope policy as a host spells it: <c>MartenOptions.StoreAuthorizationPolicy</c>.</summary>
    public string ScopePolicyOption => OptionName(ScopePolicyName);

    /// <summary>
    /// The words of the module <paramref name="settings" /> belong to - for a service whose settings are an optional constructor
    /// argument only so that a test can build it by hand, the way its <c>StudioLogThrottle</c> is: the Postgres module's, as every
    /// such test is, when there are none. The container always supplies them.
    /// </summary>
    /// <param name="settings">The module's settings, or <see langword="null" /> for a hand-built owner.</param>
    public static StudioText Of(IDiagnosticsSettings? settings) => settings?.Text ?? PostgresStudioText.Instance;

    /// <summary>What the Activity ring says of a scope a policy refused.</summary>
    public required string ScopeDeniedEntry { get; init; }

    /// <summary>What the header's Find box offers to find, for a screen reader.</summary>
    public required string FindHint { get; init; }

    /// <summary>
    /// Whether Find's empty result names the scope as the header's pickers do, with their labels ("Source Default › Database admin")
    /// rather than bare ("Default › primary").
    /// </summary>
    public bool FindNamesScopeLikeThePickers { get; init; }

    // ---------------------------------------------------------------------------------------------------------------
    // What the module's own schemas and tables are: two facts, each named for what a page decides with it.

    /// <summary>
    /// Whether the schemas <c>BrowsableSchemas</c> names exactly are the source's own - their structure shown to anyone the source
    /// policy admits, and never withheld - as a plain data source's are. <see langword="false" /> where the module's configuration
    /// declares which schemas are its own (a Marten store's), and <c>ViewDefinitions</c> is the second way to see the ones named.
    /// </summary>
    public required bool NamedSchemasAreOwn { get; init; }

    /// <summary>
    /// Whether the module's configuration declares tables, keys and sequences of its own (a Marten store's document types), so that
    /// the owner filter always has two sides, the relationships page speaks of declared keys, and the store's own sequences are
    /// structure only. <see langword="false" /> for a source that is whatever its database holds.
    /// </summary>
    public required bool ConfigurationDeclaresTables { get; init; }

    // ---------------------------------------------------------------------------------------------------------------
    // The database browser.

    /// <summary>What <c>ReadOnly</c> says about the database browser.</summary>
    /// <param name="readOnlyOption">The switch that is on, as a host spells it.</param>
    public string BrowseReadOnlyDenial(string readOnlyOption) =>
        ReadOnlyTurnsOff(readOnlyOption, CapabilityOption("BrowseDatabase"));

    /// <summary>What a <c>BrowseDatabase</c> that is off says.</summary>
    public required string BrowseCapabilityDenial { get; init; }

    /// <summary>What the write policy's refusal of <c>BrowseDatabase</c> says.</summary>
    public string BrowseWritePolicyDenial =>
        "Your account may not browse the database here. " + WritePolicyRefused("BrowseDatabase") + Scope(null);

    /// <summary>What the scope policy's refusal of the database as a whole says.</summary>
    public string BrowseStorePolicyDenial => "Your account may not browse the database here. " + ScopePolicyRefused(null);

    /// <summary>What an empty <c>BrowsableSchemas</c> says.</summary>
    public required string BrowseEmptyListDenial { get; init; }

    /// <summary>
    /// What <c>ReadOnly</c> says about a definition: it turns both ways in - <c>ViewDefinitions</c> and <c>BrowseDatabase</c> - off.
    /// </summary>
    /// <param name="readOnlyOption">The switch that is on, as a host spells it.</param>
    public string DefinitionReadOnlyDenial(string readOnlyOption) =>
        ReadOnlyTurnsOff(readOnlyOption, CapabilityOption("ViewDefinitions") + " and " + CapabilityOption("BrowseDatabase"));

    /// <summary>What it says of a definition when both <c>ViewDefinitions</c> and <c>BrowseDatabase</c> are off.</summary>
    public required string DefinitionCapabilityDenial { get; init; }

    /// <summary>
    /// What the write policy's refusal of a definition says: <paramref name="capabilities" /> is the capability, or both,
    /// the policy was asked about and refused - never one that is off, which nobody asked.
    /// </summary>
    /// <param name="capabilities">The names, as <c>ViewDefinitions</c>, <c>BrowseDatabase</c> or <c>ViewDefinitions and BrowseDatabase</c>.</param>
    public string DefinitionWritePolicyDenialFor(string capabilities) =>
        "Your account may not read definitions here. " + WritePolicyRefused(capabilities) + Scope(null);

    /// <summary>What a view reading one of Marten's own tables says about its rows.</summary>
    public required string MartenDependencyDenial { get; init; }

    /// <summary>What a relation the connection's own role may not read says, when no <c>SqlConsoleRole</c> is set.</summary>
    public string NoSelectPrivilege =>
        char.ToUpperInvariant(SourcePossessive[0]) + SourcePossessive[1..] + " Postgres role has no SELECT privilege on it.";

    /// <summary>The rail's flag on a source's own schema - <c>M</c> - or <see langword="null" /> to draw none.</summary>
    public required string? OwnSchemaFlag { get; init; }

    /// <summary>The flag's tooltip.</summary>
    public required string? OwnSchemaFlagTitle { get; init; }

    /// <summary>The tooltip of an object the <c>mt_</c> rule calls Marten's own.</summary>
    public required string MartenInternalTitle { get; init; }

    /// <summary>
    /// The database page's notice when the database holds objects named like Marten's, or <see langword="null" /> for a
    /// module that is the place to browse them.
    /// </summary>
    public required string? MartenStoreNotice { get; init; }

    /// <summary>The owner filter's tooltip on its "Marten" side.</summary>
    public required string OwnerFilterMartenTitle { get; init; }

    /// <summary>The owner filter's tooltip on its "Other" side.</summary>
    public required string OwnerFilterOtherTitle { get; init; }

    // ---------------------------------------------------------------------------------------------------------------
    // Database › Sessions.

    /// <summary>What a <c>ViewDatabaseActivity</c> that is off says.</summary>
    public string SessionsCapabilityDenial =>
        "Process ids, roles, client addresses, application names, connection times and statements need "
        + CapabilityOption("ViewDatabaseActivity") + ".";

    /// <summary>What <c>ReadOnly</c> says about session details.</summary>
    /// <param name="readOnlyOption">The switch that is on, as a host spells it.</param>
    public string SessionsReadOnlyDenial(string readOnlyOption) =>
        ReadOnlyTurnsOff(readOnlyOption, CapabilityOption("ViewDatabaseActivity"));

    /// <summary>What the scope policy's refusal of the database's sessions says.</summary>
    public string SessionsStorePolicyDenial =>
        "Your account may not see this database's sessions. " + ScopePolicyRefused("a session belongs to no tenant");

    /// <summary>What the write policy's refusal of <c>ViewDatabaseActivity</c> says.</summary>
    public string SessionsWritePolicyDenial =>
        "Your account may not see who is using this database. " + WritePolicyRefused("ViewDatabaseActivity") + Scope(null);

    // ---------------------------------------------------------------------------------------------------------------
    // Storage.

    /// <summary>What the scope policy's refusal of the database's storage says: its sizes and growth belong to no tenant.</summary>
    public string StorageStorePolicyDenial =>
        "Your account may not see this database's storage. " + ScopePolicyRefused("a table's size belongs to no tenant");

    // ---------------------------------------------------------------------------------------------------------------
    // Statements.

    /// <summary>What a <c>ViewDatabaseActivity</c> that is off says about the Statements page.</summary>
    public string StatementsCapabilityDenial =>
        "The statements this database runs, their counters and their text, need " + CapabilityOption("ViewDatabaseActivity") + ".";

    /// <summary>What <c>ReadOnly</c> says about the Statements page.</summary>
    /// <param name="readOnlyOption">The switch that is on, as a host spells it.</param>
    public string StatementsReadOnlyDenial(string readOnlyOption) =>
        ReadOnlyTurnsOff(readOnlyOption, CapabilityOption("ViewDatabaseActivity"));

    /// <summary>What the scope policy's refusal of the database's statements says.</summary>
    public string StatementsStorePolicyDenial =>
        "Your account may not see this database's statements. " + ScopePolicyRefused("a statement belongs to no tenant");

    /// <summary>What the write policy's refusal of <c>ViewDatabaseActivity</c> says about the Statements page.</summary>
    public string StatementsWritePolicyDenial =>
        "Your account may not see the statements this database runs. " + WritePolicyRefused("ViewDatabaseActivity") + Scope(null);

    /// <summary>
    /// What the cost meter counts and what it does not, inside the parentheses of the "studio cost" line - one meter
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

    /// <summary>The caption of the Relationships table, the diagram's accessible form: what each of its rows is.</summary>
    public required string RelationshipTableCaption { get; init; }

    /// <summary>The accessible name of the list, under the Relationships diagram, of what no foreign key reaches.</summary>
    public required string RelationshipIsolatedLabel { get; init; }

    /// <summary>The Relationships screen's note above the foreign keys it cannot draw.</summary>
    public required string RelationshipUnmatchedNote { get; init; }

    /// <summary>Why a foreign key that points at a table the studio does not draw is not drawn.</summary>
    public required string RelationshipUnplacedTarget { get; init; }

    /// <summary>Why a foreign key on a table the studio does not draw is not drawn.</summary>
    public required string RelationshipUnplacedSource { get; init; }

    /// <summary>
    /// The object detail's Relationships tab on the foreign keys it cannot draw, after "Its foreign keys have" or "These
    /// foreign keys have".
    /// </summary>
    public required string RelationshipNeighbourhoodUnmatched { get; init; }

    /// <summary>What the Relationships screen says when the tables could not be read, before the reason.</summary>
    public required string RelationshipTablesUnavailable { get; init; }

    // ---------------------------------------------------------------------------------------------------------------
    // Health.

    /// <summary>The sentence pointing at Database › Sessions for the statements a finding counts.</summary>
    public string StatementsOnSessionsPage =>
        "The statements themselves are on Database › Sessions, behind " + CapabilityOption("ViewDatabaseActivity") + ", "
        + "masked for the schemas you may not see.";

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

    /// <summary>T12, T13: the judged tables, plural and capitalised for an evidence label.</summary>
    public required string JudgedTables { get; init; }

    /// <summary>T12, T13: one judged table, after "The oldest".</summary>
    public required string JudgedTable { get; init; }

    /// <summary>T12, T13: what a table not judged is.</summary>
    public string NotJudged { get; } = "not among the tables the studio judges";

    /// <summary>T12, T13: where the ages are read from.</summary>
    public required string JudgedTablesRead { get; init; }

    /// <summary>T12: the quiet note when no judged table exists.</summary>
    public required string NoJudgedTable { get; init; }

    /// <summary>T12: why a table that is not judged still matters, starting with a space.</summary>
    public required string UnjudgedOldest { get; init; }

    /// <summary>
    /// T07, T08: where a person sees which storage parameters the partitions of a Marten table differ on, said inside the
    /// parentheses after "autovacuum may be switched off for some of them".
    /// </summary>
    public required string MartenTablePartitionParameters { get; init; }

    /// <summary>
    /// T14: what a timeout the DBA sets as a backstop must also respect, starting with " - " to follow "is the DBA's
    /// backstop" - the HotCold leader's idle stretch (K08) - or empty for a module with no daemon.
    /// </summary>
    public string LeaderTimeoutClause { get; init; } = string.Empty;

    /// <summary>What a migration queued behind a VACUUM at the head of a lock chain is, in a sentence's parentheses.</summary>
    public required string MigrationBehindVacuum { get; init; }

    /// <summary>S04: the connection the posture checks name, <c>the store's data source</c>.</summary>
    public required string ConnectionSource { get; init; }

    /// <summary>S10: the writes a read-only transaction refuses, after "every": <c>SaveChanges, append and projection write</c>.</summary>
    public required string ReadOnlyWrites { get; init; }

    /// <summary>S10: that the application never asks for a read-write transaction, after "is read-only - ".</summary>
    public required string ReadOnlyNeverAsks { get; init; }

    // ---------------------------------------------------------------------------------------------------------------
    // Health: the words a shared check says of the application and its tables. Each module says them in its own words - a plain data
    // source has no store, no documents, no events and no daemon, and PostgresStudioWordsTests evaluates every shared check to hold it to that.

    /// <summary>
    /// The title a check's status row and its findings carry in this module, by code, where it is not the check's own
    /// (<c>HealthCheckAttribute.Title</c>, which stays the docs heading): empty for a module whose titles are the attributes'.
    /// </summary>
    public IReadOnlyDictionary<string, string> CheckTitles { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>The title of the check <paramref name="code" /> in this module: <see cref="CheckTitles" />' entry, or <paramref name="title" />.</summary>
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
    /// for a module with no daemon.
    /// </summary>
    public string IdleTimeoutLeaderClause { get; init; } = string.Empty;

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

    /// <summary>R01: what reads a slot, in a list after "whatever consumed it - ".</summary>
    public required string ChangeDataCaptureConsumer { get; init; }

    /// <summary>R01: the writes an unread slot grows with, between dashes after "every write the server takes".</summary>
    public required string EveryWriteExamples { get; init; }

    /// <summary>R01: which databases count a server's slots, after "Slots are the server's, not this database's, so ".</summary>
    public required string EverySourceDatabase { get; init; }

    /// <summary>S02: what track_counts = off leaves behind, after "nothing is vacuumed except to stop transaction-id wraparound: ".</summary>
    public required string UnclearedDeadRows { get; init; }

    /// <summary>S02: what a lost commit means here, starting with a space - or empty.</summary>
    public string LostCommitsMeaning { get; init; } = string.Empty;

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
    /// T03: who rebuilds an invalid index no other branch of the check words - a declared one in the Marten module, a primary key
    /// in the Postgres module - starting with a space.
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
    public string ConfiguredKeyColumnClause { get; init; } = string.Empty;

    /// <summary>T23: a judged statement, in a title: <c>store statement</c>.</summary>
    public required string JudgedStatementNoun { get; init; }

    /// <summary>T23: why the application's statements are hurt by JIT, a whole sentence.</summary>
    public required string ShortStatementsJit { get; init; }

    /// <summary>T23's remedy: the line that turns JIT off for the source's own connections.</summary>
    public required string JitRemedyCode { get; init; }

    /// <summary>T23's remedy: what it changes, and what a DBA can do instead.</summary>
    public required string JitRemedyExplanation { get; init; }

    /// <summary>
    /// The connection checks' remedies (C16, C17, S11, S12, S13): how a host changes a data source's
    /// <c>NpgsqlConnectionStringBuilder</c> in code, as a template whose <c>{statements}</c> line is replaced by the statements
    /// (<c>b.ConnectionStringBuilder.KeepAlive = 30;</c>, one per line).
    /// </summary>
    public required string DataSourceBuilderRemedy { get; init; }

    /// <summary>
    /// C16: what else the same idle timeout does to the studio's own process, starting with a space after the finding's last
    /// sentence - the HotCold leader's idle stretch (K08) - or empty for a module with no daemon.
    /// </summary>
    public string PoolIdleTimeoutLeaderClause { get; init; } = string.Empty;

    /// <summary>
    /// S13's <c>Log Parameters</c> finding: what decides whether the keyword logs anything in this module's hosts, starting with a
    /// space after the finding's last sentence - or empty for a module that knows nothing of how its host builds the logger.
    /// </summary>
    public string LogParametersLoggerClause { get; init; } = string.Empty;

    /// <summary>
    /// C18's remedy: the lines that give a process one <c>NpgsqlDataSource</c>, registered once and used wherever a connection is
    /// wanted.
    /// </summary>
    public required string SharedDataSourceRemedyCode { get; init; }

    /// <summary>C18's remedy: what sharing the data source changes, and what it does not.</summary>
    public required string SharedDataSourceRemedyExplanation { get; init; }

    /// <summary>
    /// The Health page's reason a finding behind the database gate is withheld from a visitor: what the scope policy must
    /// allow.
    /// </summary>
    public required string DatabaseGateReason { get; init; }

    /// <summary>The health export's word for one source it could not reach, before " could not be reached": <c>store</c>.</summary>
    public required string ExportSourceNoun { get; init; }

    /// <summary>What a page with nothing selected asks the visitor to pick in the header, after "Pick ": <c>a store and database</c>.</summary>
    public required string PickInTheHeader { get; init; }

    /// <summary>Plan insight P04's remedy: where the index a selective sequential scan wants belongs.</summary>
    public required string PlanIndexRemedy { get; init; }

    /// <summary>Plan insight P07's remedy: what serves a pattern with a leading wildcard, and where it is declared.</summary>
    public required string PlanPatternRemedy { get; init; }

    // ---------------------------------------------------------------------------------------------------------------
    // The one shape the computed sentences share.

    private static string ReadOnlyTurnsOff(string readOnlyOption, string what)
    {
        ArgumentException.ThrowIfNullOrEmpty(readOnlyOption);
        return readOnlyOption + " is true, which turns " + what + " off with every other capability.";
    }

    /// <summary>"The write policy (…, or … when no write policy is set) refused <paramref name="what" /> for this store and database", without the closing.</summary>
    private string WritePolicyRefused(string what) =>
        "The write policy (" + OptionName("WriteAuthorizationPolicy") + ", or " + ScopePolicyName + " when no write policy is set) refused "
        + what + " for this " + SourceNoun + " and database";

    /// <summary>"The store policy (…) refused this store and database…", ending with a full stop.</summary>
    private string ScopePolicyRefused(string? because) =>
        "The " + ScopePolicyNoun + " policy (" + ScopePolicyOption + ") refused this " + SourceNoun + " and database" + Scope(because);

    /// <summary>What closes a refusal of a source: " for this …" is the caller's; this is the tenant clause and the full stop.</summary>
    private string Scope(string? because) =>
        TenantlessScopeClause.Length == 0
            ? "."
            : TenantlessScopeClause + (because is null ? string.Empty : ": " + because) + ".";
}
