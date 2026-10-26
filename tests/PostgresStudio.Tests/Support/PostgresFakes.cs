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

/// <summary>
/// The Storage page's service (W5-CAP): a view built by the real per-visitor step, in Postgres Studio's settings, over a
/// table in its own schema and one in another whose gate could not be read - so the page draws a named row, a withheld row
/// and the sentence that says why. Every request recorded.
/// </summary>
internal sealed class FakeStorage : PostgresStudio.Services.Storage.IStorageService
{
    public List<StudioScope> Requests { get; } = [];

    public PostgresStudio.Services.Storage.StorageView? View { get; set; }

    public async Task<PostgresStudio.Services.Storage.StorageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        Requests.Add(scope);
        return View ??= await BuildAsync(scope);
    }

    private static Task<PostgresStudio.Services.Storage.StorageView> BuildAsync(StudioScope scope)
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch.AddYears(56);

        StudioTable own = new("public", "orders", StudioTableKind.Relational, StudioTableOwnership.Owned);
        StudioTable other = new("legacy", "employees", StudioTableKind.Relational, StudioTableOwnership.Other) { GatedSchemas = ["legacy"] };

        PostgresStudio.Services.Storage.StorageFacts facts = PostgresStudio.Services.Storage.StorageFacts.Build(
            "default",
            "app",
            new StatisticsWindow { MeasuredAt = now, Since = now.AddHours(-3), Basis = StatisticsBasis.ServerReset },
            new PostgresStudio.Internal.Sql.StorageDatabaseRow(64L << 20, 60L << 20, 8192, 170_011),
            new OneSet([own, other]),
            [
                new PostgresStudio.Internal.Sql.StorageTableRow { Schema = "public", Table = "orders", HeapBytes = 8L << 20, EstimatedRows = 10_000 },
                new PostgresStudio.Internal.Sql.StorageTableRow { Schema = "legacy", Table = "employees", HeapBytes = 4L << 20, EstimatedRows = -1 },
            ]);

        PostgresStudio.Services.Storage.StorageService service = new(
            new PostgresStudioSettings(Microsoft.Extensions.Options.Options.Create(new PostgresStudioOptions())),
            PostgresStudioDescriptor.Instance,
            authorization: null!,
            resolver: null!,
            facts: null!,
            new StudioLogThrottle(TimeProvider.System),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresStudio.Services.Storage.StorageService>.Instance);

        return service.ShowAsync(facts, scope, static () => Task.FromResult<DatabaseGate?>(null));
    }

    private sealed class OneSet(IReadOnlyList<StudioTable> tables) : IStudioTableSet
    {
        public IReadOnlyList<StudioTable> Tables { get; } = tables;

        public string? Coverage => null;

        public StudioTable? Find(string schema, string table) => Tables.FirstOrDefault(x => x.Schema == schema && x.Table == table);

        public PostgresStudio.Services.Health.Checks.IndexOrigin OriginOf(PostgresStudio.Internal.Sql.HealthIndexRow index, StudioTable table) =>
            PostgresStudio.Services.Health.Checks.IndexOrigin.Host;
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

/// <summary>The Server page's Aurora section: a server that is not Aurora, with no capacity source, unless a test says otherwise.</summary>
internal sealed class FakeAurora : PostgresStudio.Services.Aurora.IAuroraService
{
    public List<StudioScope> Requests { get; } = [];

    /// <summary>What the section is handed.</summary>
    public PostgresStudio.Services.Aurora.AuroraView View { get; set; } = new();

    /// <summary>When set, answers in place of <see cref="View" /> - to hold a read, or to fail it.</summary>
    public Func<StudioScope, Task<PostgresStudio.Services.Aurora.AuroraView>>? Answer { get; set; }

    public Task<PostgresStudio.Services.Aurora.AuroraView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        Requests.Add(scope);
        return Answer is { } answer ? answer(scope) : Task.FromResult(View);
    }
}

/// <summary>The sidebar's badges: none.</summary>
internal sealed class FakeNavIndicators : INavIndicatorService
{
    public Task<NavIndicators> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(NavIndicators.None);
}
