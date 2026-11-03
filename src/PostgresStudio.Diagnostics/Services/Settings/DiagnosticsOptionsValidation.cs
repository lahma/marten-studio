using System.Text.RegularExpressions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Postgres;

namespace PostgresStudio.Services.Settings;

/// <summary>
/// The validation rules for the options the diagnostics engine reads - the query timeout, the console's role, the health
/// interval, the browsable schemas, the accepted checks and the source names - each as a predicate and the sentence that
/// refuses it, so that <c>AddPostgresDiagnostics</c>, <c>AddPostgresStudio</c> and <c>AddMartenStudio</c> refuse the same
/// value in the same words, in their own options type's name.
/// </summary>
/// <remarks>
/// A studio interleaves these between validations of its own (<c>StudioOptionsValidation</c>), and the failures an
/// <c>OptionsValidationException</c> lists are in registration order, so a studio calls the predicates from its own chain at
/// the places they have always been and the sentences do not move.
/// </remarks>
internal static partial class DiagnosticsOptionsValidation
{
    /// <summary>Whether <paramref name="timeout" /> is a statement timeout the studio accepts.</summary>
    public static bool IsQueryTimeout(TimeSpan timeout) => timeout >= TimeSpan.FromSeconds(1) && timeout <= TimeSpan.FromMinutes(10);

    /// <summary>The refusal of a query timeout out of bounds.</summary>
    public static string QueryTimeoutMessage(string optionsType) => optionsType + ".QueryTimeout must be between one second and ten minutes";

    /// <summary>Issue #44: whether <paramref name="max" /> is a cap on the studio's connections the studio accepts.</summary>
    public static bool IsMaxStudioConnections(int max) => max is >= 1 and <= Internal.Sql.StudioConnectionPools.MaxAllowed;

    /// <summary>The refusal of a cap out of bounds.</summary>
    public static string MaxStudioConnectionsMessage(string optionsType) =>
        optionsType + ".MaxStudioConnections must be between 1 and 100";

    /// <summary>Issue #44: whether <paramref name="name" /> is absent, empty, or a name PostgreSQL keeps as written.</summary>
    public static bool IsStudioApplicationName(string? name) =>
        name is null || Internal.Sql.StudioConnectionPools.IsValidApplicationName(name);

    /// <summary>The refusal of a session name PostgreSQL would change.</summary>
    public static string StudioApplicationNameMessage(string optionsType) =>
        optionsType + ".StudioApplicationName must be printable ASCII of at most 63 characters, or empty to leave the application's own name";

    /// <summary>Whether <paramref name="role" /> is absent or a plain Postgres identifier.</summary>
    public static bool IsSqlConsoleRole(string? role) => role is null || PostgresRoleName().IsMatch(role);

    /// <summary>The refusal of a console role that is not a plain identifier.</summary>
    public static string SqlConsoleRoleMessage(string optionsType) =>
        optionsType + ".SqlConsoleRole must be a plain Postgres identifier: it is written into SET LOCAL ROLE, where it cannot be a parameter";

    /// <summary>Whether <paramref name="interval" /> is a health interval the studio accepts.</summary>
    public static bool IsHealthInterval(TimeSpan interval) => interval >= TimeSpan.FromSeconds(15) && interval <= TimeSpan.FromHours(1);

    /// <summary>The refusal of a health interval out of bounds.</summary>
    public static string HealthIntervalMessage(string optionsType) =>
        optionsType + ".HealthInterval must be between fifteen seconds and one hour: several health checks scan the server's lock table and process array, and a page polls them no faster than this";

    /// <summary>Whether every entry of <paramref name="schemas" /> is <c>"*"</c> or a name the studio could quote.</summary>
    public static bool AreSchemas(IEnumerable<string> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        return schemas.All(BrowsableSchemaMatcher.IsValidEntry);
    }

    /// <summary>The refusal of a browsable schema the studio could never quote.</summary>
    public static string SchemasMessage(string optionsType) =>
        optionsType + ".BrowsableSchemas entries must each be \"*\" or a Postgres schema name: not " +
        "empty or whitespace, at most 63 bytes in UTF-8, and with no double quote (\") and no NUL - a " +
        "name the studio could never quote is a name no schema it can read has";

    /// <summary>
    /// The data-source and database-source names: each a valid key, none <c>default</c>, all unique ignoring case across both.
    /// </summary>
    /// <param name="builder">The options builder.</param>
    /// <param name="optionsType">The options type's name, as a host's registration spells it.</param>
    /// <param name="names">Every name <c>AddDataSource</c> and <c>AddDatabaseSource</c> were given.</param>
    public static OptionsBuilder<TOptions> ValidateSourceNames<TOptions>(
        this OptionsBuilder<TOptions> builder,
        string optionsType,
        Func<TOptions, IEnumerable<string>> names)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(names);

