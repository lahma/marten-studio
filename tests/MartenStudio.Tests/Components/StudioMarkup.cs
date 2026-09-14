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
}
