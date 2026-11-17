using System.Security.Claims;

using Microsoft.AspNetCore.Authorization;

using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Database;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Live;
using DataStudio.Postgres.Services.Postgres;
using DataStudio.Postgres.Services.Query;
using DataStudio.Postgres.Services.Sources;

namespace DataStudio.Postgres.Tests.Support;

/// <summary>
/// An <see cref="IAuthorizationService" /> that answers for <see cref="PostgresResource" />: everything by default,
/// or what a test allows. Every question is recorded.
/// </summary>
public sealed class TestSourceAuthorizationService : IAuthorizationService
{
    private Func<PostgresResource, bool> rule = static _ => true;

    public List<(string Policy, PostgresResource Resource)> Calls { get; } = [];

    public void Allow(Func<PostgresResource, bool> predicate) => rule = predicate;

    public Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, IEnumerable<IAuthorizationRequirement> requirements) =>
        Task.FromResult(resource is PostgresResource source && rule(source) ? AuthorizationResult.Success() : AuthorizationResult.Failed());

    /// <summary>
    /// A gate a test can hold shut to keep every answer pending, which is what the scope gate's "unknown" frame looks like from the inside. The rule
    /// is applied when the question is asked, not when the gate opens, so a test can hold the catalog's questions and change the rule behind them.
    /// </summary>
    public TaskCompletionSource? Gate { get; set; }

    public async Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user, object? resource, string policyName)
    {
        bool allowed = resource is PostgresResource source && rule(source);

        if (resource is PostgresResource asked)
        {
            lock (Calls)
            {
                Calls.Add((policyName, asked));
            }
        }

        if (Gate is { } gate)
        {
            await gate.Task.Bounded();
        }

        return allowed ? AuthorizationResult.Success() : AuthorizationResult.Failed();
    }
}

/// <summary>
/// The Storage page's service (W5-CAP): a view built by the real per-visitor step, in the Postgres module's settings, over a
/// table in its own schema and one in another whose gate could not be read - so the page draws a named row, a withheld row
/// and the sentence that says why. Every request recorded.
/// </summary>
internal sealed class FakeStorage : DataStudio.Postgres.Services.Storage.IStorageService
{
    public List<StudioScope> Requests { get; } = [];

    public DataStudio.Postgres.Services.Storage.StorageView? View { get; set; }

