using MartenStudio.Services.Query;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Query;

/// <summary>What LINQ mode is given, said outright by a test - and every request the panel made.</summary>
internal sealed class FakeLinqQueryService : ILinqQueryService
{
    /// <summary>What <see cref="DescribeAsync" /> answers.</summary>
    public LinqModeStatus Status { get; set; } = new(LinqAvailability.Available, null, []);

    /// <summary>What <see cref="RunAsync" /> answers.</summary>
    public LinqQueryResult? Result { get; set; }

    /// <summary>What <see cref="RunAsync" /> throws instead.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Every request the panel made.</summary>
    public List<LinqQueryRequest> Requests { get; } = [];

    public FakeLinqQueryService WithType(string alias, string typeName = "Order", bool conjoined = false, bool softDeleted = false)
    {
        Status = Status with
        {
            Types =
            [
                .. Status.Types,
                new LinqDocumentTypeInfo(alias, typeName, "MartenStudio.Sample." + typeName, null, conjoined, softDeleted, ["Name", "Total", "Status"]),
            ],
            MayRun = true,
            MayExplain = true,
        };

        return this;
    }

    public Task<LinqModeStatus> DescribeAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(Status);

    public Task<LinqQueryResult> RunAsync(StudioScope scope, LinqQueryRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);

        if (Failure is { } failure)
        {
            return Task.FromException<LinqQueryResult>(failure);
        }

        return Task.FromResult(Result ?? new LinqQueryResult(request.Alias, [], false, request.Offset, request.PageSize ?? 50, string.Empty, [], TimeSpan.Zero));
    }
}
