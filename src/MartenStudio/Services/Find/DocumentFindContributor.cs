using System.Globalization;

using MartenStudio.Services.Documents;

using Microsoft.Extensions.Options;

using PostgresStudio.Services;
using PostgresStudio.Services.Find;

namespace MartenStudio.Services.Find;

/// <summary>
/// Find's documents (1.2): the typed value as an id in every visible collection whose id column it parses against, through
/// <see cref="IDocumentDataService.FindByIdAsync" /> - <c>ProbeIdAsync</c>'s loop with the tenant, the subclass and the soft-delete
/// flag read beside it, over the same visibility gate and the same 25-collection bound.
/// </summary>
/// <remarks>
/// A hidden or infrastructure document type is never probed and never counted: the phrase says how many visible types were searched,
/// the number the rail shows. A tenant-partitioned collection is not probed while no tenant is selected, and the note says so once.
/// Each hit is a document row - one per tenant that holds the id - linked to the document page with that row's tenant. With a store
/// policy in force and no tenant selected the probe reads only the tenants the visitor may address (<see cref="FindTenants" />), so that
/// rows of other tenants cannot fill its row limit and hide the visitor's own.
/// </remarks>
internal sealed class DocumentFindContributor : IFindContributor
{
    private readonly IDocumentDataService documents;
    private readonly IFindScopes scopes;
    private readonly IOptions<MartenStudioOptions> options;

    public DocumentFindContributor(IDocumentDataService documents, IFindScopes scopes, IOptions<MartenStudioOptions> options)
    {
        this.documents = documents;
        this.scopes = scopes;
        this.options = options;
    }

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public string Noun => "Documents";

    /// <inheritdoc />
    public async Task<FindContribution> FindAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        if (!query.CouldBeAnId)
        {
            return FindContribution.NotApplicable;
        }

        // Not the event store's context: documents do not depend on it. If the tenant restriction itself cannot be read, the probe is not
        // made - never made unrestricted under a policy.
        FindTenants? tenants = await scopes.TenantsAsync(scope, query, cancellationToken).ConfigureAwait(false);
        if (tenants is null)
        {
            return new FindContribution([], null, ["Documents: could not be searched."]);
        }

        DocumentFindResult found = await documents.FindByIdAsync(scope, query.Value, tenants, cancellationToken).ConfigureAwait(false);

        List<FindHit> hits = [];
        foreach (DocumentFindHit hit in found.Hits)
        {
            for (int index = 0; index < hit.Rows.Count; index++)
            {
                DocumentFindRow row = hit.Rows[index];
                StudioScope at = (found.Scope ?? scope) with { TenantId = row.TenantId ?? (found.Scope ?? scope).TenantId };

                // The hit says what is beyond the five tenants it names, on its last line.
                string? detail = row.TypeName is null ? null : "as " + row.TypeName;

                // Counted from the admitted tenants only, and "(or more)" only when the probe's row limit was reached by rows that were all
                // admitted: a limit that refused rows helped to fill says nothing about how many more the visitor may see.
                if (index == hit.Rows.Count - 1 && hit.MoreTenants > 0)
                {
                    string more = $"and {hit.MoreTenants.ToString(CultureInfo.InvariantCulture)} more {(hit.MoreTenants == 1 ? "tenant" : "tenants")} you may see{(hit.MoreMaybe ? " (or more)" : string.Empty)}";
                    detail = detail is null ? more : detail + ", " + more;
                }

                hits.Add(new FindHit(
                    Kind: "Document",
                    Title: hit.Alias + " " + hit.Id,
                    Url: DocumentLinks.ToDocument(options.Value, at, hit.Alias, hit.Id),
                    Scope: at,
                    Id: hit.Alias + "\u001f" + hit.Id,
                    TenantChip: row.TenantId is null ? null : "tenant " + row.TenantId,
                    Mark: row.Deleted ? "deleted" : null,
                    Detail: detail));
            }
        }

        List<string> notes = [];
        if (found.PartitionedSkipped > 0)
        {
            notes.Add($"{found.PartitionedSkipped.ToString(CultureInfo.InvariantCulture)} document {(found.PartitionedSkipped == 1 ? "type" : "types")} partitioned by tenant {(found.PartitionedSkipped == 1 ? "was" : "were")} not searched: select a tenant.");
        }

        if (found.TenantRestricted)
        {
            notes.Add(FindTenants.RestrictedNote);
        }

        if (found.ProbeLimitReached)
        {
            notes.Add("Only the first 25 document types were searched.");
        }

        string? searched = found.VisibleTypes == 0
            ? null
            : $"{found.VisibleTypes.ToString(CultureInfo.InvariantCulture)} document {(found.VisibleTypes == 1 ? "type" : "types")}";

        return new FindContribution(hits, searched, notes);
    }
}
