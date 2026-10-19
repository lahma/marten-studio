using System.Globalization;

using Npgsql;

namespace MartenStudio.Sample.Generation;

/// <summary>The four "make it unhealthy" switches on the sample's landing page.</summary>
internal enum UnhealthySwitchKind
{
    /// <summary>A transaction that drew an event sequence number and holds it.</summary>
    PinnedGap,

    /// <summary>A session idle inside an open transaction.</summary>
    IdleInTransaction,

    /// <summary>A demo table full of dead rows, with autovacuum off for it.</summary>
    BloatedTable,

    /// <summary>Most of the store's own connection pool, held open and idle.</summary>
    PoolHog,
}

/// <summary>
/// A switch's clean-up failed: whatever it held may still be held. Never swallowed as "turned off" - the board shows the
/// switch as Failed, with this message, and logs it at Warning.
/// </summary>
internal sealed class UnhealthySwitchCleanUpException : Exception
{
    /// <summary>A clean-up failure.</summary>
    public UnhealthySwitchCleanUpException()
    {
    }

    /// <summary>A clean-up failure, in words.</summary>
    public UnhealthySwitchCleanUpException(string message)
        : base(message)
    {
    }

    /// <summary>A clean-up failure, in words, and what caused it.</summary>
    public UnhealthySwitchCleanUpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Which database the switches would act on - the throwaway container the sample starts when nothing is configured
/// (AGENTS.md D18), or one somebody configured - and so which switches may run there.
/// </summary>
/// <remarks>
/// The pinned gap stalls every async projection of the store, and the pool hog takes most of a server's connections;
/// on a configured database, which may be shared or may be somebody's real one, both are refused unless the host was
/// started with <c>--allow-data-generation</c>, which says that this database is a demo's to break. In the service
/// layer, by <see cref="UnhealthySwitchBoard.TryTurnOn" />; the panel only says so.
/// </remarks>
/// <param name="IsThrowaway">Whether the database is the container <c>EphemeralPostgres</c> started.</param>
/// <param name="AllowedExplicitly">Whether the host was started with <c>--allow-data-generation</c>.</param>
internal sealed record UnhealthySwitchesDatabase(bool IsThrowaway, bool AllowedExplicitly)
{
    /// <summary>Why <paramref name="switch" /> is refused here, or <see langword="null" /> when it may run.</summary>
    public string? RefusalFor(UnhealthySwitch @switch)
    {
        ArgumentNullException.ThrowIfNull(@switch);

        return !@switch.OnlyOnTheThrowawayDatabase || IsThrowaway || AllowedExplicitly
            ? null
            : @switch.Title + " is refused here: this host's database was configured (ConnectionStrings:Marten) rather than "
              + "started by the sample as a throwaway container, so it may be shared. Start the host with "
              + "--allow-data-generation to allow it.";
    }

    /// <summary>What the panel says about the database.</summary>
    public string Description => IsThrowaway
        ? "This host's database is the throwaway container the sample started, because nothing configured one."
        : "This host's database was configured (ConnectionStrings:Marten); it is not the sample's throwaway container."
          + (AllowedExplicitly ? " The host was started with --allow-data-generation, so every switch may run." : string.Empty);
}

/// <summary>Where a switch is in its life.</summary>
internal enum UnhealthySwitchState
{
    /// <summary>Off: nothing is held.</summary>
    Off,

    /// <summary>Turned on, still setting the condition up.</summary>
    Starting,

    /// <summary>Holding the condition.</summary>
    On,

    /// <summary>Asked to stop, and cleaning up.</summary>
    Stopping,

    /// <summary>Its last run failed; nothing is held.</summary>
    Failed,
}

/// <summary>What the panel and the status endpoint say about one switch.</summary>
/// <param name="Kind">Which switch.</param>
/// <param name="State">Where it is.</param>
/// <param name="Detail">What it is doing now, in a sentence, while it runs.</param>
/// <param name="StartedAt">When it was last turned on.</param>
/// <param name="TurnsOffAt">When it turns itself off, while it runs.</param>
/// <param name="LastOutcome">How its last run ended: by hand, on its timeout, with the host, or a failure.</param>
internal sealed record UnhealthySwitchStatus(
    UnhealthySwitchKind Kind,
    UnhealthySwitchState State,
    string? Detail,
    DateTimeOffset? StartedAt,
    DateTimeOffset? TurnsOffAt,
    string? LastOutcome)
{
    /// <summary>Whether anything is held, or about to be.</summary>
    public bool IsRunning => State is UnhealthySwitchState.Starting or UnhealthySwitchState.On or UnhealthySwitchState.Stopping;

    /// <summary>A switch that has never been turned on.</summary>
    public static UnhealthySwitchStatus Initial(UnhealthySwitchKind kind) => new(kind, UnhealthySwitchState.Off, null, null, null, null);
}

/// <summary>
/// One "make it unhealthy" switch: a condition it puts the store's database in, held until it is cancelled, and
/// cleaned up after itself whatever ended it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The contract.</b> <see cref="RunAsync" /> sets the condition up on a connection of its own, reports what it is
/// holding, and then waits for its token. When the token fires - the switch turned off, its timeout, or the host
/// stopping - it undoes everything in a <c>finally</c>, on a bounded token of its own rather than the cancelled one:
/// the transaction rolled back, the connections closed, autovacuum restored. A switch that fails half-way through its
/// set-up undoes what it got to, the same way. Nothing is ever left for somebody to find the next morning.
/// </para>
/// <para>
/// <b>Why these four.</b> Each is a condition the studio's health advisor exists to explain, made on purpose, so a demo
/// or a screenshot can start from a healthy store and show the advisor finding something real. None of them writes to a
/// document the demo shows or to a table Marten owns the rows of: the pinned gap appends one marker event of its own,
/// and the bloated table is a table of the sample's own.
/// </para>
/// </remarks>
internal abstract class UnhealthySwitch
{
    /// <summary>How long a clean-up may take before it is given up on, logged and left to the connection's close.</summary>
    protected static readonly TimeSpan CleanUpTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Which switch this is.</summary>
    public abstract UnhealthySwitchKind Kind { get; }

