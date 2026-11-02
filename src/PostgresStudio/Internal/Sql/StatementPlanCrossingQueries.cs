using Npgsql;

using NpgsqlTypes;

namespace PostgresStudio.Internal.Sql;

/// <summary>One edge of the walk: a view or SQL-standard-body function in a visible schema, and an object it reaches.</summary>
/// <param name="Root">The visible view's or function's name.</param>
/// <param name="Reached">The name of a view, function or relation it reaches, through <c>pg_depend</c>.</param>
/// <param name="Withheld">Whether what it reaches lives in a schema the visitor may not see.</param>
/// <param name="Expands">Whether the planner would put what it reaches into a plan's text: a view (expanded) or a function.</param>
internal sealed record CrossingEdge(string Root, string Reached, bool Withheld, bool Expands);

/// <summary>One <c>LANGUAGE sql</c> function with a string body in a visible schema: its name and its body's text.</summary>
/// <param name="Name">The function's name.</param>
/// <param name="Source">Its body, <c>prosrc</c>.</param>
internal sealed record SqlFunctionSource(string Name, string Source);

/// <summary>
/// 1.0-HARD (the Statements drill-in's 0.6 known limit): what a generic plan would expand or inline from a schema the visitor may
/// not see through an object in one they may - read from the catalog, by oid, locking nothing (D27).
/// </summary>
/// <remarks>
/// <para>
/// The planner expands a view and inlines a <c>LANGUAGE sql</c> function into the plan of a statement that names it: a visible
/// view that calls a withheld schema's SQL function plans as the function's own condition over a table the visitor may see, so
/// the withheld definition is in the plan with no withheld relation for the plan's own check to find. The walk finds every view
/// and SQL-standard-body function in a visible schema that reaches a withheld schema's view or function: a view's dependencies are
/// its <c>_RETURN</c> rule's (<c>pg_rewrite</c>), a SQL-standard body's are its own (<c>pg_depend</c>, deptype <c>n</c>), and the
/// walk goes on through the visible views and functions it reaches, at most <see cref="MaxDepth" /> deep. A materialized view is a
/// relation the plan scans and is never expanded, so the walk does not go through one.
/// </para>
/// <para>
/// A function with a string body records no dependency on what its body calls, so <see cref="SqlFunctionSourcesSql" /> reads
/// those bodies for the caller to lex. Both reads are bounded; a read past its bound is reported as truncated, and the caller
/// refuses then, failing closed.
/// </para>
/// </remarks>
internal static class StatementPlanCrossingQueries
{
    /// <summary>How deep the walk goes through views and functions.</summary>
    internal const int MaxDepth = 10;

    /// <summary>The most edges read; past it the caller cannot tell and refuses.</summary>
    internal const int MaxEdges = 50_000;

    /// <summary>The most string-bodied SQL functions read.</summary>
    internal const int MaxFunctions = 5_000;

    /// <summary>The walk. Parameters: <c>@withheld</c> (<c>text[]</c>), <c>@limit</c>.</summary>
    internal const string CrossingSql =
        """
        with recursive withheld_ns as (
            select n.oid from pg_catalog.pg_namespace n where n.nspname = any(@withheld)
        ),
        roots as (
            select 'pg_catalog.pg_class'::pg_catalog.regclass as cls, c.oid as obj, c.relname::text as name
            from pg_catalog.pg_class c
            where c.relkind = 'v'
              and c.relnamespace not in (select w.oid from withheld_ns w)
              and c.relnamespace not in ('pg_catalog'::pg_catalog.regnamespace, 'information_schema'::pg_catalog.regnamespace)
            union all
            select 'pg_catalog.pg_proc'::pg_catalog.regclass, p.oid, p.proname::text
            from pg_catalog.pg_proc p
            where p.prosqlbody is not null
              and p.pronamespace not in (select w.oid from withheld_ns w)
              and p.pronamespace not in ('pg_catalog'::pg_catalog.regnamespace, 'information_schema'::pg_catalog.regnamespace)
        ),
        walk (name, cls, obj, depth) as (
            select r.name, r.cls, r.obj, 0 from roots r
            union
            select w.name, d.refclassid, d.refobjid, w.depth + 1
            from walk w
            join pg_catalog.pg_depend d
              on d.deptype = 'n'
             and d.refclassid in ('pg_catalog.pg_class'::pg_catalog.regclass, 'pg_catalog.pg_proc'::pg_catalog.regclass)
             and ((w.cls = 'pg_catalog.pg_class'::pg_catalog.regclass
                   and d.classid = 'pg_catalog.pg_rewrite'::pg_catalog.regclass
                   and d.objid in (select rw.oid
                                   from pg_catalog.pg_rewrite rw
                                   join pg_catalog.pg_class vc on vc.oid = rw.ev_class
                                   where rw.ev_class = w.obj and vc.relkind = 'v'))
               or (w.cls = 'pg_catalog.pg_proc'::pg_catalog.regclass
                   and d.classid = 'pg_catalog.pg_proc'::pg_catalog.regclass
                   and d.objid = w.obj))
            where w.depth < @depth
        )
        select distinct
               w.name,
               coalesce(c.relname::text, p.proname::text) as reached,
               coalesce(c.relnamespace, p.pronamespace) in (select x.oid from withheld_ns x) as withheld,
               coalesce(c.relkind = 'v', p.oid is not null) as expands
        from walk w
        left join pg_catalog.pg_class c on w.cls = 'pg_catalog.pg_class'::pg_catalog.regclass and c.oid = w.obj
        left join pg_catalog.pg_proc p on w.cls = 'pg_catalog.pg_proc'::pg_catalog.regclass and p.oid = w.obj
        where w.depth > 0
          and coalesce(c.relname, p.proname) is not null
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
        command.Parameters.Add(new NpgsqlParameter("depth", NpgsqlDbType.Integer) { Value = MaxDepth });
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
