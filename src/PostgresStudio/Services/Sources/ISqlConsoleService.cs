using PostgresStudio.Services.Query;

namespace PostgresStudio.Services.Sources;

/// <summary>
/// The read-only SQL console: one statement, against the database a scope names, as the visitor's console role.
/// </summary>
/// <remarks>
/// <para>
/// Shared by every studio, because nothing in it is a store's: the gate is <see cref="StudioCapability.RunSql" />, the
/// scope is resolved with the capability named and <em>no tenant</em> (D27's "reach, not scope": nothing narrows a
/// typed statement to a tenant), the statement passes <c>ReadOnlySqlGuard</c> for a nicer refusal, and the real guard
/// is the transaction - <c>ReadOnlySqlSession</c>'s <c>SET TRANSACTION READ ONLY</c>, <c>statement_timeout</c> and
/// <c>SET LOCAL ROLE</c> to the console role (D13). Every run and every refusal is audited and logged (9204, 9205).
/// </para>
/// <para>
/// <b>What an implementation must not do.</b> Open anything but the resolved database's own connection
/// (<see cref="IStudioDatabase.CreateConnection" />); run a statement outside the read-only session; skip the capability
/// or the write policy for any reason; or hand the statement to anything that would compose a tenant or soft-delete
/// predicate around it as if it were scoped - it is not, and saying so is the console's job.
/// </para>
/// </remarks>
internal interface ISqlConsoleService
{
    /// <summary>
    /// Runs one statement in the read-only SQL console.
    /// </summary>
    /// <param name="scope">The scope the console is pointed at; its tenant is ignored, never applied.</param>
    /// <param name="request">The statement and its paging.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <exception cref="StudioCapabilityDeniedException">
    /// <see cref="StudioCapability.RunSql" /> is off, or the studio is read-only. Nothing is sent.
    /// </exception>
    /// <exception cref="StudioNotAuthorizedException">
    /// The write policy refused this scope for <c>RunSql</c>. Nothing is sent.
    /// </exception>
    Task<SqlConsoleResult> RunSqlAsync(StudioScope scope, SqlConsoleRequest request, CancellationToken cancellationToken = default);
}
