namespace PostgresStudio;

/// <summary>
/// What the studio finds in this application's PostgreSQL databases, for the application's own code: the findings with
/// their stable codes, and the measurements behind them as typed sections. Registered by <c>AddPostgresStudio()</c> and by
/// <c>AddMartenStudio()</c>; resolve it from the container.
/// </summary>
/// <remarks>
/// <para>
/// <b>Host code, not a visitor.</b> No authorization policy, capability or <c>ReadOnly</c> is asked: they decide what the
/// studio's pages show to the people who open them, and this is your own code calling. It reads nothing your code could not
/// read through the same data source, with the same roles. A result carries names - tables, indexes, settings - and with
/// <see cref="PostgresDiagnosticsRequest.IncludeActivity" /> statement texts and application names too. The studio never
/// puts a result into an HTTP response, a log line above Debug, a metric or its own Activity screen. Where it goes is your
/// decision: treat it as you would the output of <c>pg_stat_activity</c>.
/// </para>
/// <para>
/// <b>Cheap to call often.</b> A call shares the studio's own single-flight caches: the findings are at most one
/// <c>HealthInterval</c> old, and the deeper and statement checks at most five minutes. A database nobody asks about is
/// never read. Fresh reads started by calls - a sweep or a section read the caches do not hold - run at most two at a time
/// in the process, so a loop over hundreds of databases costs two connections at a time, never hundreds.
/// </para>
/// <para>
/// New members are added only as default interface members, so an implementation in a host's tests keeps compiling.
/// </para>
/// </remarks>
public interface IPostgresDiagnostics
{
    /// <summary>
    /// Every check this studio runs, with no outcome: the code vocabulary, for a host's own test that it has wording for
    /// every code and variant.
    /// </summary>
    IReadOnlyList<PostgresCheck> Checks { get; }

    /// <summary>
    /// The databases the studio would list, in the order it lists them: each data source and database source in
    /// registration order (the unkeyed data source first), and for Marten Studio each store's databases its tenancy already
    /// holds. Nothing is created to list them, no data source is built, and a source that cannot be listed is left out and
    /// logged.
    /// </summary>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <returns>The databases, each as a <see cref="PostgresDatabaseRef" /> that <see cref="GetSnapshotAsync" /> takes.</returns>
    IAsyncEnumerable<PostgresDatabaseRef> ListDatabasesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// One database's snapshot: the sections <paramref name="request" /> asks for, each as fresh as the studio's cache for it.
    /// Never throws for a database that is unknown, unreachable or failing: those are answered with
    /// <see cref="PostgresDiagnosticsSnapshot.Outcome" />. It throws only for a null argument, for cancellation, and for
    /// studio options that fail validation.
    /// </summary>
    /// <param name="database">
    /// A database from <see cref="ListDatabasesAsync" />, or one built from its source's name and the source's own name for it.
    /// </param>
    /// <param name="request">Which sections, and whether Activity-grade data is included.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never a read other callers share.</param>
    /// <returns>The snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database" /> or <paramref name="request" /> is null.</exception>
    Task<PostgresDiagnosticsSnapshot> GetSnapshotAsync(
        PostgresDatabaseRef database,
        PostgresDiagnosticsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The path of <paramref name="page" /> for <paramref name="database" />, relative to the studio's mount path with no
    /// leading slash and with the database's scope in its query (<see cref="PostgresStudioLink" />). Reads nothing.
    /// </summary>
    /// <param name="database">The database.</param>
    /// <param name="page">The page.</param>
    /// <returns>The path; the default implementation, for a host's own test double, returns an empty string.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="database" /> is null.</exception>
    string GetStudioPath(PostgresDatabaseRef database, PostgresStudioPage page) => "";
}
