using Npgsql;

namespace DataStudio.Postgres;

/// <summary>
/// Options for the Postgres module of Data Studio, configured through <c>AddPostgres(Action&lt;PostgresOptions&gt;)</c>.
/// Validation runs when the options are first read, which <c>MapDataStudio()</c> does at startup.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the studio browses is what the application registered.</b> The unkeyed <c>NpgsqlDataSource</c> in
/// the container is the source named <c>default</c> (<c>IncludeDefaultDataSource</c>); any other is named with
/// <c>AddDataSource</c>, and a set of databases the application lists itself - one per tenant, say - with
/// <c>AddDatabaseSource</c>. There is no connection string anywhere here: the studio reads through the
/// application's own data sources, with their role, TLS and settings, and adds no new place that holds credentials.
/// </para>
/// <para>
/// The members mirror <c>MartenOptions</c> where both modules have the setting, with the same defaults and the
/// same bounds, so a host that knows one knows the other. Where the studio is mounted, what it is called, who gets in and how often a
/// live page polls are the shell's (<c>DataStudioOptions</c>); <see cref="ReadOnly" /> here turns only this module's capabilities off.
/// </para>
/// </remarks>
public sealed class PostgresOptions : PostgresDiagnosticsOptions
{
    /// <summary>
    /// When set, evaluated against a <see cref="PostgresResource" /> (with a <see langword="null" /> capability)
    /// before a source or database is listed, framed or read.
    /// </summary>
    public string? SourceAuthorizationPolicy { get; set; }

    /// <summary>
    /// When set, evaluated against a <see cref="PostgresResource" /> carrying the capability name before every
    /// read authorized like a write. When <see langword="null" />, <see cref="SourceAuthorizationPolicy" /> is used for
    /// those as well.
    /// </summary>
    public string? WriteAuthorizationPolicy { get; set; }

    /// <summary>
    /// This module's master switch: when <see langword="true" /> every capability of the module is off regardless of <see cref="Capabilities" />.
    /// The shell's <c>DataStudioOptions.ReadOnly</c> turns off every module's.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>Which reads authorized like writes are enabled. All off by default.</summary>
    public PostgresCapabilities Capabilities { get; set; } = new();

    /// <summary>Rows per page when a list is first shown. Between 1 and <see cref="MaxPageSize" />.</summary>
    public int DefaultPageSize { get; set; } = 50;

    /// <summary>The largest page size a user may pick. Between 1 and 5000.</summary>
    public int MaxPageSize { get; set; } = 500;

    /// <summary>
    /// The largest cell value - a JSON document, a long text, a byte array - rendered inline; a larger one is read on
    /// demand up to this many bytes, and the SQL console reads the rest. Between 1 byte and 64 MiB.
    /// </summary>
    public int MaxInlineValueBytes { get; set; } = 512 * 1024;

    /// <summary>Row cap for the SQL console; the reader stops after this many rows. Between 1 and 10000.</summary>
    public int MaxSqlConsoleRows { get; set; } = 500;

    /// <summary>
    /// Above this row count, the studio reports the <c>pg_class.reltuples</c> estimate and declines to run
    /// <c>count(*)</c>, including when a user asks for the exact number. Zero means "never count, always estimate". It
    /// cannot be negative.
    /// </summary>
    public long ExactCountThreshold { get; set; } = 100_000;

    /// <summary>
    /// Lists one more of the application's own data sources under <paramref name="name" />, as
    /// <see cref="PostgresDiagnosticsOptions.AddDataSource" /> does, returning these options so a chain written against this
    /// type keeps compiling.
    /// </summary>
    /// <param name="name">The source's key, as <see cref="PostgresDiagnosticsOptions.AddDataSource" /> describes it.</param>
    /// <param name="resolve">Returns the application's own data source, usually from the container.</param>
    /// <returns>These options, so calls can be chained.</returns>
    /// <exception cref="ArgumentException"><paramref name="name" /> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="resolve" /> is null.</exception>
    public new PostgresOptions AddDataSource(string name, Func<IServiceProvider, NpgsqlDataSource> resolve)
    {
        base.AddDataSource(name, resolve);
        return this;
    }

    /// <summary>
    /// Lists a host-supplied set of databases under <paramref name="name" />, as
    /// <see cref="PostgresDiagnosticsOptions.AddDatabaseSource" /> does, returning these options so a chain written against this
    /// type keeps compiling.
    /// </summary>
    /// <param name="name">The source's key, as <see cref="PostgresDiagnosticsOptions.AddDatabaseSource" /> describes it.</param>
    /// <param name="resolve">Returns the application's own source, usually from the container.</param>
    /// <returns>These options, so calls can be chained.</returns>
    /// <exception cref="ArgumentException"><paramref name="name" /> is null, empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="resolve" /> is null.</exception>
    public new PostgresOptions AddDatabaseSource(string name, Func<IServiceProvider, IPostgresDatabaseSource> resolve)
    {
        base.AddDatabaseSource(name, resolve);
        return this;
    }
}
