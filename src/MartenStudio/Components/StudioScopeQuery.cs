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

using MartenStudio.Services;

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Primitives;

namespace MartenStudio.Components;

/// <summary>
/// The store, database and tenant as they travel in the query string.
/// </summary>
/// <remarks>
/// Plan D9: scope lives in the URL for the same reason a document id does — a link someone pastes into a
/// chat has to reopen the same thing. The studio writes it on every scope change and reads it back on
/// load; anything the visitor's own listings do not carry is ignored rather than reported, because the
/// values arrive from a URL and a URL is something anyone can type.
/// </remarks>
internal static class StudioScopeQuery
{
    /// <summary>The query-string name of the store key.</summary>
    internal const string StoreKeyParameter = "store";

    /// <summary>The query-string name of Marten's database identity.</summary>
    internal const string DatabaseIdParameter = "db";

    /// <summary>The query-string name of the tenant id.</summary>
    internal const string TenantIdParameter = "tenant";

    /// <summary>
    /// The scope named by <paramref name="uri" />'s query string, each part <see langword="null" /> when
    /// it is absent or blank.
    /// </summary>
    internal static (string? StoreKey, string? DatabaseId, string? TenantId) Read(string uri)
    {
        int queryStart = uri.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0)
        {
            return (null, null, null);
        }

        int fragmentStart = uri.IndexOf('#', queryStart);
        string query = fragmentStart < 0 ? uri[queryStart..] : uri[queryStart..fragmentStart];

        Dictionary<string, StringValues> parsed = QueryHelpers.ParseQuery(query);
        return (Single(parsed, StoreKeyParameter), Single(parsed, DatabaseIdParameter), Single(parsed, TenantIdParameter));
    }

    /// <summary>
    /// The current URL with <paramref name="scope" /> written into its query string, leaving every other
    /// parameter alone. A part the scope does not have is removed rather than written empty.
    /// </summary>
    internal static string WithScope(NavigationManager navigation, StudioScope? scope)
    {
        ArgumentNullException.ThrowIfNull(navigation);

        return navigation.GetUriWithQueryParameters(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [StoreKeyParameter] = NullIfBlank(scope?.StoreKey),
            [DatabaseIdParameter] = NullIfBlank(scope?.DatabaseId),
            [TenantIdParameter] = NullIfBlank(scope?.TenantId)
        });
    }

    private static string? Single(Dictionary<string, StringValues> parsed, string name) =>
        parsed.TryGetValue(name, out StringValues values) ? NullIfBlank(values.ToString()) : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
