using PostgresStudio.Services.Database;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What K05, K06 and K07 read of the studio's own options: configuration only, no I/O.</summary>
/// <param name="ReadOnly"><c>ReadOnly</c>: every capability off, whatever else is set.</param>
/// <param name="Configured">The capabilities the host switched on, without <c>ReadOnly</c>.</param>
/// <param name="SqlConsoleRole">Whether <c>SqlConsoleRole</c> is set - never the name, which is the database-wide gate's (D29).</param>
/// <param name="BrowsesEverySchema">Whether <c>BrowsableSchemas</c> contains <c>"*"</c>.</param>
/// <param name="OptionsTypeName">The options type's name, as a host's registration spells it.</param>
/// <param name="RegistrationMethod">The registration method: <c>AddMartenStudio</c>, <c>AddPostgresStudio</c>.</param>
/// <param name="CapabilitiesTypeName">The capabilities type's name.</param>
internal sealed record StudioLintFacts(
    bool ReadOnly,
    IReadOnlyList<StudioCapability> Configured,
    bool SqlConsoleRole,
    bool BrowsesEverySchema,
    string OptionsTypeName,
    string RegistrationMethod,
    string CapabilitiesTypeName)
{
    /// <summary>The capabilities that are on: configured, and not under <c>ReadOnly</c>.</summary>
    public IReadOnlyList<StudioCapability> Enabled => ReadOnly ? [] : Configured;

    /// <summary>Whether <paramref name="capability" /> is on.</summary>
    public bool IsEnabled(StudioCapability capability) => Enabled.Contains(capability);

    /// <summary><paramref name="member" /> as the host's registration spells it.</summary>
    public string OptionName(string member) => OptionsTypeName + "." + member;

    /// <summary>The method that maps the studio: <c>MapMartenStudio</c>, <c>MapPostgresStudio</c>.</summary>
    public string MapMethod => RegistrationMethod.StartsWith("Add", StringComparison.Ordinal) ? "Map" + RegistrationMethod[3..] : RegistrationMethod;

    /// <summary>Reads them off the studio's settings.</summary>
    public static StudioLintFacts Of(IStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return new StudioLintFacts(
            settings.ReadOnly,
            [.. settings.Capabilities.Where(settings.IsConfigured)],
            !string.IsNullOrWhiteSpace(settings.SqlConsoleRole),
            BrowsableSchemaMatcher.IncludesEverything(settings.BrowsableSchemas),
            settings.OptionsTypeName,
            settings.Text.RegistrationMethod,
            settings.CapabilitiesTypeName);
    }

    /// <summary>
    /// The remedy K05 and K06 share: a role the console and the browser switch to, named in the host's own registration. The
    /// role itself is a DBA's to create and grant, and the line says nothing of how - a remedy is never DDL or a grant.
    /// </summary>
    public HealthRemedy SqlConsoleRoleRemedy() => new(
        "// In " + RegistrationMethod + "(options => ...): the role the SQL console and the database browser read as\n"
        + "options.SqlConsoleRole = \"studio_reader\";",
        "A role the data source's login is a member of, granted SELECT on what the console and the browser should read and "
        + "nothing more; each of their statements switches to it with SET LOCAL ROLE (" + OptionName("SqlConsoleRole") + "). Creating "
        + "it and granting it are a DBA's - the studio never runs either.");
}

/// <summary>
/// K05: the SQL console is on, and its statements run as the data source's own role because no <c>SqlConsoleRole</c> narrows
/// them (D13).
/// </summary>
/// <remarks>
/// <para>
/// <b>The console's real boundary is the role</b> (D13): its read-only transaction stops writes, never reads, so a visitor
/// granted <c>RunSql</c> reads everything the role it runs as can - every schema, whatever <c>BrowsableSchemas</c> says.
/// Without <c>SqlConsoleRole</c> that is the role the application logs in as.
/// </para>
/// <para>
/// <b>Configuration, both studios, shown to anyone the scope admits</b>: it reads only the studio's own options, as the
/// Configuration page does, with no I/O. <c>ReadOnly</c> mutes it, because it turns <c>RunSql</c> off.
/// </para>
/// </remarks>
[HealthCheck("K05", HealthArea.Configuration, HealthCostTier.InProcess, "SQL console without a narrowed role", Gate = HealthGate.None)]
internal sealed class ConsoleWithoutRoleCheck : HealthCheck<StudioLintFacts>
{
    /// <inheritdoc />
    public override Task<StudioLintFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(StudioLintFacts.Of(context.Settings));
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(StudioLintFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        string runSql = facts.OptionName("Capabilities.RunSql");

        if (facts.ReadOnly)
        {
            return HealthVerdict.Quiet(facts.OptionName("ReadOnly") + " is true, so the SQL console is off whatever " + runSql + " says.");
        }

        if (!facts.IsEnabled(StudioCapability.RunSql))
        {
            return HealthVerdict.Quiet("The SQL console is off (" + runSql + " is false).");
        }

        if (facts.SqlConsoleRole)
        {
            return HealthVerdict.Quiet("The SQL console is on, and " + facts.OptionName("SqlConsoleRole") + " narrows what its statements read.");
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "K05",
            Severity = HealthSeverity.Warning,
            Title = "The SQL console reads as " + evaluation.Text.SourcePossessive + " own Postgres role",
            WhyItMatters =
                runSql + " is on and " + facts.OptionName("SqlConsoleRole") + " is not set, so every statement typed into the console runs "
                + "as the role " + SourceWords.DataSource(evaluation.Text) + " logs in as - the role that owns its tables. The console's "
                + "read-only transaction stops writes, never reads: whoever is granted RunSql reads every row that role can, in every "
                + "schema, whatever " + facts.OptionName("BrowsableSchemas") + " says. The role it runs as is the console's real boundary.",
            Evidence =
            [
                new("Capability", runSql + " = true"),
                new("SqlConsoleRole", "not set"),
                new("ReadOnly", "false"),
            ],
            Caveat = "Read from this process's " + facts.OptionsTypeName + ".",
            Remedy = facts.SqlConsoleRoleRemedy(),
        });
    }
}
