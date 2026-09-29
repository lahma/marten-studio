namespace MartenStudio.Services.Database;

/// <summary>What one database object is, as the database browser lists it.</summary>
internal enum DatabaseObjectKind
{
    /// <summary>An ordinary table (<c>relkind r</c>).</summary>
    Table,

    /// <summary>A partitioned table (<c>relkind p</c>); its partitions are counted, never listed.</summary>
    PartitionedTable,

    /// <summary>A view (<c>relkind v</c>).</summary>
    View,

    /// <summary>A materialized view (<c>relkind m</c>).</summary>
    MaterializedView,

    /// <summary>A foreign table (<c>relkind f</c>). Listed with its server's name; its rows are never read.</summary>
    ForeignTable,

    /// <summary>A function (<c>prokind f</c>).</summary>
    Function,

    /// <summary>A procedure (<c>prokind p</c>).</summary>
    Procedure,

    /// <summary>An aggregate (<c>prokind a</c>). It has no body Postgres will print.</summary>
    Aggregate,

    /// <summary>A window function (<c>prokind w</c>).</summary>
    WindowFunction,

    /// <summary>A trigger a person wrote.</summary>
    Trigger,

    /// <summary>A sequence.</summary>
    Sequence,

    /// <summary>An enum type.</summary>
    EnumType,

    /// <summary>A domain.</summary>
    DomainType,

    /// <summary>A range type.</summary>
    RangeType,

    /// <summary>A composite type of its own (not a table's row type).</summary>
    CompositeType,
}

/// <summary>The browser's kind tabs.</summary>
internal enum DatabaseObjectCategory
{
    /// <summary>Tables, partitioned tables and foreign tables.</summary>
    Tables,

    /// <summary>Views and materialized views.</summary>
    Views,

    /// <summary>Functions, procedures, aggregates and window functions.</summary>
    Functions,

    /// <summary>Triggers.</summary>
    Triggers,

    /// <summary>Sequences.</summary>
    Sequences,

    /// <summary>Enum, domain, range and composite types.</summary>
    Types,
}

/// <summary>Whose an object is.</summary>
internal enum DatabaseObjectOwner
{
    /// <summary>A visible document type's table. Its rows are browsed in Documents, never raw.</summary>
    MartenDocument,

    /// <summary>One of the event store's own tables. Its rows are browsed in Streams and the Feed.</summary>
    MartenEventStore,

    /// <summary>
    /// A relational object Marten manages on the host's behalf - a flat-table projection's table,
    /// <c>EventProjection.SchemaObjects</c>, <c>ExtendedSchemaObjects</c>. Its rows are the host's data and
    /// are browsable, badged "Marten-managed".
    /// </summary>
    MartenProjectionOrExtended,

    /// <summary>
    /// Marten's own bookkeeping, and every <c>mt_</c>-prefixed object in any schema: <c>mt_hilo</c>,
    /// <c>mt_tenant_databases</c> (which holds tenant connection strings), <c>mt_*_sequence</c>, the
    /// <c>mt_</c> functions, an unregistered <c>mt_doc_*</c> table. Never row-browsed.
    /// </summary>
    MartenInfrastructure,

    /// <summary>Anything else: the host's own, or another library's.</summary>
    Other,
}

/// <summary>Which owners a list shows.</summary>
internal enum DatabaseOwnerFilter
{
    /// <summary>Everything.</summary>
    All,

    /// <summary>Only what Marten owns.</summary>
    Marten,

    /// <summary>Only what Marten does not own.</summary>
    Other,
}

/// <summary>Which gate said no - or <see cref="None" />.</summary>
/// <remarks>
/// Each one has a sentence that names what would change it: an option (<c>ReadOnly</c>,
/// <c>Capabilities.BrowseDatabase</c>, <c>BrowsableSchemas</c>), the account (the write policy), or a fact
/// about the object that no option changes.
/// </remarks>
internal enum DatabaseRefusal
{
    /// <summary>Nothing refused.</summary>
    None,

    /// <summary><c>MartenStudioOptions.ReadOnly</c> is on.</summary>
    ReadOnly,

    /// <summary><c>MartenStudioOptions.Capabilities.BrowseDatabase</c> is off.</summary>
    CapabilityOff,

    /// <summary>The write policy refused this visitor for the database as a whole.</summary>
    WritePolicy,

    /// <summary>The schema is not one <c>MartenStudioOptions.BrowsableSchemas</c> admits.</summary>
    SchemaNotBrowsable,

    /// <summary>No such object where the visitor may look.</summary>
    NotFound,

    /// <summary>Marten owns it, and its rows are read where Marten's rules apply.</summary>
    MartenOwned,

    /// <summary>A foreign table: reading it would reach the remote server.</summary>
    ForeignTable,

    /// <summary>A view that reads a table the host hides with <c>IsDocumentTypeVisible</c>.</summary>
    HiddenDependency,

    /// <summary>The reading role has no <c>SELECT</c> on it.</summary>
    NoPrivilege,

    /// <summary>Its name cannot be quoted safely.</summary>
    Unquotable,

