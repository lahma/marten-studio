using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Connection;

/// <summary>Compares a connection string a visitor pasted with the one the scope's database is reached by.</summary>
/// <remarks>An interface so the page can be rendered in a component test with a fake behind it (D12).</remarks>
internal interface IConnectionCompareService
{
    /// <summary>
    /// How <paramref name="pasted" /> differs from the connection in effect, and what the advice would be if the application switched to
    /// it. Never throws but for cancellation; a refusal, or text Npgsql cannot read, is a value. The text is parsed and dropped.
    /// </summary>
    /// <param name="scope">The visitor's scope; its tenant is dropped, because a connection belongs to no tenant.</param>
    /// <param name="pasted">What the visitor pasted. Never stored, logged, audited or sent anywhere.</param>
    /// <param name="cancellationToken">Cancels this caller's wait, never the shared read.</param>
    Task<ConnectionCompareView> CompareAsync(StudioScope scope, string? pasted, CancellationToken cancellationToken = default);
}

/// <summary>
/// "Compare a connection string" on Database › Connection (1.1, PASTE): the page's own gate, the shared facts of the connection in
/// effect, and <see cref="PastedConnectionString" /> for the text.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate is the page's</b> (AGENTS.md D29): the source policy asked about the database as a whole with no tenant, before the text
/// is looked at. No new capability, and <b>not audited</b>, as the page and a pasted plan are not: it reads and runs nothing.
/// </para>
/// <para>
/// <b>The pasted text goes nowhere.</b> There is no logger in this class, no audit call and no field that holds it: it is a parameter,
/// read once by <see cref="PastedConnectionString" /> and dropped, and the model that comes back holds redacted records and the
/// names of keywords. <b>Nothing is connected to</b> - the text is parsed, never opened. The only read is the facts of the connection in
/// effect, the same cached read the page itself uses.
/// </para>
/// </remarks>
internal sealed class ConnectionCompareService : IConnectionCompareService
{
    private readonly IStudioSettings settings;
    private readonly StudioAuthorization authorization;
    private readonly StudioSourceResolver resolver;
    private readonly IConnectionFactsSource facts;
    private readonly IServiceProvider services;
    private readonly TimeProvider timeProvider;

    public ConnectionCompareService(
        IStudioSettings settings,
        StudioAuthorization authorization,
        StudioSourceResolver resolver,
        IConnectionFactsSource facts,
        IServiceProvider services,
        TimeProvider? timeProvider = null)
    {
        this.settings = settings;
        this.authorization = authorization;
        this.resolver = resolver;
        this.facts = facts;
        this.services = services;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ConnectionCompareView> CompareAsync(StudioScope scope, string? pasted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        StudioScope asked = scope with { TenantId = null };
        ResolvedSource resolved;

        // 1. The gate, before anybody looks at the text.
        try
        {
            resolved = await resolver.ResolveAsync(asked, null, cancellationToken).ConfigureAwait(false);
        }
        catch (StudioNotAuthorizedException)
        {
            return ConnectionCompareView.Of(ConnectionCompareOutcome.Refused, ConnectionInsightService.StorePolicyDenial(settings));
        }
        catch (Exception exception) when (exception is KeyNotFoundException or StudioStoreUnavailableException)
        {
            return ConnectionCompareView.Of(ConnectionCompareOutcome.Unavailable, exception.Message);
        }

        StudioScope whole = ConnectionInsightService.WholeDatabase(resolved);

        if (!await ConnectionInsightService.StorePolicyPassesAsync(authorization, asked, whole, cancellationToken).ConfigureAwait(false))
        {
            return ConnectionCompareView.Of(ConnectionCompareOutcome.Refused, ConnectionInsightService.StorePolicyDenial(settings));
        }

        // 2. The cheap refusals, which need no read of the server.
        if (string.IsNullOrWhiteSpace(pasted))
        {
            return ConnectionCompareView.Of(ConnectionCompareOutcome.Empty, ConnectionCompareView.EmptySentence);
        }

        if (pasted.Length > PastedConnectionString.MaxCharacters)
        {
            return ConnectionCompareView.Of(ConnectionCompareOutcome.TooLong, ConnectionCompareView.TooLongSentence());
        }

        // 3. The connection in effect: the page's own cached facts, redacted already.
        ConnectionFacts current;

        try
        {
            current = await facts.GetAsync(resolved, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ConnectionCompareView.Of(
                ConnectionCompareOutcome.Unavailable,
                PostgresFailure.IsConnectionFailure(exception)
                    ? PostgresFailure.CouldNotConnect(exception)
                    : "The connection settings could not be read (" + ConnectionInsightService.Describe(exception) + ").");
        }

        // 4. The text: secrets out, then read. Parsed in memory; no connection is opened to anything it names.
        return Compare(pasted, current, services.GetService<IProcessEnvironment>() ?? ProcessEnvironment.Instance, timeProvider.GetUtcNow());
    }

    /// <summary>
    /// The text against the connection in effect, past the gate: the sanitiser, the reader and the comparer. Pure; static so a test can
    /// state both sides outright.
    /// </summary>
    /// <param name="pasted">What the visitor pasted.</param>
    /// <param name="current">The connection in effect.</param>
    /// <param name="environment">The process environment.</param>
    /// <param name="now">When.</param>
    internal static ConnectionCompareView Compare(string? pasted, ConnectionFacts current, IProcessEnvironment environment, DateTimeOffset now)
    {
        PastedConnection read = PastedConnectionString.Read(
            pasted,
            new ConnectionReadContext
            {
                Environment = environment,
                Platform = current.Platform.Kind,
                Observed = current.Observed,
            });

        return read.Outcome switch
        {
            PastedConnectionOutcome.Read => ConnectionComparer.Compare(current, read, now),
            PastedConnectionOutcome.Empty => ConnectionCompareView.Of(ConnectionCompareOutcome.Empty, ConnectionCompareView.EmptySentence),
            PastedConnectionOutcome.TooLong => ConnectionCompareView.Of(ConnectionCompareOutcome.TooLong, ConnectionCompareView.TooLongSentence()),
            PastedConnectionOutcome.Rejected => new ConnectionCompareView
            {
                Outcome = ConnectionCompareOutcome.Rejected,
                Reason = string.Join(' ', read.Rejected.Select(ConnectionCompareView.Sentence)),
                Rejected = read.Rejected,
            },
            _ => ConnectionCompareView.Of(ConnectionCompareOutcome.Unreadable, ConnectionCompareView.UnreadableSentence),
        };
    }
}
