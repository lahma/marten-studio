using System.Text.RegularExpressions;

using Npgsql;

using PostgresStudio.Services.Sources;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// One source a host named in <see cref="PostgresStudioOptions" />: a data source by <c>AddDataSource</c>, or a set of
/// databases by <c>AddDatabaseSource</c> - its name, and the factory that returns the application's own instance.
/// </summary>
/// <remarks>The unkeyed DI data source, <c>default</c>, is not one of these: it is found in the container.</remarks>
/// <param name="Name">The source's key.</param>
/// <param name="Kind">What it is.</param>
/// <param name="DataSource">For <see cref="StudioSourceKind.DataSource" />: the factory.</param>
/// <param name="DatabaseSource">For <see cref="StudioSourceKind.HostProvider" />: the factory.</param>
internal sealed record PostgresSourceDefinition(
    string Name,
    StudioSourceKind Kind,
    Func<IServiceProvider, NpgsqlDataSource>? DataSource,
    Func<IServiceProvider, IPostgresDatabaseSource>? DatabaseSource)
{
    /// <summary>A data source named by <c>AddDataSource</c>.</summary>
    public static PostgresSourceDefinition ForDataSource(string name, Func<IServiceProvider, NpgsqlDataSource> resolve) =>
        new(name, StudioSourceKind.DataSource, resolve, null);

    /// <summary>A set of databases named by <c>AddDatabaseSource</c>.</summary>
    public static PostgresSourceDefinition ForDatabaseSource(string name, Func<IServiceProvider, IPostgresDatabaseSource> resolve) =>
        new(name, StudioSourceKind.HostProvider, null, resolve);
}

/// <summary>The rules a source's key and a host-listed database's name are held to.</summary>
internal static partial class PostgresSourceNames
{
    /// <summary>The key of the application's own unkeyed data source, reserved for it.</summary>
    public const string Default = "default";

    /// <summary>
    /// A source key: a letter or digit, then up to 63 letters, digits, dots, underscores or hyphens - what a URL carries
    /// in <c>?store=</c> without escaping, and never a <c>|</c> (a cache key's separator) or a <c>/</c>.
    /// </summary>
    public static bool IsValidKey(string? name) => name is not null && KeyPattern().IsMatch(name);

    /// <summary>A host-listed database's name: as a key, up to 128 characters.</summary>
    public static bool IsValidDatabaseName(string? name) => name is not null && DatabaseNamePattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex DatabaseNamePattern();
}
