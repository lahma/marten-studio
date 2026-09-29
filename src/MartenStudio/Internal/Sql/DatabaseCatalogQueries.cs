using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Internal.Sql;

/// <summary>One schema of the database, with the two facts the browsable-schema matcher needs.</summary>
/// <param name="Name">The schema name, exactly as Postgres stores it.</param>
/// <param name="HasUsage">Whether the reading role has <c>USAGE</c> on it.</param>
/// <param name="OwnedByExtension">Whether an extension created it (TimescaleDB's, pg_cron's).</param>
internal sealed record CatalogSchema(string Name, bool HasUsage, bool OwnedByExtension);

/// <summary>A bounded catalog read: the rows, and whether the cap stopped it.</summary>
/// <typeparam name="T">The row type.</typeparam>
/// <param name="Items">The rows, at most the cap.</param>
/// <param name="Truncated">Whether there were more than the cap.</param>
internal sealed record CatalogList<T>(IReadOnlyList<T> Items, bool Truncated)
{
    /// <summary>Nothing, and nothing cut off.</summary>
    public static CatalogList<T> Empty { get; } = new([], false);
}

/// <summary>One table, view, materialized view or foreign table - never a partition.</summary>
/// <param name="Oid">The relation's oid, for the detail reads. Never shown.</param>
/// <param name="Schema">Its schema.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind"><c>relkind</c>: <c>r</c>, <c>p</c>, <c>v</c>, <c>m</c> or <c>f</c>.</param>
/// <param name="EstimatedRows">
/// <c>reltuples</c>, the sum of the leaves' for a partitioned table, or <see langword="null" /> when
/// Postgres has no estimate: a view, a foreign table, or a table never analysed or vacuumed (<c>-1</c> on
/// PG 14+, <c>0</c> with no analyse or vacuum on record on PG 13).
/// </param>
/// <param name="SizeBytes"><c>relpages</c> times the block size, summed over the leaves for a partitioned table. An estimate.</param>
/// <param name="RowSecurity"><c>relrowsecurity</c>.</param>
/// <param name="Populated"><c>relispopulated</c>; <see langword="false" /> only for a materialized view never refreshed.</param>
/// <param name="Unlogged">Whether <c>relpersistence</c> is <c>u</c>.</param>
/// <param name="Readable">Whether the reading role has <c>USAGE</c> on the schema and <c>SELECT</c> on some column.</param>
/// <param name="PartitionCount">How many partitions hang directly off it.</param>
/// <param name="ForeignServer">The foreign server's name, for a foreign table.</param>
/// <param name="Comment">The object comment.</param>
/// <param name="HasPrimaryKey">Whether it has a primary key.</param>
/// <param name="ForeignDescendant">
/// For a partitioned table, or a table with inheritance children: whether any partition or child, at any
/// depth, is a foreign table - so that reading it reads a remote server's rows too.
/// </param>
/// <param name="DescendantSchemas">
/// For the same: the schemas its partitions and children live in, at any depth - where its rows really
/// are. <see langword="null" /> for a relation with none.
/// </param>
internal sealed record CatalogRelation(
    uint Oid,
    string Schema,
    string Name,
    string Kind,
    long? EstimatedRows,
    long SizeBytes,
    bool RowSecurity,
    bool Populated,
    bool Unlogged,
    bool Readable,
    int PartitionCount,
    string? ForeignServer,
    string? Comment,
    bool HasPrimaryKey,
    bool ForeignDescendant = false,
    IReadOnlyList<string>? DescendantSchemas = null);

/// <summary>One function, procedure, aggregate or window function.</summary>
/// <param name="Schema">Its schema.</param>
/// <param name="Name">Its name.</param>
/// <param name="IdentityArguments"><c>pg_get_function_identity_arguments</c>: what tells an overload apart.</param>
/// <param name="Kind"><c>prokind</c>: <c>f</c>, <c>p</c>, <c>a</c> or <c>w</c>.</param>
/// <param name="Result"><c>pg_get_function_result</c>, or <see langword="null" /> for a procedure.</param>
/// <param name="Language">The language name.</param>
/// <param name="Volatility"><c>provolatile</c>: <c>i</c>, <c>s</c> or <c>v</c>.</param>
/// <param name="SecurityDefiner"><c>prosecdef</c>.</param>
/// <param name="Config"><c>proconfig</c>: the <c>SET</c> clauses, as <c>name=value</c>.</param>
/// <param name="Comment">The object comment.</param>
internal sealed record CatalogRoutine(
    string Schema,
    string Name,
    string IdentityArguments,
    string Kind,
    string? Result,
    string Language,
    string Volatility,
    bool SecurityDefiner,
    IReadOnlyList<string> Config,
    string? Comment);

/// <summary>One trigger a person wrote: never an internal one, never a partition's clone.</summary>
/// <param name="Schema">The table's schema.</param>
/// <param name="Table">The table it is on.</param>
/// <param name="Name">The trigger name.</param>
/// <param name="Type"><c>tgtype</c>'s bits: row, before, insert, delete, update, truncate, instead.</param>
/// <param name="Enabled"><c>tgenabled</c>: <c>O</c>, <c>D</c>, <c>R</c> or <c>A</c>.</param>
/// <param name="FunctionSchema">The trigger function's schema.</param>
/// <param name="FunctionName">The trigger function's name.</param>
/// <param name="IsConstraintTrigger">Whether it is a <c>CONSTRAINT TRIGGER</c>.</param>
internal sealed record CatalogTrigger(
    string Schema,
    string Table,
    string Name,
    int Type,
    string Enabled,
    string FunctionSchema,
    string FunctionName,
    bool IsConstraintTrigger);

/// <summary>One sequence, as a list reads it: its settings, never its value.</summary>
/// <param name="Schema">Its schema.</param>
/// <param name="Name">Its name.</param>
/// <param name="DataType">Its data type.</param>
/// <param name="Start"><c>seqstart</c>.</param>
/// <param name="Increment"><c>seqincrement</c>.</param>
/// <param name="Minimum"><c>seqmin</c>.</param>
/// <param name="Maximum"><c>seqmax</c>.</param>
/// <param name="Cycles"><c>seqcycle</c>.</param>
/// <param name="CanReadValue">Whether the reading role has <c>SELECT</c> or <c>USAGE</c> on it.</param>
/// <param name="LastValue">
/// Always <see langword="null" /> from a list: <c>pg_sequence_last_value</c> takes a lock on the sequence it
/// reads, so the value is read on demand, one sequence at a time
/// (<see cref="DatabaseCatalogQueries.ReadSequenceValueAsync" />).
/// </param>
/// <param name="OwnerSchema">The owning table's schema, for an owned sequence.</param>
/// <param name="OwnerTable">The owning table.</param>
/// <param name="OwnerColumn">The owning column.</param>
/// <param name="Comment">The object comment.</param>
internal sealed record CatalogSequence(
    string Schema,
    string Name,
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
    string? Comment);

/// <summary>One attribute of a composite type.</summary>
/// <param name="Name">The attribute name.</param>
/// <param name="Type">Its type, as <c>format_type</c> spells it.</param>
internal sealed record CatalogTypeAttribute(string Name, string Type);

/// <summary>One enum, domain, range or composite type.</summary>
/// <param name="Schema">Its schema.</param>
/// <param name="Name">Its name.</param>
/// <param name="Kind"><c>typtype</c>: <c>e</c>, <c>d</c>, <c>r</c> or <c>c</c>.</param>
/// <param name="BaseType">A domain's base type.</param>
/// <param name="NotNull">A domain's <c>NOT NULL</c>.</param>
/// <param name="Default">A domain's default expression.</param>
/// <param name="Labels">An enum's labels, in sort order.</param>
/// <param name="Checks">A domain's <c>CHECK</c> constraints.</param>
/// <param name="Attributes">A composite type's attributes, in order.</param>
/// <param name="RangeSubtype">A range type's subtype.</param>
/// <param name="UsedByColumns">
/// How many columns of non-partition relations in the schema set use it directly - counted only where the
/// visitor may look, so the number says nothing about a withheld schema.
/// </param>
/// <param name="Comment">The object comment.</param>
internal sealed record CatalogType(
    string Schema,
    string Name,
    string Kind,
    string? BaseType,
    bool NotNull,
    string? Default,
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Checks,
    IReadOnlyList<CatalogTypeAttribute> Attributes,
    string? RangeSubtype,
    int UsedByColumns,
    string? Comment);

/// <summary>One foreign key, both ends by name - never a partition's clone.</summary>
/// <param name="Schema">The pointing table's schema.</param>
/// <param name="Table">The pointing table.</param>
/// <param name="Columns">Its columns, in constraint order.</param>
/// <param name="LinkedSchema">The referenced table's schema.</param>
/// <param name="LinkedTable">The referenced table.</param>
/// <param name="LinkedColumns">The referenced columns, in constraint order.</param>
/// <param name="Name">The constraint name.</param>
/// <param name="Validated"><c>convalidated</c>; <see langword="false" /> for a <c>NOT VALID</c> key.</param>
/// <param name="OnDelete"><c>confdeltype</c>.</param>
/// <param name="OnUpdate"><c>confupdtype</c>.</param>
internal sealed record CatalogForeignKey(
    string Schema,
    string Table,
    IReadOnlyList<string> Columns,
    string LinkedSchema,
    string LinkedTable,
    IReadOnlyList<string> LinkedColumns,
    string Name,
    bool Validated,
    string OnDelete,
    string OnUpdate);

/// <summary>One relation a view or materialized view reads, directly or through other views.</summary>
/// <param name="ViewSchema">The view's schema.</param>
/// <param name="ViewName">The view's name.</param>
/// <param name="Schema">The relation's schema.</param>
/// <param name="Name">The relation's name.</param>
/// <param name="Kind">The relation's <c>relkind</c>.</param>
/// <param name="Depth">One for a direct dependency, more for one reached through another view.</param>
/// <param name="ForeignDescendant">
/// For a partitioned table or a table with inheritance children: whether any partition or child is a
/// foreign table, which a read of the view reaches too.
/// </param>
/// <param name="DescendantSchemas">
/// For the same: the schemas its partitions and children live in, or <see langword="null" />.
/// </param>
/// <param name="ThroughMaterializedView">
/// Whether every path from the view to this relation passes through a materialized view - whose rows are
/// stored locally, so reading the view never reads this relation's rows live.
/// </param>
internal sealed record CatalogViewDependency(
    string ViewSchema,
    string ViewName,
    string Schema,
    string Name,
    string Kind,
    int Depth,
    bool ForeignDescendant = false,
    IReadOnlyList<string>? DescendantSchemas = null,
    bool ThroughMaterializedView = false);

