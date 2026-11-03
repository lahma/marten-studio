using PostgresStudio.Services.Connection;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// S12: a credential of the data source is readable from the application's configuration or the process's environment, by more
/// than the code that needs it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three variants.</b>
/// </para>
/// <list type="bullet">
/// <item><c>persist-security-info</c> (Warning): <c>Persist Security Info=true</c> and a <c>Password</c> in the connection
/// string. Npgsql removes the password from <c>NpgsqlDataSource.ConnectionString</c> unless this is on
/// (<c>ToStringWithoutPassword</c>, Npgsql 9.0.4), and names the pool by that string (<c>NpgsqlDataSource.Name = name ??
/// ConnectionString</c>, the <c>pool.name</c> tag of its metrics), so the password reaches anything that prints the connection
/// string or exports the metrics.</item>
/// <item><c>ssl-password</c> (Warning): <c>SSL Password</c> is set. Npgsql clears only <c>Password</c>, so the client key's
/// password stays in <c>NpgsqlDataSource.ConnectionString</c> and in the pool's name whatever <c>Persist Security Info</c> says, and
/// reaches any code that prints the connection string and any OpenTelemetry exporter.</item>
/// <item><c>pgpassword-env</c> (Info): <c>PGPASSWORD</c> is set in the process's environment, which Npgsql falls back to when the
/// data source has no password of its own. The PostgreSQL documentation (libpq, <i>Environment Variables</i>) advises against it
/// because some operating systems let other users see a process's environment; a password file is the advised alternative. When
/// <c>Persist Security Info</c> is off the studio cannot see whether the connection string has a password of its own - in which case
/// <c>PGPASSWORD</c> is ignored - and the finding says so; when it is on and the connection string has one, the finding is not made.</item>
/// </list>
/// <para>
/// <b>Redaction.</b> The evidence says a credential is <i>set</i>, never what it is, and names the keyword or the environment
/// variable, never a value or a path (NP-DESIGN §2; <c>ConnectionSentinelTests</c> hold it).
/// </para>
/// </remarks>
[HealthCheck(
    "S12",
    HealthArea.Server,
    HealthCostTier.Catalog,
    "A credential is readable from configuration",
    Variants = [PersistSecurityInfoVariant, SslPasswordVariant, PasswordEnvironmentVariant])]
internal sealed class ReadableCredentialCheck : HealthCheck<ConnectionFacts>
{
    /// <summary>The variant of a finding about <c>Persist Security Info</c>.</summary>
    public const string PersistSecurityInfoVariant = "persist-security-info";

    /// <summary>The variant of a finding about <c>SSL Password</c>.</summary>
    public const string SslPasswordVariant = "ssl-password";

    /// <summary>The variant of a finding about <c>PGPASSWORD</c>.</summary>
    public const string PasswordEnvironmentVariant = "pgpassword-env";

    /// <inheritdoc />
    public override Task<ConnectionFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ConnectionCheckSupport.ReadAsync(context);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ConnectionFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        ConnectionSettings settings = facts.Settings;
        bool passwordInString = settings.Rows.Any(static x =>
            string.Equals(x.Keyword, "Password", StringComparison.OrdinalIgnoreCase) && x.Origin == ConnectionSettingOrigin.ConnectionString);

        List<HealthFinding> findings = [];

        if (settings.PersistSecurityInfo && passwordInString)
        {
            findings.Add(PersistFinding(evaluation));
        }

        if (settings.SslPasswordSet)
        {
            findings.Add(SslPasswordFinding(evaluation));
        }

        // With Persist Security Info on, the studio sees the connection string's password, and PGPASSWORD is then ignored.
        if (settings.PasswordEnvironmentSet && !(settings.PersistSecurityInfo && passwordInString))
        {
            findings.Add(EnvironmentFinding(settings, evaluation));
        }

