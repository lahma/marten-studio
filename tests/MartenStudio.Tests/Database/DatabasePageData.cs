using Bunit;

using MartenStudio.Services.Database;
using MartenStudio.Tests.Components;
using MartenStudio.Tests.Support;

namespace MartenStudio.Tests.Database;

/// <summary>
/// What the database browser's page tests need beyond <see cref="FakeDatabaseObjects" />: a context that is
/// ready to render a page, and the handful of shapes that sample does not carry - a comment, an unlogged
/// table, row-level security, a sequence the role may not read, a truncated list.
/// </summary>
/// <remarks>
/// Built from the same DTOs the service returns, so a page test drives the shapes a live page gets. The
/// shared sample stays the one place most tests read from; this is only what one or two tests say outright.
/// </remarks>
internal static class DatabasePageData
{
    /// <summary>A context with the default store listed, so the pages settle on a scope.</summary>
    public static StudioComponentContext Context()
    {
        StudioComponentContext context = new();
        context.WithStores("default");
        return context;
    }

    /// <summary>Renders the browser at <paramref name="query" />, the way the address bar would deliver it.</summary>
    public static IRenderedComponent<MartenStudio.Components.Pages.Database.DatabaseObjects> RenderBrowser(
        StudioComponentContext context,
        string query = "")
    {
        context.Navigate("marten/database" + (query.Length == 0 ? string.Empty : "?" + query));

        var page = context.Render<MartenStudio.Components.Pages.Database.DatabaseObjects>();
        page.WaitForAssertion(() => page.FindAll(".ms-loading-spinner").Should().BeEmpty());
        return page;
    }

    /// <summary>Renders object detail for one relation, on one tab or the page's default.</summary>
    public static IRenderedComponent<MartenStudio.Components.Pages.Database.ObjectDetail> RenderObject(
        StudioComponentContext context,
        string schema,
        string name,
        string? tab = null)
    {
        context.Navigate(
            "marten/database/object?schema=" + Uri.EscapeDataString(schema) + "&name=" + Uri.EscapeDataString(name)
            + (tab is null ? string.Empty : "&tab=" + tab));

        var page = context.Render<MartenStudio.Components.Pages.Database.ObjectDetail>();
        page.WaitForAssertion(() => page.FindAll(".ms-db-object-header, .ms-empty, .ms-error-alert").Should().NotBeEmpty());
        return page;
    }

    /// <summary>A list of exactly these objects, as the service would answer it.</summary>
    public static DatabaseObjectList ListOf(
        DatabaseObjectCategory category,
        IReadOnlyList<DatabaseObjectSummary> items,
        bool truncated = false,
        DatabaseRefusal refusal = DatabaseRefusal.None)
    {
        int marten = items.Count(static x => x.Ownership.IsMarten);

        return new DatabaseObjectList(
            new DatabaseObjectQuery(category, Limit: items.Count),
            items,
            new DatabaseOwnerCounts(items.Count, marten, items.Count - marten),
            truncated,
            items.Count,
            FakeDatabaseObjects.Access(refusal),
            DatabaseRefusal.None,
            null);
    }

    /// <summary>One table, with every flag the sample leaves at its default said here instead.</summary>
    public static DatabaseRelationSummary Table(
        string schema,
        string name,
        DatabaseObjectOwnership? owner = null,
        DatabaseObjectKind kind = DatabaseObjectKind.Table,
        long? estimatedRows = 100,
        string? comment = null,
        bool unlogged = false,
        bool rowSecurity = false,
        bool hasPrimaryKey = true) =>
        new(
            kind,
            schema,
            name,
            owner ?? new DatabaseObjectOwnership(DatabaseObjectOwner.Other),
            comment,
            true,
            estimatedRows,
            8192,
            hasPrimaryKey,
            0,
            0,
            0,
            unlogged,
            true,
            rowSecurity,
            null,
            true,
            DatabaseRowAccess.Granted,
            kind is DatabaseObjectKind.View or DatabaseObjectKind.MaterializedView);

    /// <summary>A sequence, readable or not.</summary>
    public static DatabaseSequenceSummary Sequence(string schema, string name, bool canReadValue, long? lastValue = null) =>
        new(
            DatabaseObjectKind.Sequence,
            schema,
            name,
            new DatabaseObjectOwnership(DatabaseObjectOwner.Other),
            null,
            true,
            "bigint",
            1,
            1,
            1,
            long.MaxValue,
            false,
            canReadValue,
            lastValue,
            null,
            null,
            null,
            false,
            0);
}