/// <summary>
/// One thing a view or materialized view refers to that is not a relation it reads rows from: a function,
/// an operator, a sequence or a type - directly, or through another view.
/// </summary>
/// <param name="ViewSchema">The view's schema.</param>
/// <param name="ViewName">The view's name.</param>
/// <param name="Kind">
/// <c>f</c> a function, <c>o</c> an operator, <c>S</c> a sequence, <c>t</c> a type, <c>n</c> a schema named as
/// a value (<c>'hr'::regnamespace</c>), <c>C</c> a collation, <c>T</c> a text search configuration, <c>D</c> a
/// text search dictionary.
/// </param>
/// <param name="Schema">
/// Its schema - for <c>n</c>, the schema itself - never <c>pg_catalog</c> or <c>information_schema</c>,
/// which are not read.
/// </param>
/// <param name="Name">Its name.</param>
/// <param name="UserCode">
/// For a function (called directly, or the one behind an operator): that it is code somebody wrote whose
/// reads the studio cannot follow - not an extension's, and not a SQL-standard body, whose dependencies
/// Postgres records and the walk follows.
/// </param>
/// <param name="Depth">One when the view refers to it directly, more through another view or a function.</param>
/// <param name="SecurityDefiner">For a function: that it runs as its owner, not as the reading role.</param>
internal sealed record CatalogViewReference(
    string ViewSchema,
    string ViewName,
    string Kind,
    string Schema,
    string Name,
    bool UserCode,
    int Depth,
    bool SecurityDefiner = false);

/// <summary>How many objects of one kind one schema holds, as the browser would list them.</summary>
/// <param name="Kind">
/// <c>tables</c>, <c>views</c>, <c>functions</c>, <c>triggers</c>, <c>sequences</c> or <c>types</c> - one per
/// browser tab.
/// </param>
/// <param name="Schema">The schema.</param>
/// <param name="Count">The exact count.</param>
internal sealed record CatalogObjectCount(string Kind, string Schema, int Count);

/// <summary>A sequence's value, read on demand.</summary>
/// <param name="CanReadValue">Whether the reading role has <c>SELECT</c> or <c>USAGE</c> on it.</param>
/// <param name="LastValue">
/// <c>pg_sequence_last_value</c>: <see langword="null" /> when the sequence has never been called, or when
/// the role may not read it.
/// </param>
internal sealed record CatalogSequenceValue(bool CanReadValue, long? LastValue);

/// <summary>One column of a relation.</summary>
/// <param name="Name">The column name.</param>
/// <param name="Position"><c>attnum</c>.</param>
/// <param name="Type">Its type, as <c>format_type</c> spells it (<c>character varying(200)</c>).</param>
/// <param name="NotNull"><c>attnotnull</c>.</param>
/// <param name="Default">The default expression, or a generated column's expression.</param>
/// <param name="Identity"><c>attidentity</c>: empty, <c>a</c> (always) or <c>d</c> (by default).</param>
/// <param name="Generated"><c>attgenerated</c>: empty or <c>s</c> (stored).</param>
/// <param name="Sortable">Whether its type, or a domain's base type, has a default btree operator class.</param>
/// <param name="Comment">The column comment.</param>
internal sealed record CatalogColumn(
    string Name,
    int Position,
    string Type,
    bool NotNull,
    string? Default,
    string Identity,
    string Generated,
    bool Sortable,
    string? Comment);

/// <summary>One constraint that is not a foreign key.</summary>
/// <param name="Name">The constraint name.</param>
/// <param name="Kind"><c>contype</c>: <c>p</c>, <c>u</c>, <c>c</c>, <c>x</c> or <c>t</c>.</param>
/// <param name="Definition"><c>pg_get_constraintdef</c>.</param>
internal sealed record CatalogConstraint(string Name, string Kind, string Definition);

/// <summary>One index of a relation.</summary>
/// <param name="Name">The index name.</param>
/// <param name="Definition"><c>pg_get_indexdef</c>.</param>
/// <param name="IsPrimary"><c>indisprimary</c>.</param>
/// <param name="IsUnique"><c>indisunique</c>.</param>
/// <param name="IsValid"><c>indisvalid</c>.</param>
/// <param name="HasPredicate">Whether it is a partial index.</param>
/// <param name="HasExpressions">Whether any key is an expression.</param>
/// <param name="KeyColumns">
/// The key columns only - the first <c>indnkeyatts</c> of <c>indkey</c>, so an <c>INCLUDE</c> column is
/// never taken for a key - with <see langword="null" /> for an expression.
/// </param>
internal sealed record CatalogIndex(
    string Name,
    string Definition,
    bool IsPrimary,
    bool IsUnique,
    bool IsValid,
    bool HasPredicate,
    bool HasExpressions,
    IReadOnlyList<string?> KeyColumns);

/// <summary>Everything the detail view reads about one relation, in one transaction.</summary>
/// <param name="Relation">The relation's own row.</param>
/// <param name="Columns">Its columns.</param>
/// <param name="Constraints">Its constraints, foreign keys excepted.</param>
/// <param name="Indexes">Its indexes.</param>
/// <param name="Triggers">Its triggers.</param>
/// <param name="ForeignKeys">Every foreign key with either end at it, in any schema - the gate decides which ends are named.</param>
/// <param name="Dependencies">For a view or materialized view, everything it reads, followed through other views.</param>
internal sealed record CatalogRelationDetail(
    CatalogRelation Relation,
    IReadOnlyList<CatalogColumn> Columns,
    IReadOnlyList<CatalogConstraint> Constraints,
    IReadOnlyList<CatalogIndex> Indexes,
    IReadOnlyList<CatalogTrigger> Triggers,
    CatalogList<CatalogForeignKey> ForeignKeys,
    CatalogList<CatalogViewDependency> Dependencies)
{
    /// <summary>
    /// For a view or materialized view, the functions, operators, sequences and types it refers to,
    /// followed through other views - what <see cref="Dependencies" /> does not hold, and what decides
    /// whether the view calls code the studio cannot see into, or reaches into a schema the visitor may
    /// not see.
    /// </summary>
    public CatalogList<CatalogViewReference> References { get; init; } = CatalogList<CatalogViewReference>.Empty;
}

/// <summary>A definition, as Postgres reconstructs it.</summary>
/// <param name="Sql">The text, or <see langword="null" /> when there is none to show (an aggregate).</param>
/// <param name="RoutineKind">For a routine, its <c>prokind</c>.</param>
/// <param name="FunctionSchema">For a trigger, the schema of the function it executes.</param>
internal sealed record CatalogDefinition(string? Sql, string? RoutineKind, string? FunctionSchema = null);

/// <summary>
/// The <c>pg_catalog</c> reads behind the database browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every statement here is a constant, and no identifier or value is ever interpolated</b> (AGENTS.md
/// hard rule 4): the schema set travels as one <c>text[]</c> parameter compared with
/// <c>= any(@schemas)</c> - in every list and every lookup, a lookup passing a one-element array - and
/// names, oids, the name filter and the cap are parameters too. The only statement without
/// <c>any(@schemas)</c> is <see cref="SchemasSql" />, whose job is to find the schemas in the first place;
/// its names never leave the server's memory, only the count of the ones a visitor may not see does.
/// </para>
/// <para>
/// <b><c>pg_catalog</c>, never <c>information_schema</c>.</b> The information schema shows only what the
/// current role has some privilege on, so a table the role cannot read would simply not exist on screen -
/// which is a lie about the database, and the wrong answer to "why can I not browse this?". Every
/// built-in function is <c>pg_catalog.</c>-qualified, so a reading role's <c>search_path</c> cannot
/// substitute one of its own; <c>has_*_privilege</c> answers for that role because every read runs inside
/// <see cref="ReadOnlySqlSession.InTransactionAsync{T}" /> after <c>SET LOCAL ROLE</c>.
/// </para>
/// <para>
/// <b>Bounded, and never sized the expensive way.</b> Every list has <c>limit @cap</c> (one more than
/// the caller will show, so "there are more" is a fact rather than a guess) and a name filter,
/// <c>strpos(lower(name), lower(@q)) &gt; 0</c>, which an empty <c>@q</c> passes for every row.
/// <c>pg_total_relation_size</c> and its relatives take an <c>AccessShareLock</c> per relation and stat
/// every file; a list under <c>BrowsableSchemas = ["*"]</c> on a large catalog would do that thousands of
/// times, so sizes here are <c>relpages</c> times the block size - the planner's own figure.
/// </para>
/// <para>
/// <b>A list locks nothing.</b> Every function a list statement calls reads the system catalogs or the
/// statistics collector and takes no lock on the object it describes - measured on PG 17 against
/// <c>pg_locks</c> for the calling backend (<c>DatabaseCatalogLockLiveTests</c>): <c>format_type</c>,
/// <c>pg_get_constraintdef</c>, <c>pg_get_function_*</c>, <c>has_*_privilege</c>,
/// <c>obj_description</c>, <c>pg_stat_get_*</c> and <c>to_regtype</c> leave none. Two that do were taken
/// out, because a list reads every part in one transaction and its locks pile up until it ends:
/// <c>pg_partition_tree</c> (through <c>find_all_inheritors</c>, one <c>AccessShareLock</c> per partition -
/// 308 for a parent with 300, and a Marten store partitioned per tenant has thousands) is replaced by a
/// recursive walk of <c>pg_inherits</c>; <c>pg_sequence_last_value</c> (one <c>RowExclusiveLock</c> per
/// sequence, through <c>init_sequence</c>) is read on demand, for one sequence, by
/// <see cref="SequenceValueSql" />. Enough of either overflows the shared lock table
/// (<c>max_locks_per_transaction</c> times <c>max_connections</c>) with <c>53200</c> for every session in
/// the cluster, queues behind partition DDL, and fails the whole list with <c>55P03</c> when a partition is
/// dropped beside it. The detail reads of one object may lock that object briefly: <c>pg_get_viewdef</c>
/// takes an <c>AccessShareLock</c> on every relation the view reads until its transaction ends, and
/// <c>pg_get_expr</c> and <c>pg_get_triggerdef</c> take one and release it.
/// </para>
/// <para>
/// <b>Deparsed text names every schema.</b> <c>format_type</c>, <c>pg_get_expr</c>, <c>pg_get_viewdef</c>,
/// <c>pg_get_triggerdef</c>, <c>pg_get_indexdef</c> and <c>pg_get_constraintdef</c> qualify a name only when
/// the <c>search_path</c> would not find it - so a withheld schema on the reading role's path would come
/// out unqualified and could not be masked. <see cref="PinSearchPathSql" /> runs first in every catalog
/// transaction and leaves <c>pg_catalog</c> alone on the path, so everything else is printed with its
/// schema, and the database browser's redaction (<c>WithheldNames</c>) can find it.
/// </para>
/// <para>
/// <b>A catalog is read while other people change it.</b> Every <c>has_*_privilege(oid, …)</c> answers
/// <c>NULL</c> rather than raising for an object dropped between the scan and the call - a migration, or
/// another application recreating its schema - so each one is <c>coalesce</c>d to <see langword="false" />:
/// an object that has just vanished is not readable. The <c>pg_get_*def</c> functions answer <c>NULL</c> the
/// same way, and every reader treats their columns as nullable. Measured, not assumed: the no-DDL live test
/// opens the browser on <c>"*"</c> while the rest of the suite drops and recreates schemas beside it.
/// </para>
/// <para>
/// <b>What is never listed.</b> Partitions (<c>relispartition</c>) roll up into their parent's
/// <see cref="CatalogRelation.PartitionCount" />, because Marten names a tenant's partition after the
/// tenant and a list of them is a list of tenants. A trigger Postgres cloned onto a partition
/// (<c>tgparentid &lt;&gt; 0</c>), a trigger on a partition and a foreign key cloned onto one
/// (<c>conparentid &lt;&gt; 0</c>) are left out for the same reason. So is every object an extension owns
/// (<c>pg_depend.deptype = 'e'</c>): TimescaleDB's catalog and pg_cron's function are the extension's,
/// not the host's.
/// </para>
/// </remarks>
internal static class DatabaseCatalogQueries
{
    /// <summary>
    /// Leaves <c>pg_catalog</c> alone on the <c>search_path</c> for the rest of the transaction, so every
    /// deparsed name outside it is printed with its schema. A constant, with no parameter at all.
    /// </summary>
    /// <remarks>
    /// Every statement here already qualifies every built-in function and table it names, which is what
    /// makes this safe to run first: nothing below relies on the reading role's own path. The setting is
    /// local to the transaction, which the read-only session always rolls back.
    /// </remarks>
    internal const string PinSearchPathSql =
        """
        select pg_catalog.set_config('search_path', 'pg_catalog', true)
        """;

