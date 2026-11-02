using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// One edge of the walk: a visible object whose definition the planner may put into a plan, and an object that definition uses.
/// </summary>
/// <param name="Root">
/// The visible object's name: a view, a function, an operator's symbol, or a table whose policies or generated columns the walk read.
/// </param>
/// <param name="Reached">The name of the view, function, operator or relation its definition depends on, through <c>pg_depend</c>.</param>
/// <param name="Withheld">Whether what it reaches lives in a schema the visitor may not see.</param>
/// <param name="Expands">
/// Whether the planner would put what it reaches into a plan's text: a view (expanded), a function or an operator (inlined).
/// </param>
internal sealed record CrossingEdge(string Root, string Reached, bool Withheld, bool Expands);

/// <summary>One <c>LANGUAGE sql</c> function with a string body in a visible schema: its name and its body's text.</summary>
/// <param name="Name">The function's name.</param>
/// <param name="Source">Its body, <c>prosrc</c>.</param>
internal sealed record SqlFunctionSource(string Name, string Source);

/// <summary>
/// 1.0-HARD, widened by RVH-FIX: what a generic plan would expand or inline from a schema the visitor may not see through an
/// object in one they may - read from the catalog, by oid, locking nothing (D27).
/// </summary>
/// <remarks>
/// <para>
/// The planner expands a view and inlines a <c>LANGUAGE sql</c> function into the plan of a statement that names it: a visible
/// view that calls a withheld schema's SQL function plans as the function's own condition over a table the visitor may see, so
/// the withheld definition is in the plan with no withheld relation for the plan's own check to find. It does the same for an
/// operator whose function is a SQL function (the expression records the operator, not its function), for a parameter default a
/// call leaves out (recorded against the function), for a row-level-security policy's condition on a table the plan reads, and,
/// from PostgreSQL 18, for a virtual generated column's expression.
/// </para>
/// <para>
/// <see cref="CrossingSql" /> reads, for every such holder in a visible schema, the objects its definition <i>directly</i> depends
/// on (<c>pg_depend</c>, deptype <c>n</c>): a view's are its <c>_RETURN</c> rule's, a function's its own (a SQL-standard body's
/// calls and every parameter default), an operator's its function's, a table's its policies' and virtual generated columns'.
/// Following those edges to a fixed point is the caller's (<see cref="Services.Statements.StatementCrossing" />), where the
/// bound is visible; the read is flat, so each holder's dependencies are found through <c>pg_depend</c>'s index on
/// <c>(classid, objid)</c>, never by scanning it once per row.
/// </para>
/// <para>
/// A function with a string body records no dependency on what its body calls, so <see cref="SqlFunctionSourcesSql" /> reads
/// those bodies for the caller to lex. Both reads are bounded; a read past its bound is reported as truncated, and the caller
/// refuses then, failing closed.
/// </para>
/// </remarks>
internal static class StatementPlanCrossingQueries
{
    /// <summary>The most edges read; past it the caller cannot tell and refuses.</summary>
    internal const int MaxEdges = 50_000;

    /// <summary>The most string-bodied SQL functions read.</summary>
    internal const int MaxFunctions = 5_000;

    /// <summary>What "Show SQL" says of <see cref="CrossingSql" />.</summary>
    internal const string EdgesLabel = "the views, functions, operators and policies that reach a schema you may not see";

    /// <summary>What "Show SQL" says of <see cref="SqlFunctionSourcesSql" />.</summary>
    internal const string FunctionsLabel = "the bodies of SQL functions";

