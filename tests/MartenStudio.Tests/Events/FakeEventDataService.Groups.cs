using MartenStudio.Services.Events;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Events;

/// <summary>M3: the grouped dead-letter view's half of the fake.</summary>
internal sealed partial class FakeEventDataService
{
    /// <summary>What the grouped view is told.</summary>
    public DeadLetterGroupList Groups { get; set; } = DeadLetterGroupList.Empty;

    /// <summary>The scope each grouped read was asked in, in order.</summary>
    public List<StudioScope> GroupReads { get; } = [];

    /// <summary>What "Skip this group"'s dialog is told it would do.</summary>
    public DeadLetterGroupSkipPlan SkipPlan { get; set; } = new(3, 3, 0, null);

    /// <summary>The plans that were asked for: the scope and the group.</summary>
    public List<(StudioScope Scope, DeadLetterGroupKey Group)> PlanRequests { get; } = [];

    /// <summary>The bulk skips that were asked for: the scope, the group and what was typed.</summary>
    public List<(StudioScope Scope, DeadLetterGroupKey Group, string Confirmation)> GroupSkips { get; } = [];

    public Task<DeadLetterGroupList> ListDeadLetterGroupsAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        GroupReads.Add(scope);
        return Task.FromResult(Groups);
    }

    public Task<DeadLetterGroupSkipPlan> DescribeDeadLetterGroupSkipAsync(
        StudioScope scope,
        DeadLetterGroupKey group,
        CancellationToken cancellationToken = default)
    {
        PlanRequests.Add((scope, group));
        return Task.FromResult(SkipPlan);
    }

    public Task<long> SkipDeadLetterGroupAsync(
        StudioScope scope,
        DeadLetterGroupKey group,
        string confirmation,
        CancellationToken cancellationToken = default)
    {
        if (MutationFailure is not null)
        {
            return Task.FromException<long>(MutationFailure);
        }

        GroupSkips.Add((scope, group, confirmation));
        return Task.FromResult(SkipPlan.ToSkip);
    }

    /// <summary>A group with everything a row can draw.</summary>
    public static DeadLetterGroup Group(
        string projection = "OrderSummary",
        string? eventType = "order_shipped",
        string exceptionType = "InvalidOperationException",
        long letters = 3,
        long events = 3,
        long? skipped = 0,
        long tenantCount = 1,
        IReadOnlyList<string>? tenantNames = null) =>
        new(
            new DeadLetterGroupKey(projection, "All", eventType, exceptionType, "Failure to apply event #{n} Id({guid})"),
            letters,
            events,
            skipped,
            new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero),
            tenantCount,
            tenantNames ?? ["*DEFAULT*"],
            "Failure to apply event #42 Id(5f9b3c1e-9a4f-4b6e-8f0c-3a2b1c0d9e8f)");

    /// <summary>A list of groups that recorded skips and was read just now.</summary>
    public static DeadLetterGroupList GroupList(params DeadLetterGroup[] groups) =>
        new(groups, groups.Length, groups.Sum(static x => x.Letters), new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero))
        {
            RecordsSkips = true,
        };
}
