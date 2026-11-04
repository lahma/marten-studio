using PostgresStudio.Components.Pages.Database;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Find;

/// <summary>
/// Find's tables and views (1.2): a typed <c>name</c> or <c>schema.name</c>, matched exactly and case-insensitively against what the
/// database browser lists, under the browser's own gate (D27).
/// </summary>
/// <remarks>
/// <para>
/// <b>The schema is never passed to the list.</b> A schema the gate withholds would be a refusal the service audits; with no schema
/// the list holds only the schemas the visitor may see, so a withheld schema never answers and nothing is audited. A schema typed
/// beside the name is compared here, in C#, against what the list returned. What the list rolls up - a hidden type's table, a
/// tenant's partition - is not in it, so cannot be found.
/// </para>
/// <para>A refusal of the whole list is no match and no note: it would say that something is behind the gate.</para>
/// </remarks>
internal sealed class RelationFindContributor : IFindContributor
{
    /// <summary>How many rows of each list Find asks for before it compares names.</summary>
    internal const int ListLimit = 50;

    private readonly IDatabaseObjectService objects;
    private readonly IStudioSettings settings;
    private readonly StudioDescriptor descriptor;

    public RelationFindContributor(IDatabaseObjectService objects, IStudioSettings settings, StudioDescriptor descriptor)
    {
        this.objects = objects;
        this.settings = settings;
        this.descriptor = descriptor;
    }

    /// <inheritdoc />
    public int Order => 50;

    /// <inheritdoc />
    public string Noun => "Tables and views";

    /// <inheritdoc />
    public async Task<FindContribution> FindAsync(StudioScope scope, FindQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        if (!query.CouldBeAnId || query.Identifier is not { } identifier)
        {
            return FindContribution.NotApplicable;
        }

        List<FindHit> hits = [];
        bool failed = false;

        foreach (DatabaseObjectCategory category in new[] { DatabaseObjectCategory.Tables, DatabaseObjectCategory.Views })
        {
            DatabaseObjectList list = await objects
                .ListAsync(scope, new DatabaseObjectQuery(category, Schema: null, NameFilter: identifier.Name, Limit: ListLimit), cancellationToken)
                .ConfigureAwait(false);

            failed |= list.Refusal == DatabaseRefusal.Unavailable;

            foreach (DatabaseObjectSummary item in list.Items)
            {
                if (!string.Equals(item.Name, identifier.Name, StringComparison.OrdinalIgnoreCase)
                    || (identifier.Schema is not null && !string.Equals(item.Schema, identifier.Schema, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                string qualified = DatabaseLinks.QualifiedName(item.Schema, item.Name);
                hits.Add(new FindHit(
                    Kind: DatabasePresentation.KindLabel(item.Kind) is { } label ? char.ToUpperInvariant(label[0]) + label[1..] : "Table",
                    Title: qualified,
                    Url: DatabaseLinks.ToObject(settings, descriptor, scope, item.Schema, item.Name),
                    Scope: scope,
                    Id: qualified,
                    Detail: item.Comment));
            }
        }

        return new FindContribution(
            hits,
            "tables and views",
            failed ? ["Tables and views: could not be searched."] : []);
    }
}
