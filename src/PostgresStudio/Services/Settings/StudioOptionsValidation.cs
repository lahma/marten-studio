using Microsoft.Extensions.Options;

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
internal static class StudioOptionsValidation
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
                options => DiagnosticsOptionsValidation.IsQueryTimeout(shape(options).QueryTimeout),
                DiagnosticsOptionsValidation.QueryTimeoutMessage(optionsType))
            .Validate(
                options => shape(options).MaxInlineBytes is > 0 and <= 64 * 1024 * 1024,
                optionsType + "." + inlineMember + " must be between 1 and 64 MiB")
            .Validate(
                options => shape(options).MaxSqlConsoleRows is >= 1 and <= 10_000,
                optionsType + ".MaxSqlConsoleRows must be between 1 and 10000")
            .Validate(
                options => DiagnosticsOptionsValidation.IsSqlConsoleRole(shape(options).SqlConsoleRole),
                DiagnosticsOptionsValidation.SqlConsoleRoleMessage(optionsType))
            .Validate(
                options => shape(options).ExactCountThreshold >= 0,
                optionsType + ".ExactCountThreshold cannot be negative")
            .Validate(
                options => shape(options).RefreshInterval is var interval && interval >= TimeSpan.FromSeconds(1) && interval <= TimeSpan.FromMinutes(5),
                optionsType + ".RefreshInterval must be between one second and five minutes")
            .Validate(
                options => DiagnosticsOptionsValidation.IsHealthInterval(shape(options).HealthInterval),
                DiagnosticsOptionsValidation.HealthIntervalMessage(optionsType));
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
            options => DiagnosticsOptionsValidation.AreSchemas(shape(options).BrowsableSchemas),
            DiagnosticsOptionsValidation.SchemasMessage(optionsType));
    }
}