    /// <summary>
    /// The request does not apply to this kind of object: a table's "definition", an aggregate's body.
    /// </summary>
    NotApplicable,

    /// <summary>Something failed, and <c>Reason</c> says what.</summary>
    Unavailable,

    /// <summary>
    /// The store policy (<c>MartenStudioOptions.StoreAuthorizationPolicy</c>) refused this visitor the
    /// store - the resolved one for the database as a whole, or another store whose object this is.
    /// </summary>
    StorePolicy,

    /// <summary>
    /// A view that reads from, or refers to something in, a schema this visitor may not see - so its query
    /// and its rows would show what that schema holds.
    /// </summary>
    WithheldDependency,
}

/// <summary>Whose an object is, and what that lets the page link to.</summary>
/// <param name="Owner">The owner.</param>
/// <param name="StoreKey">The store that declares it, for a Marten object a store declares.</param>
/// <param name="Alias">The collection alias, for <see cref="DatabaseObjectOwner.MartenDocument" />.</param>
/// <param name="RecognisedAs">
/// For <see cref="DatabaseObjectOwner.Other" />, a neutral "recognised by name" hint: <c>Quartz.NET</c>,
/// <c>Wolverine</c>, <c>EF Core</c>, <c>Hangfire</c>, <c>Flyway</c>. A guess from the name, and said as one.
/// </param>
internal sealed record DatabaseObjectOwnership(
    DatabaseObjectOwner Owner,
    string? StoreKey = null,
    string? Alias = null,
    string? RecognisedAs = null)
{
    /// <summary>Whether Marten owns it in any of the four ways.</summary>
    public bool IsMarten => Owner != DatabaseObjectOwner.Other;
}

/// <summary>Enough to find one object again.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Schema">Its schema - for a trigger, its table's.</param>
/// <param name="Name">Its name.</param>
/// <param name="Arguments">For a routine, its identity arguments: what tells overloads apart.</param>
/// <param name="Table">For a trigger, its table.</param>
internal sealed record DatabaseObjectRef(
    DatabaseObjectKind Kind,
    string Schema,
    string Name,
    string? Arguments = null,
    string? Table = null)
{
    /// <summary>The tab the kind is listed under.</summary>
    public DatabaseObjectCategory Category => DatabaseObjectKinds.CategoryOf(Kind);
}

/// <summary>Whether an object's rows may be read, and if not, the sentence that says why.</summary>
/// <param name="Allowed">Whether they may.</param>
/// <param name="Refusal">Which gate said no.</param>
/// <param name="Reason">The sentence, naming what would change it.</param>
internal sealed record DatabaseRowAccess(bool Allowed, DatabaseRefusal Refusal, string? Reason)
{
    /// <summary>Rows may be read.</summary>
    public static DatabaseRowAccess Granted { get; } = new(true, DatabaseRefusal.None, null);

    /// <summary>Rows may not be read, and this is why.</summary>
    public static DatabaseRowAccess Refused(DatabaseRefusal refusal, string reason) => new(false, refusal, reason);
}

/// <summary>
/// The per-visitor gate, as a page draws it: what is open, and when something is not, the gate that is
/// shut and the sentence naming it.
/// </summary>
/// <param name="CapabilityEnabled">
/// <c>Capabilities.BrowseDatabase</c> and not <c>ReadOnly</c> - process-wide.
/// </param>
/// <param name="ReadOnly"><c>MartenStudioOptions.ReadOnly</c>.</param>
/// <param name="Authorized">
/// Whether the store policy and then the write policy pass this visitor for the database with no tenant;
/// <see langword="null" /> when they were not asked because the capability is off. When it is
/// <see langword="false" />, <paramref name="Refusal" /> names which of the two said no.
/// </param>
/// <param name="BrowsableSchemasConfigured">Whether <c>MartenStudioOptions.BrowsableSchemas</c> names anything.</param>
/// <param name="StoreSchemas">The store's own schemas: structure visible without the capability.</param>
/// <param name="BrowsableSchemas">
/// The schemas whose non-Marten rows and definitions this visitor may read - empty while the gate is shut.
/// </param>
/// <param name="WithheldSchemaCount">
/// How many other schemas exist that this visitor is not shown. A count, never the names.
/// </param>
/// <param name="Refusal">The gate that is shut, or <see cref="DatabaseRefusal.None" />.</param>
/// <param name="Denial">The sentence naming it.</param>
internal sealed record DatabaseAccessState(
    bool CapabilityEnabled,
    bool ReadOnly,
    bool? Authorized,
    bool BrowsableSchemasConfigured,
    IReadOnlyList<string> StoreSchemas,
    IReadOnlyList<string> BrowsableSchemas,
    int WithheldSchemaCount,
    DatabaseRefusal Refusal,
    string? Denial)
{
    /// <summary>
    /// Whether this visitor can see anything past the store's own structure: the capability is on, the
    /// policy said yes, and <c>BrowsableSchemas</c> names something. <see cref="Refusal" /> says which of
    /// the three is missing when it is not.
    /// </summary>
    public bool IsOpen => Refusal == DatabaseRefusal.None;

    /// <summary>Nothing known yet: the state before a read, or after one failed.</summary>
    public static DatabaseAccessState Unknown { get; } =
        new(false, false, null, false, [], [], 0, DatabaseRefusal.Unavailable, null);
}

