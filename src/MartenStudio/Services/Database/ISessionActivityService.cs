namespace MartenStudio.Services.Database;

/// <summary>
/// What Database › Sessions reads: one database's sessions, locks and prepared transactions, as one visitor may
/// see them.
/// </summary>
/// <remarks>
/// <para>
/// Two members because there are two audiences (decision 4). <see cref="GetSummaryAsync" /> answers anybody the
/// store policy passes for the database as a whole - counts, ages, states, wait classes, lock modes and the
/// Marten label of what is locked - and is a plain read. <see cref="GetDetailsAsync" /> is the
/// <c>ViewDatabaseActivity</c> read: process ids, roles, client addresses, application names, connection times
/// and statements, refused in the service when the capability, <c>ReadOnly</c> or either policy says no, every
/// refusal audited, and the first read per circuit audited by signature.
/// </para>
/// <para>
/// Both read one shared snapshot (<see cref="SessionActivitySnapshots" />), so the page asking for both costs one
/// read of the process array and the lock table, and asking again within ten seconds costs none.
/// </para>
/// </remarks>
internal interface ISessionActivityService
{
    /// <summary>
    /// What anybody the store policy passes for the database as a whole may see. Never throws but for
    /// cancellation; a refusal or a failed read is a value.
    /// </summary>
    /// <param name="scope">The visitor's scope; its tenant is dropped, because a session belongs to no tenant.</param>
    /// <param name="cancellationToken">Cancels this caller's wait.</param>
    Task<SessionActivitySummary> GetSummaryAsync(StudioScope scope, CancellationToken cancellationToken = default);

    /// <summary>
    /// The details, through the <c>ViewDatabaseActivity</c> gate: the capability, then the store policy and the
    /// write policy, both asked with <c>TenantId = null</c>. Never throws but for cancellation; a refusal is a
    /// value, and is audited.
    /// </summary>
    /// <param name="scope">The visitor's scope; its tenant is dropped.</param>
    /// <param name="cancellationToken">Cancels this caller's wait.</param>
    Task<SessionActivityDetails> GetDetailsAsync(StudioScope scope, CancellationToken cancellationToken = default);
}
