using PostgresStudio.Services;
using PostgresStudio.Services.Connection;

namespace MartenStudio.Tests.Support;

/// <summary>
/// What Database › Connection and both Overviews' connection line are given, said outright by a test (NP3).
/// </summary>
/// <remarks>
/// The real service resolves a scope and reads a data source's connection string; a page test is about what the page draws
/// from a view, so the view is handed over as it is. By default the page is refused with a sentence, so an Overview test that is
/// not about the connection line draws none - and every request is recorded, so a test can say what the page asked for.
/// </remarks>
internal sealed class FakeConnectionInsightService : IConnectionInsightService
{
    /// <summary>What <see cref="GetAsync" /> answers.</summary>
    public ConnectionPageView View { get; set; } = ConnectionPageView.Refused(ConnectionRefusal.Unavailable, "No connection facts in this test.");

    /// <summary>What <see cref="GetApplicationsAsync" /> answers.</summary>
    public ConnectionApplicationsView Applications { get; set; } = new() { Role = "app", MeasuredAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero) };

    /// <summary>When set, <see cref="GetAsync" /> throws it.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Every scope the page asked the view of.</summary>
    public List<StudioScope> Requests { get; } = [];

    /// <summary>Every scope the page asked the application's sessions of.</summary>
    public List<StudioScope> ApplicationRequests { get; } = [];

    /// <summary>Every scope the Overview's line asked the glance of (no peak).</summary>
    public List<StudioScope> GlanceRequests { get; } = [];

    public Task<ConnectionPageView> GetGlanceAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        GlanceRequests.Add(scope);
        return Failure is not null ? Task.FromException<ConnectionPageView>(Failure) : Task.FromResult(View);
    }

    public Task<ConnectionPageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        Requests.Add(scope);
        return Failure is not null ? Task.FromException<ConnectionPageView>(Failure) : Task.FromResult(View);
    }

    public Task<ConnectionApplicationsView> GetApplicationsAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ApplicationRequests.Add(scope);
        return Task.FromResult(Applications);
    }
}