/// <summary>One schema in the browser's rail.</summary>
/// <param name="Name">The schema name.</param>
/// <param name="IsStoreSchema">Whether the store declares it - the rail's <b>M</b> flag.</param>
/// <param name="RowsBrowsable">Whether this visitor may read non-Marten rows and definitions in it.</param>
/// <param name="Tables">Tables, partitioned tables and foreign tables.</param>
/// <param name="Views">Views and materialized views.</param>
/// <param name="Functions">Routines of every kind.</param>
/// <param name="Triggers">Triggers.</param>
/// <param name="Sequences">Sequences.</param>
/// <param name="Types">Types.</param>
internal sealed record DatabaseSchemaSummary(
    string Name,
    bool IsStoreSchema,
    bool RowsBrowsable,
    int Tables,
    int Views,
    int Functions,
    int Triggers,
    int Sequences,
    int Types)
{
    /// <summary>The count for one tab.</summary>
    public int CountOf(DatabaseObjectCategory category) => category switch
    {
        DatabaseObjectCategory.Tables => Tables,
        DatabaseObjectCategory.Views => Views,
        DatabaseObjectCategory.Functions => Functions,
        DatabaseObjectCategory.Triggers => Triggers,
        DatabaseObjectCategory.Sequences => Sequences,
        DatabaseObjectCategory.Types => Types,
        _ => 0,
    };
}

/// <summary>The browser's landing answer: the schemas this visitor may see, and the gate.</summary>
/// <param name="Schemas">The visible schemas with their counts, the store's own first.</param>
/// <param name="Access">The gate.</param>
/// <param name="Truncated">Whether a kind hit the catalog cap somewhere, so a count is a floor.</param>
/// <param name="Refusal">Why nothing could be read, or <see cref="DatabaseRefusal.None" />.</param>
/// <param name="Reason">The sentence, when something could not be read.</param>
internal sealed record DatabaseBrowserOverview(
    IReadOnlyList<DatabaseSchemaSummary> Schemas,
    DatabaseAccessState Access,
    bool Truncated,
    DatabaseRefusal Refusal,
    string? Reason)
{
    /// <summary>Nothing could be read, and this is why.</summary>
    public static DatabaseBrowserOverview Unavailable(string reason, DatabaseRefusal refusal = DatabaseRefusal.Unavailable, DatabaseAccessState? access = null) =>
        new([], access ?? DatabaseAccessState.Unknown, false, refusal, reason);
}

/// <summary>What one list shows.</summary>
/// <param name="Category">The kind tab.</param>
/// <param name="Schema">One schema, or <see langword="null" /> for every visible one.</param>
/// <param name="Owner">Which owners.</param>
/// <param name="NameFilter">A case-insensitive substring of the name, or <see langword="null" />.</param>
/// <param name="Limit">How many rows at most; the service clamps it.</param>
internal sealed record DatabaseObjectQuery(
    DatabaseObjectCategory Category,
    string? Schema = null,
    DatabaseOwnerFilter Owner = DatabaseOwnerFilter.All,
    string? NameFilter = null,
    int? Limit = null);

/// <summary>How many objects of a list's kind each owner filter would show.</summary>
/// <param name="All">Everything that matched.</param>
/// <param name="Marten">What Marten owns.</param>
/// <param name="Other">What it does not.</param>
internal sealed record DatabaseOwnerCounts(int All, int Marten, int Other)
{
    /// <summary>Nothing.</summary>
    public static DatabaseOwnerCounts None { get; } = new(0, 0, 0);
}

/// <summary>One list, bounded.</summary>
/// <param name="Query">What was asked, after clamping.</param>
/// <param name="Items">The objects, at most <see cref="Limit" />.</param>
/// <param name="Counts">The owner filter's counts over everything that matched the rest of the query.</param>
/// <param name="Truncated">Whether there were more than were read or shown - "refine the filter".</param>
/// <param name="Limit">The cap that applied.</param>
/// <param name="Access">The gate.</param>
/// <param name="Refusal">Why the list is empty because of a gate or a failure.</param>
/// <param name="Reason">The sentence.</param>
internal sealed record DatabaseObjectList(
    DatabaseObjectQuery Query,
    IReadOnlyList<DatabaseObjectSummary> Items,
    DatabaseOwnerCounts Counts,
    bool Truncated,
    int Limit,
    DatabaseAccessState Access,
    DatabaseRefusal Refusal,
    string? Reason)
{
    /// <summary>Nothing listed, and this is why.</summary>
    public static DatabaseObjectList Refused(
        DatabaseObjectQuery query,
        DatabaseRefusal refusal,
        string reason,
        DatabaseAccessState? access = null) =>
        new(query, [], DatabaseOwnerCounts.None, false, query.Limit ?? 0, access ?? DatabaseAccessState.Unknown, refusal, reason);
}