    public async Task<DataStudio.Postgres.Services.Storage.StorageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        Requests.Add(scope);
        return View ??= await BuildAsync(scope);
    }

    private static Task<DataStudio.Postgres.Services.Storage.StorageView> BuildAsync(StudioScope scope)
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch.AddYears(56);

        StudioTable own = new("public", "orders", StudioTableKind.Relational, StudioTableOwnership.Owned);
        StudioTable other = new("legacy", "employees", StudioTableKind.Relational, StudioTableOwnership.Other) { GatedSchemas = ["legacy"] };

        DataStudio.Postgres.Services.Storage.StorageFacts facts = DataStudio.Postgres.Services.Storage.StorageFacts.Build(
            "default",
            "app",
            new StatisticsWindow { MeasuredAt = now, Since = now.AddHours(-3), Basis = StatisticsBasis.ServerReset },
            new DataStudio.Postgres.Internal.Sql.StorageDatabaseRow(64L << 20, 60L << 20, 8192, 170_011),
            new OneSet([own, other]),
            [
                new DataStudio.Postgres.Internal.Sql.StorageTableRow { Schema = "public", Table = "orders", HeapBytes = 8L << 20, EstimatedRows = 10_000 },
                new DataStudio.Postgres.Internal.Sql.StorageTableRow { Schema = "legacy", Table = "employees", HeapBytes = 4L << 20, EstimatedRows = -1 },
            ]);

        DataStudio.Postgres.Services.Storage.StorageService service = new(
            new PostgresSettings(Microsoft.Extensions.Options.Options.Create(new PostgresOptions()), new DataStudio.Services.Settings.ShellSettings(Microsoft.Extensions.Options.Options.Create(new DataStudioOptions()))),
            PostgresDescriptor.Instance,
            authorization: null!,
            resolver: null!,
            facts: null!,
            new StudioLogThrottle(TimeProvider.System),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DataStudio.Postgres.Services.Storage.StorageService>.Instance);

        return service.ShowAsync(facts, scope, static () => Task.FromResult<DatabaseGate?>(null));
    }

    private sealed class OneSet(IReadOnlyList<StudioTable> tables) : IStudioTableSet
    {
        public IReadOnlyList<StudioTable> Tables { get; } = tables;

        public string? Coverage => null;

        public StudioTable? Find(string schema, string table) => Tables.FirstOrDefault(x => x.Schema == schema && x.Table == table);

        public DataStudio.Postgres.Services.Health.Checks.IndexOrigin OriginOf(DataStudio.Postgres.Internal.Sql.HealthIndexRow index, StudioTable table) =>
            DataStudio.Postgres.Services.Health.Checks.IndexOrigin.Host;
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

    /// <summary>What the next run returns, or <see langword="null" /> for an empty result.</summary>
    public SqlConsoleResult? Next { get; set; }

    public Task<SqlConsoleResult> RunSqlAsync(StudioScope scope, SqlConsoleRequest request, CancellationToken cancellationToken = default)
    {
        Runs.Add((scope, request));
        return Task.FromResult(Next ?? new SqlConsoleResult([], [], false, 500, TimeSpan.Zero, [], null, null));
    }

    /// <summary>Every plan the Plan page asked for (X3), with the scope.</summary>
    public List<(StudioScope Scope, PlanRunRequest Request)> Plans { get; } = [];

    /// <summary>What the next plan returns, or <see langword="null" /> for a refusal that says the fake was not told what to answer.</summary>
    public PlanRunResult? NextPlan { get; set; }

    /// <summary>Answers queued for the next plans, one each, before <see cref="NextPlan" /> answers every one (0.10-B: two plans in one action).</summary>
    public Queue<PlanRunResult> QueuedPlans { get; } = new();

    /// <summary>When set, a plan waits for it before answering - a run in flight a test can move the scope under. Ignores the token, as a slow service may.</summary>
    public TaskCompletionSource? PlanGate { get; set; }

    /// <summary>When set, a what-if in place waits for it before answering.</summary>
    public TaskCompletionSource? WhatIfGate { get; set; }

    public async Task<PlanRunResult> PlanAsync(StudioScope scope, PlanRunRequest request, CancellationToken cancellationToken = default)
    {
        Plans.Add((scope, request));

        if (PlanGate is { } gate)
        {
            await gate.Task.Bounded();
        }

        if (QueuedPlans.Count > 0)
        {
            return QueuedPlans.Dequeue();
        }

        return NextPlan ?? PlanRunResult.Refused(new SqlRejection("Fake", "The fake console was not told what to answer.", null, -1), 0);
    }

    /// <summary>Every what-if the Plan page asked for in place (0.10-B), with the request it was for.</summary>
    public List<(PlanRunRequest Request, HypotheticalIndex Index)> PlanWhatIfs { get; } = [];

    public async Task<WhatIfResult> PlanWhatIfAsync(StudioScope scope, PlanRunRequest request, HypotheticalIndex index, CancellationToken cancellationToken = default)
    {
        PlanWhatIfs.Add((request, index));

        if (WhatIfGate is { } gate)
        {
            await gate.Task.Bounded();
        }

        return WhatIf?.Invoke(index)
            ?? WhatIfResult.NotCompared(index, HypotheticalIndexAvailability.NotInstalledSentence, HypotheticalIndexState.NotInstalled);
    }

    /// <summary>Every what-if asked (X2), with the statement it was for.</summary>
    public List<(string Statement, HypotheticalIndex Index)> WhatIfs { get; } = [];

    /// <summary>What a what-if answers; hypopg not installed when a test says nothing.</summary>
    public Func<HypotheticalIndex, WhatIfResult>? WhatIf { get; set; }

    public Task<WhatIfResult> WhatIfAsync(StudioScope scope, string statement, HypotheticalIndex index, CancellationToken cancellationToken = default)
    {
        WhatIfs.Add((statement, index));
        return Task.FromResult(WhatIf?.Invoke(index)
            ?? WhatIfResult.NotCompared(index, HypotheticalIndexAvailability.NotInstalledSentence, HypotheticalIndexState.NotInstalled));
    }
}

/// <summary>The Plan page's facts read (0.10-A): records each plan it was asked about and answers what a test says.</summary>
internal sealed class FakePlanFacts : IPlanFactsService
{
    /// <summary>Every plan the page asked the facts of, with the scope.</summary>
    public List<(StudioScope Scope, QueryPlan Plan)> Reads { get; } = [];

    /// <summary>What a read answers, or <see langword="null" /> for facts that name no relation.</summary>
    public Func<QueryPlan, PlanFacts>? Answer { get; set; }

    /// <summary>When set, a read throws it - a gate that answered differently a moment after the plan was run.</summary>
    public Exception? Throws { get; set; }

