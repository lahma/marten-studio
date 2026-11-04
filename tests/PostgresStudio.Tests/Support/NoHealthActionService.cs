using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Tests.Support;

/// <summary>
/// The Health page's action seam with nothing registered, as Postgres Studio has it: no finding offers an action, and none is run.
/// </summary>
internal sealed class NoHealthActionService : IHealthActionService
{
    public IReadOnlyList<HealthActionOffer> OffersFor(HealthReportFinding finding) => [];

    public Task<HealthActionResult> RunAsync(StudioScope scope, HealthReportFinding finding, string actionId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("No action is offered, so none is run.");
}