/// <summary>One object in a list. The derived record carries what its kind has.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Schema">Its schema.</param>
/// <param name="Name">Its name.</param>
/// <param name="Ownership">Whose it is.</param>
/// <param name="Comment">Its comment.</param>
/// <param name="Quotable">Whether the studio can put its name (and its schema's) into SQL safely.</param>
internal abstract record DatabaseObjectSummary(
    DatabaseObjectKind Kind,
    string Schema,
    string Name,
    DatabaseObjectOwnership Ownership,
    string? Comment,
    bool Quotable)
{
    /// <summary>Enough to find it again.</summary>
    public virtual DatabaseObjectRef Ref => new(Kind, Schema, Name);

    /// <summary>Its tab.</summary>
    public DatabaseObjectCategory Category => DatabaseObjectKinds.CategoryOf(Kind);
}

/// <summary>A table, view, materialized view or foreign table.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Schema">Its schema.</param>
/// <param name="Name">Its name.</param>
/// <param name="Ownership">Whose it is.</param>
/// <param name="Comment">Its comment.</param>
/// <param name="Quotable">Whether its schema and name can be quoted.</param>
/// <param name="EstimatedRows"><c>reltuples</c>, or <see langword="null" /> when Postgres has no estimate.</param>
/// <param name="SizeBytes">An estimate from <c>relpages</c>.</param>
/// <param name="HasPrimaryKey">Whether it has a primary key.</param>
/// <param name="ForeignKeysOut">Its foreign keys, those into a hidden type's table excepted.</param>
/// <param name="ForeignKeysIn">Foreign keys pointing at it from tables this visitor may see.</param>
/// <param name="PartitionCount">
/// Its partitions, rolled up - or <see langword="null" /> while the gate is shut: a per-tenant partition count
/// is the number of tenants, so it is not structure the store's own schemas give away for free (DB-1 review
/// F9). <paramref name="Kind" /> still says it is partitioned.
/// </param>
/// <param name="Unlogged">Whether it is unlogged.</param>
/// <param name="Populated">For a materialized view, whether it has ever been refreshed.</param>
/// <param name="RowSecurity">Whether row-level security is on.</param>
/// <param name="ForeignServer">For a foreign table, the server's name - and nothing else about it.</param>
/// <param name="Readable">Whether the reading role (<c>SqlConsoleRole</c>, or the store's) may select from it.</param>
/// <param name="Rows">Whether this visitor may read its rows, and why not.</param>
/// <param name="DefinitionAvailable">For a view or materialized view, whether this visitor may read its query.</param>
internal sealed record DatabaseRelationSummary(
    DatabaseObjectKind Kind,
    string Schema,
    string Name,
    DatabaseObjectOwnership Ownership,
    string? Comment,
    bool Quotable,
    long? EstimatedRows,
    long SizeBytes,
    bool HasPrimaryKey,
    int ForeignKeysOut,
    int ForeignKeysIn,
    int? PartitionCount,
    bool Unlogged,
    bool Populated,
    bool RowSecurity,
    string? ForeignServer,
    bool Readable,
    DatabaseRowAccess Rows,
    bool DefinitionAvailable)
    : DatabaseObjectSummary(Kind, Schema, Name, Ownership, Comment, Quotable);

/// <summary>A function, procedure, aggregate or window function.</summary>
/// <param name="Kind">What it is.</param>
/// <param name="Schema">Its schema.</param>
/// <param name="Name">Its name.</param>
/// <param name="Ownership">Whose it is.</param>
/// <param name="Comment">Its comment.</param>
/// <param name="Quotable">Whether its schema and name can be quoted.</param>
/// <param name="IdentityArguments">What tells this overload apart.</param>
/// <param name="Result">What it returns; <see langword="null" /> for a procedure.</param>
/// <param name="Language">Its language.</param>
/// <param name="Volatility"><c>immutable</c>, <c>stable</c> or <c>volatile</c>.</param>
/// <param name="SecurityDefiner">Whether it runs as its owner.</param>
/// <param name="SearchPathPinned">Whether it sets <c>search_path</c> - the check a SECURITY DEFINER function needs.</param>
/// <param name="Config">Its <c>SET</c> clauses.</param>
/// <param name="DefinitionAvailable">
/// Whether this visitor may read its body: never for an aggregate, which has none Postgres will print,
/// and otherwise per the gate.
/// </param>
internal sealed record DatabaseRoutineSummary(
    DatabaseObjectKind Kind,
    string Schema,
    string Name,
    DatabaseObjectOwnership Ownership,
    string? Comment,
    bool Quotable,
    string IdentityArguments,
    string? Result,
    string Language,
    string Volatility,
    bool SecurityDefiner,
    bool SearchPathPinned,
    IReadOnlyList<string> Config,
    bool DefinitionAvailable)
    : DatabaseObjectSummary(Kind, Schema, Name, Ownership, Comment, Quotable)
{
    /// <inheritdoc />
    public override DatabaseObjectRef Ref => new(Kind, Schema, Name, IdentityArguments);

    /// <summary>Whether it is SECURITY DEFINER without a pinned <c>search_path</c> - the badge-worthy case.</summary>
    public bool UnpinnedSecurityDefiner => SecurityDefiner && !SearchPathPinned;
}

