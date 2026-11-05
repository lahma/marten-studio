using Microsoft.Extensions.Logging;

namespace PostgresStudio.Services.Find;

/// <summary>
/// Runs Find (1.2): what the header's box does with a typed value - every contributor whose kind the value fits, one after another,
/// each through the data service of the page its hit opens.
/// </summary>
/// <remarks>
/// <para>
/// <b>Bounds</b> (D-SCOPE F4). One kind at a time, because each is a connection of the visitor's share. One Find per circuit at a
/// time: a new one cancels the one in flight, and the cancelled caller sees an <see cref="OperationCanceledException" />. Ten seconds
/// for the whole of it, on the injected clock; a kind the budget did not reach, or that was cut off by it, is a note saying so. At
/// most <see cref="MaxHits" /> hits are kept.
/// </para>
/// <para>
/// <b>Nothing here reads, gates or audits.</b> The service asks the store policy once, for the scope itself, so a scope the visitor
/// may not have answers exactly as a scope with nothing in it; everything else is the contributors' and the pages' own. Find audits
/// nothing itself. The value reaches a log only through <see cref="StudioLogValue.Cap" />, at Debug, on a failure.
/// </para>
/// </remarks>
internal sealed class FindService : IDisposable
{
    /// <summary>The most hits one Find keeps.</summary>
    public const int MaxHits = 20;

    /// <summary>How long a whole Find may take.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyList<IFindContributor> contributors;
    private readonly StudioAuthorization authorization;
    private readonly ILogger<FindService> logger;
    private readonly TimeProvider time;
    private readonly Lock gate = new();
    private CancellationTokenSource? inFlight;

    public FindService(
        IEnumerable<IFindContributor> contributors,
        StudioAuthorization authorization,
        ILogger<FindService> logger,
        TimeProvider? time = null)
    {
        this.contributors = [.. contributors.OrderBy(static x => x.Order)];
        this.authorization = authorization;
        this.logger = logger;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Whether any kind is registered, which is whether the header draws the box.</summary>
    public bool IsAvailable => contributors.Count > 0;

    /// <summary>Searches <paramref name="scope" /> for what was typed.</summary>
    /// <param name="scope">The store, database and tenant to search.</param>
    /// <param name="typed">The box's text.</param>
    /// <param name="cancellationToken">Cancels the Find; a newer Find cancels it too.</param>
    /// <exception cref="OperationCanceledException">The Find was cancelled by the caller or by a newer Find.</exception>
    public Task<FindResult> FindAsync(StudioScope scope, string? typed, CancellationToken cancellationToken = default) =>
        FindAsync(scope, typed, elsewhere: false, cancellationToken);

    /// <summary>Searches <paramref name="scope" /> for what was typed.</summary>
    /// <param name="scope">The store, database and tenant to search.</param>
    /// <param name="typed">The box's text.</param>
    /// <param name="elsewhere">
    /// Whether <paramref name="scope" /> is not the scope on screen (the box's "Search in"): the kinds whose page does not apply its own
    /// address's scope (<see cref="IFindContributor.SearchesOtherScopes" />) are left out, and say nothing.
    /// </param>
    /// <param name="cancellationToken">Cancels the Find; a newer Find cancels it too.</param>
    /// <exception cref="OperationCanceledException">The Find was cancelled by the caller or by a newer Find.</exception>
    public async Task<FindResult> FindAsync(StudioScope scope, string? typed, bool elsewhere, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        FindQuery query = FindQuery.Parse(typed);
        if (query.IsEmpty || query.Refusal is not null)
        {
            return FindResult.Nothing(query.Value, scope, query.Refusal);
        }

        using CancellationTokenSource mine = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (gate)
        {
            previous = inFlight;
            inFlight = mine;
        }

        Cancel(previous);

        try
        {
            return await RunAsync(scope, query, elsewhere, mine.Token).ConfigureAwait(false);
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(inFlight, mine))
                {
                    inFlight = null;
                }
            }
        }
    }

    /// <summary>Cancels the Find in flight, if there is one: the box going away.</summary>
    public void Cancel()
    {
        CancellationTokenSource? current;
        lock (gate)
        {
            current = inFlight;
            inFlight = null;
        }

        Cancel(current);
    }

    /// <inheritdoc />
    public void Dispose() => Cancel();

    private static void Cancel(CancellationTokenSource? source)
    {
        try
        {
            source?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It finished between the swap and the cancel.
        }
    }

    private async Task<FindResult> RunAsync(StudioScope scope, FindQuery query, bool elsewhere, CancellationToken cancellationToken)
    {
        // A scope the store policy refuses answers as a scope with nothing in it (F6): no phrase, no note.
        if (!await authorization.IsAuthorizedAsync(scope, capability: null, cancellationToken).ConfigureAwait(false))
        {
            return FindResult.Nothing(query.Value, scope);
        }

        using CancellationTokenSource budget = new(Budget, time);
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);

        List<FindHit> hits = [];
        List<string> searched = [];
        List<string> notes = [];
        int omitted = 0;
        bool narrowed = false;

        foreach (IFindContributor contributor in contributors)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (elsewhere && !contributor.SearchesOtherScopes)
            {
                continue;
            }

            if (budget.IsCancellationRequested)
            {
                notes.Add($"{contributor.Noun}: not searched, out of time.");
                continue;
            }

            FindContribution contribution;
            try
            {
                contribution = await contributor.FindAsync(scope, query, bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                notes.Add($"{contributor.Noun}: not searched, out of time.");
                continue;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The contract is that a contributor never throws; one that does is a note, and what it said stays out of the page.
                if (logger.IsEnabled(LogLevel.Debug))
                {
                    logger.LogDebug(exception, "Find could not search {Noun} for '{Value}'", contributor.Noun, StudioLogValue.Cap(query.Value));
                }

                notes.Add($"{contributor.Noun}: could not be searched.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(contribution.Searched))
            {
                searched.Add(contribution.Searched);
            }

            notes.AddRange(contribution.Notes);
            narrowed |= contribution.TenantsNarrowed;

            foreach (FindHit hit in contribution.Hits)
            {
                if (hits.Count < MaxHits)
                {
                    hits.Add(hit);
                }
                else
                {
                    omitted++;
                }
            }
        }

        return new FindResult(query.Value, scope, hits, omitted, [.. searched.Distinct(StringComparer.Ordinal)], [.. notes.Distinct(StringComparer.Ordinal)], null, narrowed);
    }
}
