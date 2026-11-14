namespace PostgresStudio.Services.Settings;

/// <summary>
/// What the studio's shell reads from a host's options: the product's name, the mount, who gets in, the master switch and how
/// often a live page polls. Nothing here is about a source, a database or a tenant.
/// </summary>
/// <remarks>
/// <b>Live, never a copy,</b> as <see cref="IStudioSettings" />'s members are: an implementation reads its options object on every
/// access. It is a singleton, and the container hands out the very object the studio's settings are
/// (<c>IShellSettings</c> and <c>IStudioSettings</c> resolve to one instance, as D30 says of the engine's interface).
/// </remarks>
internal interface IShellSettings
{
    /// <summary>The product's name in a sentence: <c>Marten Studio</c>.</summary>
    string ProductName { get; }

    /// <summary>The browser title.</summary>
    string Title { get; }

    /// <summary>The mount path, rooted and without a trailing slash: <c>/marten</c>, <c>/ops/marten</c>.</summary>
    string TrimmedPath { get; }

    /// <summary><see cref="TrimmedPath" /> percent-encoded, as a browser emits it in a request URI and a <c>&lt;base href&gt;</c>.</summary>
    string EscapedPath { get; }

    /// <summary>Whether <see cref="TrimmedPath" /> differs from the studio's compile-time default. A fact, not a mode.</summary>
    bool HasCustomPath { get; }

    /// <summary>
    /// The header's "Back to application" target, exactly as the host wrote it (a host-relative path or an absolute http(s) URL;
    /// never re-rooted under the mount), or <see langword="null" /> for no link.
    /// </summary>
    string? ApplicationPath { get; }

    /// <summary>The endpoint policy every studio endpoint requires, or <see langword="null" /> for none of its own.</summary>
    string? AuthorizationPolicy { get; }

    /// <summary>The master switch: every capability off, whatever else is set.</summary>
    bool ReadOnly { get; }

    /// <summary>The master switch's option as the host spells it: <c>MartenStudioOptions.ReadOnly</c>.</summary>
    string ReadOnlyOptionName { get; }

    /// <summary>How often a live page polls.</summary>
    TimeSpan RefreshInterval { get; }
}