    public Task<PlanFacts> ReadAsync(StudioScope scope, QueryPlan plan, CancellationToken cancellationToken = default)
    {
        Reads.Add((scope, plan));

        if (Throws is { } exception)
        {
            throw exception;
        }

        return Task.FromResult(Answer?.Invoke(plan) ?? new PlanFacts(DateTimeOffset.UnixEpoch, [], []));
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
internal sealed class FakeAurora : DataStudio.Postgres.Services.Aurora.IAuroraService
{
    public List<StudioScope> Requests { get; } = [];

    /// <summary>What the section is handed.</summary>
    public DataStudio.Postgres.Services.Aurora.AuroraView View { get; set; } = new();

    /// <summary>When set, answers in place of <see cref="View" /> - to hold a read, or to fail it.</summary>
    public Func<StudioScope, Task<DataStudio.Postgres.Services.Aurora.AuroraView>>? Answer { get; set; }

    public Task<DataStudio.Postgres.Services.Aurora.AuroraView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        Requests.Add(scope);
        return Answer is { } answer ? answer(scope) : Task.FromResult(View);
    }
}

/// <summary>
/// Database › Connection and both Overviews' connection line (NP3): the view said outright by a test. Refused by default, so an
/// Overview test that is not about the line draws none. The same shape as DataStudio.Marten.Tests' fake of the same name, which this
/// project cannot link without a change to its project file.
/// </summary>
internal sealed class FakeConnectionInsightService : DataStudio.Postgres.Services.Connection.IConnectionInsightService
{
    public DataStudio.Postgres.Services.Connection.ConnectionPageView View { get; set; } =
        DataStudio.Postgres.Services.Connection.ConnectionPageView.Refused(DataStudio.Postgres.Services.Connection.ConnectionRefusal.Unavailable, "No connection facts in this test.");

    public DataStudio.Postgres.Services.Connection.ConnectionApplicationsView Applications { get; set; } =
        new() { Role = "app", MeasuredAt = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero) };

    public Exception? Failure { get; set; }

    public List<StudioScope> Requests { get; } = [];

    public List<StudioScope> ApplicationRequests { get; } = [];

    /// <summary>Every scope the Overview's line asked the glance of (no peak).</summary>
    public List<StudioScope> GlanceRequests { get; } = [];

    public Task<DataStudio.Postgres.Services.Connection.ConnectionPageView> GetGlanceAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        GlanceRequests.Add(scope);

        return Failure is not null
            ? Task.FromException<DataStudio.Postgres.Services.Connection.ConnectionPageView>(Failure)
            : Task.FromResult(View);
    }

    public Task<DataStudio.Postgres.Services.Connection.ConnectionPageView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        Requests.Add(scope);

        return Failure is not null
            ? Task.FromException<DataStudio.Postgres.Services.Connection.ConnectionPageView>(Failure)
            : Task.FromResult(View);
    }

    public Task<DataStudio.Postgres.Services.Connection.ConnectionApplicationsView> GetApplicationsAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ApplicationRequests.Add(scope);
        return Task.FromResult(Applications);
    }
}

/// <summary>
/// "Compare a connection string" (1.1-PASTE): the comparison said outright by a test, or computed by a function it hands over (the
/// real sanitiser and comparer over stated facts). Records every request - scope and text - so a test can say what was asked and
/// that the text reached the service only through the call.
/// </summary>
internal sealed class FakeConnectionCompareService : DataStudio.Postgres.Services.Connection.IConnectionCompareService
{
    public DataStudio.Postgres.Services.Connection.ConnectionCompareView View { get; set; } =
        DataStudio.Postgres.Services.Connection.ConnectionCompareView.Of(DataStudio.Postgres.Services.Connection.ConnectionCompareOutcome.Unavailable, "No comparison in this test.");

    /// <summary>When set, the answer to a request, from its text.</summary>
    public Func<string?, DataStudio.Postgres.Services.Connection.ConnectionCompareView>? Answer { get; set; }

    public Exception? Failure { get; set; }

    public List<(StudioScope Scope, string? Text)> Requests { get; } = [];

    public Task<DataStudio.Postgres.Services.Connection.ConnectionCompareView> CompareAsync(StudioScope scope, string? pasted, CancellationToken cancellationToken = default)
    {
        Requests.Add((scope, pasted));

        return Failure is not null
            ? Task.FromException<DataStudio.Postgres.Services.Connection.ConnectionCompareView>(Failure)
            : Task.FromResult(Answer is { } answer ? answer(pasted) : View);
    }
}

/// <summary>The sidebar's badges: none.</summary>
internal sealed class FakeNavIndicators : INavIndicatorService
{
    public Task<NavIndicators> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(NavIndicators.None);
}
