using PostgresStudio.Services;
using PostgresStudio.Services.Connection;

namespace MartenStudio.Tests.Support;

/// <summary>
/// The Connection page's paste-to-compare service for component tests that render the page but never compare: Database › Connection
/// injects it (1.1-PASTE). A test that compares registers a fake of its own; this one says so if anything calls it.
/// </summary>
internal sealed class UnusedConnectionCompareService : IConnectionCompareService
{
    public Task<ConnectionCompareView> CompareAsync(StudioScope scope, string? pasted, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This component test renders the Connection page but does not compare a connection string.");
}
