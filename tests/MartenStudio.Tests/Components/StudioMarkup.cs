using AngleSharp.Dom;

using Bunit;

using Microsoft.AspNetCore.Components;

namespace MartenStudio.Tests.Components;

/// <summary>
/// Readers for the markup shapes the studio's shared components render, so a test says what it is about
/// rather than repeating a CSS selector - and fails with a sentence rather than a null reference.
/// </summary>
public static class StudioMarkup
{
    /// <summary>The value shown on the stat card with the given title.</summary>
    public static string StatCardValue<TComponent>(this IRenderedComponent<TComponent> component, string title)
        where TComponent : IComponent =>
        StatCard(component, title).QuerySelector(".ms-stat-card-value")?.TextContent.Trim() ?? string.Empty;

    /// <summary>The classes on the stat card with the given title, which carry its colour.</summary>
    public static ITokenList StatCardClasses<TComponent>(this IRenderedComponent<TComponent> component, string title)
        where TComponent : IComponent =>
        StatCard(component, title).ClassList;

    private static IElement StatCard<TComponent>(IRenderedComponent<TComponent> component, string title)
        where TComponent : IComponent
    {
        foreach (IElement card in component.FindAll(".ms-stat-card"))
        {
            if (string.Equals(card.QuerySelector(".ms-stat-card-title")?.TextContent.Trim(), title, StringComparison.Ordinal))
            {
                return card;
            }
        }

        throw new InvalidOperationException(
            $"The page rendered no stat card titled '{title}'. It rendered: " +
            string.Join(", ", component.FindAll(".ms-stat-card-title").Select(x => x.TextContent.Trim())));
    }

    /// <summary>The value of the key/value row with the given key.</summary>
    public static string KeyValue<TComponent>(this IRenderedComponent<TComponent> component, string key)
        where TComponent : IComponent
    {
        foreach (IElement row in component.FindAll(".ms-kv-row"))
        {
            if (string.Equals(row.QuerySelector(".ms-kv-key")?.TextContent.Trim(), key, StringComparison.Ordinal))
            {
                return row.QuerySelector(".ms-kv-value")?.TextContent.Trim() ?? string.Empty;
            }
        }

        throw new InvalidOperationException(
            $"The page rendered no key/value row named '{key}'. It rendered: " +
            string.Join(", ", component.FindAll(".ms-kv-key").Select(x => x.TextContent.Trim())));
    }

    /// <summary>The <c>select</c> with the given id, or <see langword="null" /> when it is not rendered.</summary>
    public static IElement? Selector<TComponent>(this IRenderedComponent<TComponent> component, string id)
        where TComponent : IComponent =>
        component.Nodes.QuerySelector("#" + id);

    /// <summary>The option values of the <c>select</c> with the given id.</summary>
    public static List<string> SelectorOptions<TComponent>(this IRenderedComponent<TComponent> component, string id)
        where TComponent : IComponent
    {
        IElement select = component.Selector(id)
            ?? throw new InvalidOperationException($"The page rendered no selector with id '{id}'.");

        return [.. select.QuerySelectorAll("option").Select(x => x.GetAttribute("value") ?? string.Empty)];
    }

    /// <summary>The text of every element matching <paramref name="selector" />, trimmed.</summary>
    public static List<string> TextOfAll<TComponent>(this IRenderedComponent<TComponent> component, string selector)
        where TComponent : IComponent =>
        [.. component.FindAll(selector).Select(x => x.TextContent.Trim())];

    /// <summary>Whether the page rendered the theme root with this <c>data-theme</c>.</summary>
    public static string ThemeAttribute<TComponent>(this IRenderedComponent<TComponent> component)
        where TComponent : IComponent =>
        component.Find(".ms-studio").GetAttribute("data-theme") ?? string.Empty;

    /// <summary>
    /// Every data table on the page is inside a scrollable region that a keyboard can reach and a
    /// screen reader can name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>.ms-table</c> is <c>width: 100%</c> and <c>.ms-main</c> hides horizontal overflow, so a
    /// table wider than the content column is not scrolled - it is <em>cut off</em>, with no scrollbar
    /// and no way at all to reach the columns on the right. That was true of every table in the studio
    /// but the documents list, and on the event-types screen it hid four of six columns at 1440px.
    /// </para>
    /// <para>
    /// The region is the fix, and it is asserted here rather than in one test per page because the
    /// rule is "every table", and a rule that is only checked where somebody remembered to check it is
    /// the rule that comes back. <c>.ms-table-wrap</c> counts: it is the documents list's own scroll
    /// container and shares the same stylesheet rules.
    /// </para>
    /// </remarks>
    public static void ShouldPutEveryTableInALabelledScrollRegion<TComponent>(this IRenderedComponent<TComponent> component)
        where TComponent : IComponent
    {
        IReadOnlyList<IElement> tables = component.FindAll("table.ms-table");

        tables.Should().NotBeEmpty(
            "a page test that asserts the scroll regions has to have rendered a table for there to be one");

        foreach (IElement table in tables)
        {
            table.ShouldBeInsideALabelledScrollRegion();
        }
    }

    /// <summary>
    /// This one table is inside a scrollable region that a keyboard can reach and a screen reader can
    /// name.
    /// </summary>
    /// <remarks>
    /// The single-table form, for a page that also renders a table another packet owns and this one may
    /// not touch.
    /// </remarks>
    public static void ShouldBeInsideALabelledScrollRegion(this IElement table)
    {
        IElement region = ScrollRegionAround(table)
            ?? throw new InvalidOperationException(
                "This table is in no scroll region, so a viewport narrower than it simply clips it: "
                + Describe(table));

        region.GetAttribute("role").Should().Be("region", "a scrollable box has to be announced as one");
        region.GetAttribute("tabindex").Should().Be("0", "a region only a mouse can scroll is content a keyboard cannot reach");
        region.GetAttribute("aria-label").Should().NotBeNullOrWhiteSpace(
            "an unnamed region is one a screen reader drops the visitor into with nothing to say about it");
    }

    /// <summary>The nearest scrollable ancestor of <paramref name="table" />, or <see langword="null" />.</summary>
    private static IElement? ScrollRegionAround(IElement table)
    {
        for (IElement? ancestor = table.ParentElement; ancestor is not null; ancestor = ancestor.ParentElement)
        {
            if (ancestor.ClassList.Contains("ms-table-scroll") || ancestor.ClassList.Contains("ms-table-wrap"))
            {
                return ancestor;
            }
        }

        return null;
    }

    /// <summary>Enough of a table to find it in the markup when the assertion above fails.</summary>
    private static string Describe(IElement table)
    {
        string classes = table.GetAttribute("class") ?? string.Empty;
        string headers = string.Join(", ", table.QuerySelectorAll("thead th").Select(x => x.TextContent.Trim()));
        return $"<table class=\"{classes}\"> with the columns {headers}";
    }
}