    /// <summary>
    /// Every schema, with <c>USAGE</c> and extension ownership. The one statement with no schema filter.
    /// </summary>
    /// <remarks>
    /// Temporary schemas (<c>pg_temp_N</c>, <c>pg_toast_temp_N</c>) are left out in SQL - there is one per
    /// backend that ever used a temporary table, and none of them is ever browsable. Every other system
    /// schema is left to <c>BrowsableSchemaMatcher</c>, which refuses them by name.
    /// </remarks>
    internal const string SchemasSql =
        """
        select n.nspname::text,
               coalesce(pg_catalog.has_schema_privilege(n.oid, 'USAGE'), false),
               exists (
                   select 1
                   from pg_catalog.pg_depend d
                   where d.classid = 'pg_catalog.pg_namespace'::pg_catalog.regclass
                     and d.objid = n.oid
                     and d.deptype = 'e')
        from pg_catalog.pg_namespace n
        where n.nspname !~ '^pg_temp_' and n.nspname !~ '^pg_toast_temp_'
        order by n.nspname
        """;

    /// <summary>Tables, partitioned tables, views, materialized views and foreign tables.</summary>
    /// <remarks>
    /// <para>
    /// <c>@exact</c> is empty for a list and a name for a lookup; <c>@q</c> is empty for "everything".
    /// Both are typed <c>text</c> parameters, so neither needs a second statement text.
    /// </para>
    /// <para>
    /// A partitioned table's estimate and size are the sums over its partition tree, walked through
    /// <c>pg_inherits</c> - catalog rows only. <c>pg_partition_tree</c> gives the same answer and takes an
    /// <c>AccessShareLock</c> on every partition it visits (see the class remarks). The walk starts only from
    /// the partitioned tables, and the tables with inheritance children, this read would list, so a lookup of
    /// one relation walks at most its own tree. A leaf is a partition that is not itself partitioned: its
    /// <c>reltuples</c> count, and the sum is <see langword="null" /> when none of them has an estimate, as
    /// <c>pg_partition_tree</c> made it.
    /// </para>
    /// <para>
    /// <b>Where the rows really are.</b> A <c>select</c> from a partitioned table reads every partition, and
    /// one from a table with inheritance children reads every child: the same walk says whether any of them,
    /// at any depth, is a foreign table (<see cref="CatalogRelation.ForeignDescendant" /> - reading the parent
    /// would reach a remote server) and which schemas they live in
    /// (<see cref="CatalogRelation.DescendantSchemas" /> - a partition in a schema the visitor may not see
    /// holds rows the parent would show them). A legacy parent's own estimate and size stay its own.
    /// </para>
    /// </remarks>
    internal const string RelationsSql =
        """
        with recursive tree (root, relid) as (
            select p.oid, i.inhrelid
            from pg_catalog.pg_class p
            join pg_catalog.pg_namespace pn on pn.oid = p.relnamespace
            join pg_catalog.pg_inherits i on i.inhparent = p.oid
            where p.relkind in ('p', 'r')
              and not p.relispartition
              and pn.nspname = any(@schemas)
              and pg_catalog.strpos(pg_catalog.lower(p.relname::text), pg_catalog.lower(@q)) > 0
              and (@exact = '' or p.relname::text = @exact)
            union all
            select tree.root, i.inhrelid
            from tree
            join pg_catalog.pg_inherits i on i.inhparent = tree.relid
        ),
        partitioned (root, estimated, pages, foreign_descendant, descendant_schemas) as (
            select tree.root,
                   case
                       when pg_catalog.bool_and(x.reltuples < 0) filter (where x.relkind <> 'p') then null
                       else (pg_catalog.sum(greatest(x.reltuples, 0)) filter (where x.relkind <> 'p'))::bigint
                   end,
                   pg_catalog.sum(x.relpages::bigint),
                   pg_catalog.bool_or(x.relkind = 'f'),
                   pg_catalog.array_agg(distinct xn.nspname::text)
            from tree
            join pg_catalog.pg_class x on x.oid = tree.relid
            join pg_catalog.pg_namespace xn on xn.oid = x.relnamespace
            group by tree.root
        )
        select c.oid,
               n.nspname::text,
               c.relname::text,
               c.relkind::text,
               case
                   when c.relkind = 'p' then pt.estimated
                   when c.relkind in ('v', 'f') then null
                   when c.reltuples < 0 then null
                   when c.reltuples = 0
                        and pg_catalog.pg_stat_get_last_analyze_time(c.oid) is null
                        and pg_catalog.pg_stat_get_last_autoanalyze_time(c.oid) is null
                        and pg_catalog.pg_stat_get_last_vacuum_time(c.oid) is null
                        and pg_catalog.pg_stat_get_last_autovacuum_time(c.oid) is null then null
                   else c.reltuples::bigint
               end,
               case
                   when c.relkind = 'p' then coalesce(pt.pages, 0)
                   else c.relpages::bigint
               end * pg_catalog.current_setting('block_size')::bigint,
               c.relrowsecurity,
               c.relispopulated,
               c.relpersistence = 'u',
               coalesce(
                   pg_catalog.has_schema_privilege(n.oid, 'USAGE')
                       and pg_catalog.has_any_column_privilege(c.oid, 'SELECT'),
                   false),
               (select pg_catalog.count(*)
                  from pg_catalog.pg_inherits i
                  join pg_catalog.pg_class ch on ch.oid = i.inhrelid
                  where i.inhparent = c.oid and ch.relispartition)::int,
               fs.srvname::text,
               pg_catalog.obj_description(c.oid, 'pg_class'),
               exists (select 1 from pg_catalog.pg_index pk where pk.indrelid = c.oid and pk.indisprimary),
               coalesce(pt.foreign_descendant, false),
               pt.descendant_schemas
        from pg_catalog.pg_class c
        join pg_catalog.pg_namespace n on n.oid = c.relnamespace
        left join partitioned pt on pt.root = c.oid
        left join pg_catalog.pg_foreign_table ft on ft.ftrelid = c.oid
        left join pg_catalog.pg_foreign_server fs on fs.oid = ft.ftserver
        where c.relkind in ('r', 'p', 'v', 'm', 'f')
          and not c.relispartition
          and n.nspname = any(@schemas)
          and pg_catalog.strpos(pg_catalog.lower(c.relname::text), pg_catalog.lower(@q)) > 0
          and (@exact = '' or c.relname::text = @exact)
          and not exists (
              select 1
              from pg_catalog.pg_depend d
              where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                and d.objid = c.oid
                and d.deptype = 'e')
        order by n.nspname, c.relname
        limit @cap
        """;

    /// <summary>Functions, procedures, aggregates and window functions.</summary>
    /// <remarks>
    /// A routine that is an <em>internal</em> dependency of another object (<c>pg_depend.deptype = 'i'</c>)
    /// is left out as well as an extension's: those are the constructors Postgres writes for a range type
    /// and its multirange - five functions nobody wrote for every <c>create type … as range</c>.
    /// </remarks>
    internal const string RoutinesSql =
        """
        select n.nspname::text,
               p.proname::text,
               pg_catalog.pg_get_function_identity_arguments(p.oid),
               p.prokind::text,
               case when p.prokind = 'p' then null else pg_catalog.pg_get_function_result(p.oid) end,
               l.lanname::text,
               p.provolatile::text,
               p.prosecdef,
               p.proconfig,
               pg_catalog.obj_description(p.oid, 'pg_proc')
        from pg_catalog.pg_proc p
        join pg_catalog.pg_namespace n on n.oid = p.pronamespace
        join pg_catalog.pg_language l on l.oid = p.prolang
        where n.nspname = any(@schemas)
          and pg_catalog.strpos(pg_catalog.lower(p.proname::text), pg_catalog.lower(@q)) > 0
          and (@exact = '' or p.proname::text = @exact)
          and not exists (
              select 1
              from pg_catalog.pg_depend d
              where d.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
                and d.objid = p.oid
                and d.deptype in ('e', 'i'))
        order by n.nspname, p.proname, 3
        limit @cap
        """;

    /// <summary>
    /// Triggers a person wrote: <c>not tgisinternal</c> (a foreign key's own triggers are internal),
    /// <c>tgparentid = 0</c> (not a partition's clone), and never on a partition.
    /// </summary>
    /// <remarks><c>@table</c> is empty for a list and a table name for one table's triggers.</remarks>
    internal const string TriggersSql =
        """
        select n.nspname::text,
               c.relname::text,
               t.tgname::text,
               t.tgtype::int,
               t.tgenabled::text,
               fn.nspname::text,
               p.proname::text,
               t.tgconstraint <> 0
        from pg_catalog.pg_trigger t
        join pg_catalog.pg_class c on c.oid = t.tgrelid
        join pg_catalog.pg_namespace n on n.oid = c.relnamespace
        join pg_catalog.pg_proc p on p.oid = t.tgfoid
        join pg_catalog.pg_namespace fn on fn.oid = p.pronamespace
        where not t.tgisinternal
          and t.tgparentid = 0
          and not c.relispartition
          and n.nspname = any(@schemas)
          and pg_catalog.strpos(pg_catalog.lower(t.tgname::text), pg_catalog.lower(@q)) > 0
          and (@exact = '' or t.tgname::text = @exact)
          and (@table = '' or c.relname::text = @table)
          and not exists (
              select 1
              from pg_catalog.pg_depend d
              where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                and d.objid = c.oid
                and d.deptype = 'e')
        order by n.nspname, c.relname, t.tgname
        limit @cap
        """;

