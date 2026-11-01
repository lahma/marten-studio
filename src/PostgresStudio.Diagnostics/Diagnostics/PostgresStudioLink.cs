namespace PostgresStudio;

/// <summary>A place in the studio, relative to where the host mapped it.</summary>
/// <remarks>
/// <see cref="Path" /> has no leading slash and carries the scope query (<c>?store=…&amp;db=…</c>), so
/// <c>$"{options.Path}/{link.Path}"</c> - the <c>Path</c> the host passed to its studio options - opens it. The route and
/// query are the studio's own and may change between releases; take them from here rather than building them.
/// </remarks>
public sealed record PostgresStudioLink
{
    /// <summary>What the link says, in English (<c>Open the index advisor</c>).</summary>
    public string Label { get; init; } = "";

    /// <summary>The page and its query, relative to the studio's mount path, with no leading slash.</summary>
    public string Path { get; init; } = "";
}