/// <summary>A trigger.</summary>
/// <param name="Kind">Always <see cref="DatabaseObjectKind.Trigger" />.</param>
/// <param name="Schema">Its table's schema.</param>
/// <param name="Name">Its name.</param>
/// <param name="Ownership">Whose it is - by its own name, not its table's.</param>
/// <param name="Comment">Unused; triggers are listed without comments.</param>
/// <param name="Quotable">Whether its names can be quoted.</param>
/// <param name="Table">Its table.</param>
/// <param name="TableOwnership">Whose the table is.</param>
/// <param name="Timing"><c>BEFORE</c>, <c>AFTER</c> or <c>INSTEAD OF</c>.</param>
/// <param name="Events"><c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c>, <c>TRUNCATE</c>.</param>
/// <param name="ForEachRow">Row-level rather than statement-level.</param>
/// <param name="Enabled">Whether it fires in an ordinary session (<c>tgenabled</c> <c>O</c> or <c>A</c>).</param>
/// <param name="EnabledMode"><c>tgenabled</c> itself: <c>O</c>, <c>D</c>, <c>R</c> or <c>A</c>.</param>
/// <param name="FunctionSchema">Its function's schema, or <see langword="null" /> when that schema is not one this visitor may see.</param>
/// <param name="FunctionName">Its function's name, or <see langword="null" /> likewise.</param>
/// <param name="IsConstraintTrigger">Whether it is a <c>CONSTRAINT TRIGGER</c>.</param>
/// <param name="DefinitionAvailable">Whether this visitor may read its definition.</param>
internal sealed record DatabaseTriggerSummary(
    DatabaseObjectKind Kind,
    string Schema,
    string Name,
    DatabaseObjectOwnership Ownership,
    string? Comment,
    bool Quotable,
    string Table,
    DatabaseObjectOwnership TableOwnership,
    string Timing,
    IReadOnlyList<string> Events,
    bool ForEachRow,
    bool Enabled,
    string EnabledMode,
    string? FunctionSchema,
    string? FunctionName,
    bool IsConstraintTrigger,
    bool DefinitionAvailable)
    : DatabaseObjectSummary(Kind, Schema, Name, Ownership, Comment, Quotable)
{
    /// <inheritdoc />
    public override DatabaseObjectRef Ref => new(Kind, Schema, Name, null, Table);
}

/// <summary>A sequence.</summary>
/// <param name="Kind">Always <see cref="DatabaseObjectKind.Sequence" />.</param>
/// <param name="Schema">Its schema.</param>
/// <param name="Name">Its name.</param>
/// <param name="Ownership">Whose it is.</param>
/// <param name="Comment">Its comment.</param>
/// <param name="Quotable">Whether its names can be quoted.</param>
/// <param name="DataType">Its type.</param>
/// <param name="Start">Where it starts.</param>
/// <param name="Increment">Its step.</param>
/// <param name="Minimum">Its minimum.</param>
/// <param name="Maximum">Its maximum.</param>
/// <param name="Cycles">Whether it wraps.</param>
/// <param name="CanReadValue">
/// Whether this visitor may ask for its value (<c>IDatabaseObjectService.GetSequenceValueAsync</c>): the
/// reading role has <c>SELECT</c> or <c>USAGE</c> on it, and the gate allows reading the object's data -
/// always for Marten's own, by name, in the store's schemas.
/// </param>
/// <param name="LastValue">
/// Always <see langword="null" /> in a list: reading a sequence's value locks it, so the value is asked for
/// one sequence at a time. Kept for the shape's sake.
/// </param>
/// <param name="OwnerSchema">The owning column's table's schema, when owned and visible.</param>
/// <param name="OwnerTable">The owning table, when owned and visible.</param>
/// <param name="OwnerColumn">The owning column, when owned and visible.</param>
/// <param name="OwnedOutsideView">Whether it is owned by a column of a table this visitor may not see.</param>
/// <param name="RolledUp">
/// For <c>mt_events_sequence</c>: how many per-tenant <c>mt_events_sequence_&lt;tenant&gt;</c> sequences
/// were rolled into it rather than listed, since their names are the tenant list - or
/// <see langword="null" /> while the gate is shut, since their number is the number of tenants (DB-1 review
/// F9).
/// </param>
internal sealed record DatabaseSequenceSummary(
    DatabaseObjectKind Kind,
    string Schema,
    string Name,
    DatabaseObjectOwnership Ownership,
    string? Comment,
    bool Quotable,
    string DataType,
    long Start,
    long Increment,
    long Minimum,
    long Maximum,
    bool Cycles,
    bool CanReadValue,
    long? LastValue,
    string? OwnerSchema,
    string? OwnerTable,
    string? OwnerColumn,
    bool OwnedOutsideView,
    int? RolledUp)
    : DatabaseObjectSummary(Kind, Schema, Name, Ownership, Comment, Quotable);

/// <summary>One attribute of a composite type.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Type">Its type.</param>
internal sealed record DatabaseTypeAttribute(string Name, string Type);

