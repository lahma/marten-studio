using MartenStudio.Services.Events;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Events;

// 1.2, M6: the event flow's seam, in a file of its own beside the fake's other areas.
internal sealed partial class FakeEventDataService
{
    /// <summary>What the event flow screen answers.</summary>
    public EventFlowResult EventFlow { get; set; } = new(EventFlowModel.None, null);

    /// <summary>How many times the event flow was read.</summary>
    public int EventFlowReads { get; private set; }

    /// <summary>The scopes the event flow was read for.</summary>
    public List<StudioScope> EventFlowScopes { get; } = [];

    /// <inheritdoc />
    public Task<EventFlowResult> GetEventFlowAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        EventFlowReads++;
        EventFlowScopes.Add(scope);

        return Task.FromResult(EventFlow);
    }
}
