using DataStudio.Services.Settings;

namespace DataStudio;

/// <summary>
/// Options for the Data Studio shell, configured through <c>AddDataStudio(Action&lt;DataStudioOptions&gt;)</c>: where it is mounted, what it
/// is called, who gets in, whether it is read-only and how often a live page polls. A module's own options (a database, a Kafka cluster) are
/// that module's. Validation runs when the options are first read, which <c>MapDataStudio()</c> does at startup.
/// </summary>
/// <remarks>
/// Nothing here is about a source, a database, a topic or a tenant: a module that needs one asks for it in its own options, and the shell's
/// <see cref="ReadOnly" /> turns every module's capabilities off.
/// </remarks>
public sealed class DataStudioOptions
{
    internal const string DefaultPath = "/studio";

    /// <summary>
    /// The path the studio is mounted at. Must start with <c>/</c> and be a plain routable path (no route parameters, query or fragment).
    /// <c>MapDataStudio(pattern)</c> overrides it. Defaults to <c>/studio</c>.
    /// </summary>
    public string Path { get; set; } = DefaultPath;

    /// <summary>The title shown in the browser tab and the header.</summary>
    public string Title { get; set; } = "Data Studio";

    /// <summary>
    /// Where the header's "Back to application" link goes, or <see langword="null" /> (the default) for no link. Either a
    /// host-relative path that starts with a single <c>/</c> (<c>/</c>, <c>/app</c>) or an absolute <c>http</c> or
    /// <c>https</c> URL without user information. It is used exactly as written and is never re-rooted under the studio's mount
    /// path, so <c>/</c> is the host's own front page, not the studio's. A host that runs behind <c>UsePathBase</c> includes its
    /// base (<c>/myapp/</c>): the option is not rewritten for it. Validated at startup.
    /// </summary>
    public string? ApplicationPath { get; set; }

    /// <summary>
    /// When set, the policy applied to every studio endpoint: pages, the Blazor circuit and the static assets. When
    /// <see langword="null" /> the studio adds no policy of its own and refuses to start unless the mapping calls
    /// <c>RequireAuthorization()</c> or <c>AllowAnonymous()</c>, or the host has an <c>AuthorizationOptions.FallbackPolicy</c>.
    /// </summary>
    public string? AuthorizationPolicy { get; set; }

    /// <summary>
    /// Master switch: when <see langword="true" /> every capability of every module is off, whatever the module's own options say. A module
    /// has a switch of its own that turns only its capabilities off.
    /// </summary>
    public bool ReadOnly { get; set; }

    /// <summary>
    /// How often live pages poll for changes. Between one second and five minutes. Polling pauses while the tab is hidden.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// <see cref="Path" /> normalized to a rooted path without a trailing slash, falling back to <see cref="DefaultPath" /> when unset or empty.
    /// </summary>
    internal string TrimmedPath => PathCache.Trimmed;

    /// <summary>Whether <see cref="Path" /> differs from the compile-time default <c>/studio</c>. A fact, and nothing behaves differently on it.</summary>
    internal bool HasCustomPath => PathCache.HasCustom;

    /// <summary><see cref="TrimmedPath" /> percent-encoded, as browsers emit it in request URIs and the &lt;base href&gt;.</summary>
    internal string EscapedPath => PathCache.Escaped;

    private StudioPath? pathCache;

    /// <summary>
    /// Values derived from <see cref="Path" />, computed once per value and held behind one reference, so a concurrent reader always
    /// observes a fully populated instance even if the option is written mid-render (<c>MapDataStudio(pattern)</c> writes it after startup).
    /// </summary>
    private StudioPath PathCache
    {
        get
        {
            StudioPath cache = StudioPath.Of(Path, DefaultPath, pathCache);
            pathCache = cache;
            return cache;
        }
    }
}
