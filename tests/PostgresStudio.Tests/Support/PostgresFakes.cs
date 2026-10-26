using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;

using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Live;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Support;

/// <summary>
/// An <see cref="IAuthorizationService" /> that answers for <see cref="PostgresStudioResource" />: everything by default,
/// or what a test allows. Every question is recorded.
/// </summary>
public sealed class TestSourceAuthorizationService : IAuthorizationService
{
    private Func<PostgresStudioResource, bool> rule = static _ => true;

    public List<(string Policy, PostgresStudioResource Resource)> Calls { get; } = [];

    public void Allow(Func<PostgresStudioResource, bool> predicate) => rule = predicate;

    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
        Task.FromResult(resource is PostgresStudioResource source && rule(source) ? AuthorizationResult.Success() : AuthorizationResult.Failed());

    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
    {
        if (resource is PostgresStudioResource source)
        {
            Calls.Add((policyName, source));
        }

        return Task.FromResult(resource is PostgresStudioResource allowed && rule(allowed) ? AuthorizationResult.Success() : AuthorizationResult.Failed());
    }
}

/// <summary>Database › Sessions' service: three sessions, no details, every request recorded.</summary>
internal sealed class FakeSessions : ISessionActivityService
{
    public List<StudioScope> SummaryRequests { get; } = [];

    public SessionActivitySummary Summary { get; set; } = new()
    {
        MeasuredAt = DateTimeOffset.UnixEpoch,
        SessionCount = 3,
        ByState = [new("active", 1), new("idle", 2)],
    };

    public Task<SessionActivitySummary> GetSummaryAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        SummaryRequests.Add(scope);
        return Task.FromResult(Summary);
    }

    public Task<SessionActivityDetails> GetDetailsAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(new SessionActivityDetails { Refusal = DatabaseRefusal.CapabilityOff, Reason = "Off in this test." });
}

/// <summary>The SQL console's service: records each run and answers an empty result.</summary>
internal sealed class FakeSqlConsole : ISqlConsoleService
{
    public List<(StudioScope Scope, SqlConsoleRequest Request)> Runs { get; } = [];

    public Task<SqlConsoleResult> RunSqlAsync(StudioScope scope, SqlConsoleRequest request, CancellationToken cancellationToken = default)
    {
        Runs.Add((scope, request));
        return Task.FromResult(new SqlConsoleResult([], [], false, 500, TimeSpan.Zero, [], null, null));
    }
}

/// <summary>The Overview's server read: PostgreSQL 17.4 on a server of somebody's own.</summary>
internal sealed class FakeServerReader : IPostgresServerReader
{
    public List<StudioScope> Requests { get; } = [];

    public List<bool> Refreshes { get; } = [];

    /// <summary>What the read answers: 17.4, self-hosted, unless a test says otherwise.</summary>
    public PostgresServerFacts Facts { get; init; } = new(DateTimeOffset.UnixEpoch, 170004, "17.4", PostgresPlatform.SelfHosted);

    public Task<PostgresServerRead> ReadAsync(StudioScope scope, bool refresh = false, CancellationToken cancellationToken = default)
    {
        Requests.Add(scope);
        Refreshes.Add(refresh);
        return Task.FromResult(new PostgresServerRead(Facts, null));
    }
}

/// <summary>The sidebar's badges: none.</summary>
internal sealed class FakeNavIndicators : INavIndicatorService
{
    public Task<NavIndicators> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(NavIndicators.None);
}
