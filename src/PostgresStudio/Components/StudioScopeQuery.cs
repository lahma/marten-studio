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

using PostgresStudio.Services;
using PostgresStudio.Services.Settings;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace PostgresStudio.Components;

/// <summary>
/// The source, database and tenant as they travel in the query string.
/// </summary>
/// <remarks>
/// <para>
/// Plan D9: scope lives in the URL for the same reason a document id does — a link someone pastes into a
/// chat has to reopen the same thing. The studio writes it on every scope change and reads it back on
/// load; anything the visitor's own listings do not carry is ignored rather than reported, because the
/// values arrive from a URL and a URL is something anyone can type.
/// </para>
/// <para>
/// The source key's parameter is the studio's own (<see cref="StudioDescriptor.SourceQueryParameter" />:
/// <c>store</c> in MartenStudio); the database and the tenant are spelled the same in every studio.
/// </para>
/// </remarks>
internal static class StudioScopeQuery
{
    /// <summary>The query-string name of the database identity.</summary>
    internal const string DatabaseIdParameter = "db";

    /// <summary>The query-string name of the tenant id.</summary>
    internal const string TenantIdParameter = "tenant";

    /// <summary>
    /// The scope named by <paramref name="uri" />'s query string, each part <see langword="null" /> when
    /// it is absent or blank.
    /// </summary>
    /// <param name="uri">The URI to read.</param>
    /// <param name="sourceParameter">The studio's source parameter (<see cref="StudioDescriptor.SourceQueryParameter" />).</param>
    internal static (string? StoreKey, string? DatabaseId, string? TenantId) Read(string uri, string sourceParameter)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceParameter);

        int queryStart = uri.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return (null, null, null);
        }

        int fragmentStart = uri.IndexOf('#', queryStart);
        string query = fragmentStart < 0 ? uri[queryStart..] : uri[queryStart..fragmentStart];

        Dictionary<string, StringValues> parsed = QueryHelpers.ParseQuery(query);
        return (Single(parsed, sourceParameter), Single(parsed, DatabaseIdParameter), Single(parsed, TenantIdParameter));
    }

    /// <summary>
    /// The current URL with <paramref name="scope" /> written into its query string, leaving every other
    /// parameter alone. A part the scope does not have is removed rather than written empty.
    /// </summary>
    /// <param name="navigation">The circuit's navigation manager.</param>
    /// <param name="scope">The scope to write, or <see langword="null" /> to remove all three.</param>
    /// <param name="sourceParameter">The studio's source parameter (<see cref="StudioDescriptor.SourceQueryParameter" />).</param>
    internal static string WithScope(NavigationManager navigation, StudioScope? scope, string sourceParameter)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceParameter);

        return navigation.GetUriWithQueryParameters(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [sourceParameter] = NullIfBlank(scope?.StoreKey),
            [DatabaseIdParameter] = NullIfBlank(scope?.DatabaseId),
            [TenantIdParameter] = NullIfBlank(scope?.TenantId)
        });
    }

    /// <summary>
    /// <paramref name="url" /> with the source, database and tenant of <paramref name="scope" /> appended -
    /// each only when the scope has it - for a link built rather than navigated to.
    /// </summary>
    /// <param name="url">The link, which may already carry a query string.</param>
    /// <param name="scope">The scope to carry, or <see langword="null" /> for none.</param>
    /// <param name="sourceParameter">The studio's source parameter (<see cref="StudioDescriptor.SourceQueryParameter" />).</param>
    internal static string Append(string url, StudioScope? scope, string sourceParameter)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceParameter);

        if (scope is null)
        {
            return url;
        }

        if (!string.IsNullOrEmpty(scope.StoreKey))
        {
            url = QueryHelpers.AddQueryString(url, sourceParameter, scope.StoreKey);
        }

        if (!string.IsNullOrEmpty(scope.DatabaseId))
        {
            url = QueryHelpers.AddQueryString(url, DatabaseIdParameter, scope.DatabaseId);
        }

        if (!string.IsNullOrEmpty(scope.TenantId))
        {
            url = QueryHelpers.AddQueryString(url, TenantIdParameter, scope.TenantId);
        }

        return url;
    }

    private static string? Single(Dictionary<string, StringValues> parsed, string name) =>
        parsed.TryGetValue(name, out StringValues values) ? NullIfBlank(values.ToString()) : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
