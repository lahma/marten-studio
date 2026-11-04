using MartenStudio.Services.Query;
using MartenStudio.Tests.Support;
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

    /// <summary>When set, <see cref="RunAsync" /> does not answer until it is completed: a run held in flight.</summary>
    public TaskCompletionSource? Hold { get; set; }

    public async Task<LinqQueryResult> RunAsync(StudioScope scope, LinqQueryRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);

        if (Hold is { } hold)
        {
            await hold.Task.Bounded();
        }

        if (Failure is { } failure)
        {
            throw failure;
        }

        return Result ?? new LinqQueryResult(request.Alias, [], false, request.Offset, request.PageSize ?? 50, string.Empty, [], TimeSpan.Zero);
    }

    /// <summary>X2: what <see cref="WhatIfAsync" /> answers; hypopg not installed when a test says nothing.</summary>
    public Func<PostgresStudio.Services.Query.HypotheticalIndex, PostgresStudio.Services.Query.WhatIfResult>? WhatIf { get; set; }

    /// <summary>Every what-if the panel asked for, with the request it was for.</summary>
    public List<(LinqQueryRequest Request, PostgresStudio.Services.Query.HypotheticalIndex Index)> WhatIfs { get; } = [];

    public Task<PostgresStudio.Services.Query.WhatIfResult> WhatIfAsync(
        StudioScope scope,
        LinqQueryRequest request,
        PostgresStudio.Services.Query.HypotheticalIndex index,
        CancellationToken cancellationToken = default)
    {
        WhatIfs.Add((request, index));

        return Task.FromResult(WhatIf?.Invoke(index) ?? PostgresStudio.Services.Query.WhatIfResult.NotCompared(
            index, PostgresStudio.Services.Query.HypotheticalIndexAvailability.NotInstalledSentence, PostgresStudio.Services.Query.HypotheticalIndexState.NotInstalled));
    }
}
