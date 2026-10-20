namespace PostgresStudio;

/// <summary>
/// The resource Postgres Studio passes to <c>IAuthorizationService.AuthorizeAsync(user, resource, policy)</c> when
/// evaluating <see cref="PostgresStudioOptions.SourceAuthorizationPolicy" /> and
/// <see cref="PostgresStudioOptions.WriteAuthorizationPolicy" />. A host writes one
/// <c>AuthorizationHandler&lt;TRequirement, PostgresStudioResource&gt;</c> and it answers for the source picker, the page
/// frame and every data call.
/// </summary>
/// <remarks>
/// There is no tenant: a plain PostgreSQL source has none, and a database listed per tenant by a host's
/// <see cref="IPostgresDatabaseSource" /> is a database, named by <paramref name="DatabaseIdentifier" />. A handler maps
/// that name onto the host's own tenant claims.
/// </remarks>
/// <param name="SourceName">
/// <c>default</c> for the application's own unkeyed <c>NpgsqlDataSource</c>, or the name given to
/// <see cref="PostgresStudioOptions.AddDataSource" /> or <see cref="PostgresStudioOptions.AddDatabaseSource" />.
/// </param>
/// <param name="DatabaseIdentifier">
/// The database's stable name, never a connection string: a data source's registration name, or the name a host's
/// <see cref="IPostgresDatabaseSource" /> lists the database under. Empty when no database is named yet.
/// </param>
/// <param name="Capability">
/// The name of the capability being exercised (for example <c>RunSql</c>), or <see langword="null" /> for a read.
/// </param>
public sealed record PostgresStudioResource(string SourceName, string DatabaseIdentifier, string? Capability);
