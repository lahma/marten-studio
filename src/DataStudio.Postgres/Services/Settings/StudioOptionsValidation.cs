using Microsoft.Extensions.Options;

namespace DataStudio.Postgres.Services.Settings;

/// <summary>
/// The options both database modules share, as one module's options type spells them - what the shared validation reads. (The mount path, the
/// application path and the refresh interval are the shell's, validated with its options.)
/// </summary>
/// <param name="DefaultPageSize">The page size a grid opens with.</param>
/// <param name="MaxPageSize">The largest page size.</param>
/// <param name="QueryTimeout">The statement timeout.</param>
/// <param name="MaxInlineBytes">The inline-value cap: MartenStudio's <c>MaxInlineValueBytes</c>, PostgresStudio's <c>MaxInlineValueBytes</c>.</param>
/// <param name="MaxSqlConsoleRows">The console's row cap.</param>
/// <param name="SqlConsoleRole">The console's role.</param>
/// <param name="ExactCountThreshold">The exact-count threshold.</param>
/// <param name="HealthInterval">The health interval.</param>
/// <param name="HasCapabilities">Whether the capabilities object is set.</param>
/// <param name="BrowsableSchemas">The browsable schemas.</param>
/// <param name="MaxStudioConnections">The cap on the studio's connections per pool (issue #44).</param>
/// <param name="StudioApplicationName">The studio sessions' application_name, if the host set one (issue #44).</param>
internal sealed record StudioOptionsShape(
    int DefaultPageSize,
    int MaxPageSize,
    TimeSpan QueryTimeout,
    int MaxInlineBytes,
    int MaxSqlConsoleRows,
    string? SqlConsoleRole,
    long ExactCountThreshold,
    TimeSpan HealthInterval,
    bool HasCapabilities,
    IEnumerable<string> BrowsableSchemas,
    int MaxStudioConnections = Internal.Sql.StudioConnectionPools.DefaultMax,
    string? StudioApplicationName = null);

/// <summary>
/// One validation chain for the options both database modules have - the same bounds, and sentences that differ only in the type
/// and member names a host's own registration spells - so the two modules cannot drift apart (design §5.1).
/// </summary>
/// <remarks>
/// In three parts, because a module interleaves validations of its own between them and the failures an
/// <c>OptionsValidationException</c> lists are in registration order: the Marten module's rebuild timeout sits between the
/// first part and the capabilities, and its tenant ids between the capabilities and the schemas.
/// </remarks>
internal static class StudioOptionsValidation
{
    /// <summary>The page sizes, the timeouts, the inline cap, the console's rows and role, the count threshold and the health interval.</summary>
    /// <param name="builder">The studio's options builder.</param>
    /// <param name="optionsType">The options type's name: <c>MartenOptions</c>.</param>
    /// <param name="inlineMember">The inline cap's member name: <c>MaxInlineValueBytes</c>.</param>
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
                options => DiagnosticsOptionsValidation.IsHealthInterval(shape(options).HealthInterval),
                DiagnosticsOptionsValidation.HealthIntervalMessage(optionsType))
            .Validate(
                options => DiagnosticsOptionsValidation.IsMaxStudioConnections(shape(options).MaxStudioConnections),
                DiagnosticsOptionsValidation.MaxStudioConnectionsMessage(optionsType))
            .Validate(
                options => DiagnosticsOptionsValidation.IsStudioApplicationName(shape(options).StudioApplicationName),
                DiagnosticsOptionsValidation.StudioApplicationNameMessage(optionsType));
    }

    /// <summary>The capabilities object, which a host may set to <see langword="null" /> by mistake.</summary>
    /// <param name="builder">The studio's options builder.</param>
    /// <param name="optionsType">The options type's name.</param>
    /// <param name="capabilitiesType">The capabilities type's name: <c>MartenCapabilities</c>.</param>
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
