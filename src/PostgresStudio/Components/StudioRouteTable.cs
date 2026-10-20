#region License
/*
 * All content copyright Marko Lahma, unless otherwise indicated. All rights reserved.
 *
 * Licensed under the Apache License, Version 2.0 (the "License"); you may not
 * use this file except in compliance with the License. You may obtain a copy
 * of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS, WITHOUT
 * WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the
 * License for the specific language governing permissions and limitations
 * under the License.
 *
 */
#endregion

using System.Reflection;

using PostgresStudio.Services.Settings;

using Microsoft.AspNetCore.Components;

namespace PostgresStudio.Components;

/// <summary>
/// Matches dashboard URLs against the route templates declared by one studio's page components.
/// The standalone dashboard root uses this instead of the built-in Blazor router so the studio
/// can be served from a configurable mount path; the built-in router can only match the
/// compile-time templates (<c>/marten/...</c>) against the browser URL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built from a list of (assembly, compile-time root) pairs</b> - <see cref="StudioDescriptor.PageAssemblies" /> -
/// rather than from this assembly alone, because a studio's pages may live in more than one assembly and each
/// assembly declares its own root. Every template is stored relative to its assembly's root, so two assemblies'
/// pages share one relative route space.
/// </para>
/// <para>
/// <b>Two failures are thrown when the table is first built, not discovered at request time.</b> A template not
/// under its assembly's root would get a server-side endpoint from <c>MapRazorComponents</c> but could never be
/// matched here, silently breaking interactive navigation to it. A relative route two pages claim - in one
/// assembly or across two - would be two endpoints on one pattern, an <c>AmbiguousMatchException</c> on the first
/// request for it, and a page this table would resolve to whichever it saw first.
/// </para>
/// </remarks>
internal sealed class StudioRouteTable
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyRouteValues =
        new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

    private readonly List<RouteEntry> routes;

    /// <summary>Builds the table, validating every page template.</summary>
    /// <param name="pageAssemblies">The assemblies whose routable components are pages, each with its root.</param>
    /// <exception cref="InvalidOperationException">
    /// A template is not under its assembly's root, or two templates claim the same relative route.
    /// </exception>
    public StudioRouteTable(IEnumerable<StudioPageAssembly> pageAssemblies)
        : this(Pages(pageAssemblies))
    {
    }

    /// <summary>Builds the table from explicit components, each with the compile-time root its templates must start with.</summary>
    /// <param name="pages">The routable components, each with its assembly's root.</param>
    /// <exception cref="InvalidOperationException">
    /// A template is not under its root, or two templates claim the same relative route.
    /// </exception>
    internal StudioRouteTable(IEnumerable<(Type Page, string CompileTimeRoot)> pages)
    {
        ArgumentNullException.ThrowIfNull(pages);

        routes = Build(pages);
    }

    private static IEnumerable<(Type Page, string CompileTimeRoot)> Pages(IEnumerable<StudioPageAssembly> pageAssemblies)
    {
        ArgumentNullException.ThrowIfNull(pageAssemblies);

        foreach (StudioPageAssembly pageAssembly in pageAssemblies)
        {
            foreach (Type type in pageAssembly.Assembly.GetTypes())
            {
                if (typeof(IComponent).IsAssignableFrom(type))
                {
                    yield return (type, pageAssembly.CompileTimeRoot);
                }
            }
        }
    }

    /// <summary>
    /// Matches like <see cref="Match(string)"/> but retries with the studio path prefix
    /// stripped when the direct match fails. The relative path form is ambiguous when the
    /// application path base happens to end with the custom dashboard path: static server-side
    /// rendering then produces a dashboard-prefixed relative path that the base URI shape check
    /// in <see cref="StudioLink.ToDashboardRelativePath"/> cannot distinguish from an
    /// interactive leaf, so a failed match gets a second chance without the prefix.
    /// </summary>
    internal RouteData? Match(string dashboardRelativePath, IStudioSettings settings)
    {
        return Match(ResolveLeaf(dashboardRelativePath, settings));
    }

    /// <summary>
    /// Resolves a dashboard-relative path to the leaf that actually maps to a dashboard page,
    /// applying the same prefix-strip retry as <see cref="Match(string, IStudioSettings)"/>.
    /// Used where the leaf itself is needed (navigation highlighting) rather than the route data.
    /// Returns the direct form when neither the direct nor the stripped path maps to a page.
    /// </summary>
    internal string ResolveLeaf(string dashboardRelativePath, IStudioSettings settings)
    {
        string normalized = StudioLink.NormalizeRelativePath(dashboardRelativePath);
        if (Match(normalized) is not null)
        {
            return normalized;
        }

        if (StudioLink.TryStripDashboardPrefix(normalized, settings, out string stripped) && Match(stripped) is not null)
        {
            return stripped;
        }

        return normalized;
    }

    /// <summary>
    /// Matches a dashboard-root-relative path ("" for the studio root, no leading slash, as
    /// produced by <see cref="StudioLink.ToDashboardRelativePath"/>) against the studio
    /// route table. Returns <see langword="null"/> when the path does not map to a dashboard page.
    /// </summary>
    internal RouteData? Match(string dashboardRelativePath)
    {
        dashboardRelativePath = StudioLink.NormalizeRelativePath(dashboardRelativePath);

        string[] segments = dashboardRelativePath.Length == 0 ? [] : dashboardRelativePath.Split('/');

        foreach (RouteEntry route in routes)
        {
            if (TryMatch(route, segments, out IReadOnlyDictionary<string, object?>? routeValues))
            {
                return new RouteData(route.PageType, routeValues);
            }
        }

        return null;
    }

    private static bool TryMatch(RouteEntry route, string[] segments, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IReadOnlyDictionary<string, object?>? routeValues)
    {
        routeValues = null;

        if (route.Segments.Length != segments.Length)
        {
            return false;
        }

        // Allocate the value dictionary only once a parameter segment is reached, so
        // non-matching literal routes of the same length don't allocate at all
        Dictionary<string, object?>? values = null;
        for (int i = 0; i < segments.Length; i++)
        {
            string templateSegment = route.Segments[i];
            if (templateSegment.Length > 1 && templateSegment[0] == '{' && templateSegment[^1] == '}')
            {
                string parameterName = templateSegment[1..^1];
                values ??= new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                values[parameterName] = Uri.UnescapeDataString(segments[i]);
            }
            else if (!string.Equals(templateSegment, segments[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        routeValues = values ?? EmptyRouteValues;
        return true;
    }

    private static List<RouteEntry> Build(IEnumerable<(Type Page, string CompileTimeRoot)> pages)
    {
        List<RouteEntry> built = [];

        // Keyed by the relative template with every parameter name blanked, ignoring case: "/a/{id}" and "/A/{key}"
        // are one route to the endpoint router and to this table alike.
        Dictionary<string, Type> claimed = new(StringComparer.OrdinalIgnoreCase);

        foreach ((Type type, string root) in pages)
        {
            foreach (RouteAttribute attribute in type.GetCustomAttributes<RouteAttribute>())
            {
                string template = attribute.Template;
                if (!template.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    || (template.Length > root.Length && template[root.Length] != '/'))
                {
                    // Fail fast: such a page would get a server-side endpoint from MapRazorComponents
                    // but could never be matched here, silently breaking interactive navigation to it
                    throw new InvalidOperationException(
                        $"Dashboard page component '{type.FullName}' declares route template '{template}' which is not rooted at " +
                        $"'{root}'. All dashboard page templates in {type.Assembly.GetName().Name} must start with '{root}'.");
                }

                string remainder = template.Substring(root.Length);
                string[] segments = remainder.Length == 0 ? [] : remainder.TrimStart('/').Split('/');

                string shape = string.Join('/', segments.Select(static x => x.Length > 1 && x[0] == '{' ? "{}" : x));
                if (claimed.TryGetValue(shape, out Type? first) && first != type)
                {
                    throw new InvalidOperationException(
                        $"Dashboard page components '{first.FullName}' and '{type.FullName}' both claim the route '/{string.Join('/', segments)}' " +
                        "below the studio root. Each relative route belongs to exactly one page, whichever assembly declares it.");
                }

                claimed[shape] = type;
                built.Add(new RouteEntry(type, segments));
            }
        }

        // Deterministic precedence: shorter routes first, literal segments before parameters
        built.Sort(static (left, right) =>
        {
            int byLength = left.Segments.Length.CompareTo(right.Segments.Length);
            return byLength != 0 ? byLength : CountParameters(left).CompareTo(CountParameters(right));
        });

        return built;

        static int CountParameters(RouteEntry route)
        {
            int count = 0;
            foreach (string segment in route.Segments)
            {
                if (segment.Length > 0 && segment[0] == '{')
                {
                    count++;
                }
            }

            return count;
        }
    }

    private sealed record RouteEntry(Type PageType, string[] Segments);
}
