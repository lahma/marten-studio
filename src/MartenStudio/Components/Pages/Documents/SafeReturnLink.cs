using MartenStudio.Services.Settings;

namespace MartenStudio.Components.Pages.Documents;

/// <summary>
/// Decides whether the <c>?from=</c> a link is carrying may be rendered as an <c>href</c>.
/// </summary>
/// <remarks>
/// <para>
/// The documents list puts its own URL in <c>?from=</c> so that "back" from a document returns to the same
/// page of the same filtered list. That value arrives from the query string, which means it arrives from
/// whoever wrote the link — and rendering it into an <c>href</c> unchecked is a cross-site scripting hole
/// with a very short exploit: <c>?from=javascript%3Aalert(document.domain)</c> renders
/// <c>&lt;a href="javascript:…"&gt;</c>, and a visitor who clicks "back to customer" runs it in the
/// studio's own origin, with the studio's own cookie, against a page whose whole purpose is writing to the
/// database.
/// </para>
/// <para>
/// <b>The rule is an allow-list, not a deny-list.</b> Blocking <c>javascript:</c> and hoping the list is
/// complete is how this bug comes back: <c>data:</c>, <c>vbscript:</c>, a protocol-relative <c>//evil</c>,
/// the <c>/\evil</c> that some browsers normalise to <c>//evil</c>, a percent-encoded or whitespace-split
/// scheme. What is accepted instead is exactly one shape — a relative path, below the studio's own base,
/// inside the one area the caller names (the documents list's <c>documents</c>, the database browser's
/// <c>database/object</c>) — and everything else falls back to the page's own link, which is where "back" was
/// going anyway.
/// </para>
/// </remarks>
internal static class SafeReturnLink
{
    /// <summary>
    /// The value if it is a return link into <paramref name="root" />, or <see langword="null" />: the database
    /// browser's row detail returns to <c>database/object</c>, a document to its collection under <c>documents</c>.
    /// </summary>
    /// <param name="from">The <c>?from=</c> value, as the query string delivered it.</param>
    /// <param name="settings">The studio's settings, for the mount path.</param>
    /// <param name="root">The one studio-relative area the link may point into, with no leading or trailing slash.</param>
    public static string? Sanitize(string? from, IStudioSettings settings, string root)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        if (string.IsNullOrWhiteSpace(from))
        {
            return null;
        }

        string value = from.Trim();

        // A leading separator is a root-relative path ("/evil"), a protocol-relative URL ("//evil") or
        // the backslash variant browsers fold into one ("/\evil"). None of them resolves against the
        // studio's base, which is the only thing a return link is allowed to do.
        if (value[0] is '/' or '\\')
        {
            return null;
        }

        // Backslashes anywhere: the WHATWG URL parser treats "\" as "/" inside the path of an http(s)
        // URL, so "documents\..\..\evil" walks out of the studio on the browsers that matter while
        // reading as a harmless relative path here.
        if (value.Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        foreach (char c in value)
        {
            // A tab or newline inside a scheme is stripped by the browser before the URL is parsed, so
            // "java\tscript:alert(1)" is javascript: by the time it matters.
            if (char.IsControl(c))
            {
                return null;
            }
        }

        // Anything with a scheme at all: javascript:, data:, https://evil, mailto:. A relative path
        // never parses as an absolute URI, so this is one test for the whole class.
        if (Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            return null;
        }

        string path = StudioLink.NormalizeRelativePath(value);

        // Both base shapes the studio renders under: on the interactive circuit the base URI is the
        // studio root, so the path is already studio-relative; under static rendering it still carries
        // the mount prefix. Either is fine, as long as what is left is the area asked for.
        if (StudioLink.TryStripDashboardPrefix(path, settings, out string stripped))
        {
            path = stripped;
        }

        bool inside =
            path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

        return inside ? value : null;
    }
}
