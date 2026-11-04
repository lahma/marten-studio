using PostgresStudio.Services;

namespace MartenStudio.Services.Tenants;

/// <summary>
/// What the Tenants page reads (1.2): the tenants the visitor may address, placed in their databases, with the volumes that compare
/// them. An interface so a component test can hand the page a table without a Postgres.
/// </summary>
internal interface ITenantOverviewService
{
    /// <summary>
    /// One page of the overview of <paramref name="scope" />'s store. The scope is resolved with no tenant (the store policy for the
    /// database as a whole comes first); the scope's own tenant only picks the page that holds it. A refusal is a value; the call
    /// never throws but for cancellation.
    /// </summary>
    /// <param name="scope">The store and database; a tenant in it is read as the one to open the page on.</param>
    /// <param name="request">The prefix and the page.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    Task<TenantOverview> GetAsync(StudioScope scope, TenantOverviewRequest request, CancellationToken cancellationToken = default);
}