    /// <summary>
    /// Sequences, with the owning column where there is one (<c>pg_depend</c> <c>'a'</c> for
    /// <c>serial</c>, <c>'i'</c> for an identity column) - and never their values.
    /// </summary>
    /// <remarks>
    /// <c>pg_sequence_last_value</c> opens the sequence through <c>init_sequence</c>, which takes a
    /// <c>RowExclusiveLock</c> held to the end of the transaction - one per sequence a list read, and a
    /// Marten store with per-tenant event sequences has one per tenant. So a list says only whether the role
    /// could read the value, and <see cref="SequenceValueSql" /> reads it for one sequence when asked.
    /// </remarks>
    internal const string SequencesSql =
        """
        select n.nspname::text,
               c.relname::text,
               pg_catalog.format_type(s.seqtypid, null),
               s.seqstart,
               s.seqincrement,
               s.seqmin,
               s.seqmax,
               s.seqcycle,
               coalesce(pg_catalog.has_sequence_privilege(c.oid, 'SELECT,USAGE'), false),
               own.nspname::text,
               oc.relname::text,
               a.attname::text,
               pg_catalog.obj_description(c.oid, 'pg_class')
        from pg_catalog.pg_class c
        join pg_catalog.pg_namespace n on n.oid = c.relnamespace
        join pg_catalog.pg_sequence s on s.seqrelid = c.oid
        left join pg_catalog.pg_depend dep
               on dep.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
              and dep.objid = c.oid
              and dep.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass
              and dep.refobjsubid > 0
              and dep.deptype in ('a', 'i')
        left join pg_catalog.pg_class oc on oc.oid = dep.refobjid
        left join pg_catalog.pg_namespace own on own.oid = oc.relnamespace
        left join pg_catalog.pg_attribute a on a.attrelid = dep.refobjid and a.attnum = dep.refobjsubid
        where c.relkind = 'S'
          and n.nspname = any(@schemas)
          and pg_catalog.strpos(pg_catalog.lower(c.relname::text), pg_catalog.lower(@q)) > 0
          and (@exact = '' or c.relname::text = @exact)
          and not exists (
              select 1
              from pg_catalog.pg_depend d
              where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                and d.objid = c.oid
                and d.deptype = 'e')
        order by n.nspname, c.relname
        limit @cap
        """;

    /// <summary>
    /// One sequence's last value, asked for on its own: whether the role may read it, and the value when
    /// it may.
    /// </summary>
    /// <remarks>
    /// The only statement here that locks what it reads - <c>pg_sequence_last_value</c> takes a
    /// <c>RowExclusiveLock</c> on the sequence - which is why it reads exactly one, by exact name, in a
    /// transaction of its own that ends as soon as it has answered. The function raises for a role with
    /// neither <c>SELECT</c> nor <c>USAGE</c>, so it is behind the same <c>has_sequence_privilege</c> test
    /// <c>pg_sequences</c> itself uses; the function is volatile, so the <c>case</c> is not folded away.
    /// </remarks>
    internal const string SequenceValueSql =
        """
        select coalesce(pg_catalog.has_sequence_privilege(c.oid, 'SELECT,USAGE'), false),
               case
                   when pg_catalog.has_sequence_privilege(c.oid, 'SELECT,USAGE')
                   then pg_catalog.pg_sequence_last_value(c.oid::pg_catalog.regclass)
               end
        from pg_catalog.pg_class c
        join pg_catalog.pg_namespace n on n.oid = c.relnamespace
        where c.relkind = 'S'
          and n.nspname = any(@schemas)
          and c.relname::text = @exact
          and not exists (
              select 1
              from pg_catalog.pg_depend d
              where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                and d.objid = c.oid
                and d.deptype = 'e')
        """;

    /// <summary>
    /// Enums, domains, ranges and composite types - a composite only when it is a type of its own
    /// (<c>relkind = 'c'</c>), not the row type every table has.
    /// </summary>
    internal const string TypesSql =
        """
        select n.nspname::text,
               t.typname::text,
               t.typtype::text,
               case when t.typtype = 'd' then pg_catalog.format_type(t.typbasetype, t.typtypmod) end,
               t.typnotnull,
               t.typdefault,
               (select pg_catalog.array_agg(e.enumlabel::text order by e.enumsortorder)
                  from pg_catalog.pg_enum e
                  where e.enumtypid = t.oid),
               (select pg_catalog.array_agg(pg_catalog.pg_get_constraintdef(k.oid, true) order by k.conname)
                  from pg_catalog.pg_constraint k
                  where k.contypid = t.oid),
               (select pg_catalog.array_agg(a.attname::text order by a.attnum)
                  from pg_catalog.pg_attribute a
                  where t.typtype = 'c' and a.attrelid = t.typrelid and a.attnum > 0 and not a.attisdropped),
               (select pg_catalog.array_agg(pg_catalog.format_type(a.atttypid, a.atttypmod) order by a.attnum)
                  from pg_catalog.pg_attribute a
                  where t.typtype = 'c' and a.attrelid = t.typrelid and a.attnum > 0 and not a.attisdropped),
               (select pg_catalog.format_type(r.rngsubtype, null)
                  from pg_catalog.pg_range r
                  where r.rngtypid = t.oid),
               (select pg_catalog.count(*)
                  from pg_catalog.pg_attribute ua
                  join pg_catalog.pg_class uc on uc.oid = ua.attrelid
                  join pg_catalog.pg_namespace un on un.oid = uc.relnamespace
                  where ua.atttypid = t.oid
                    and ua.attnum > 0
                    and not ua.attisdropped
                    and uc.relkind in ('r', 'p', 'v', 'm', 'f')
                    and not uc.relispartition
                    and un.nspname = any(@schemas))::int,
               pg_catalog.obj_description(t.oid, 'pg_type')
        from pg_catalog.pg_type t
        join pg_catalog.pg_namespace n on n.oid = t.typnamespace
        left join pg_catalog.pg_class tc on tc.oid = t.typrelid
        where t.typtype in ('e', 'd', 'r', 'c')
          and (t.typtype <> 'c' or tc.relkind = 'c')
          and n.nspname = any(@schemas)
          and pg_catalog.strpos(pg_catalog.lower(t.typname::text), pg_catalog.lower(@q)) > 0
          and (@exact = '' or t.typname::text = @exact)
          and not exists (
              select 1
              from pg_catalog.pg_depend d
              where d.classid = 'pg_catalog.pg_type'::pg_catalog.regclass
                and d.objid = t.oid
                and d.deptype = 'e')
        order by n.nspname, t.typname
        limit @cap
        """;

    /// <summary>
    /// Every foreign key with <em>either</em> end in the schema set, so a table's inbound keys are here
    /// as well as its outbound ones.
    /// </summary>
    /// <remarks>
    /// The same column-name reconstruction as <see cref="RelationshipQueries.ForeignKeysSql" /> -
    /// <c>unnest(...) with ordinality</c>, because <c>array_agg</c> over a bare <c>unnest</c> has no defined
    /// order - plus <c>convalidated</c>, and never an end that is a partition. <c>@relation</c> is empty for
    /// every key in the set, or one relation's name (in <c>@relation_schema</c>) for the keys at either
    /// end of it, which is what the detail view reads so that a large catalog's cap cannot hide one.
    /// </remarks>
    internal const string ForeignKeysSql =
        """
        select ns.nspname::text,
               cl.relname::text,
               (select pg_catalog.array_agg(a.attname::text order by u.ord)
                  from pg_catalog.unnest(con.conkey) with ordinality as u(attnum, ord)
                  join pg_catalog.pg_attribute a on a.attrelid = con.conrelid and a.attnum = u.attnum),
               fns.nspname::text,
               fcl.relname::text,
               (select pg_catalog.array_agg(a.attname::text order by u.ord)
                  from pg_catalog.unnest(con.confkey) with ordinality as u(attnum, ord)
                  join pg_catalog.pg_attribute a on a.attrelid = con.confrelid and a.attnum = u.attnum),
               con.conname::text,
               con.convalidated,
               con.confdeltype::text,
               con.confupdtype::text
        from pg_catalog.pg_constraint con
        join pg_catalog.pg_class cl on cl.oid = con.conrelid
        join pg_catalog.pg_namespace ns on ns.oid = cl.relnamespace
        join pg_catalog.pg_class fcl on fcl.oid = con.confrelid
        join pg_catalog.pg_namespace fns on fns.oid = fcl.relnamespace
        where con.contype = 'f'
          and con.conparentid = 0
          and not cl.relispartition
          and not fcl.relispartition
          and (ns.nspname = any(@schemas) or fns.nspname = any(@schemas))
          and (@relation = ''
               or (ns.nspname::text = @relation_schema and cl.relname::text = @relation)
               or (fns.nspname::text = @relation_schema and fcl.relname::text = @relation))
        order by ns.nspname, cl.relname, con.conname
        limit @cap
        """;

    /// <summary>
    /// The walk <see cref="ViewDependenciesSql" /> and <see cref="ViewReferencesSql" /> share: from every
    /// view and materialized view in the set, everything it reaches - the relations its rule reads, the
    /// functions and operators it calls, and, recursively, what those read and call.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A node is a relation, a function or an operator (<c>classid</c>, <c>objid</c>). A relation's
    /// dependencies are its <c>_RETURN</c> rule's <c>pg_depend</c> rows (a table has none, and ends the
    /// walk); a function's or an operator's are its own. That is what follows a function with a SQL-standard
    /// body (<c>BEGIN ATOMIC</c> or <c>RETURN</c>): Postgres records a dependency on every table and function
    /// that body names, so a view calling it is judged on what it really reads. A function whose body is
    /// source text - PL/pgSQL, or SQL in a string - records none, and is <see cref="CatalogViewReference.UserCode" />.
    /// </para>
    /// <para>
    /// The depth is capped at sixteen, which no real view stack reaches, and each view starts at depth zero
    /// on itself. <c>through_matview</c> says that the path to a node passed through a materialized view
    /// below the view itself, whose rows are stored locally.
    /// </para>
    /// </remarks>
    private const string ViewWalk =
        """
        with recursive walk (view_oid, classid, objid, depth, through_matview) as (
            select v.oid,
                   'pg_catalog.pg_class'::pg_catalog.regclass::pg_catalog.oid,
                   v.oid,
                   0,
                   false
            from pg_catalog.pg_class v
            join pg_catalog.pg_namespace vn on vn.oid = v.relnamespace
            where v.relkind in ('v', 'm')
              and vn.nspname = any(@schemas)
              and (@exact = '' or v.relname::text = @exact)
            union
            select walk.view_oid,
                   d.refclassid,
                   d.refobjid,
                   walk.depth + 1,
                   walk.through_matview or coalesce(walk.depth > 0 and rc.relkind = 'm', false)
            from walk
            left join pg_catalog.pg_class rc
                   on walk.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                  and rc.oid = walk.objid
            left join pg_catalog.pg_rewrite r on r.ev_class = rc.oid
            join pg_catalog.pg_depend d
              on d.classid = case
                                 when walk.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                                 then 'pg_catalog.pg_rewrite'::pg_catalog.regclass::pg_catalog.oid
                                 else walk.classid
                             end
             and d.objid = case
                               when walk.classid = 'pg_catalog.pg_class'::pg_catalog.regclass then r.oid
                               else walk.objid
                           end
            where d.refclassid in (
                      'pg_catalog.pg_class'::pg_catalog.regclass,
                      'pg_catalog.pg_proc'::pg_catalog.regclass,
                      'pg_catalog.pg_operator'::pg_catalog.regclass)
              and not (d.refclassid = walk.classid and d.refobjid = walk.objid)
              and walk.depth < 16
        )
        """;