/// <summary>An enum, domain, range or composite type - described, not printed as DDL.</summary>
/// <remarks>
/// The description is a definition, gated like one: when <see cref="DefinitionAvailable" /> is
/// <see langword="false" /> the base type, default, labels, checks, attributes and subtype are empty and only
/// the name, kind and owner are shown.
/// </remarks>
/// <param name="Kind">What it is.</param>
/// <param name="Schema">Its schema.</param>
/// <param name="Name">Its name.</param>
/// <param name="Ownership">Whose it is.</param>
/// <param name="Comment">Its comment.</param>
/// <param name="Quotable">Whether its names can be quoted.</param>
/// <param name="BaseType">A domain's base type.</param>
/// <param name="NotNull">A domain's <c>NOT NULL</c>.</param>
/// <param name="Default">A domain's default.</param>
/// <param name="Labels">An enum's labels, in order.</param>
/// <param name="Checks">A domain's checks.</param>
/// <param name="Attributes">A composite's attributes.</param>
/// <param name="RangeSubtype">A range's subtype.</param>
/// <param name="UsedByColumns">How many columns use it directly.</param>
/// <param name="DefinitionAvailable">Whether this visitor may read its description.</param>
internal sealed record DatabaseTypeSummary(
    DatabaseObjectKind Kind,
    string Schema,
    string Name,
    DatabaseObjectOwnership Ownership,
    string? Comment,
    bool Quotable,
    string? BaseType,
    bool NotNull,
    string? Default,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Checks,
    IReadOnlyList<DatabaseTypeAttribute> Attributes,
    string? RangeSubtype,
    int UsedByColumns,
    bool DefinitionAvailable)
    : DatabaseObjectSummary(Kind, Schema, Name, Ownership, Comment, Quotable);

/// <summary>Whether a column is an identity column, and which kind.</summary>
internal enum DatabaseIdentityKind
{
    /// <summary>Not an identity column.</summary>
    None,

    /// <summary><c>GENERATED ALWAYS AS IDENTITY</c>.</summary>
    Always,

    /// <summary><c>GENERATED BY DEFAULT AS IDENTITY</c>.</summary>
    ByDefault,
}

/// <summary>One column of a relation.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Position">Its <c>attnum</c>.</param>
/// <param name="Type">Its type, as <c>format_type</c> spells it.</param>
/// <param name="Nullable">Whether it accepts null.</param>
/// <param name="Default">Its default, or a generated column's expression.</param>
/// <param name="Identity">Whether it is an identity column.</param>
/// <param name="Generated">Whether it is a stored generated column.</param>
/// <param name="Sortable">Whether its type has a default btree operator class - what lets it be sorted and compared.</param>
/// <param name="Comment">Its comment.</param>
/// <param name="Quotable">Whether its name can be quoted.</param>
internal sealed record DatabaseColumnInfo(
    string Name,
    int Position,
    string Type,
    bool Nullable,
    string? Default,
    DatabaseIdentityKind Identity,
    bool Generated,
    bool Sortable,
    string? Comment,
    bool Quotable);

/// <summary>Where a relation's row key came from.</summary>
internal enum DatabaseRowKeySource
{
    /// <summary>The primary key.</summary>
    PrimaryKey,

    /// <summary>A valid, unconditional unique index over plain NOT NULL columns.</summary>
    UniqueIndex,
}

/// <summary>The columns that identify one row, which is what keyset paging and row detail need.</summary>
/// <param name="Source">Where it came from.</param>
/// <param name="IndexName">The index behind it.</param>
/// <param name="Columns">The key columns, in index order.</param>
internal sealed record DatabaseRowKey(DatabaseRowKeySource Source, string IndexName, IReadOnlyList<string> Columns);

/// <summary>What kind of constraint.</summary>
internal enum DatabaseConstraintKind
{
    /// <summary>A primary key.</summary>
    PrimaryKey,

    /// <summary>A unique constraint.</summary>
    Unique,

    /// <summary>A check constraint.</summary>
    Check,

    /// <summary>An exclusion constraint.</summary>
    Exclusion,

    /// <summary>A constraint trigger's constraint.</summary>
    ConstraintTrigger,

    /// <summary>Anything newer than this list.</summary>
    Other,
}

/// <summary>One constraint that is not a foreign key.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Kind">What kind.</param>
/// <param name="Definition"><c>pg_get_constraintdef</c>.</param>
internal sealed record DatabaseConstraintInfo(string Name, DatabaseConstraintKind Kind, string Definition);

/// <summary>One index.</summary>
/// <param name="Name">Its name.</param>
/// <param name="Definition"><c>pg_get_indexdef</c>.</param>
/// <param name="IsPrimary">Whether it backs the primary key.</param>
/// <param name="IsUnique">Whether it is unique.</param>
/// <param name="IsValid">Whether it is valid (a failed concurrent build is not).</param>
/// <param name="IsPartial">Whether it has a predicate.</param>
/// <param name="KeyColumns">Its key columns, <see langword="null" /> for an expression; never an <c>INCLUDE</c> column.</param>
internal sealed record DatabaseIndexInfo(
    string Name,
    string Definition,
    bool IsPrimary,
    bool IsUnique,
    bool IsValid,
    bool IsPartial,
    IReadOnlyList<string?> KeyColumns);

