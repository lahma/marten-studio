using Microsoft.Extensions.Options;

namespace DataStudio.Services.Settings;

/// <summary>
/// The shell's <see cref="IShellSettings" />: a live adapter over <see cref="DataStudioOptions" />.
/// </summary>
/// <remarks>
/// Every member reads <see cref="IOptions{TOptions}.Value" /> again, so <c>MapDataStudio(pattern)</c>'s write of
/// <see cref="DataStudioOptions.Path" /> onto the resolved instance after startup is seen by every component that builds a link. The container's
/// <see cref="IShellSettings" /> is this object; a module's own settings answer the shell's members from it and add what is theirs.
/// </remarks>
/// <param name="options">The shell's options.</param>
internal sealed class ShellSettings(IOptions<DataStudioOptions> options) : IShellSettings
{
    /// <summary>The product's name in a sentence.</summary>
    public const string Product = "Data Studio";

    /// <summary>The master switch as a host spells it.</summary>
    public const string ReadOnlyOption = "DataStudioOptions.ReadOnly";

    private DataStudioOptions Value => options.Value;

    /// <inheritdoc />
    public string ProductName => Product;

    /// <inheritdoc />
    public string Title => Value.Title;

    /// <inheritdoc />
    public string TrimmedPath => Value.TrimmedPath;

    /// <inheritdoc />
    public string EscapedPath => Value.EscapedPath;

    /// <inheritdoc />
    public bool HasCustomPath => Value.HasCustomPath;

    /// <inheritdoc />
    public string? ApplicationPath => Value.ApplicationPath;

    /// <inheritdoc />
    public string? AuthorizationPolicy => Value.AuthorizationPolicy;

    /// <inheritdoc />
    public bool ReadOnly => Value.ReadOnly;

    /// <inheritdoc />
    public string ReadOnlyOptionName => ReadOnlyOption;

    /// <inheritdoc />
    public TimeSpan RefreshInterval => Value.RefreshInterval;
}