    /// <summary>The direct dependencies. Parameters: <c>@withheld</c> (<c>text[]</c>), <c>@limit</c>.</summary>
    internal const string CrossingSql =
        """
        with withheld_ns as (
            select n.oid from pg_catalog.pg_namespace n where n.nspname = any(@withheld)
        ),
        visible_ns as (
            select n.oid from pg_catalog.pg_namespace n
            where n.nspname <> all(@withheld) and n.nspname not in ('pg_catalog', 'information_schema')
        ),
        holders as (
            select c.relname::text as name, 'pg_catalog.pg_rewrite'::pg_catalog.regclass as classid, rw.oid as objid
            from pg_catalog.pg_class c
            join pg_catalog.pg_rewrite rw on rw.ev_class = c.oid and rw.rulename = '_RETURN'
            where c.relkind = 'v' and c.relnamespace in (select v.oid from visible_ns v)
            union all
            select p.proname::text, 'pg_catalog.pg_proc'::pg_catalog.regclass, p.oid
            from pg_catalog.pg_proc p
            where p.prokind = 'f' and p.pronamespace in (select v.oid from visible_ns v)
            union all
            select o.oprname::text, 'pg_catalog.pg_operator'::pg_catalog.regclass, o.oid
            from pg_catalog.pg_operator o
            where o.oprnamespace in (select v.oid from visible_ns v)
            union all
            select c.relname::text, 'pg_catalog.pg_policy'::pg_catalog.regclass, pol.oid
            from pg_catalog.pg_policy pol
            join pg_catalog.pg_class c on c.oid = pol.polrelid
            where c.relnamespace in (select v.oid from visible_ns v)
            union all
            select c.relname::text, 'pg_catalog.pg_attrdef'::pg_catalog.regclass, ad.oid
            from pg_catalog.pg_attrdef ad
            join pg_catalog.pg_attribute a on a.attrelid = ad.adrelid and a.attnum = ad.adnum
            join pg_catalog.pg_class c on c.oid = ad.adrelid
            where a.attgenerated = 'v' and c.relnamespace in (select v.oid from visible_ns v)
        )
        select distinct
               h.name,
               coalesce(c.relname::text, p.proname::text, o.oprname::text) as reached,
               coalesce(c.relnamespace, p.pronamespace, o.oprnamespace) in (select x.oid from withheld_ns x) as withheld,
               (coalesce(c.relkind = 'v', false) or p.oid is not null or o.oid is not null) as expands
        from holders h
        join pg_catalog.pg_depend d
          on d.classid = h.classid
         and d.objid = h.objid
         and d.deptype = 'n'
         and d.refclassid in ('pg_catalog.pg_class'::pg_catalog.regclass,
                              'pg_catalog.pg_proc'::pg_catalog.regclass,
                              'pg_catalog.pg_operator'::pg_catalog.regclass)
        left join pg_catalog.pg_class c on d.refclassid = 'pg_catalog.pg_class'::pg_catalog.regclass and c.oid = d.refobjid
        left join pg_catalog.pg_proc p on d.refclassid = 'pg_catalog.pg_proc'::pg_catalog.regclass and p.oid = d.refobjid
        left join pg_catalog.pg_operator o on d.refclassid = 'pg_catalog.pg_operator'::pg_catalog.regclass and o.oid = d.refobjid
        where coalesce(c.relname, p.proname, o.oprname) is not null
        limit @limit
        """;

    /// <summary>
    /// Every <c>LANGUAGE sql</c> function with a string body in a visible schema, with its body. Parameters: <c>@withheld</c>
    /// (<c>text[]</c>), <c>@limit</c>.
    /// </summary>
    internal const string SqlFunctionSourcesSql =
        """
        select p.proname::text, p.prosrc
        from pg_catalog.pg_proc p
        join pg_catalog.pg_language l on l.oid = p.prolang
        join pg_catalog.pg_namespace n on n.oid = p.pronamespace
        where l.lanname = 'sql'
          and p.prosqlbody is null
          and n.nspname <> all(@withheld)
          and n.nspname not in ('pg_catalog', 'information_schema')
        limit @limit
        """;

    /// <summary>The walk's edges, and whether it was cut at <see cref="MaxEdges" />.</summary>
    public static async Task<(IReadOnlyList<CrossingEdge> Edges, bool Truncated)> ReadEdgesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlySet<string> withheld,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(withheld);

        await using NpgsqlCommand command = new(CrossingSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(new NpgsqlParameter("withheld", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = withheld.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = MaxEdges + 1 });

        List<CrossingEdge> edges = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            edges.Add(new CrossingEdge(reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        }

        return edges.Count > MaxEdges ? (edges[..MaxEdges], true) : (edges, false);
    }

    /// <summary>The string-bodied SQL functions in visible schemas, and whether the read was cut at <see cref="MaxFunctions" />.</summary>
    public static async Task<(IReadOnlyList<SqlFunctionSource> Functions, bool Truncated)> ReadSqlFunctionSourcesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlySet<string> withheld,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(withheld);

        await using NpgsqlCommand command = new(SqlFunctionSourcesSql, connection, transaction) { CommandTimeout = commandTimeoutSeconds };
        command.Parameters.Add(new NpgsqlParameter("withheld", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = withheld.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("limit", NpgsqlDbType.Integer) { Value = MaxFunctions + 1 });

        List<SqlFunctionSource> functions = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            functions.Add(new SqlFunctionSource(reader.GetString(0), reader.IsDBNull(1) ? string.Empty : reader.GetString(1)));
        }

        return functions.Count > MaxFunctions ? (functions[..MaxFunctions], true) : (functions, false);
    }
}
