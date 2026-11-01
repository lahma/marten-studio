namespace PostgresStudio.Services.Settings;

/// <summary>
/// A studio's mount path, as every path-shaped value derives from it: rooted and trimmed, percent-encoded, and
/// whether it differs from the compile-time default.
/// </summary>
/// <remarks>
/// Shared by every studio's options type so that the rules - the default when unset, the trim, the encoding a browser
/// emits - are written once. An options type keeps one of these behind a single reference keyed by the raw path string
/// (<see cref="Of" />), so a reader on a render path either sees the old triple or the new one and never a mixture,
/// even while <c>Map*Studio(pattern)</c> writes the path after startup.
/// </remarks>
/// <param name="Source">The raw configured path this was derived from.</param>
/// <param name="Trimmed">Rooted, without a trailing slash; the default when the source is blank.</param>
/// <param name="Escaped"><paramref name="Trimmed" /> percent-encoded.</param>
/// <param name="HasCustom">Whether <paramref name="Trimmed" /> differs, ignoring case, from the default.</param>
internal sealed record StudioPath(string Source, string Trimmed, string Escaped, bool HasCustom)
{
    private static readonly char[] InvalidPathChars = ['{', '}', '?', '#'];

    /// <summary>
    /// <paramref name="cached" /> when it was derived from <paramref name="source" />, otherwise a fresh derivation.
    /// </summary>
    /// <param name="source">The configured path.</param>
    /// <param name="defaultPath">The studio's compile-time root, such as <c>/marten</c>.</param>
    /// <param name="cached">The last derivation, or <see langword="null" />.</param>
    public static StudioPath Of(string? source, string defaultPath, StudioPath? cached)
    {
        ArgumentNullException.ThrowIfNull(defaultPath);

        string raw = source ?? string.Empty;
        if (cached is not null && string.Equals(cached.Source, raw, StringComparison.Ordinal))
        {
            return cached;
        }

        string trimmed = defaultPath;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            string candidate = raw.Trim().Trim('/');
            if (candidate.Length > 0)
            {
                trimmed = "/" + candidate;
            }
        }

        string escaped = new Uri("http://localhost" + trimmed).AbsolutePath;
        bool hasCustom = !string.Equals(trimmed, defaultPath, StringComparison.OrdinalIgnoreCase);
        return new StudioPath(raw, trimmed, escaped, hasCustom);
    }

    /// <summary>
    /// Whether <paramref name="path" /> is a plain URL path: it is concatenated into route templates (where <c>{</c> and
    /// <c>}</c> would be parsed as route parameters) and percent-encoded for client-side comparisons (where <c>?</c>,
    /// <c>#</c> and <c>.</c>/<c>..</c> segments would be truncated or collapsed, diverging from the server route).
    /// </summary>
    /// <remarks>Blank, and a path of slashes only, pass: the first is reported by the "must start with '/'" rule, and the
    /// second normalizes to the default.</remarks>
    public static bool IsRoutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        string trimmed = path.Trim().Trim('/');
        if (trimmed.Length == 0)
        {
            return true;
        }

        foreach (string segment in trimmed.Split('/'))
        {
            if (segment.Length == 0
                || segment == "."
                || segment == ".."
                || segment.IndexOfAny(InvalidPathChars) >= 0)
            {
                return false;
            }
        }

        return true;
    }
}