        return builder
            .Validate(
                options => names(options).All(PostgresSourceNames.IsValidKey),
                optionsType + ".AddDataSource and AddDatabaseSource names must each be a letter or digit followed by " +
                "at most 63 letters, digits, '.', '_' or '-': the name travels in the URL and keys every cache")
            .Validate(
                options => !names(options).Contains(PostgresSourceNames.Default, StringComparer.OrdinalIgnoreCase),
                optionsType + ".AddDataSource and AddDatabaseSource cannot name a source 'default': it is the " +
                "application's unkeyed NpgsqlDataSource's, listed unless IncludeDefaultDataSource is false")
            .Validate(
                options => names(options).Distinct(StringComparer.OrdinalIgnoreCase).Count() == names(options).Count(),
                optionsType + ".AddDataSource and AddDatabaseSource names must be unique, ignoring case, across both");
    }

    /// <summary>
    /// The accepted checks (issue #35): each key a check code of <paramref name="descriptor" />'s own registry - a Marten code
    /// in Postgres Studio is unknown - and each reason non-blank and at most <see cref="MaxAcceptedReasonLength" />
    /// characters. A message names the offending key and nothing else: no list of the codes that would have been valid.
    /// </summary>
    /// <param name="builder">The studio's options builder.</param>
    /// <param name="optionsType">The options type's name.</param>
    /// <param name="descriptor">The studio, whose checks are the valid codes.</param>
    /// <param name="accepted">The options' accepted checks.</param>
    public static OptionsBuilder<TOptions> ValidateSharedAcceptedChecks<TOptions>(
        this OptionsBuilder<TOptions> builder,
        string optionsType,
        DiagnosticsDescriptor descriptor,
        Func<TOptions, IEnumerable<KeyValuePair<string, string>>> accepted)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(accepted);

        builder.Services.AddSingleton<IValidateOptions<TOptions>>(
            provider => new AcceptedChecksValidator<TOptions>(builder.Name, optionsType, descriptor, accepted, provider.GetService<ILoggerFactory>()));

        return builder;
    }

    /// <summary>The longest reason an accepted check may carry.</summary>
    public const int MaxAcceptedReasonLength = 500;

    /// <summary>
    /// A Postgres role name as <c>SET LOCAL ROLE</c> will accept it. The console's role cannot be a parameter - it is an
    /// identifier - so it is held to the shape of an unquoted identifier here, at startup, rather than escaped later.
    /// <c>\A</c> and <c>\z</c>, because .NET's <c>$</c> also matches before a final line feed.
    /// </summary>
    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_$]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PostgresRoleName();
}

/// <summary>
/// Validates <c>AcceptedChecks</c> (issue #35) against the studio's own check codes: an unknown key, a blank reason and a
/// reason over <see cref="DiagnosticsOptionsValidation.MaxAcceptedReasonLength" /> are each a failure naming the key and nothing
/// else. A type of its own, not a lambda, because the message names the offending key. A key naming a code the studio retired
/// (<see cref="DiagnosticsDescriptor.RetiredCheckCodes" />) is neither: it is accepted and ignored, and logged once per code at
/// Information, so a release that retires a check never stops a host that accepted it from starting.
/// </summary>
internal sealed class AcceptedChecksValidator<TOptions>(
    string name,
    string optionsType,
    DiagnosticsDescriptor descriptor,
    Func<TOptions, IEnumerable<KeyValuePair<string, string>>> accepted,
    ILoggerFactory? loggerFactory = null) : IValidateOptions<TOptions>
    where TOptions : class
{
    private readonly HashSet<string> reported = new(StringComparer.OrdinalIgnoreCase);

    public ValidateOptionsResult Validate(string? optionsName, TOptions options)
    {
        if (optionsName is not null && !string.Equals(optionsName, name, StringComparison.Ordinal))
        {
            return ValidateOptionsResult.Skip;
        }

        List<string> failures = [];
        IReadOnlySet<string>? codes = null;

        foreach ((string key, string? reason) in accepted(options))
        {
            codes ??= StudioCheckCodes.Of(descriptor);

            if (!codes.Contains(key) && Retired(key) is { } retired)
            {
                ReportRetired(retired.Key, retired.Value);
            }
            else if (!codes.Contains(key))
            {
                failures.Add(optionsType + ".AcceptedChecks key '" + key + "' is not a health check code " + descriptor.CheckCodeOwner);
            }
            else if (string.IsNullOrWhiteSpace(reason))
            {
                failures.Add(optionsType + ".AcceptedChecks['" + key + "'] needs a reason: an empty or whitespace one explains nothing to the people who read the Health page");
            }
            else if (reason.Length > DiagnosticsOptionsValidation.MaxAcceptedReasonLength)
            {
                failures.Add(optionsType + ".AcceptedChecks['" + key + "'] has a reason longer than " + DiagnosticsOptionsValidation.MaxAcceptedReasonLength + " characters");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private KeyValuePair<string, string>? Retired(string key)
    {
        foreach (KeyValuePair<string, string> retired in descriptor.RetiredCheckCodes)
        {
            if (string.Equals(retired.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return retired;
            }
        }

        return null;
    }

    // Options are validated again wherever the container resolves a snapshot, so the memory is the validator's, which is the
    // registration's: once per code for the life of the container, not once per validation.
    private void ReportRetired(string code, string release)
    {
        bool first;

        lock (reported)
        {
            first = reported.Add(code);
        }

        if (first && loggerFactory is not null)
        {
            loggerFactory.CreateLogger("PostgresStudio.Services.Settings.AcceptedChecks").AcceptedCheckRetired(code, release);
        }
    }
}
