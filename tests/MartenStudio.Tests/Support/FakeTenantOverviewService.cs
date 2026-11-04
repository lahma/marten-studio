using MartenStudio.Services.Tenants;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Support;

/// <summary>The overview a Tenants page test says the page is given, and what the page asked for.</summary>
internal sealed class FakeTenantOverviewService : ITenantOverviewService
{
    /// <summary>What <see cref="GetAsync" /> answers; a function so a test can answer by request.</summary>
    public Func<StudioScope, TenantOverviewRequest, TenantOverview> Answer { get; set; } = static (_, _) => new TenantOverview();

    /// <summary>What <see cref="GetAsync" /> throws, when a test is about the failure frame.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Every ask, in order.</summary>
    public List<(StudioScope Scope, TenantOverviewRequest Request)> Asks { get; } = [];

    public Task<TenantOverview> GetAsync(StudioScope scope, TenantOverviewRequest request, CancellationToken cancellationToken = default)
    {
        Asks.Add((scope, request));

        return Failure is not null ? Task.FromException<TenantOverview>(Failure) : Task.FromResult(Answer(scope, request));
    }

    /// <summary>A row with every cell a number or a dash and nothing remarkable.</summary>
    public static TenantRow Row(string tenant, string database = "localhost.marten", TenantPlacement placement = TenantPlacement.SharedTables) =>
        new(
            database,
            tenant,
            placement,
            placement == TenantPlacement.OwnDatabase ? "Own database" : "Shared tables",
            new TenantCell("~1,200"),
            new TenantCell("~9,400", "title"),
            new TenantCell("~300"),
            new TenantCell("#12"),
            null,
            null);
}