    /// <summary>Its name in a URL: <c>pinned-gap</c>.</summary>
    public abstract string Slug { get; }

    /// <summary>Its name on the panel.</summary>
    public abstract string Title { get; }

    /// <summary>What it does, in a sentence or two, for the panel.</summary>
    public abstract string Description { get; }

    /// <summary>The health check codes it makes fire.</summary>
    public abstract IReadOnlyList<string> Codes { get; }

    /// <summary>
    /// Whether it may run only on the sample's throwaway database unless the host was started with
    /// <c>--allow-data-generation</c> (<see cref="UnhealthySwitchesDatabase" />): a switch whose effect reaches beyond the
    /// demo's own rows - every projection stalled, a server's connections used up.
    /// </summary>
    public virtual bool OnlyOnTheThrowawayDatabase => false;

    /// <summary>Where to look, and what else shows up, in a sentence for the panel.</summary>
    public abstract string WhereToLook { get; }

    /// <summary>
    /// Sets the condition up, reports it through <paramref name="report" />, holds it until
    /// <paramref name="cancellationToken" /> fires, and then undoes it.
    /// </summary>
    /// <param name="report">Says what the switch is doing; the first call is what turns the panel's state to On.</param>
    /// <param name="cancellationToken">Fires when the switch is turned off, times out or the host stops.</param>
    public abstract Task RunAsync(Action<string> report, CancellationToken cancellationToken);

    /// <summary>
    /// Waits until <paramref name="cancellationToken" /> fires, and returns rather than throws: being turned off is how
    /// every switch is meant to end.
    /// </summary>
    protected static async Task UntilCancelledAsync(CancellationToken cancellationToken)
    {
        // RunContinuationsAsynchronously, so the clean-up never runs inline on the thread that cancelled - which is the
        // request turning the switch off, or the host's shutdown.
        TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        await using (cancellationToken.Register(static state => ((TaskCompletionSource) state!).TrySetResult(), cancelled))
        {
            await cancelled.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Names the session after the switch, so Database › Sessions and <c>pg_stat_activity</c> say who it is.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="transaction">
    /// The transaction to name it inside, which makes the name last only as long as the transaction: the connection goes
    /// back to the pool under its own name. <see langword="null" /> names the session itself.
    /// </param>
    /// <param name="cancellationToken">Cancels the statement.</param>
    protected async Task NameSessionAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using NpgsqlCommand command = new("select pg_catalog.set_config('application_name', @name, @local)", connection, transaction);
        command.Parameters.AddWithValue("name", ApplicationName);
        command.Parameters.AddWithValue("local", transaction is not null);

        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The application name the switch's sessions carry: <c>marten-studio-sample: pinned gap</c>.</summary>
    public string ApplicationName => ApplicationNamePrefix + Slug.Replace('-', ' ');

    /// <summary>What every switch's application name starts with.</summary>
    public const string ApplicationNamePrefix = "marten-studio-sample: ";

    /// <summary>
    /// Runs a clean-up on a bounded token of its own - it must run although the switch's own token has fired - and turns
    /// its failure into an <see cref="UnhealthySwitchCleanUpException" />, so the board reports a switch that could not
    /// undo itself as Failed rather than as turned off.
    /// </summary>
    /// <param name="cleanUp">The clean-up, given its bounded token.</param>
    protected async Task CleanUpAsync(Func<CancellationToken, Task> cleanUp)
    {
        ArgumentNullException.ThrowIfNull(cleanUp);

        using CancellationTokenSource bounded = new(CleanUpTimeout);

        try
        {
            await cleanUp(bounded.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not UnhealthySwitchCleanUpException)
        {
            throw new UnhealthySwitchCleanUpException(Title + " could not clean up after itself: " + exception.Message, exception);
        }
    }

    /// <summary>A number for a sentence.</summary>
    protected static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
