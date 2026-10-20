using Npgsql;

namespace MartenStudio.Services.Sources;

/// <summary>Which pool a connection is taken from, where a source distinguishes read replicas from the primary.</summary>
internal enum StudioConnectionUsage
{
    /// <summary>A read: a source with a read pool may answer from a replica.</summary>
    Read,

    /// <summary>The primary - for a statement whose answer must be the primary's (pool and lock observation, a health counter).</summary>
    ReadWrite,
}

/// <summary>How many databases a source spans, which decides whether a database picker is drawn and whether it needs a refresh.</summary>
internal enum StudioDatabaseCardinality
{
    /// <summary>One database, whatever the tenancy.</summary>
    Single,

    /// <summary>A fixed set of databases known from configuration.</summary>
    StaticMultiple,

    /// <summary>A set that can change while the process runs, so the listing is a snapshot with a refresh beside it.</summary>
    DynamicMultiple,
}

/// <summary>
/// One PostgreSQL database a source can reach: its stable identity, and connections to it.
/// </summary>
/// <remarks>
/// <para>
/// This is everything the shared studio needs of a database, and deliberately nothing more: every shared read is
/// <see cref="CreateConnection" /> plus the studio's own SQL (D6), and the pool checks read <see cref="DataSource" />.
/// MartenStudio implements it over <c>IMartenDatabase</c>; a plain data source implements it over the host's own
/// <see cref="NpgsqlDataSource" />, and a host-supplied database provider (issue #8, G11) over whatever it enumerates.
/// </para>
/// <para>
/// <b>What it must not offer.</b> Nothing here may build schema objects, run a migration or open a session: no
/// <c>AllSchemaNames()</c>, no storage-ensuring call, no Marten session of any kind (AGENTS.md hard rules 14 and 15). A
/// shared caller that needs a Marten session is not shared code. A connection from here is opened and disposed by its
/// caller, used only for the studio's own parameterised SQL, and never handed to anything that would cache it.
/// </para>
/// <para>
/// <b>Identity is a source-defined stable identity.</b> It is what the URL carries in <c>?db=</c>, what a policy is
/// shown as the resource's database, and what every cache, the sweep history and the audit log key on - so it must be
/// the same string in every process and across restarts for the same database of the same source. Each source defines
/// it, and no shape is normative:
/// </para>
/// <list type="bullet">
/// <item><description>a Marten store: the database's <c>Id.Identity</c> (JasperFx's <c>DatabaseId</c>), unchanged from
/// every earlier release;</description></item>
/// <item><description>a DI-registered <see cref="NpgsqlDataSource" />: its registration name;</description></item>
/// <item><description>a host-supplied provider (issue #8, G11): the name the host lists the database under.</description></item>
/// </list>
/// <para>
/// It is compared ignoring case, it is never a connection string and it never carries a credential. Two instances may
/// share one identity (a Marten master-table tenancy's twins); the first is the one a scope without a tenant resolves to.
/// </para>
/// </remarks>
internal interface IStudioDatabase
{
    /// <summary>The database's source-defined stable identity (see the remarks). Compared ignoring case.</summary>
    string Identity { get; }

    /// <summary>The PostgreSQL database name.</summary>
    string Name { get; }

    /// <summary>The server, as the connection string names it (Npgsql's comma list for a multi-host string).</summary>
    string Server { get; }

    /// <summary>
    /// The pool every connection of this database comes from, for the pool observations (C01's saturation, the
    /// Sessions page's pool settings) - or <see langword="null" /> when the source does not expose one. Never disposed
    /// by the studio: the application owns it.
    /// </summary>
    NpgsqlDataSource? DataSource { get; }

    /// <summary>
    /// A new, unopened connection to this database. The caller opens it, owns it and disposes it.
    /// </summary>
    /// <param name="usage">Which pool, where the source tells them apart.</param>
    NpgsqlConnection CreateConnection(StudioConnectionUsage usage);
}