    /// <summary>
    /// What every view and materialized view in the schema set reads, followed through other views and
    /// through the functions whose bodies Postgres records dependencies for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A view's query is its <c>_RETURN</c> rule in <c>pg_rewrite</c>, and the rule's <c>pg_depend</c>
    /// rows name every relation (and column) it touches. Recursing through the rules of the views it
    /// touches is what catches a view over a view over a hidden type's table - a view is refused rows if
    /// anything under it is hidden, not only its first level - and recursing through a function with a
    /// SQL-standard body catches a view over a function over one (see <see cref="ViewWalk" />).
    /// <c>@exact</c> is empty for every view in the set, or one view's name - which is what the row gate
    /// reads, so that the answer for the view being opened never depends on a list's cap.
    /// </para>
    /// <para>
    /// <b>What a view's rows reach.</b> A view over a partitioned table reads every partition, and one over
    /// a table with inheritance children reads every child - but <c>pg_depend</c> names only the parent. So
    /// each table the walk reaches is followed down <c>pg_inherits</c> as well (catalog rows only, as
    /// <see cref="RelationsSql" /> does it), and the row says whether a foreign table is among its partitions
    /// or children and which schemas they live in. It also says whether every path to it passes through a
    /// materialized view, whose rows are stored locally: a view over a materialized view over a foreign
    /// table reads nothing remote.
    /// </para>
    /// </remarks>
    internal const string ViewDependenciesSql = ViewWalk + ",\n" +
        """
        descendants (root, relid) as (
            select reached.objid, i.inhrelid
            from (select distinct walk.objid
                  from walk
                  where walk.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                    and walk.depth > 0) as reached
            join pg_catalog.pg_inherits i on i.inhparent = reached.objid
            union all
            select descendants.root, i.inhrelid
            from descendants
            join pg_catalog.pg_inherits i on i.inhparent = descendants.relid
        ),
        inherited (root, foreign_descendant, descendant_schemas) as (
            select descendants.root,
                   pg_catalog.bool_or(x.relkind = 'f'),
                   pg_catalog.array_agg(distinct xn.nspname::text)
            from descendants
            join pg_catalog.pg_class x on x.oid = descendants.relid
            join pg_catalog.pg_namespace xn on xn.oid = x.relnamespace
            group by descendants.root
        )
        select vn.nspname::text,
               v.relname::text,
               n.nspname::text,
               c.relname::text,
               c.relkind::text,
               pg_catalog.min(walk.depth)::int,
               coalesce(ih.foreign_descendant, false),
               ih.descendant_schemas,
               pg_catalog.bool_and(walk.through_matview)
        from walk
        join pg_catalog.pg_class v on v.oid = walk.view_oid
        join pg_catalog.pg_namespace vn on vn.oid = v.relnamespace
        join pg_catalog.pg_class c on c.oid = walk.objid
        join pg_catalog.pg_namespace n on n.oid = c.relnamespace
        left join inherited ih on ih.root = walk.objid
        where walk.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
          and walk.depth > 0
          and c.relkind in ('r', 'p', 'v', 'm', 'f')
        group by vn.nspname, v.relname, n.nspname, c.relname, c.relkind, ih.foreign_descendant, ih.descendant_schemas
        order by 1, 2, 6, 3, 4
        limit @cap
        """;

    /// <summary>
    /// What every view and materialized view in the schema set refers to besides the relations it reads:
    /// the functions it calls, the operators it uses and the functions behind them, the sequences and the
    /// types it names, the schemas it names as values, the collations and text search configurations and
    /// dictionaries it uses - through other views and followed functions too, like
    /// <see cref="ViewDependenciesSql" />.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> <see cref="ViewDependenciesSql" /> follows only relations, and the functions whose bodies
    /// Postgres records dependencies for. A function whose body is source text records none, so the studio
    /// cannot tell what it reads - which is what <see cref="CatalogViewReference.UserCode" /> says, for every
    /// function outside <c>pg_catalog</c> and <c>information_schema</c> that no extension owns and that has
    /// no SQL-standard body (Postgres stores such a body parsed, and leaves <c>prosrc</c> empty - on every
    /// version, which is why this reads <c>prosrc</c> and not the PG 14 <c>prosqlbody</c> column). An operator is
    /// recorded as the operator, not as the function it runs, so both are returned. The rest are here for
    /// their schema: a view that names one in a schema the visitor may not see is a view whose query and rows
    /// reach into that schema - a sequence, a type, an operator, a collation (<c>COLLATE hr.c</c>), a text
    /// search configuration or dictionary (<c>'hr.cfg'::regconfig</c>), and a schema itself
    /// (<c>'hr'::regnamespace</c>, whose value is the withheld schema's name).
    /// </para>
    /// <para>
    /// References into <c>pg_catalog</c> and <c>information_schema</c> are never returned: they are
    /// Postgres' own and say nothing about anybody's schema.
    /// </para>
    /// </remarks>
    internal const string ViewReferencesSql = ViewWalk + ",\n" +
        """
        refs (view_oid, depth, ref_class, ref_oid) as (
            select walk.view_oid, walk.depth, d.refclassid, d.refobjid
            from walk
            left join pg_catalog.pg_class rc
                   on walk.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                  and rc.oid = walk.objid
            left join pg_catalog.pg_rewrite r on r.ev_class = rc.oid
            join pg_catalog.pg_depend d
              on d.classid = case
                                 when walk.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                                 then 'pg_catalog.pg_rewrite'::pg_catalog.regclass::pg_catalog.oid
                                 else walk.classid
                             end
             and d.objid = case
                               when walk.classid = 'pg_catalog.pg_class'::pg_catalog.regclass then r.oid
                               else walk.objid
                           end
            where d.refclassid in (
                'pg_catalog.pg_proc'::pg_catalog.regclass,
                'pg_catalog.pg_operator'::pg_catalog.regclass,
                'pg_catalog.pg_type'::pg_catalog.regclass,
                'pg_catalog.pg_class'::pg_catalog.regclass,
                'pg_catalog.pg_namespace'::pg_catalog.regclass,
                'pg_catalog.pg_collation'::pg_catalog.regclass,
                'pg_catalog.pg_ts_config'::pg_catalog.regclass,
                'pg_catalog.pg_ts_dict'::pg_catalog.regclass)
        ),
        named (view_oid, depth, kind, schema_oid, name, user_code, definer) as (
            select refs.view_oid, refs.depth, 'f'::text, p.pronamespace, p.proname::text,
                   not exists (
                       select 1
                       from pg_catalog.pg_depend e
                       where e.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
                         and e.objid = p.oid
                         and e.deptype = 'e')
                   and not (l.lanname = 'sql' and p.prosrc = ''),
                   p.prosecdef
            from refs
            join pg_catalog.pg_proc p on p.oid = refs.ref_oid
            join pg_catalog.pg_language l on l.oid = p.prolang
            where refs.ref_class = 'pg_catalog.pg_proc'::pg_catalog.regclass
            union all
            select refs.view_oid, refs.depth, 'o'::text, o.oprnamespace, o.oprname::text, false, false
            from refs
            join pg_catalog.pg_operator o on o.oid = refs.ref_oid
            where refs.ref_class = 'pg_catalog.pg_operator'::pg_catalog.regclass
            union all
            select refs.view_oid, refs.depth, 'f'::text, p.pronamespace, p.proname::text,
                   not exists (
                       select 1
                       from pg_catalog.pg_depend e
                       where e.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
                         and e.objid = p.oid
                         and e.deptype = 'e')
                   and not (l.lanname = 'sql' and p.prosrc = ''),
                   p.prosecdef
            from refs
            join pg_catalog.pg_operator o on o.oid = refs.ref_oid
            join pg_catalog.pg_proc p on p.oid = o.oprcode
            join pg_catalog.pg_language l on l.oid = p.prolang
            where refs.ref_class = 'pg_catalog.pg_operator'::pg_catalog.regclass
            union all
            select refs.view_oid, refs.depth, 't'::text, t.typnamespace, t.typname::text, false, false
            from refs
            join pg_catalog.pg_type t on t.oid = refs.ref_oid
            where refs.ref_class = 'pg_catalog.pg_type'::pg_catalog.regclass
            union all
            select refs.view_oid, refs.depth, 'S'::text, s.relnamespace, s.relname::text, false, false
            from refs
            join pg_catalog.pg_class s on s.oid = refs.ref_oid
            where refs.ref_class = 'pg_catalog.pg_class'::pg_catalog.regclass
              and s.relkind = 'S'
            union all
            select refs.view_oid, refs.depth, 'n'::text, ns.oid, ns.nspname::text, false, false
            from refs
            join pg_catalog.pg_namespace ns on ns.oid = refs.ref_oid
            where refs.ref_class = 'pg_catalog.pg_namespace'::pg_catalog.regclass
            union all
            select refs.view_oid, refs.depth, 'C'::text, co.collnamespace, co.collname::text, false, false
            from refs
            join pg_catalog.pg_collation co on co.oid = refs.ref_oid
            where refs.ref_class = 'pg_catalog.pg_collation'::pg_catalog.regclass
            union all
            select refs.view_oid, refs.depth, 'T'::text, cf.cfgnamespace, cf.cfgname::text, false, false
            from refs
            join pg_catalog.pg_ts_config cf on cf.oid = refs.ref_oid
            where refs.ref_class = 'pg_catalog.pg_ts_config'::pg_catalog.regclass
            union all
            select refs.view_oid, refs.depth, 'D'::text, dc.dictnamespace, dc.dictname::text, false, false
            from refs
            join pg_catalog.pg_ts_dict dc on dc.oid = refs.ref_oid
            where refs.ref_class = 'pg_catalog.pg_ts_dict'::pg_catalog.regclass
        )
        select vn.nspname::text,
               v.relname::text,
               named.kind,
               n.nspname::text,
               named.name,
               pg_catalog.bool_or(named.user_code),
               pg_catalog.min(named.depth + 1)::int,
               pg_catalog.bool_or(named.definer)
        from named
        join pg_catalog.pg_class v on v.oid = named.view_oid
        join pg_catalog.pg_namespace vn on vn.oid = v.relnamespace
        join pg_catalog.pg_namespace n on n.oid = named.schema_oid
        where n.nspname <> 'pg_catalog'
          and n.nspname <> 'information_schema'
        group by vn.nspname, v.relname, named.kind, n.nspname, named.name
        order by 1, 2, 7, 3, 4, 5
        limit @cap
        """;

