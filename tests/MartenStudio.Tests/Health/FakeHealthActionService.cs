using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// What the Health page's finding cards are told about actions, said outright by a test (1.2-H-C).
/// </summary>
/// <remarks>
/// The real <c>HealthActionService</c> resolves a scope and asks the gates; a page test is about what the card draws from an
/// offer and a result, so both are handed over as they are. By default nothing is offered. It records every press, and can hold
/// a press open on a <see cref="TaskCompletionSource{T}" /> so a test sees the page while it is running.
/// </remarks>
internal sealed class FakeHealthActionService : IHealthActionService
{
    private readonly List<(StudioScope Scope, string MuteKey, string ActionId)> runs = [];

    /// <summary>The code whose findings are offered <see cref="Offer" />, or <see langword="null" /> for none.</summary>
    public string? OfferedFor { get; set; }

    /// <summary>What a finding of <see cref="OfferedFor" /> is offered.</summary>
    public HealthActionOffer Offer { get; set; } = new(
        "test.action",
        "Find the test rows",
        "Reads one table once. For you only, audited, and kept nowhere.");

    /// <summary>What a press answers.</summary>
    public HealthActionResult Result { get; set; } = HealthActionResult.Refused("Not set.", DateTimeOffset.UnixEpoch);

    /// <summary>What a press throws, for a page that must survive a service that does not turn its failures into values.</summary>
    public Exception? Throw { get; set; }

    /// <summary>When set, a press waits for it before answering, so the running state can be seen.</summary>
    public TaskCompletionSource? Hold { get; set; }

    /// <summary>Every press, in order.</summary>
    public IReadOnlyList<(StudioScope Scope, string MuteKey, string ActionId)> Runs => runs;

    /// <summary>How many times the page asked what a finding offers.</summary>
    public int OfferQuestions { get; private set; }

    public IReadOnlyList<HealthActionOffer> OffersFor(HealthReportFinding finding)
    {
        OfferQuestions++;
        return OfferedFor is { } code && string.Equals(finding.Code, code, StringComparison.Ordinal) ? [Offer] : [];
    }

    public async Task<HealthActionResult> RunAsync(StudioScope scope, HealthReportFinding finding, string actionId, CancellationToken cancellationToken = default)
    {
        runs.Add((scope, finding.MuteKey, actionId));

        if (Hold is { } hold)
        {
            await hold.Task.WaitAsync(cancellationToken);
        }

        return Throw is not null ? throw Throw : Result;
    }
}
