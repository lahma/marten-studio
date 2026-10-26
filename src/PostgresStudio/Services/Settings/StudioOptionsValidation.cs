using System.Text.RegularExpressions;

using Microsoft.Extensions.Options;

using PostgresStudio.Services.Database;

namespace PostgresStudio.Services.Settings;

/// <summary>
/// The options both studios share, as one studio's options type spells them - what the shared validation reads.
/// </summary>
/// <param name="Path">The mount path.</param>
/// <param name="DefaultPageSize">The page size a grid opens with.</param>
/// <param name="MaxPageSize">The largest page size.</param>
/// <param name="QueryTimeout">The statement timeout.</param>
/// <param name="MaxInlineBytes">The inline-value cap: MartenStudio's <c>MaxInlineDocumentBytes</c>, PostgresStudio's <c>MaxInlineValueBytes</c>.</param>
/// <param name="MaxSqlConsoleRows">The console's row cap.</param>
/// <param name="SqlConsoleRole">The console's role.</param>
/// <param name="ExactCountThreshold">The exact-count threshold.</param>
/// <param name="RefreshInterval">The poll interval.</param>
/// <param name="HealthInterval">The health interval.</param>
/// <param name="HasCapabilities">Whether the capabilities object is set.</param>
/// <param name="BrowsableSchemas">The browsable schemas.</param>
internal sealed record StudioOptionsShape(
    string? Path,
    int DefaultPageSize,
    int MaxPageSize,
    TimeSpan QueryTimeout,
    int MaxInlineBytes,
    int MaxSqlConsoleRows,
    string? SqlConsoleRole,
    long ExactCountThreshold,
    TimeSpan RefreshInterval,
    TimeSpan HealthInterval,
    bool HasCapabilities,
    IEnumerable<string> BrowsableSchemas);

/// <summary>
/// One validation chain for the options both studios have - the same bounds, and sentences that differ only in the type
/// and member names a host's own registration spells - so the two studios cannot drift apart (design §5.1).
/// </summary>
/// <remarks>
/// In three parts, because a studio interleaves validations of its own between them and the failures an
/// <c>OptionsValidationException</c> lists are in registration order: MartenStudio's rebuild timeout sits between the
/// first part and the capabilities, and its tenant ids between the capabilities and the schemas, exactly where they have
/// always been - so its messages, and their order, are unchanged.
/// </remarks>
internal static partial class StudioOptionsValidation
{
    /// <summary>The path, the page sizes, the timeouts, the inline cap, the console's rows and role, the count threshold and the intervals.</summary>
    /// <param name="builder">The studio's options builder.</param>
    /// <param name="optionsType">The options type's name: <c>MartenStudioOptions</c>.</param>
    /// <param name="inlineMember">The inline cap's member name: <c>MaxInlineDocumentBytes</c>.</param>
    /// <param name="shape">The shared options of one instance.</param>
    public static OptionsBuilder<TOptions> ValidateSharedBounds<TOptions>(
        this OptionsBuilder<TOptions> builder,
        string optionsType,
        string inlineMember,
        Func<TOptions, StudioOptionsShape> shape)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(shape);

        return builder
            .Validate(
                options => shape(options).Path is { } path && !string.IsNullOrWhiteSpace(path) && path.StartsWith('/'),
                optionsType + ".Path must start with '/'")
            .Validate(
                options => StudioPath.IsRoutable(shape(options).Path),
                optionsType + ".Path must be a simple URL path: it cannot contain '{', '}', '?', '#', '.' or '..' segments, or empty segments ('//')")
            .Validate(
                options => shape(options) is { } x && x.DefaultPageSize >= 1 && x.DefaultPageSize <= x.MaxPageSize,
                optionsType + ".DefaultPageSize must be between 1 and MaxPageSize")
            .Validate(
                options => shape(options).MaxPageSize is >= 1 and <= 5000,
                optionsType + ".MaxPageSize must be between 1 and 5000: a larger page is a sequential scan rendered into a browser")
            .Validate(
                options => shape(options).QueryTimeout is var timeout && timeout >= TimeSpan.FromSeconds(1) && timeout <= TimeSpan.FromMinutes(10),
                optionsType + ".QueryTimeout must be between one second and ten minutes")
            .Validate(
                options => shape(options).MaxInlineBytes is > 0 and <= 64 * 1024 * 1024,
                optionsType + "." + inlineMember + " must be between 1 and 64 MiB")
            .Validate(
                options => shape(options).MaxSqlConsoleRows is >= 1 and <= 10_000,
                optionsType + ".MaxSqlConsoleRows must be between 1 and 10000")
            .Validate(
                options => shape(options).SqlConsoleRole is not { } role || PostgresRoleName().IsMatch(role),
                optionsType + ".SqlConsoleRole must be a plain Postgres identifier: it is written into SET LOCAL ROLE, where it cannot be a parameter")
            .Validate(
                options => shape(options).ExactCountThreshold >= 0,
                optionsType + ".ExactCountThreshold cannot be negative")
            .Validate(
                options => shape(options).RefreshInterval is var interval && interval >= TimeSpan.FromSeconds(1) && interval <= TimeSpan.FromMinutes(5),
                optionsType + ".RefreshInterval must be between one second and five minutes")
            .Validate(
                options => shape(options).HealthInterval is var interval && interval >= TimeSpan.FromSeconds(15) && interval <= TimeSpan.FromHours(1),
                optionsType + ".HealthInterval must be between fifteen seconds and one hour: several health checks scan the server's lock table and process array, and a page polls them no faster than this");
    }

    /// <summary>The capabilities object, which a host may set to <see langword="null" /> by mistake.</summary>
    /// <param name="builder">The studio's options builder.</param>
    /// <param name="optionsType">The options type's name.</param>
    /// <param name="capabilitiesType">The capabilities type's name: <c>MartenStudioCapabilities</c>.</param>
    /// <param name="shape">The shared options of one instance.</param>
    public static OptionsBuilder<TOptions> ValidateSharedCapabilities<TOptions>(
        this OptionsBuilder<TOptions> builder,
        string optionsType,
        string capabilitiesType,
        Func<TOptions, StudioOptionsShape> shape)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(shape);

        return builder.Validate(
            options => shape(options).HasCapabilities,
            optionsType + ".Capabilities cannot be null: use new " + capabilitiesType + "() for none, or " + capabilitiesType + ".All()");
    }

    /// <summary>The browsable schemas: each <c>"*"</c> or a name the studio could quote.</summary>
    /// <param name="builder">The studio's options builder.</param>
    /// <param name="optionsType">The options type's name.</param>
    /// <param name="shape">The shared options of one instance.</param>
    public static OptionsBuilder<TOptions> ValidateSharedSchemas<TOptions>(
        this OptionsBuilder<TOptions> builder,
        string optionsType,
        Func<TOptions, StudioOptionsShape> shape)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(shape);

        return builder.Validate(
            options => shape(options).BrowsableSchemas.All(BrowsableSchemaMatcher.IsValidEntry),
            optionsType + ".BrowsableSchemas entries must each be \"*\" or a Postgres schema name: not " +
            "empty or whitespace, at most 63 bytes in UTF-8, and with no double quote (\") and no NUL - a " +
            "name the studio could never quote is a name no schema it can read has");
    }

    /// <summary>
    /// A Postgres role name as <c>SET LOCAL ROLE</c> will accept it. The console's role cannot be a parameter - it is an
    /// identifier - so it is held to the shape of an unquoted identifier here, at startup, rather than escaped later.
    /// </summary>
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_$]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex PostgresRoleName();
}
