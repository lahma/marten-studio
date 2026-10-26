using PostgresStudio.Services.Server;
using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What S10 reads.</summary>
/// <param name="ReadOnly">
/// <c>default_transaction_read_only</c> as the source's sessions start with it, or <see langword="null" /> when not shown.
/// </param>
/// <param name="InRecovery">Whether the server the source's write connection reached is a standby.</param>
/// <param name="Platform">Who runs the server.</param>
internal sealed record ReadOnlyStoreFacts(ServerSettingValue? ReadOnly, bool InRecovery, PostgresPlatform Platform);

/// <summary>
/// S10: the source's connections cannot write - every transaction starts read-only, or the write connection reached a standby.
/// </summary>
/// <remarks>
/// <para>
/// <b><c>read-only</c>, Critical</b>: <c>default_transaction_read_only = on</c> as the source's own role sees it, which
/// <c>pg_settings.source</c> says who set. Every <c>SaveChanges</c>, append and projection write fails with <c>25006</c>.
/// Azure Database for PostgreSQL sets it itself when a server runs short of storage, so there the remedy is storage rather
/// than a setting (Microsoft Learn, <i>Storage options in Azure Database for PostgreSQL</i>, not re-read by this packet).
/// </para>
/// <para>
/// <b><c>standby</c>, Warning</b>: the connection the advisor opens for writing (<c>StudioConnectionUsage.ReadWrite</c>, the
/// host a multi-host data source writes to) answered <c>pg_is_in_recovery() = true</c>. A store that only reads is told to
/// mute it.
/// </para>
/// </remarks>
[HealthCheck("S10", HealthArea.Server, HealthCostTier.Catalog, "Store connections cannot write", Variants = ["read-only", "standby"])]
internal sealed class ReadOnlyStoreCheck : HealthCheck<ReadOnlyStoreFacts>
{
    /// <summary>The setting the <c>read-only</c> variant reads.</summary>
    public const string Setting = "default_transaction_read_only";

    /// <inheritdoc />
    public override async Task<ReadOnlyStoreFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ServerSettingsRead settings = await ServerSettingsRead.SharedAsync(context).ConfigureAwait(false);
        ServerConnectionPosture posture = await ServerConnectionPosture.SharedAsync(context).ConfigureAwait(false);
        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        return new ReadOnlyStoreFacts(settings.Find(Setting), posture.InRecovery, server.Platform);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ReadOnlyStoreFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        List<HealthFinding> findings = [];
        string source = evaluation.Text.SourcePossessive;

        if (facts.ReadOnly is { IsOn: true } setting)
        {
            findings.Add(ReadOnly(setting, facts.Platform, evaluation.Text));
        }

        if (facts.InRecovery)
        {
            findings.Add(new HealthFinding
            {
                Code = "S10",
                Severity = HealthSeverity.Warning,
                Variant = "standby",
                Title = "The server " + source + " write connection reaches is a standby",
                WhyItMatters =
                    "A standby replays the primary's WAL and accepts no write: every transaction there is read-only, so every SaveChanges, "
                    + "append and projection write fails with 25006 \"cannot execute … in a read-only transaction\". If " + SourceWords.DataSource(evaluation.Text) + " "
                    + "should reach the primary, point it there - or list every host with Target Session Attributes, so Npgsql "
                    + "picks the one that writes. If the store only ever reads here, mute this finding.",
                Evidence =
                [
                    new("pg_is_in_recovery()", "true"),
                    new("Connection", "the one the advisor opens for writing (the primary of a multi-host data source)"),
                    new("Platform", facts.Platform.Name),
                ],
                Caveat = "Read on the connection the health advisor opens for writing, from " + SourceWords.DataSource(evaluation.Text) + ".",
                Remedy = new HealthRemedy(
                    "Target Session Attributes=read-write",
                    "In the connection string " + SourceWords.DataSource(evaluation.Text) + " is built from, with every host of the cluster in Host: Npgsql "
                    + "then opens connections only to the one that accepts writes, and finds the new primary after a failover."),
            });
        }

        if (findings.Count > 0)
        {
            return new HealthVerdict(findings);
        }

        return HealthVerdict.Quiet(facts.ReadOnly is null
            ? "default_transaction_read_only is not shown to the reading role, so only the standby half was checked: the server is a primary."
            : "Sessions start read-write (default_transaction_read_only = " + facts.ReadOnly.Value + "), and the server is a primary.");
    }

    private static HealthFinding ReadOnly(ServerSettingValue setting, PostgresPlatform platform, StudioText text)
    {
        string source = text.SourcePossessive;
        bool azure = platform.Kind == PostgresPlatformKind.Azure;

        HealthRemedy? remedy = azure
            ? HealthRemedy.ForManagedPlatform(
                "Grow the server's storage, or turn on storage autogrow",
                "Azure Database for PostgreSQL switches a server to read-only when its storage is about 95% used, or has less than 5 GiB "
                + "free, and back once there is room again (Microsoft Learn, Storage options in Azure Database for PostgreSQL). In the "
                + "Azure portal, the server's Compute + storage page grows it or turns autogrow on.")
            : setting.Source is "configuration file" or "command line" or "environment variable"
                ? ServerSettingRemedy.Build(platform, setting.Name, "off", text, setting.Context, ":", where => "Set it in " + where + ".")
                : null;

        string whereSet = setting.Source switch
        {
            "database" or "user" or "database user" or "global" =>
                " It is set for this role or this database (ALTER ROLE … SET, ALTER DATABASE … SET): whoever owns it resets it there, and "
                + "new sessions start read-write.",
            "client" =>
                " It arrives with the connection: removing it from the Options of the connection string gives every new connection the "
                + "server's value.",
            _ => string.Empty,
        };

        return new HealthFinding
        {
            Code = "S10",
            Severity = HealthSeverity.Critical,
            Variant = "read-only",
            Title = "Every transaction of " + source + " sessions starts read-only",
            WhyItMatters =
                "With default_transaction_read_only = on, a transaction that does not ask otherwise is read-only - and Marten, like most "
                + "data access, never asks: every SaveChanges, append and projection write fails with 25006 \"cannot execute … in a "
                + "read-only transaction\"." + whereSet
                + (azure ? " On Azure the server itself sets it when its storage runs short, which is the first thing to check." : string.Empty),
            Evidence =
            [
                new("Setting", setting.Name + " = " + setting.Value),
                new("Set by", DangerousSettingsCheck.SetBy(setting.Source)),
                new("Platform", platform.Name),
            ],
            Caveat =
                "Read in pg_settings on a connection from " + SourceWords.DataSource(text) + ", so a value set for its role, its database or in "
                + "its connection string is the one its sessions start with.",
            Remedy = remedy,
        };
    }
}
