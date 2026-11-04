using PostgresStudio.Services;
using PostgresStudio.Services.Find;

namespace PostgresStudio.Tests.Support;

/// <summary>
/// One kind of Find, said outright by a test: what it answers is a delegate, and every call is recorded.
/// </summary>
internal sealed class FakeFindContributor(int order, string noun) : IFindContributor
{
    /// <summary>What the contributor answers; the default is a value that is not its kind's shape.</summary>
    public Func<StudioScope, FindQuery, CancellationToken, Task<FindContribution>> Behavior { get; set; } =
        static (_, _, _) => Task.FromResult(FindContribution.NotApplicable);

    /// <summary>Every call, with the scope and the value it was asked.</summary>
    public List<(StudioScope Scope, string Value)> Calls { get; } = [];

    public int Order { get; } = order;

    public string Noun { get; } = noun;

    /// <summary>Answers these hits, with a phrase and notes.</summary>
    public FakeFindContributor Returns(string? searched, IReadOnlyList<string>? notes = null, params FindHit[] hits)
    {
        Behavior = (_, _, _) => Task.FromResult(new FindContribution(hits, searched, notes ?? []));
        return this;
    }

    public Task<FindContribution> FindAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken)
    {
        Calls.Add((scope, query.Value));
        return Behavior(scope, query, cancellationToken);
    }

    /// <summary>A hit in <paramref name="scope" />, linked to a made-up address.</summary>
    public static FindHit Hit(StudioScope scope, string kind, string id, string? tenantChip = null, string? mark = null) =>
        new(kind, kind + " " + id, "/postgres/" + kind.ToLowerInvariant() + "?id=" + id, scope, id, tenantChip, mark);

    /// <summary>A contributor that waits for its token to be cancelled and then throws, announcing that it has started.</summary>
    public static FakeFindContributor Blocking(int order, string noun, TaskCompletionSource started)
    {
        FakeFindContributor contributor = new(order, noun);
        contributor.Behavior = async (_, _, token) =>
        {
            started.TrySetResult();

            TaskCompletionSource<FindContribution> never = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await using CancellationTokenRegistration registration = token.Register(() => never.TrySetCanceled(token));
            return await never.Task;
        };

        return contributor;
    }
}
