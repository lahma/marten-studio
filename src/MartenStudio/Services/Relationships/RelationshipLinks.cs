using MartenStudio.Components;
using MartenStudio.Services.Documents;

using Microsoft.AspNetCore.WebUtilities;

namespace MartenStudio.Services.Relationships;

/// <summary>
/// The links the relationship screens build: to a table's object detail, and to the Relationships screen
/// itself with a view and a schema chip.
/// </summary>
/// <remarks>
/// <para>
/// <b>For consolidation.</b> The object detail route belongs to the database browser, whose link builder
/// (<c>DatabaseLinks</c>) arrives in a packet of its own. This builds the same URL with the same parameter
/// names - <c>database/object?schema=&amp;name=</c> and the scope - so that when both exist the orchestrator
/// can point <see cref="ToObject" /> at <c>DatabaseLinks</c> and delete it without a single href changing.
/// </para>
/// <para>
/// Relative, like every studio link (<see cref="StudioLink" />), and carrying the scope in the query string
/// (D9): a diagram whose nodes dropped it would send every click to the default store.
/// </para>
/// </remarks>
internal static class RelationshipLinks
{
    /// <summary>The Relationships screen's route below the studio root.</summary>
    public const string Root = "relationships";

    /// <summary>The object detail route below the studio root.</summary>
    public const string ObjectRoot = "database/object";

    /// <summary>The link to one table's object detail.</summary>
    public static string ToObject(MartenStudioOptions options, StudioScope? scope, string schema, string name)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(name);

        string url = QueryHelpers.AddQueryString(StudioLink.To(options, ObjectRoot), "schema", schema);
        url = QueryHelpers.AddQueryString(url, "name", name);

        return DocumentLinks.WithScope(url, scope);
    }

    /// <summary>The link to the Relationships screen with a view and a schema chip.</summary>
    public static string ToRelationships(
        MartenStudioOptions options,
        StudioScope? scope,
        RelationshipViewMode view,
        string? schema)
    {
        string url = DocumentLinks.WithScope(StudioLink.To(options, Root), scope);

        if (RelationshipViews.Token(view) is { } token)
        {
            url = QueryHelpers.AddQueryString(url, "view", token);
        }

        return string.IsNullOrEmpty(schema) ? url : QueryHelpers.AddQueryString(url, "schema", schema);
    }
}
