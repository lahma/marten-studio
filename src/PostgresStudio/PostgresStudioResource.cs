namespace PostgresStudio;

/// <summary>
/// The resource Postgres Studio passes to <c>IAuthorizationService.AuthorizeAsync(user, resource, policy)</c> when
/// evaluating <see cref="PostgresStudioOptions.SourceAuthorizationPolicy" /> and
/// <see cref="PostgresStudioOptions.WriteAuthorizationPolicy" />. A host writes one
/// <c>AuthorizationHandler&lt;TRequirement, PostgresStudioResource&gt;</c> and it answers for the source picker, the page
/// frame and every data call.
/// </summary>
/// <remarks>
/// <para>
/// There is no tenant: a plain PostgreSQL source has none, and a database listed per tenant by a host's
/// <see cref="IPostgresDatabaseSource" /> is a database, named by <paramref name="DatabaseIdentifier" />. A handler maps
/// that name onto the host's own tenant claims.
/// </para>
/// <para>
/// <b>An empty <paramref name="DatabaseIdentifier" /> is not "any database".</b> The studio asks with an empty one only
/// to decide whether to name a source that would not build, or whose databases could not be listed: the source picker, the
/// Overview and, since 1.2, the Stores page. It reads nothing on such an answer: every scope it resolves for a read
/// names its database, and one that names none is refused. A handler must not treat an empty identifier as permission for
/// every database of the source.
/// </para>
/// <para>
/// <b>A handler is always asked the registered names before anything is read.</b> A link's source and database match in
/// any case, so the first question about a link can carry its spelling (<c>ADMIN</c>). Whenever that spelling differs,
/// the studio asks again with <paramref name="SourceName" /> and <paramref name="DatabaseIdentifier" /> exactly as
/// registered and listed, and reads nothing unless that answer is yes too - so a handler that compares ordinally, a
/// deny-list included, decides about the names it knows.
/// </para>
/// </remarks>
/// <param name="SourceName">
/// <c>default</c> for the application's own unkeyed <c>NpgsqlDataSource</c>, or the name given to
/// <see cref="PostgresDiagnosticsOptions.AddDataSource" /> or <see cref="PostgresDiagnosticsOptions.AddDatabaseSource" />.
/// </param>
/// <param name="DatabaseIdentifier">
/// The database's stable name, never a connection string: a data source's registration name, or the name a host's
/// <see cref="IPostgresDatabaseSource" /> lists the database under. Empty only when the studio asks whether to name a
/// source that would not build, or whose databases could not be listed (see the remarks); nothing is read on that answer.
/// </param>
/// <param name="Capability">
/// The name of the capability being exercised (for example <c>RunSql</c>), or <see langword="null" /> for a read.
/// </param>
public sealed record PostgresStudioResource(string SourceName, string DatabaseIdentifier, string? Capability);
