using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Database;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// The sweep's one read of the lock table (X3, H3's half): every check that needs <c>pg_locks</c> takes it from here.
/// </summary>
/// <remarks>
/// <para>
/// <b>Once per sweep.</b> The read is <see cref="HealthConnectionQueries.LockTable" />, through the sweep's memo
/// (<see cref="HealthCheckContext.Shared{T}" />) under <see cref="Key" />, so whichever check asks first runs it and every
/// other check of the sweep gets the same answer: the table checks' strong and <c>ACCESS EXCLUSIVE</c> locks, C04's relation
/// locks, C04's and T14's daemon-lock holders, and the blocking graph C06 and C12 judge. Its statement is in the "Show SQL"
/// of every check that asked for it. A regular sweep read <c>pg_locks</c> seven times in five statements before 0.6
/// (<c>LockTableReadLiveTests</c> measured it), and reads it once now.
/// </para>
/// <para>
/// <b>One more read, on one path only.</b> The index read's deparse guard needs the lock table as it is when it deparses: a
/// migration that takes <c>ACCESS EXCLUSIVE</c> after this read makes the deparse wait out <c>lock_timeout</c>, and the index
/// read then reads the lock table once more, with <see cref="ReadAsync" />, before it asks again - as its in-statement
/// snapshot did before 0.6.
/// </para>
/// </remarks>
internal static class LockTableReads
{
    /// <summary>The memo key of the sweep's read.</summary>
    public const string Key = "connections.locks";

    /// <summary>The sweep's read of the lock table, read once whichever check asks first.</summary>
    public static Task<LockTableRead> SharedAsync(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Shared(Key, token => ReadAsync(context, token));
    }

    /// <summary>A read of the lock table of its own, past the sweep's memo - for the index read's second try alone.</summary>
    public static async Task<LockTableRead> ReadAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<LockTableRow> rows = await context.Sql
            .QueryAsync(HealthConnectionQueries.LockTable(), LockTableRead.Map, cancellationToken)
            .ConfigureAwait(false);

        return LockTableRead.Fold(rows, context.Now);
    }
}
