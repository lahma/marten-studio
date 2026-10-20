using MartenStudio.Services.Query;
using MartenStudio.Services.Sources;

namespace MartenStudio.Services;

/// <summary>
/// MartenStudio's <see cref="ISqlConsoleService" />: today's console, <see cref="IQueryService.RunSqlAsync" />.
/// PS-1a's pass-through; the query packet (PS-1d) moves the console out of <c>QueryService</c> into a shared
/// <c>SqlConsoleService</c> and has <c>QueryService</c> delegate to it instead.
/// </summary>
internal sealed class MartenSqlConsoleService : ISqlConsoleService
{
    private readonly IQueryService queries;

    public MartenSqlConsoleService(IQueryService queries)
    {
        this.queries = queries;
    }

    /// <inheritdoc />
    public Task<SqlConsoleResult> RunSqlAsync(StudioScope scope, SqlConsoleRequest request, CancellationToken cancellationToken = default) =>
        queries.RunSqlAsync(scope, request, cancellationToken);
}