        return findings.Count > 0
            ? new HealthVerdict(findings)
            : HealthVerdict.Quiet(
                "No credential is kept where more than the code that needs it can read it: Persist Security Info is "
                + (settings.PersistSecurityInfo ? "on, but the connection string has no password" : "off")
                + ", no SSL Password is set, and PGPASSWORD is not in the process's environment.");
    }

    private static HealthFinding PersistFinding(HealthEvaluation evaluation) => new()
    {
        Code = "S12",
        Variant = PersistSecurityInfoVariant,
        Severity = HealthSeverity.Warning,
        Title = "Persist Security Info keeps the data source's password readable",
        WhyItMatters =
            "With Persist Security Info=true Npgsql keeps the Password in NpgsqlDataSource.ConnectionString, and it names the pool by that "
            + "string (the pool.name tag of its metrics), so the password reaches any code that prints the connection string - a log "
            + "line, an exception message, a diagnostics page - and any OpenTelemetry exporter. By default Npgsql removes it from both "
            + "after it has built the data source.",
        Evidence =
        [
            new("Persist Security Info", "on"),
            new("Password", "set in the connection string"),
        ],
        Caveat =
            "Read from the data source's own settings. The studio shows that a password is set and never what it is. Some tools "
            + "(a connection string a host reads back to reconnect) need the setting; the check can then be accepted with AcceptedChecks.",
        Remedy = new HealthRemedy(
            ConnectionCheckSupport.Block(
                evaluation.Text,
                "In the connection string the data source is built from (Npgsql's default):",
                "Persist Security Info=false",
                "b.ConnectionStringBuilder.PersistSecurityInfo = false;"),
            "Npgsql then removes the password from the data source's connection string and its pool's name. The data source keeps "
            + "using it to log in."),
    };

    private static HealthFinding SslPasswordFinding(HealthEvaluation evaluation) => new()
    {
        Code = "S12",
        Variant = SslPasswordVariant,
        Severity = HealthSeverity.Warning,
        Title = "SSL Password is readable from the data source's connection string",
        WhyItMatters =
            "Npgsql keeps SSL Password in NpgsqlDataSource.ConnectionString and in the pool name it reports to metrics, whatever "
            + "Persist Security Info says - it removes only Password. So the password of the client key reaches any code that prints "
            + "the connection string and any OpenTelemetry exporter.",
        Evidence =
        [
            new("SSL Password", "set in the connection string"),
        ],
        Caveat = "Read from the data source's own settings. The studio shows that it is set and never what it is.",
        Remedy = new HealthRemedy(
            ConnectionCheckSupport.CodeBlock(
                evaluation.Text,
                "Load the client certificate in code, from a secrets manager, and leave SSL Password out of the connection string:",
                "b.UseSslClientAuthenticationOptionsCallback(options =>",
                "    options.ClientCertificates = new X509CertificateCollection { certificate });"),
            "Npgsql 9 and 10 take the certificate through the callback (UseClientCertificate is obsolete). Alternatively use a key file "
            + "without a password and restrict its permissions to the account the application runs as."),
    };

    private static HealthFinding EnvironmentFinding(ConnectionSettings settings, HealthEvaluation evaluation) => new()
    {
        Code = "S12",
        Variant = PasswordEnvironmentVariant,
        Severity = HealthSeverity.Info,
        Title = "The password may come from the PGPASSWORD environment variable",
        WhyItMatters =
            "Npgsql falls back to PGPASSWORD when the data source has no password of its own. The PostgreSQL documentation advises "
            + "against the variable, because some operating systems let other users of the machine see a process's environment; a "
            + "password file is the advised alternative.",
        Evidence =
        [
            new("PGPASSWORD", "set in this process's environment"),
        ],
        Caveat = settings.PersistSecurityInfo
            ? "The connection string has no password of its own, so Npgsql uses PGPASSWORD."
            : "Persist Security Info is off, so the studio cannot see whether the connection string has a password of its own; if it does, "
              + "Npgsql ignores PGPASSWORD. The studio shows that the variable is set and never its value.",
        Remedy = new HealthRemedy(
            ConnectionCheckSupport.Block(
                evaluation.Text,
                "A password file only the application's account can read, in the connection string the data source is built from:",
                "Passfile=<path to a pgpass file>",
                "b.ConnectionStringBuilder.Passfile = \"<path to a pgpass file>\";"),
            "Npgsql reads the password for the host, port, database and user from the file (PGPASSFILE does the same through the "
            + "environment, with a path rather than a password). Then unset PGPASSWORD."),
    };
}