/// <summary>One foreign key, seen from one end.</summary>
/// <param name="Name">The constraint name.</param>
/// <param name="Schema">The pointing table's schema.</param>
/// <param name="Table">The pointing table.</param>
/// <param name="Columns">The pointing columns.</param>
/// <param name="LinkedSchema">The referenced table's schema, or <see langword="null" /> when this visitor may not see it.</param>
/// <param name="LinkedTable">The referenced table, or <see langword="null" /> likewise.</param>
/// <param name="LinkedColumns">The referenced columns - empty when the table is not named.</param>
/// <param name="LinkedVisible">Whether the referenced end is one this visitor may see.</param>
/// <param name="OtherEndOwnership">Whose the far end is, when it is visible.</param>
/// <param name="Validated"><see langword="false" /> for a <c>NOT VALID</c> key.</param>
/// <param name="OnDelete">The delete action, in SQL words (<c>no action</c>, <c>cascade</c>, ...).</param>
/// <param name="OnUpdate">The update action, likewise.</param>
internal sealed record DatabaseForeignKeyInfo(
    string Name,
    string Schema,
    string Table,
    IReadOnlyList<string> Columns,
    string? LinkedSchema,
    string? LinkedTable,
    IReadOnlyList<string> LinkedColumns,
    bool LinkedVisible,
    DatabaseObjectOwnership? OtherEndOwnership,
    bool Validated,
    string OnDelete,
    string OnUpdate);

/// <summary>One relation a view reads.</summary>
/// <param name="Schema">Its schema, or <see langword="null" /> when this visitor may not see it.</param>
/// <param name="Name">Its name, or <see langword="null" /> likewise.</param>
/// <param name="Kind">What it is.</param>
/// <param name="Depth">One when the view reads it directly.</param>
/// <param name="Visible">Whether it is named.</param>
/// <param name="Ownership">Whose it is, when named.</param>
internal sealed record DatabaseViewDependency(
    string? Schema,
    string? Name,
    DatabaseObjectKind Kind,
    int Depth,
    bool Visible,
    DatabaseObjectOwnership? Ownership);

/// <summary>Everything the object detail view shows about one relation.</summary>
/// <param name="Relation">The relation, or <see langword="null" /> when it could not be shown.</param>
/// <param name="Columns">Its columns.</param>
/// <param name="RowKey">Its row key, or <see langword="null" /> when it has none.</param>
/// <param name="Constraints">Its constraints, foreign keys excepted.</param>
/// <param name="Indexes">Its indexes.</param>
/// <param name="Triggers">Its triggers.</param>
/// <param name="ForeignKeysOut">Its foreign keys.</param>
/// <param name="ForeignKeysIn">Keys pointing at it from tables this visitor may see.</param>
/// <param name="Dependencies">For a view, what it reads.</param>
/// <param name="Access">The gate.</param>
/// <param name="Refusal">Why <see cref="Relation" /> is missing.</param>
/// <param name="Reason">The sentence.</param>
internal sealed record DatabaseObjectDetail(
    DatabaseRelationSummary? Relation,
    IReadOnlyList<DatabaseColumnInfo> Columns,
    DatabaseRowKey? RowKey,
    IReadOnlyList<DatabaseConstraintInfo> Constraints,
    IReadOnlyList<DatabaseIndexInfo> Indexes,
    IReadOnlyList<DatabaseTriggerSummary> Triggers,
    IReadOnlyList<DatabaseForeignKeyInfo> ForeignKeysOut,
    IReadOnlyList<DatabaseForeignKeyInfo> ForeignKeysIn,
    IReadOnlyList<DatabaseViewDependency> Dependencies,
    DatabaseAccessState Access,
    DatabaseRefusal Refusal,
    string? Reason)
{
    /// <summary>Whether there is a relation to show.</summary>
    public bool Found => Relation is not null;

    /// <summary>Nothing to show, and this is why.</summary>
    public static DatabaseObjectDetail Unavailable(
        DatabaseRefusal refusal,
        string reason,
        DatabaseAccessState? access = null) =>
        new(null, [], null, [], [], [], [], [], [], access ?? DatabaseAccessState.Unknown, refusal, reason);
}

/// <summary>A definition, or why it cannot be shown.</summary>
/// <param name="Ref">What was asked for.</param>
/// <param name="Sql">View SQL, a function body, a trigger definition - or <see langword="null" />.</param>
/// <param name="Type">For a type, its description instead of text.</param>
/// <param name="Refusal">Why there is nothing, or <see cref="DatabaseRefusal.None" />.</param>
/// <param name="Reason">The sentence.</param>
internal sealed record DatabaseObjectDefinition(
    DatabaseObjectRef Ref,
    string? Sql,
    DatabaseTypeSummary? Type,
    DatabaseRefusal Refusal,
    string? Reason)
{
    /// <summary>Whether there is something to show.</summary>
    public bool Found => Refusal == DatabaseRefusal.None && (Sql is not null || Type is not null);

    /// <summary>Nothing to show, and this is why.</summary>
    public static DatabaseObjectDefinition Unavailable(DatabaseObjectRef reference, DatabaseRefusal refusal, string reason) =>
        new(reference, null, null, refusal, reason);
}

