using PostgresStudio.Services;

namespace MartenStudio.Services.Events;

// 1.2, M6: Events > Event flow. In a file of its own; the class's documentation is EventDataService.cs's.
internal sealed partial class EventDataService
{
    /// <inheritdoc />
    public async Task<EventFlowResult> GetEventFlowAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scope);

        try
        {
            // The scope first, which applies the store policy (D5), and before the cache: the cache is the same for every
            // visitor, so what a visitor may reach is decided in front of it and never inside it.
            ResolvedScope resolved = await resolver.ResolveAsync(scope, null, cancellationToken).ConfigureAwait(false);

            // A passive configuration read, like the Event types page without counts: no connection, and so no audit entry.
            return new EventFlowResult(flows.Get(resolved.Store, options.Value.IsDocumentTypeVisible), null);
        }
        catch (Exception exception) when (IsReadFailure(exception))
        {
            return EventFlowResult.Failed(Describe(scope, exception, "read the event flow").Display);
        }
    }
}