    /// <summary>
    /// How many objects of each kind each schema in the set holds, exactly as the browser's lists would
    /// show them - the overview's numbers, which a list's cap must never turn into floors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One row per kind and schema, so the answer is as small as the schema set whatever the catalog
    /// holds, and no <c>limit</c> is needed. Every branch applies the filter its list applies - partitions,
    /// extension members, internal routines, internal and cloned triggers and a table's row type left out
    /// the same way - and, like the lists, calls nothing that locks: no <c>pg_partition_*</c> function and
    /// no <c>pg_sequence_last_value</c>.
    /// </para>
    /// <para>
    /// <c>@hidden</c> is the lower-cased <c>schema.table</c> of every hidden document type's table, which a
    /// list drops in C#: the table itself, the triggers on it, a sequence one of its columns owns - and, as
    /// <c>schema.function</c> names beside them, the per-type functions an older Marten left for each
    /// (<c>DatabaseObjectClassifier.HiddenTablesAndRoutines</c>). The per-tenant event sequences (<c>mt_events_sequence_&lt;tenant&gt;</c>) are rolled up rather than
    /// listed, so they are not counted either.
    /// </para>
    /// </remarks>
    internal const string ObjectCountsSql =
        """
        select k.kind, k.schema_name, k.total
        from (
            select 'tables'::text as kind, n.nspname::text as schema_name, pg_catalog.count(*)::int as total
            from pg_catalog.pg_class c
            join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('r', 'p', 'f')
              and not c.relispartition
              and n.nspname = any(@schemas)
              and pg_catalog.lower(n.nspname::text || '.' || c.relname::text) <> all(@hidden)
              and not exists (
                  select 1
                  from pg_catalog.pg_depend d
                  where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                    and d.objid = c.oid
                    and d.deptype = 'e')
            group by n.nspname
            union all
            select 'views'::text, n.nspname::text, pg_catalog.count(*)::int
            from pg_catalog.pg_class c
            join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where c.relkind in ('v', 'm')
              and not c.relispartition
              and n.nspname = any(@schemas)
              and not exists (
                  select 1
                  from pg_catalog.pg_depend d
                  where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                    and d.objid = c.oid
                    and d.deptype = 'e')
            group by n.nspname
            union all
            select 'functions'::text, n.nspname::text, pg_catalog.count(*)::int
            from pg_catalog.pg_proc p
            join pg_catalog.pg_namespace n on n.oid = p.pronamespace
            where n.nspname = any(@schemas)
              and pg_catalog.lower(n.nspname::text || '.' || p.proname::text) <> all(@hidden)
              and not exists (
                  select 1
                  from pg_catalog.pg_depend d
                  where d.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
                    and d.objid = p.oid
                    and d.deptype in ('e', 'i'))
            group by n.nspname
            union all
            select 'triggers'::text, n.nspname::text, pg_catalog.count(*)::int
            from pg_catalog.pg_trigger t
            join pg_catalog.pg_class c on c.oid = t.tgrelid
            join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where not t.tgisinternal
              and t.tgparentid = 0
              and not c.relispartition
              and n.nspname = any(@schemas)
              and pg_catalog.lower(n.nspname::text || '.' || c.relname::text) <> all(@hidden)
              and not exists (
                  select 1
                  from pg_catalog.pg_depend d
                  where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                    and d.objid = c.oid
                    and d.deptype = 'e')
            group by n.nspname
            union all
            select 'sequences'::text, n.nspname::text, pg_catalog.count(*)::int
            from pg_catalog.pg_class c
            join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where c.relkind = 'S'
              and n.nspname = any(@schemas)
              and not (pg_catalog.starts_with(pg_catalog.lower(c.relname::text), 'mt_events_sequence_')
                       and pg_catalog.length(c.relname::text) > 19)
              and not exists (
                  select 1
                  from pg_catalog.pg_depend dep
                  join pg_catalog.pg_class oc on oc.oid = dep.refobjid
                  join pg_catalog.pg_namespace own on own.oid = oc.relnamespace
                  where dep.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                    and dep.objid = c.oid
                    and dep.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass
                    and dep.refobjsubid > 0
                    and dep.deptype in ('a', 'i')
                    and pg_catalog.lower(own.nspname::text || '.' || oc.relname::text) = any(@hidden))
              and not exists (
                  select 1
                  from pg_catalog.pg_depend d
                  where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                    and d.objid = c.oid
                    and d.deptype = 'e')
            group by n.nspname
            union all
            select 'types'::text, n.nspname::text, pg_catalog.count(*)::int
            from pg_catalog.pg_type t
            join pg_catalog.pg_namespace n on n.oid = t.typnamespace
            left join pg_catalog.pg_class tc on tc.oid = t.typrelid
            where t.typtype in ('e', 'd', 'r', 'c')
              and (t.typtype <> 'c' or tc.relkind = 'c')
              and n.nspname = any(@schemas)
              and not exists (
                  select 1
                  from pg_catalog.pg_depend d
                  where d.classid = 'pg_catalog.pg_type'::pg_catalog.regclass
                    and d.objid = t.oid
                    and d.deptype = 'e')
            group by n.nspname
        ) k
        order by 2, 1
        """;

    /// <summary>
    /// One relation's columns, by oid - with a "sortable" flag: the type (or a domain's base type) has a
    /// default btree operator class, directly, by binary coercion (<c>varchar</c> to <c>text</c>), or
    /// through the polymorphic array, enum and range classes.
    /// </summary>
    /// <remarks>
    /// The polymorphic types are looked up with <c>to_regtype</c>, which answers null rather than raising
    /// for <c>anymultirange</c> on PG 13, where it does not exist. A domain over a domain is followed one
    /// level only.
    /// </remarks>
    internal const string ColumnsSql =
        """
        select a.attname::text,
               a.attnum::int,
               pg_catalog.format_type(a.atttypid, a.atttypmod),
               a.attnotnull,
               pg_catalog.pg_get_expr(ad.adbin, ad.adrelid),
               a.attidentity::text,
               a.attgenerated::text,
               exists (
                   select 1
                   from pg_catalog.pg_opclass oc
                   join pg_catalog.pg_am am on am.oid = oc.opcmethod
                   where am.amname = 'btree'
                     and oc.opcdefault
                     and (oc.opcintype = bt.oid
                          or exists (
                              select 1
                              from pg_catalog.pg_cast pc
                              where pc.castsource = bt.oid
                                and pc.casttarget = oc.opcintype
                                and pc.castmethod = 'b')
                          or (oc.opcintype = pg_catalog.to_regtype('pg_catalog.anyarray') and bt.typcategory = 'A')
                          or (oc.opcintype = pg_catalog.to_regtype('pg_catalog.anyenum') and bt.typtype = 'e')
                          or (oc.opcintype = pg_catalog.to_regtype('pg_catalog.anyrange') and bt.typtype = 'r')
                          or (oc.opcintype = pg_catalog.to_regtype('pg_catalog.anymultirange') and bt.typtype = 'm'))),
               pg_catalog.col_description(a.attrelid, a.attnum)
        from pg_catalog.pg_attribute a
        join pg_catalog.pg_type t on t.oid = a.atttypid
        join pg_catalog.pg_type bt on bt.oid = case when t.typtype = 'd' then t.typbasetype else t.oid end
        left join pg_catalog.pg_attrdef ad on ad.adrelid = a.attrelid and ad.adnum = a.attnum
        where a.attrelid = @oid and a.attnum > 0 and not a.attisdropped
        order by a.attnum
        """;

    /// <summary>One relation's constraints, foreign keys excepted: those are read with both ends named.</summary>
    internal const string ConstraintsSql =
        """
        select k.conname::text,
               k.contype::text,
               pg_catalog.pg_get_constraintdef(k.oid, true)
        from pg_catalog.pg_constraint k
        where k.conrelid = @oid and k.contype <> 'f'
        order by case k.contype when 'p' then 0 when 'u' then 1 else 2 end, k.conname
        """;

    /// <summary>
    /// One relation's indexes, with the key columns only: the first <c>indnkeyatts</c> entries of
    /// <c>indkey</c>, so an <c>INCLUDE</c> column is never mistaken for part of a key.
    /// </summary>
    internal const string IndexesSql =
        """
        select ic.relname::text,
               pg_catalog.pg_get_indexdef(i.indexrelid),
               i.indisprimary,
               i.indisunique,
               i.indisvalid,
               i.indpred is not null,
               i.indexprs is not null,
               (select pg_catalog.array_agg(a.attname::text order by u.ord)
                  from pg_catalog.unnest(i.indkey::pg_catalog.int2[]) with ordinality as u(attnum, ord)
                  left join pg_catalog.pg_attribute a on a.attrelid = i.indrelid and a.attnum = u.attnum
                  where u.ord <= i.indnkeyatts)
        from pg_catalog.pg_index i
        join pg_catalog.pg_class ic on ic.oid = i.indexrelid
        where i.indrelid = @oid
        order by i.indisprimary desc, ic.relname
        """;

    /// <summary>A view's or materialized view's query.</summary>
    internal const string ViewDefinitionSql =
        """
        select pg_catalog.pg_get_viewdef(c.oid, true)
        from pg_catalog.pg_class c
        join pg_catalog.pg_namespace n on n.oid = c.relnamespace
        where n.nspname = any(@schemas)
          and c.relname::text = @exact
          and c.relkind in ('v', 'm')
          and not c.relispartition
          and not exists (
              select 1
              from pg_catalog.pg_depend d
              where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                and d.objid = c.oid
                and d.deptype = 'e')
        """;

