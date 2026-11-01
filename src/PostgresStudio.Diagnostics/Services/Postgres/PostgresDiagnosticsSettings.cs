using Microsoft.Extensions.Options;

using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Postgres;

/// <summary>
/// The diagnostics engine's <see cref="IDiagnosticsSettings" /> for a host that registered
/// <c>AddPostgresDiagnostics()</c>: a live adapter over <see cref="PostgresDiagnosticsOptions" />, with Postgres Studio's
/// name and words (<see cref="PostgresStudioText" />).
/// </summary>
/// <remarks>
/// Every member reads <see cref="IOptions{TOptions}.Value" /> again, as <c>PostgresStudioSettings</c> does. A host that
/// registered a studio has the studio's own settings object instead, which answers the same interface.
/// </remarks>
internal sealed class PostgresDiagnosticsSettings : IDiagnosticsSettings
{
    private readonly IOptions<PostgresDiagnosticsOptions> options;

    /// <summary>Creates the adapter over <paramref name="options" />.</summary>
    /// <param name="options">The options the host configured.</param>
    public PostgresDiagnosticsSettings(IOptions<PostgresDiagnosticsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    private PostgresDiagnosticsOptions Value => options.Value;

    /// <inheritdoc />
    public string ProductName => "Postgres Studio";

    /// <inheritdoc />
    public string OptionsTypeName => nameof(PostgresDiagnosticsOptions);

    /// <inheritdoc />
    public StudioText Text => PostgresStudioText.DiagnosticsInstance;

    /// <inheritdoc />
    public TimeSpan QueryTimeout => Value.QueryTimeout;

    /// <inheritdoc />
    /// <remarks>There is no refresh interval without a UI, so the health interval is the cadence as set.</remarks>
    public TimeSpan EffectiveHealthInterval => Value.HealthInterval;

    /// <inheritdoc />
    public string? SqlConsoleRole => Value.SqlConsoleRole;

    /// <inheritdoc />
    public IReadOnlyList<string> BrowsableSchemas =>
        Value.BrowsableSchemas as IReadOnlyList<string> ?? [.. Value.BrowsableSchemas];

    /// <inheritdoc />
    public IReadOnlyDictionary<string, string> AcceptedChecks => Value.AcceptedCheckReasons;

    /// <inheritdoc />
    /// <remarks>Always <see langword="null" />: Postgres has no document types, so none can be hidden.</remarks>
    public Func<Type, bool>? IsDocumentTypeVisible => null;

    /// <inheritdoc />
    /// <remarks>"Source" once <c>AddDataSource</c> or <c>AddDatabaseSource</c> named one, as <c>PostgresStudioSettings</c> says.</remarks>
    public string? SourceLabel => Value.DataSources.Count + Value.DatabaseSources.Count > 0 ? "Source" : null;
}
