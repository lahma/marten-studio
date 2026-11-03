using PostgresStudio.Services.Connection;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What S13 reads.</summary>
/// <param name="Connection">The data source's settings.</param>
/// <param name="EnvironmentName">The host's environment, or <see langword="null" />.</param>
internal sealed record DriverLoggingFacts(ConnectionFacts Connection, string? EnvironmentName)
{
    /// <summary>Whether the host says this is Production.</summary>
    public bool IsProduction => HostEnvironmentName.IsProduction(EnvironmentName);
}

/// <summary>
/// S13: in Production, a driver setting that copies values into logs or exceptions is on.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three variants, all Info.</b> Each setting is off by default and is a development aid: <c>Log Parameters</c> logs every
/// command's parameter values; <c>Include Error Detail</c> puts PostgreSQL's error and notice details (often the offending row's
/// values, as a unique violation's "Key (email)=(a@b.example) already exists") on <c>PostgresException.Detail</c> and
/// <c>PostgresNotice.Detail</c>; <c>Include Failed Batched Command</c> (Npgsql 10) puts the failed statement on
/// <c>NpgsqlException.BatchCommand</c> (<c>NpgsqlConnectionStringBuilder</c>, Npgsql 9.0.4 and 10.0.3). Each can carry personal or
/// secret data into a log line, an error page or a telemetry export.
/// </para>
/// <para>
/// <b><c>Log Parameters</c> logs only through a logger factory.</b> The parameters reach a log line only where the data source has one
/// (<c>NpgsqlCommand.LogExecutingCompleted</c>, Npgsql 9.0.4; <c>LogParameters</c> is read there beside the builder's
/// <c>EnableParameterLogging()</c>), which the studio cannot see, so the finding says "where the data source has a logger factory" and
/// that <c>EnableParameterLogging()</c> turns it on whatever the keyword says. Marten 9 defaults <c>StoreOptions.DisableNpgsqlLogging</c>
/// to <see langword="true" /> (V9.57.0), which leaves its data sources without one: Marten Studio's words say so
/// (<see cref="Settings.StudioText.LogParametersLoggerClause" />); Postgres Studio's say nothing of it.
/// </para>
/// <para>
/// <b>Only in Production, as the host says.</b> The environment is <c>IHostEnvironment.EnvironmentName</c>, as K01 and S04 read it;
/// a host that registers none is not Production, and the quiet note says so. Nothing is raised elsewhere: these settings are meant
/// for development. Severity is Info, because whether the data they copy is sensitive is the application's knowledge.
/// </para>
/// </remarks>
[HealthCheck(
    "S13",
    HealthArea.Server,
    HealthCostTier.Catalog,
    "Driver settings that copy values into logs or exceptions",
    Variants = [LogParametersVariant, IncludeErrorDetailVariant, IncludeFailedBatchedCommandVariant])]
internal sealed class DriverLoggingSettingsCheck : HealthCheck<DriverLoggingFacts>
{
    /// <summary>The variant of a finding about <c>Log Parameters</c>.</summary>
    public const string LogParametersVariant = "log-parameters";

    /// <summary>The variant of a finding about <c>Include Error Detail</c>.</summary>
    public const string IncludeErrorDetailVariant = "include-error-detail";

    /// <summary>The variant of a finding about <c>Include Failed Batched Command</c> (Npgsql 10).</summary>
    public const string IncludeFailedBatchedCommandVariant = "include-failed-batched-command";

    /// <inheritdoc />
    public override async Task<DriverLoggingFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ConnectionFacts facts = await ConnectionCheckSupport.ReadAsync(context).ConfigureAwait(false);

        return new DriverLoggingFacts(facts, HostEnvironmentName.Of(context));
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(DriverLoggingFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        ConnectionValues v = facts.Connection.Settings.Values;

        if (!facts.IsProduction)
        {
            return HealthVerdict.Quiet(
                "These settings are judged in Production only; this host's environment is " + HostEnvironmentName.Describe(facts.EnvironmentName) + ".");
        }

        List<HealthFinding> findings = [];

        if (v.Bool("Log Parameters") == true)
        {
            findings.Add(Finding(
                evaluation,
                LogParametersVariant,
                "Log Parameters",
                "Every command's parameter values are written to the log",
                "Where the data source has a logger factory, Npgsql logs the parameter values of every command it runs, at the log level it uses "
                + "for commands. Those values are the application's data - names, email addresses, tokens, whatever the query takes - and they "
                + "end up in log files and log services that are kept, searched and shared more widely than the database."
                + evaluation.Text.LogParametersLoggerClause,
                "LogParameters",
                " Npgsql writes parameters only through a logger factory, which the studio cannot see on the data source.",
                " NpgsqlDataSourceBuilder.EnableParameterLogging() turns parameter logging on whatever this keyword says, so removing the keyword "
                + "does not end it where the host calls that; the studio cannot see whether it does."));
        }

        if (v.Bool("Include Error Detail") == true)
        {
            findings.Add(Finding(
                evaluation,
                IncludeErrorDetailVariant,
                "Include Error Detail",
                "PostgreSQL's error details are copied into exceptions",
                "PostgreSQL's error details often quote the row that caused the error - a unique violation's \"Key (email)=(a@b.example) "
                + "already exists\" - and with this setting Npgsql puts them on PostgresException.Detail and PostgresNotice.Detail, where "
                + "an exception handler, an error page or a log line carries them on. Npgsql warns that they can contain sensitive data.",
                "IncludeErrorDetail"));
        }

        if (v.Bool("Include Failed Batched Command") == true)
        {
            findings.Add(Finding(
                evaluation,
                IncludeFailedBatchedCommandVariant,
                "Include Failed Batched Command",
                "A failed batch's statement is copied into the exception",
                "With this setting Npgsql puts the statement that failed in a batch on NpgsqlException.BatchCommand, where an exception "
                + "handler, an error page or a log line carries it on. A statement can name tables, columns and literals the application "
                + "would rather keep out of its logs.",
                "IncludeFailedBatchedCommand"));
        }

        return findings.Count > 0
            ? new HealthVerdict(findings)
            : HealthVerdict.Quiet("This is Production, and Log Parameters, Include Error Detail and Include Failed Batched Command are off.");
    }

    private static HealthFinding Finding(
        HealthEvaluation evaluation,
        string variant,
        string keyword,
        string title,
        string why,
        string property,
        string caveatExtra = "",
        string remedyExtra = "") => new()
    {
        Code = "S13",
        Variant = variant,
        Severity = HealthSeverity.Info,
        Title = title + " in Production",
        WhyItMatters = why,
        Evidence =
        [
            new(keyword, "on"),
            new("Environment", "Production"),
        ],
        Caveat =
            "Info rather than Warning: whether the values these settings copy are sensitive is the application's knowledge, not the studio's. "
            + "The setting is off by default and meant for development."
            + caveatExtra,
        Remedy = new HealthRemedy(
            ConnectionCheckSupport.Block(
                evaluation.Text,
                "In the connection string the data source is built from (Npgsql's default is off), or leave the keyword out:",
                keyword + "=false",
                "b.ConnectionStringBuilder." + property + " = false;"),
            "Turn it on for a development environment only, through that environment's own configuration." + remedyExtra),
    };
}
