using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What S09 reads.</summary>
/// <param name="Settings">The curated settings, the <c>enable_*</c> family among them.</param>
/// <param name="Platform">Who runs the server, for where a setting changes.</param>
internal sealed record PlannerSwitchesFacts(ServerSettingsRead Settings, PostgresPlatform Platform);

/// <summary>
/// S09: one of the planner's methods (<c>enable_*</c>) is switched off for the source's sessions while its default is on.
/// </summary>
/// <remarks>
/// <para>
/// <b>As the source's sessions see it.</b> Read on a connection of the source's own data source, so a value set for its role,
/// its database or in its connection string's startup options counts, and <c>pg_settings.source</c> says which. A switch
/// whose built-in value is off - one a release ships disabled - is never a finding.
/// </para>
/// <para>
/// <b>Warning, one per setting.</b> An <c>enable_*</c> switched off is a workaround for one query's plan applied to every
/// query of every session it reaches. The remedy is the server setting through <see cref="ServerSettingRemedy" /> when the
/// server's configuration set it, and words for a role's, a database's or the connection's own, whose owner resets it there.
/// </para>
/// </remarks>
[HealthCheck("S09", HealthArea.Server, HealthCostTier.Catalog, "Planner method switched off")]
internal sealed class PlannerSwitchesCheck : HealthCheck<PlannerSwitchesFacts>
{
    /// <summary>The family S09 reads.</summary>
    public const string Prefix = "enable_";

    /// <inheritdoc />
    public override async Task<PlannerSwitchesFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ServerSettingsRead settings = await ServerSettingsRead.SharedAsync(context).ConfigureAwait(false);
        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        return new PlannerSwitchesFacts(settings, server.Platform);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(PlannerSwitchesFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<ServerSettingValue> switches = [.. facts.Settings.Rows.Where(static x => x.Name.StartsWith(Prefix, StringComparison.Ordinal))];
        List<ServerSettingValue> off = [.. switches.Where(static x => x.IsOff && string.Equals(x.BootValue, "on", StringComparison.OrdinalIgnoreCase))];

        if (off.Count == 0)
        {
            return HealthVerdict.Quiet(switches.Count == 0
                ? "No enable_* planner switch is shown to the reading role."
                : "Every enable_* planner switch whose default is on is on (" + switches.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " read).");
        }

        return new HealthVerdict([.. off.Select(x => Finding(x, facts.Platform, evaluation.Text))]);
    }

    private static HealthFinding Finding(ServerSettingValue setting, PostgresPlatform platform, StudioText text)
    {
        string whereSet = setting.Source switch
        {
            "database" or "user" or "database user" or "global" =>
                " It is set for this role or this database, not for the server: whoever owns the role or the database resets it there "
                + "(ALTER ROLE … RESET " + setting.Name + ", or ALTER DATABASE … RESET " + setting.Name + "), and new sessions take the default.",
            "client" =>
                " It arrives with the connection: removing it from the Options of the connection string the data source is built from "
                + "gives every new connection the default.",
            _ => string.Empty,
        };

        bool serverConfiguration = setting.Source is "configuration file" or "command line" or "environment variable";

        return new HealthFinding
        {
            Code = "S09",
            Severity = HealthSeverity.Warning,
            Subject = setting.Name,
            Title = "Planner method " + setting.Name + " is switched off",
            WhyItMatters =
                "With " + setting.Name + " = off the planner avoids that kind of plan wherever another exists, for every query of every "
                + "session it reaches - a workaround for one query's plan, applied to all of them. A query whose best plan needs it gets a "
                + "slower one, and nothing in the query or its plan's cost says why." + whereSet,
            Evidence =
            [
                new("Setting", setting.Name + " = " + setting.Value),
                new("Set by", DangerousSettingsCheck.SetBy(setting.Source)),
                new("Built-in value", setting.BootValue ?? "on"),
            ],
            Caveat =
                "Read in pg_settings on a connection from " + SourceWords.DataSource(text) + ", so a value set for its role, its "
                + "database or in its connection string is the one its sessions plan with. A query that sets it for itself (SET LOCAL) is "
                + "not seen.",
            Remedy = serverConfiguration
                ? ServerSettingRemedy.Build(
                    platform,
                    setting.Name,
                    "on",
                    text,
                    setting.Context,
                    ":",
                    where => "Set it in " + where + ", or remove the line there, which leaves the default.")
                : null,
        };
    }
}