/// <summary>A sequence's last value, read on demand, or why it cannot be shown.</summary>
/// <param name="Ref">The sequence asked about.</param>
/// <param name="LastValue">
/// Its last value, or <see langword="null" /> - with <see cref="Refusal" /> <see cref="DatabaseRefusal.None" />
/// - when it has never been called.
/// </param>
/// <param name="Refusal">Why there is no value, or <see cref="DatabaseRefusal.None" />.</param>
/// <param name="Reason">The sentence.</param>
internal sealed record DatabaseSequenceValue(
    DatabaseObjectRef Ref,
    long? LastValue,
    DatabaseRefusal Refusal,
    string? Reason)
{
    /// <summary>Whether the value was read - it may still be <see langword="null" />, for a sequence never called.</summary>
    public bool Found => Refusal == DatabaseRefusal.None;

    /// <summary>No value, and this is why.</summary>
    public static DatabaseSequenceValue Unavailable(DatabaseObjectRef reference, DatabaseRefusal refusal, string reason) =>
        new(reference, null, refusal, reason);
}

/// <summary>
/// What one visitor may know and read of another registered store's objects in the resolved database.
/// </summary>
/// <param name="IdentityVisible">
/// Whether the visitor passes that store's store policy for the database with no tenant. When not, its
/// objects are still classified as Marten's - so their rows are still never read raw - but its store key
/// and collection aliases are blanked: the resolver answers a refused store and an unknown one alike, and
/// an ownership badge must not tell them apart either.
/// </param>
/// <param name="Rows">
/// Whether its Marten-managed relational tables (projections, <c>ExtendedSchemaObjects</c>) may have their
/// rows read: that store's store policy, and the write policy with <c>BrowseDatabase</c> named, both for
/// the database with no tenant.
/// </param>
internal sealed record DatabaseStoreAccess(bool IdentityVisible, DatabaseRowAccess Rows)
{
    /// <summary>Everything allowed - the answer for a host with no store policy.</summary>
    public static DatabaseStoreAccess Open { get; } = new(true, DatabaseRowAccess.Granted);
}

/// <summary>Kind arithmetic, in one place.</summary>
internal static class DatabaseObjectKinds
{
    /// <summary>The tab a kind is listed under.</summary>
    public static DatabaseObjectCategory CategoryOf(DatabaseObjectKind kind) => kind switch
    {
        DatabaseObjectKind.Table or DatabaseObjectKind.PartitionedTable or DatabaseObjectKind.ForeignTable
            => DatabaseObjectCategory.Tables,
        DatabaseObjectKind.View or DatabaseObjectKind.MaterializedView => DatabaseObjectCategory.Views,
        DatabaseObjectKind.Function or DatabaseObjectKind.Procedure or DatabaseObjectKind.Aggregate
            or DatabaseObjectKind.WindowFunction => DatabaseObjectCategory.Functions,
        DatabaseObjectKind.Trigger => DatabaseObjectCategory.Triggers,
        DatabaseObjectKind.Sequence => DatabaseObjectCategory.Sequences,
        _ => DatabaseObjectCategory.Types,
    };

    /// <summary>Whether the kind is a relation, with columns and, perhaps, rows.</summary>
    public static bool IsRelation(DatabaseObjectKind kind) =>
        CategoryOf(kind) is DatabaseObjectCategory.Tables or DatabaseObjectCategory.Views;

    /// <summary>Whether the kind is a routine.</summary>
    public static bool IsRoutine(DatabaseObjectKind kind) => CategoryOf(kind) == DatabaseObjectCategory.Functions;

    /// <summary>A relation's kind from its <c>relkind</c>.</summary>
    public static DatabaseObjectKind FromRelkind(string relkind) => relkind switch
    {
        "p" => DatabaseObjectKind.PartitionedTable,
        "v" => DatabaseObjectKind.View,
        "m" => DatabaseObjectKind.MaterializedView,
        "f" => DatabaseObjectKind.ForeignTable,
        _ => DatabaseObjectKind.Table,
    };

    /// <summary>A routine's kind from its <c>prokind</c>.</summary>
    public static DatabaseObjectKind FromProkind(string prokind) => prokind switch
    {
        "p" => DatabaseObjectKind.Procedure,
        "a" => DatabaseObjectKind.Aggregate,
        "w" => DatabaseObjectKind.WindowFunction,
        _ => DatabaseObjectKind.Function,
    };

    /// <summary>A type's kind from its <c>typtype</c>.</summary>
    public static DatabaseObjectKind FromTyptype(string typtype) => typtype switch
    {
        "e" => DatabaseObjectKind.EnumType,
        "d" => DatabaseObjectKind.DomainType,
        "r" => DatabaseObjectKind.RangeType,
        _ => DatabaseObjectKind.CompositeType,
    };
}