    /// <summary>
    /// A routine's definition, one overload by its identity arguments.
    /// </summary>
    /// <remarks>
    /// <c>pg_get_functiondef</c> raises <c>42809</c> for an aggregate, so an aggregate's row carries its
    /// kind and no text: the <c>case</c> keeps the call from being made at all. Procedures are answered
    /// (<c>CREATE OR REPLACE PROCEDURE</c>) - measured on PG 17 by <c>DatabaseCatalogLiveTests</c>.
    /// </remarks>
    internal const string RoutineDefinitionSql =
        """
        select p.prokind::text,
               case when p.prokind = 'a' then null else pg_catalog.pg_get_functiondef(p.oid) end
        from pg_catalog.pg_proc p
        join pg_catalog.pg_namespace n on n.oid = p.pronamespace
        where n.nspname = any(@schemas)
          and p.proname::text = @exact
          and pg_catalog.pg_get_function_identity_arguments(p.oid) = @arguments
          and not exists (
              select 1
              from pg_catalog.pg_depend d
              where d.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
                and d.objid = p.oid
                and d.deptype in ('e', 'i'))
        """;

    /// <summary>A trigger's definition, by its table and its name, with the schema of the function it runs.</summary>
    /// <remarks>
    /// The function's schema comes back beside the text because the text names it: a trigger whose
    /// function lives in a schema the visitor may not see is refused its definition rather than printed
    /// with that schema's name in it.
    /// </remarks>
    internal const string TriggerDefinitionSql =
        """
        select pg_catalog.pg_get_triggerdef(t.oid, true),
               fn.nspname::text
        from pg_catalog.pg_trigger t
        join pg_catalog.pg_class c on c.oid = t.tgrelid
        join pg_catalog.pg_namespace n on n.oid = c.relnamespace
        join pg_catalog.pg_proc p on p.oid = t.tgfoid
        join pg_catalog.pg_namespace fn on fn.oid = p.pronamespace
        where n.nspname = any(@schemas)
          and c.relname::text = @table
          and t.tgname::text = @exact
          and not t.tgisinternal
          and t.tgparentid = 0
          and not c.relispartition
          and not exists (
              select 1
              from pg_catalog.pg_depend d
              where d.classid = 'pg_catalog.pg_class'::pg_catalog.regclass
                and d.objid = c.oid
                and d.deptype = 'e')
        """;

    /// <summary>
    /// Every statement a list is read with - the ones <c>DatabaseCatalogQueriesTests</c> holds to
    /// <c>limit @cap</c>, the name filter and the absence of size functions.
    /// </summary>
    internal static IReadOnlyList<string> ListStatements { get; } =
    [
        RelationsSql, RoutinesSql, TriggersSql, SequencesSql, TypesSql, ForeignKeysSql, ViewDependenciesSql,
        ViewReferencesSql,
    ];

    /// <summary>
    /// Every statement that must take no lock on anything it describes: the lists, and the overview's
    /// counts. <c>DatabaseCatalogQueriesTests</c> holds them to the absence of every function that does,
    /// and <c>DatabaseCatalogLockLiveTests</c> measures it.
    /// </summary>
    internal static IReadOnlyList<string> LockFreeStatements { get; } = [.. ListStatements, ObjectCountsSql];

    /// <summary>Every statement that reads within a schema set - all of them but <see cref="SchemasSql" />.</summary>
    internal static IReadOnlyList<string> ScopedStatements { get; } =
    [
        .. ListStatements, ObjectCountsSql, ViewDefinitionSql, RoutineDefinitionSql, TriggerDefinitionSql,
        SequenceValueSql,
    ];

    /// <summary>The statements that set up the transaction rather than read anything.</summary>
    internal static IReadOnlyList<string> SessionStatements { get; } = [PinSearchPathSql];

    /// <summary>The statements that read one relation by oid, which its lookup already proved in scope.</summary>
    internal static IReadOnlyList<string> ByOidStatements { get; } = [ColumnsSql, ConstraintsSql, IndexesSql];

    /// <summary>
    /// Whether <see cref="SqlIdentifier.Quote" /> would accept <paramref name="name" />. The answer is the
    /// method's own, not a copy of its rules: an object whose name it refuses is listed and never read.
    /// </summary>
    public static bool IsQuotable(string? name)
    {
        if (name is null)
        {
            return false;
        }

        try
        {
            _ = SqlIdentifier.Quote(name);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Reads every schema.</summary>
    public static async Task<IReadOnlyList<CatalogSchema>> ReadSchemasAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = Command(connection, transaction, SchemasSql, commandTimeoutSeconds);

        List<CatalogSchema> schemas = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            schemas.Add(new CatalogSchema(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        }

        return schemas;
    }

    /// <summary>Reads relations in the schema set, at most <paramref name="cap" />.</summary>
    public static Task<CatalogList<CatalogRelation>> ReadRelationsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> schemas,
        string? filter,
        string? exact,
        int cap,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default) =>
        ReadListAsync(
            connection,
            transaction,
            RelationsSql,
            command => BindList(command, schemas, filter, exact, cap),
            static reader => new CatalogRelation(
                reader.GetFieldValue<uint>(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                NullableInt64(reader, 4),
                reader.IsDBNull(5) ? 0 : reader.GetInt64(5),
                reader.GetBoolean(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8),
                reader.GetBoolean(9),
                reader.GetInt32(10),
                NullableString(reader, 11),
                NullableString(reader, 12),
                reader.GetBoolean(13),
                reader.GetBoolean(14),
                reader.IsDBNull(15) ? null : reader.GetFieldValue<string[]>(15)),
            schemas,
            cap,
            commandTimeoutSeconds,
            cancellationToken);

    /// <summary>Reads routines in the schema set, at most <paramref name="cap" />.</summary>
    public static Task<CatalogList<CatalogRoutine>> ReadRoutinesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> schemas,
        string? filter,
        string? exact,
        int cap,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default) =>
        ReadListAsync(
            connection,
            transaction,
            RoutinesSql,
            command => BindList(command, schemas, filter, exact, cap),
            static reader => new CatalogRoutine(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.GetString(3),
                NullableString(reader, 4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetBoolean(7),
                Strings(reader, 8),
                NullableString(reader, 9)),
            schemas,
            cap,
            commandTimeoutSeconds,
            cancellationToken);

    /// <summary>Reads triggers in the schema set - one table's, when <paramref name="table" /> is given.</summary>
    public static Task<CatalogList<CatalogTrigger>> ReadTriggersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> schemas,
        string? filter,
        string? exact,
        string? table,
        int cap,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default) =>
        ReadListAsync(
            connection,
            transaction,
            TriggersSql,
            command =>
            {
                BindList(command, schemas, filter, exact, cap);
                command.Parameters.Add(new NpgsqlParameter("table", NpgsqlDbType.Text) { Value = table ?? string.Empty });
            },
            static reader => new CatalogTrigger(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetBoolean(7)),
            schemas,
            cap,
            commandTimeoutSeconds,
            cancellationToken);

    /// <summary>Reads sequences in the schema set, at most <paramref name="cap" />.</summary>
    public static Task<CatalogList<CatalogSequence>> ReadSequencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> schemas,
        string? filter,
        string? exact,
        int cap,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default) =>
        ReadListAsync(
            connection,
            transaction,
            SequencesSql,
            command => BindList(command, schemas, filter, exact, cap),
            static reader => new CatalogSequence(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetBoolean(7),
                reader.GetBoolean(8),
                null,
                NullableString(reader, 9),
                NullableString(reader, 10),
                NullableString(reader, 11),
                NullableString(reader, 12)),
            schemas,
            cap,
            commandTimeoutSeconds,
            cancellationToken);

    /// <summary>
    /// Reads one sequence's last value, or <see langword="null" /> when the schema has no such sequence.
    /// </summary>
    /// <remarks>
    /// Takes a <c>RowExclusiveLock</c> on that one sequence until the transaction ends (see
    /// <see cref="SequenceValueSql" />), so the caller runs it in a transaction of its own.
    /// </remarks>
    public static async Task<CatalogSequenceValue?> ReadSequenceValueAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string name,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(schema);
        ArgumentException.ThrowIfNullOrEmpty(name);

