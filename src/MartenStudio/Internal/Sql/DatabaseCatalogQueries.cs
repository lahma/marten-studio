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
    bool HasPrimaryKey);

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

/// <summary>One sequence, with its value only when the reading role may see it.</summary>
/// <param name="Schema">Its schema.</param>
/// <param name="Name">Its name.</param>
/// <param name="DataType">Its data type.</param>
/// <param name="Start"><c>seqstart</c>.</param>
/// <param name="Increment"><c>seqincrement</c>.</param>
/// <param name="Minimum"><c>seqmin</c>.</param>
/// <param name="Maximum"><c>seqmax</c>.</param>
/// <param name="Cycles"><c>seqcycle</c>.</param>
/// <param name="CanReadValue">Whether the reading role has <c>SELECT</c> or <c>USAGE</c> on it.</param>
/// <param name="LastValue"><c>pg_sequence_last_value</c>, or <see langword="null" /> - never called, or not readable.</param>
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
internal sealed record CatalogViewDependency(
    string ViewSchema,
    string ViewName,
    string Schema,
    string Name,
    string Kind,
    int Depth);

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
    CatalogList<CatalogViewDependency> Dependencies);

/// <summary>A definition, as Postgres reconstructs it.</summary>
/// <param name="Sql">The text, or <see langword="null" /> when there is none to show (an aggregate).</param>
/// <param name="RoutineKind">For a routine, its <c>prokind</c>.</param>
internal sealed record CatalogDefinition(string? Sql, string? RoutineKind);

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
    /// <c>@exact</c> is empty for a list and a name for a lookup; <c>@q</c> is empty for "everything".
    /// Both are typed <c>text</c> parameters, so neither needs a second statement text.
    /// </remarks>
    internal const string RelationsSql =
        """
        select c.oid,
               n.nspname::text,
               c.relname::text,
               c.relkind::text,
               case
                   when c.relkind = 'p' then (
                       select case
                                  when pg_catalog.bool_and(x.reltuples < 0) then null
                                  else pg_catalog.sum(greatest(x.reltuples, 0))::bigint
                              end
                       from pg_catalog.pg_partition_tree(c.oid) pt
                       join pg_catalog.pg_class x on x.oid = pt.relid
                       where pt.isleaf)
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
                   when c.relkind = 'p' then (
                       select coalesce(pg_catalog.sum(x.relpages::bigint), 0)
                       from pg_catalog.pg_partition_tree(c.oid) pt
                       join pg_catalog.pg_class x on x.oid = pt.relid)
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
               exists (select 1 from pg_catalog.pg_index pk where pk.indrelid = c.oid and pk.indisprimary)
        from pg_catalog.pg_class c
        join pg_catalog.pg_namespace n on n.oid = c.relnamespace
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
    /// Sequences, with the last value only where the role may read it and the owning column where there
    /// is one (<c>pg_depend</c> <c>'a'</c> for <c>serial</c>, <c>'i'</c> for an identity column).
    /// </summary>
    /// <remarks>
    /// <c>pg_sequence_last_value</c> raises for a role with neither <c>SELECT</c> nor <c>USAGE</c>, so it is
    /// behind the same <c>has_sequence_privilege</c> test <c>pg_sequences</c> itself uses; the function is
    /// volatile, so the <c>case</c> is not folded away.
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
               case
                   when pg_catalog.has_sequence_privilege(c.oid, 'SELECT,USAGE')
                   then pg_catalog.pg_sequence_last_value(c.oid::pg_catalog.regclass)
               end,
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
    /// What every view and materialized view in the schema set reads, followed through other views.
    /// </summary>
    /// <remarks>
    /// A view's query is its <c>_RETURN</c> rule in <c>pg_rewrite</c>, and the rule's <c>pg_depend</c>
    /// rows name every relation (and column) it touches. Recursing through the rules of the views it
    /// touches is what catches a view over a view over a hidden type's table - a view is refused rows if
    /// anything under it is hidden, not only its first level. The depth is capped at sixteen, which no
    /// real view stack reaches; the rule's dependency on its own view is excluded so the walk starts one
    /// level down. <c>@exact</c> is empty for every view in the set, or one view's name - which is what the
    /// row gate reads, so that the answer for the view being opened never depends on a list's cap.
    /// </remarks>
    internal const string ViewDependenciesSql =
        """
        with recursive deps (view_oid, ref_oid, depth) as (
            select v.oid, d.refobjid, 1
            from pg_catalog.pg_class v
            join pg_catalog.pg_namespace vn on vn.oid = v.relnamespace
            join pg_catalog.pg_rewrite r on r.ev_class = v.oid
            join pg_catalog.pg_depend d
              on d.classid = 'pg_catalog.pg_rewrite'::pg_catalog.regclass
             and d.objid = r.oid
            where v.relkind in ('v', 'm')
              and vn.nspname = any(@schemas)
              and (@exact = '' or v.relname::text = @exact)
              and d.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass
              and d.refobjid <> v.oid
            union
            select deps.view_oid, d.refobjid, deps.depth + 1
            from deps
            join pg_catalog.pg_rewrite r on r.ev_class = deps.ref_oid
            join pg_catalog.pg_depend d
              on d.classid = 'pg_catalog.pg_rewrite'::pg_catalog.regclass
             and d.objid = r.oid
            where d.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass
              and d.refobjid <> deps.ref_oid
              and deps.depth < 16
        )
        select vn.nspname::text,
               v.relname::text,
               n.nspname::text,
               c.relname::text,
               c.relkind::text,
               pg_catalog.min(deps.depth)::int
        from deps
        join pg_catalog.pg_class v on v.oid = deps.view_oid
        join pg_catalog.pg_namespace vn on vn.oid = v.relnamespace
        join pg_catalog.pg_class c on c.oid = deps.ref_oid
        join pg_catalog.pg_namespace n on n.oid = c.relnamespace
        where c.relkind in ('r', 'p', 'v', 'm', 'f')
        group by vn.nspname, v.relname, n.nspname, c.relname, c.relkind
        order by 1, 2, 6, 3, 4
        limit @cap
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

    /// <summary>A trigger's definition, by its table and its name.</summary>
    internal const string TriggerDefinitionSql =
        """
        select pg_catalog.pg_get_triggerdef(t.oid, true)
        from pg_catalog.pg_trigger t
        join pg_catalog.pg_class c on c.oid = t.tgrelid
        join pg_catalog.pg_namespace n on n.oid = c.relnamespace
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
    ];

    /// <summary>Every statement that reads within a schema set - all of them but <see cref="SchemasSql" />.</summary>
    internal static IReadOnlyList<string> ScopedStatements { get; } =
    [
        .. ListStatements, ViewDefinitionSql, RoutineDefinitionSql, TriggerDefinitionSql,
    ];

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
                reader.GetBoolean(13)),
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
                NullableInt64(reader, 9),
                NullableString(reader, 10),
                NullableString(reader, 11),
                NullableString(reader, 12),
                NullableString(reader, 13)),
            schemas,
            cap,
            commandTimeoutSeconds,
            cancellationToken);

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
                reader.GetInt32(5)),
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

        return new CatalogRelationDetail(relation, columns, constraints, indexes, triggers.Items, foreignKeys, dependencies);
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
            ? new CatalogDefinition(NullableString(reader, 0), null)
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