        await using NpgsqlCommand command = Command(connection, transaction, SequenceValueSql, commandTimeoutSeconds);
        BindSchemas(command, [schema]);
        command.Parameters.Add(new NpgsqlParameter("exact", NpgsqlDbType.Text) { Value = name });

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CatalogSequenceValue(reader.GetBoolean(0), NullableInt64(reader, 1))
            : null;
    }

    /// <summary>
    /// Leaves <c>pg_catalog</c> alone on the transaction's <c>search_path</c> (see
    /// <see cref="PinSearchPathSql" />). Run first in every catalog transaction.
    /// </summary>
    public static async Task PinSearchPathAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlCommand command = Command(connection, transaction, PinSearchPathSql, commandTimeoutSeconds);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the exact per-kind, per-schema counts of what the lists would show within the schema set.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">The read-only transaction.</param>
    /// <param name="schemas">The schema set.</param>
    /// <param name="hiddenTables">
    /// The hidden document types' tables, as <c>schema.table</c>, in any case: compared lower-cased, the way
    /// the classifier compares them.
    /// </param>
    /// <param name="commandTimeoutSeconds">The client-side timeout.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    public static async Task<IReadOnlyList<CatalogObjectCount>> ReadObjectCountsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> schemas,
        IReadOnlyCollection<string> hiddenTables,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(hiddenTables);

        if (schemas.Count == 0)
        {
            return [];
        }

        await using NpgsqlCommand command = Command(connection, transaction, ObjectCountsSql, commandTimeoutSeconds);
        BindSchemas(command, schemas);
        command.Parameters.Add(new NpgsqlParameter("hidden", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = hiddenTables.Select(static x => x.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray(),
        });

        List<CatalogObjectCount> counts = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            counts.Add(new CatalogObjectCount(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        }

        return counts;
    }

    /// <summary>Reads types in the schema set, at most <paramref name="cap" />.</summary>
    public static Task<CatalogList<CatalogType>> ReadTypesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> schemas,
        string? filter,
        string? exact,
        int cap,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default) =>
        ReadListAsync(
            connection,
            transaction,
            TypesSql,
            command => BindList(command, schemas, filter, exact, cap),
            static reader =>
            {
                string[] names = Strings(reader, 8);
                string[] types = Strings(reader, 9);

                List<CatalogTypeAttribute> attributes = new(names.Length);
                for (int i = 0; i < names.Length; i++)
                {
                    attributes.Add(new CatalogTypeAttribute(names[i], i < types.Length ? types[i] : string.Empty));
                }

                return new CatalogType(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    NullableString(reader, 3),
                    reader.GetBoolean(4),
                    NullableString(reader, 5),
                    Strings(reader, 6),
                    Strings(reader, 7),
                    attributes,
                    NullableString(reader, 10),
                    reader.GetInt32(11),
                    NullableString(reader, 12));
            },
            schemas,
            cap,
            commandTimeoutSeconds,
            cancellationToken);

    /// <summary>
    /// Reads the foreign keys with either end in the schema set - or, when <paramref name="relation" /> is
    /// given, only those with either end at that one relation.
    /// </summary>
    public static Task<CatalogList<CatalogForeignKey>> ReadForeignKeysAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> schemas,
        string? relationSchema,
        string? relation,
        int cap,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default) =>
        ReadListAsync(
            connection,
            transaction,
            ForeignKeysSql,
            command =>
            {
                BindSchemas(command, schemas);
                command.Parameters.Add(new NpgsqlParameter("relation_schema", NpgsqlDbType.Text) { Value = relationSchema ?? string.Empty });
                command.Parameters.Add(new NpgsqlParameter("relation", NpgsqlDbType.Text) { Value = relation ?? string.Empty });
                BindCap(command, cap);
            },
            static reader => new CatalogForeignKey(
                reader.GetString(0),
                reader.GetString(1),
                Strings(reader, 2),
                reader.GetString(3),
                reader.GetString(4),
                Strings(reader, 5),
                reader.GetString(6),
                reader.GetBoolean(7),
                reader.IsDBNull(8) ? "a" : reader.GetString(8),
                reader.IsDBNull(9) ? "a" : reader.GetString(9)),
            schemas,
            cap,
            commandTimeoutSeconds,
            cancellationToken);

    /// <summary>
    /// Reads what the views in the schema set depend on, recursively - or one view's, when
    /// <paramref name="view" /> is given.
    /// </summary>
    public static Task<CatalogList<CatalogViewDependency>> ReadViewDependenciesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> schemas,
        string? view,
        int cap,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default) =>
        ReadListAsync(
            connection,
            transaction,
            ViewDependenciesSql,
            command =>
            {
                BindSchemas(command, schemas);
                command.Parameters.Add(new NpgsqlParameter("exact", NpgsqlDbType.Text) { Value = view ?? string.Empty });
                BindCap(command, cap);
            },
            static reader => new CatalogViewDependency(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetBoolean(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<string[]>(7),
                !reader.IsDBNull(8) && reader.GetBoolean(8)),
            schemas,
            cap,
            commandTimeoutSeconds,
            cancellationToken);

    /// <summary>
    /// Reads the functions, operators, sequences and types the views in the schema set refer to,
    /// recursively - or one view's, when <paramref name="view" /> is given.
    /// </summary>
    public static Task<CatalogList<CatalogViewReference>> ReadViewReferencesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<string> schemas,
        string? view,
        int cap,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default) =>
        ReadListAsync(
            connection,
            transaction,
            ViewReferencesSql,
            command =>
            {
                BindSchemas(command, schemas);
                command.Parameters.Add(new NpgsqlParameter("exact", NpgsqlDbType.Text) { Value = view ?? string.Empty });
                BindCap(command, cap);
            },
            static reader => new CatalogViewReference(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                !reader.IsDBNull(5) && reader.GetBoolean(5),
                reader.GetInt32(6),
                !reader.IsDBNull(7) && reader.GetBoolean(7)),
            schemas,
            cap,
            commandTimeoutSeconds,
            cancellationToken);

    /// <summary>
    /// Reads one relation and everything the detail view shows about it, or <see langword="null" /> when
    /// the schema set has no such relation.
    /// </summary>
    public static async Task<CatalogRelationDetail?> ReadRelationDetailAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string name,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(schema);
        ArgumentException.ThrowIfNullOrEmpty(name);

        string[] schemas = [schema];

        CatalogList<CatalogRelation> found = await ReadRelationsAsync(
                connection, transaction, schemas, null, name, 1, commandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        if (found.Items.Count == 0)
        {
            return null;
        }

        CatalogRelation relation = found.Items[0];

        List<CatalogColumn> columns = await ReadByOidAsync(
                connection,
                transaction,
                ColumnsSql,
                relation.Oid,
                static reader => new CatalogColumn(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetString(2),
                    reader.GetBoolean(3),
                    NullableString(reader, 4),
                    reader.IsDBNull(5) ? string.Empty : reader.GetString(5),
                    reader.IsDBNull(6) ? string.Empty : reader.GetString(6),
                    reader.GetBoolean(7),
                    NullableString(reader, 8)),
                commandTimeoutSeconds,
                cancellationToken)
            .ConfigureAwait(false);

        List<CatalogConstraint> constraints = await ReadByOidAsync(
                connection,
                transaction,
                ConstraintsSql,
                relation.Oid,
                static reader => new CatalogConstraint(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? string.Empty : reader.GetString(2)),
                commandTimeoutSeconds,
                cancellationToken)
            .ConfigureAwait(false);

        List<CatalogIndex> indexes = await ReadByOidAsync(
                connection,
                transaction,
                IndexesSql,
                relation.Oid,
                static reader => new CatalogIndex(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                    reader.GetBoolean(2),
                    reader.GetBoolean(3),
                    reader.GetBoolean(4),
                    reader.GetBoolean(5),
                    reader.GetBoolean(6),
                    reader.IsDBNull(7) ? [] : reader.GetFieldValue<string?[]>(7)),
                commandTimeoutSeconds,
                cancellationToken)
            .ConfigureAwait(false);

        CatalogList<CatalogTrigger> triggers = await ReadTriggersAsync(
                connection, transaction, schemas, null, null, name, MaxPerRelation, commandTimeoutSeconds,
                cancellationToken)
            .ConfigureAwait(false);

        // The keys at either end of this one relation, whichever schema the far end is in: the schema set
        // holds this relation's schema, so every key touching it qualifies, and the gate - not this read -
        // decides which far ends may be named.
        CatalogList<CatalogForeignKey> foreignKeys = await ReadForeignKeysAsync(
                connection, transaction, schemas, schema, name, MaxPerRelation, commandTimeoutSeconds, cancellationToken)
            .ConfigureAwait(false);

        CatalogList<CatalogViewDependency> dependencies = relation.Kind is "v" or "m"
            ? await ReadViewDependenciesAsync(
                    connection, transaction, schemas, name, MaxPerRelation, commandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false)
            : CatalogList<CatalogViewDependency>.Empty;

        CatalogList<CatalogViewReference> references = relation.Kind is "v" or "m"
            ? await ReadViewReferencesAsync(
                    connection, transaction, schemas, name, MaxPerRelation, commandTimeoutSeconds, cancellationToken)
                .ConfigureAwait(false)
            : CatalogList<CatalogViewReference>.Empty;

        return new CatalogRelationDetail(relation, columns, constraints, indexes, triggers.Items, foreignKeys, dependencies)
        {
            References = references,
        };
    }

    /// <summary>How many triggers, keys or dependencies one relation's detail reads before it stops.</summary>
    internal const int MaxPerRelation = 1000;

    /// <summary>Reads a view's query, or <see langword="null" /> when there is no such view in the schema set.</summary>
    public static async Task<CatalogDefinition?> ReadViewDefinitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string name,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = Command(connection, transaction, ViewDefinitionSql, commandTimeoutSeconds);
        BindSchemas(command, [schema]);
        command.Parameters.Add(new NpgsqlParameter("exact", NpgsqlDbType.Text) { Value = name });

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CatalogDefinition(NullableString(reader, 0), null)
            : null;
    }

    /// <summary>Reads one routine overload's definition, or <see langword="null" /> when there is no such overload.</summary>
    public static async Task<CatalogDefinition?> ReadRoutineDefinitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string name,
        string identityArguments,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = Command(connection, transaction, RoutineDefinitionSql, commandTimeoutSeconds);
        BindSchemas(command, [schema]);
        command.Parameters.Add(new NpgsqlParameter("exact", NpgsqlDbType.Text) { Value = name });
        command.Parameters.Add(new NpgsqlParameter("arguments", NpgsqlDbType.Text) { Value = identityArguments });

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CatalogDefinition(NullableString(reader, 1), reader.GetString(0))
            : null;
    }

    /// <summary>Reads one trigger's definition, or <see langword="null" /> when there is no such trigger.</summary>
    public static async Task<CatalogDefinition?> ReadTriggerDefinitionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string schema,
        string table,
        string name,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        await using NpgsqlCommand command = Command(connection, transaction, TriggerDefinitionSql, commandTimeoutSeconds);
        BindSchemas(command, [schema]);
        command.Parameters.Add(new NpgsqlParameter("table", NpgsqlDbType.Text) { Value = table });
        command.Parameters.Add(new NpgsqlParameter("exact", NpgsqlDbType.Text) { Value = name });

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new CatalogDefinition(NullableString(reader, 0), null, NullableString(reader, 1))
            : null;
    }

    private static async Task<CatalogList<T>> ReadListAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        Action<NpgsqlCommand> bind,
        Func<NpgsqlDataReader, T> map,
        IReadOnlyList<string> schemas,
        int cap,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);

        if (schemas.Count == 0)
        {
            // Nothing is in scope, so nothing is asked: an empty text[] would answer the same, one round
            // trip later.
            return CatalogList<T>.Empty;
        }

        await using NpgsqlCommand command = Command(connection, transaction, sql, commandTimeoutSeconds);
        bind(command);

        List<T> items = [];
        bool truncated = false;

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (items.Count >= cap)
            {
                // The statement asked for one more than the cap, only to know there were more.
                truncated = true;
                break;
            }

            items.Add(map(reader));
        }

        return new CatalogList<T>(items, truncated);
    }

    private static async Task<List<T>> ReadByOidAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        uint oid,
        Func<NpgsqlDataReader, T> map,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = Command(connection, transaction, sql, commandTimeoutSeconds);
        command.Parameters.Add(new NpgsqlParameter("oid", NpgsqlDbType.Oid) { Value = oid });

        List<T> items = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            items.Add(map(reader));
        }

        return items;
    }

    private static NpgsqlCommand Command(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        int commandTimeoutSeconds) =>
        new(sql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };

    private static void BindList(NpgsqlCommand command, IReadOnlyList<string> schemas, string? filter, string? exact, int cap)
    {
        BindSchemas(command, schemas);
        command.Parameters.Add(new NpgsqlParameter("q", NpgsqlDbType.Text) { Value = filter ?? string.Empty });
        command.Parameters.Add(new NpgsqlParameter("exact", NpgsqlDbType.Text) { Value = exact ?? string.Empty });
        BindCap(command, cap);
    }

    private static void BindSchemas(NpgsqlCommand command, IReadOnlyList<string> schemas) =>
        command.Parameters.Add(new NpgsqlParameter("schemas", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = schemas as string[] ?? [.. schemas],
        });

    /// <summary>One more than the caller will keep, so the reader can tell "exactly the cap" from "more".</summary>
    private static void BindCap(NpgsqlCommand command, int cap) =>
        command.Parameters.Add(new NpgsqlParameter("cap", NpgsqlDbType.Integer) { Value = checked(cap + 1) });

    private static long? NullableInt64(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static string[] Strings(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? [] : reader.GetFieldValue<string[]>(ordinal);
}
