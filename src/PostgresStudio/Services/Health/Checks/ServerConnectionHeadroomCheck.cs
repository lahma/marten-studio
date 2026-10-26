using System.Globalization;

using PostgresStudio.Services.Aurora;
using PostgresStudio.Services.Database;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What C03 reads.</summary>
/// <param name="Limits">The server's connection budget, and the login role's and this database's limits (C02's read too).</param>
/// <param name="Platform">Who runs the server, for where its connections are raised (issue #8 A6).</param>
/// <param name="Capacity">What the host's capacity source said this interval (A8), or <see langword="null" /> when not asked.</param>
/// <param name="PoolCeiling">This process's pool ceiling for the database (C01's <c>Max Pool Size</c>), or <see langword="null" /> unpooled or unknown.</param>
internal sealed record ServerConnectionHeadroomFacts(
    ServerConnectionLimits Limits,
    PostgresPlatform Platform,
    CapacityReading? Capacity,
    int? PoolCeiling)
{
    /// <summary>A self-hosted server's limits and nothing else - what a unit test of the budget alone states.</summary>
    public static implicit operator ServerConnectionHeadroomFacts(ServerConnectionLimits limits) => new(limits, PostgresPlatform.SelfHosted, null, null);
}

/// <summary>
/// C03: the server's connections are nearly used up - every client backend on the server against
/// <c>max_connections</c> less what is reserved - or the login role's, or this database's, own connection limit is.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole server, deliberately, and only as a count.</b> Every other session read is bounded to this
/// database; this one cannot be, because <c>max_connections</c> is the server's and every database on it spends
/// it. Only the number of client backends is read, and nothing about the other databases' sessions.
/// </para>
/// <para>
/// <b>Whole without <c>pg_read_all_stats</c>, by counting what it cannot see into.</b> <c>pg_stat_activity</c>
/// has a row for every backend for every role, but a role without <c>pg_read_all_stats</c> reads another role's
/// row with its <c>backend_type</c> null. Such a row is counted when it has a role behind it and a database
/// (<see cref="PostgresStudio.Internal.Sql.HealthConnectionQueries.ClientSession" />), which every client session has - so a
/// hidden background worker running for a role in a database is counted too, and the count can be over by that
/// much, never under.
/// </para>
/// <para>
/// Background workers, autovacuum and (from PostgreSQL 12) replication senders have slots of their own and are
/// not counted against <c>max_connections</c>, so they are left out. A superuser may use the reserved slots,
/// which is how the count can pass what an ordinary role may open.
/// </para>
/// <para>
/// <b>The limits Postgres enforces first</b> (W5-AUR, the 2026-10-26 health-gap analysis). A role's <c>CONNECTION LIMIT</c>
/// refuses "too many connections for role" and a database's "too many connections for database" while
/// <c>max_connections</c> still has room - so the login role's (against its sessions on the whole server) and this database's
/// (against its client sessions) are judged on the same 80 % and 95 % lines, and the finding names the nearest of the three:
/// variant <c>role</c> or <c>database</c>, and the server's own finding as it always was (its variant stays unset until the
/// API packet's split, VERS's ruling). Neither limit is enforced against a superuser, so a superuser login is judged against
/// the server alone (S04's <c>superuser</c> variant says why that is a problem of its own). This process's pool ceiling - C01's
/// - is compared with the role's limit: "this process alone may open 100 connections as role app, which may open 50".
/// </para>
/// <para>
/// <b>Worded for the platform</b> (issue #8 A6, A8). On Aurora <c>max_connections</c> is not a setting to raise: Aurora derives
/// it from the instance's memory, and on Serverless v2 from the memory of the cluster's maximum capacity, which it holds while
/// the instance scales - "When Aurora serverless evaluates the formula, it uses the memory size based on the maximum Aurora
/// capacity units (ACUs) for the DB instance, not the current ACU value" (<i>Performance and scaling for Aurora serverless</i>,
/// Aurora User Guide, read 2026-10-26). So with an <c>ACU</c> reading from the host's capacity source the remedy is the
/// cluster's maximum capacity, named; without one it says both cases and asks for a source. Every other platform's remedy is
/// the text it always was. A capacity reading, on any platform, is evidence.
/// </para>
/// </remarks>
[HealthCheck("C03", HealthArea.Connections, HealthCostTier.Catalog, "Server connection headroom", Variants = ["server", "role", "database"])]
internal sealed class ServerConnectionHeadroomCheck : HealthCheck<ServerConnectionHeadroomFacts>
{
    /// <summary>The variant of a finding about the login role's own limit.</summary>
    public const string RoleVariant = "role";

    /// <summary>The variant of a finding about this database's own limit.</summary>
    public const string DatabaseVariant = "database";

    /// <inheritdoc />
    public override async Task<ServerConnectionHeadroomFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ServerConnectionLimits limits = await ServerConnectionLimits.SharedAsync(context).ConfigureAwait(false);

        // The platform and the capacity only word the finding: a failure of either judges the budget as on a server of
        // somebody's own, with no capacity, as C08 does (W5-AUR-fix F4) - S01's row says why the platform read failed.
        PostgresPlatform platform = PostgresPlatform.SelfHosted;
        CapacityReading? capacity = null;

        try
        {
            platform = (await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false)).Platform;
            capacity = await CapacityReading.SharedAsync(context).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            capacity = null;
        }

        int? pool = DatabasePoolSettings.Of(context.Target) is { Pooling: true } settings ? settings.MaxPoolSize : null;

        return new ServerConnectionHeadroomFacts(limits, platform, capacity, pool);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ServerConnectionHeadroomFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        ServerConnectionLimits limits = facts.Limits;

        double share = (double) limits.ClientBackends / limits.Available;
        string used = limits.ClientBackends.ToString("N0", CultureInfo.InvariantCulture) + " of "
            + limits.Available.ToString("N0", CultureInfo.InvariantCulture);

        // The nearest limit: the largest share, and of two alike the one with fewer connections left.
        List<(string? Variant, double Share, long Left)> candidates = [(null, share, limits.Available - limits.ClientBackends)];

        if (limits.RoleLimited)
        {
            candidates.Add((RoleVariant, Share(limits.RoleSessions, limits.RoleLimit), limits.RoleLimit - limits.RoleSessions));
        }

        if (limits.DatabaseLimited)
        {
            candidates.Add((DatabaseVariant, Share(limits.ThisDatabase, limits.DatabaseLimit), limits.DatabaseLimit - limits.ThisDatabase));
        }

        (string? nearest, double nearestShare, _) = candidates.OrderByDescending(static x => x.Share).ThenBy(static x => x.Left).First();

        if (nearestShare <= ConnectionThresholds.ServerUsedWarning)
        {
            return HealthVerdict.Quiet(used + " connections an ordinary role may open are in use on this server." + LimitsNote(facts));
        }

        bool critical = nearestShare >= ConnectionThresholds.ServerUsedCritical;

        return HealthVerdict.Of(nearest switch
        {
            RoleVariant => RoleFinding(facts, critical, used),
            DatabaseVariant => DatabaseFinding(facts, critical, used),
            _ => ServerFinding(facts, critical, used, share),
        });
    }

    /// <summary>The server's own finding - its text as it always was, but where the platform or a capacity reading adds to it.</summary>
    private static HealthFinding ServerFinding(ServerConnectionHeadroomFacts facts, bool critical, string used, double share)
    {
        ServerConnectionLimits limits = facts.Limits;

        List<HealthEvidence> evidence =
        [
            new("In use", used + " (" + SessionText.Percent(share) + ")"),
            new("To this database", limits.ThisDatabase.ToString("N0", CultureInfo.InvariantCulture)),
            new("Available to ordinary roles", limits.AvailableText),
        ];

        evidence.AddRange(LimitEvidence(facts));

        return new HealthFinding
        {
            Code = "C03",
            Severity = critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Title = critical
                ? "The server has almost no connections left"
                : "The server's connections are more than " + SessionText.Percent(ConnectionThresholds.ServerUsedWarning) + " used",
            WhyItMatters =
                "Once every connection an ordinary role may open is taken, the next one is refused with \"sorry, too many clients "
                + "already\" - a pool that wants to grow, a deployment's new instance, the async daemon's next connection, a person "
                + "with psql. Only superusers keep the reserved slots. Connections to every database on the server count, this "
                + "store's and everybody else's.",
            Evidence = evidence,
            Caveat =
                "Counts every client backend on the server, every database included. A session the studio's role may not read into "
                + "is counted too, because Postgres withholds its backend type rather than its row - so a hidden background worker that "
                + "runs for a role can be counted with them. Background workers it can see, autovacuum and replication have slots of "
                + "their own and are not counted. A moment's reading: a burst can come and go between two checks.",
            Remedy = facts.Platform.Kind == PostgresPlatformKind.AmazonAurora
                ? AuroraRemedy(facts.Capacity, limits)
                : new HealthRemedy(
                    """
                    // Fewer connections per process - every data source Marten builds from a connection string:
                    opts.ConfigureNpgsqlDataSourceBuilder(b => b.ConnectionStringBuilder.MaxPoolSize = 50);
                    """,
                    "Lower pool ceilings across the applications that share the server, or a pooler such as PgBouncer in front of it. "
                    + "max_connections itself is the server's setting, which the studio never changes; raising it costs memory per "
                    + "connection on the server."),
        };
    }

    /// <summary>Aurora caps <c>max_connections</c> at this with a minimum capacity of 0 or 0.5 ACU.</summary>
    internal const int AuroraLowMinimumCap = 2_000;

    /// <summary>Aurora PostgreSQL's default <c>max_connections</c> from <see cref="AuroraCeilingAcu" /> upward: its ceiling.</summary>
    internal const int AuroraCeiling = 5_000;

    /// <summary>The maximum capacity from which Aurora PostgreSQL's default <c>max_connections</c> is <see cref="AuroraCeiling" />.</summary>
    internal const double AuroraCeilingAcu = 32;

    /// <summary>
    /// What raises Aurora's connections (A6, W5-AUR-fix F1), from the capacity range and <c>max_connections</c> - never
    /// <c>max_connections</c> in a file nobody on Aurora has.
    /// </summary>
    /// <remarks>
    /// <para>
    /// From <i>Performance and scaling for Aurora serverless</i>, Aurora User Guide
    /// (https://docs.aws.amazon.com/AmazonRDS/latest/AuroraUserGuide/aurora-serverless-v2.setting-capacity.html, read 2026-10-26):
    /// "When Aurora serverless evaluates the formula, it uses the memory size based on the maximum Aurora capacity units (ACUs)
    /// for the DB instance, not the current ACU value"; "when you specify a minimum capacity of 0 or 0.5 ACUs on
    /// PostgreSQL-compatible DB instances, the maximum value of <c>max_connections</c> is capped at 2,000"; the default table
    /// gives 5,000 from 32 ACU upward on every platform version; <c>max_connections</c> "is a static parameter for Aurora
    /// serverless", so a new range takes effect when the instances reboot; and "If you change the default value, we recommend
    /// using a variation of the formula instead of specifying a constant value" - a custom parameter group's value replaces
    /// the formula.
    /// </para>
    /// <list type="number">
    /// <item>A minimum of 0.5 ACU or less with <c>max_connections</c> at the 2,000 cap: raise the minimum to 1 ACU or more.</item>
    /// <item>A maximum of 32 ACU or more, or <c>max_connections</c> at 5,000: Aurora's ceiling - fewer connections, or a pooler.</item>
    /// <item>Otherwise, with an ACU reading: raise the maximum.</item>
    /// <item>Without one: both cases, and a capacity source to tell them apart.</item>
    /// </list>
    /// </remarks>
    internal static HealthRemedy AuroraRemedy(CapacityReading? capacity, ServerConnectionLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);

        const string Source = "(Performance and scaling for Aurora serverless, Aurora User Guide)";
        const string Override =
            "A max_connections set in a custom DB cluster or DB parameter group replaces Aurora's formula whatever the capacity, and "
            + "AWS recommends \"a variation of the formula instead of specifying a constant value\".";
        const string Pools = "Or lower the pool ceilings of the applications that share the cluster.";

        string now = "max_connections is " + limits.MaxConnections.ToString("N0", CultureInfo.InvariantCulture);
        PostgresComputeCapacity? acu = capacity is { IsAcu: true, Capacity: { } reading } ? reading : null;

        if (acu is not null && acu.Minimum <= 0.5 && limits.MaxConnections >= AuroraLowMinimumCap && limits.MaxConnections < AuroraCeiling)
        {
            return HealthRemedy.ForManagedPlatform(
                "Raise the cluster's minimum capacity to 1 ACU or more (now " + CapacityReading.Amount(acu.Minimum) + " ACU)",
                now + ", Aurora's cap for a minimum capacity of 0 or 0.5 ACU: \"the maximum value of max_connections is capped at "
                + "2,000\" whatever the maximum " + Source + ". With a minimum of 1 ACU or more it follows the maximum capacity (now "
                + CapacityReading.Amount(acu.Maximum) + " ACU) again, up to 5,000, once the instances reboot - max_connections \"is a "
                + "static parameter for Aurora serverless\". " + Override + " " + Pools);
        }

        if (limits.MaxConnections >= AuroraCeiling || acu?.Maximum >= AuroraCeilingAcu)
        {
            return HealthRemedy.ForManagedPlatform(
                "Lower the pool ceilings of the applications that share the cluster, or put RDS Proxy or PgBouncer in front of it",
                now + (acu is null ? string.Empty : " with a maximum capacity of " + CapacityReading.Amount(acu.Maximum) + " ACU")
                + ": at Aurora's ceiling. Its default max_connections is 5,000 from a maximum of 32 ACU upward " + Source
                + ", so more capacity adds no connections; a pooler shares fewer server connections among more clients. " + Override);
        }

        if (acu is not null)
        {
            return HealthRemedy.ForManagedPlatform(
                "Raise the cluster's maximum capacity (now " + CapacityReading.Amount(acu.Maximum) + " ACU)",
                "On Aurora Serverless v2 max_connections follows the maximum capacity: \"it uses the memory size based on the maximum "
                + "Aurora capacity units (ACUs) for the DB instance, not the current ACU value\" " + Source + ". Its default grows with "
                + "the maximum up to 5,000 at 32 ACU, and a new maximum takes effect when the instances reboot - max_connections \"is "
                + "a static parameter for Aurora serverless\". " + Override + " " + Pools);
        }

        return HealthRemedy.ForManagedPlatform(
            "On Aurora Serverless v2: raise the cluster's maximum capacity (ACU), or its minimum to 1 ACU or more if it is 0 or 0.5\n"
            + "On a provisioned instance: a larger instance class, or max_connections in the DB cluster parameter group",
            "Aurora derives max_connections from the instance's memory - \"derived from a formula based on the memory size of the DB "
            + "instance\" - and on Serverless v2 from the memory of the cluster's maximum capacity, which it holds while the instance "
            + "scales " + Source + ". On Serverless v2 a minimum capacity of 0 or 0.5 ACU caps it at 2,000, and from 32 ACU it is "
            + "5,000, Aurora's ceiling, where only fewer connections or a pooler such as RDS Proxy or PgBouncer helps. " + Override
            + " The studio cannot tell which this cluster is, nor its capacity: register an IPostgresCapacitySource (docs/aurora.md) "
            + "and this remedy says which applies.");
    }

    private static HealthFinding RoleFinding(ServerConnectionHeadroomFacts facts, bool critical, string used)
    {
        ServerConnectionLimits limits = facts.Limits;
        string role = limits.LoginRole ?? "(the login role)";
        double share = Share(limits.RoleSessions, limits.RoleLimit);

        List<HealthEvidence> evidence =
        [
            new("In use", Count(limits.RoleSessions) + " of " + Count(limits.RoleLimit) + " (" + SessionText.Percent(share) + ")"),
            new("Role", role + ", CONNECTION LIMIT " + Count(limits.RoleLimit) + ", counted in every database on the server"),
        ];

        if (PoolSentence(facts) is { } pool)
        {
            evidence.Add(new HealthEvidence("This process's pool", pool));
        }

        evidence.Add(new HealthEvidence("Server", used + " connections an ordinary role may open"));

        if (facts.Capacity?.Text is { } capacity)
        {
            evidence.Add(new HealthEvidence("Compute capacity", capacity));
        }

        return new HealthFinding
        {
            Code = "C03",
            Variant = RoleVariant,
            Severity = critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Title = critical
                ? "The login role has almost no connections left under its connection limit"
                : "The login role's sessions are more than " + SessionText.Percent(ConnectionThresholds.ServerUsedWarning) + " of its connection limit",
            WhyItMatters =
                "Once the role the data source logs in as has as many sessions as its CONNECTION LIMIT, the next connection is refused "
                + "with \"too many connections for role\" - whatever max_connections has left: a pool that wants to grow, a "
                + "deployment's new instance, the async daemon's next connection. Every session of the role counts, in every database "
                + "on the server. The limit is not enforced against a superuser.",
            Evidence = evidence,
            Caveat =
                "Counts the role's client sessions on the whole server in pg_stat_activity, which every role may read - as Postgres "
                + "counts them against its limit. A moment's reading: a burst can come and go between two checks.",
            Remedy = PoolRemedy(limits.RoleLimit, facts.PoolCeiling,
                "So that the processes that log in as " + role + " stay under its limit together - every one of them shares it. "
                + "Raising the limit itself (ALTER ROLE ... CONNECTION LIMIT) is for whoever administers the role."),
        };
    }

    private static HealthFinding DatabaseFinding(ServerConnectionHeadroomFacts facts, bool critical, string used)
    {
        ServerConnectionLimits limits = facts.Limits;
        double share = Share(limits.ThisDatabase, limits.DatabaseLimit);

        List<HealthEvidence> evidence =
        [
            new("In use", Count(limits.ThisDatabase) + " of " + Count(limits.DatabaseLimit) + " (" + SessionText.Percent(share) + ")"),
            new("Database", "CONNECTION LIMIT " + Count(limits.DatabaseLimit) + ", every role's sessions counted"),
            new("Server", used + " connections an ordinary role may open"),
        ];

        if (facts.Capacity?.Text is { } capacity)
        {
            evidence.Add(new HealthEvidence("Compute capacity", capacity));
        }

        return new HealthFinding
        {
            Code = "C03",
            Variant = DatabaseVariant,
            Severity = critical ? HealthSeverity.Critical : HealthSeverity.Warning,
            Title = critical
                ? "This database has almost no connections left under its connection limit"
                : "This database's sessions are more than " + SessionText.Percent(ConnectionThresholds.ServerUsedWarning) + " of its connection limit",
            WhyItMatters =
                "Once this database has as many sessions as its CONNECTION LIMIT, the next connection to it is refused with \"too many "
                + "connections for database\" - whatever max_connections has left. Every role's sessions in it count, the studio's "
                + "included. The limit is not enforced against a superuser.",
            Evidence = evidence,
            Caveat =
                "Counts this database's client sessions in pg_stat_activity, as Postgres counts them against its limit. A moment's "
                + "reading: a burst can come and go between two checks.",
            Remedy = PoolRemedy(limits.DatabaseLimit, facts.PoolCeiling,
                "So that the processes that connect to this database stay under its limit together - every one of them shares it. "
                + "Raising the limit itself (ALTER DATABASE ... CONNECTION LIMIT) is for whoever owns the database."),
        };
    }

    /// <summary>
    /// The pool ceiling that keeps this one process under the warning line of <paramref name="limit" />, as the connection
    /// string's keyword - a line for the host's own configuration, never a statement: the limit itself is the DBA's.
    /// </summary>
    private static HealthRemedy PoolRemedy(int limit, int? ceiling, string explanation)
    {
        int suggested = Math.Max(1, (int) Math.Floor(limit * ConnectionThresholds.ServerUsedWarning));

        return new HealthRemedy(
            "Maximum Pool Size=" + suggested.ToString(CultureInfo.InvariantCulture),
            (ceiling is { } now ? "This process's pool ceiling is " + Count(now) + " now. " : string.Empty) + explanation);
    }

    /// <summary>The role's and the database's limits, as evidence of the server's finding - only those that apply.</summary>
    private static IEnumerable<HealthEvidence> LimitEvidence(ServerConnectionHeadroomFacts facts)
    {
        ServerConnectionLimits limits = facts.Limits;

        if (limits.RoleLimited)
        {
            yield return new HealthEvidence("Role connection limit", Count(limits.RoleSessions) + " of " + Count(limits.RoleLimit)
                + " for role " + (limits.LoginRole ?? "(the login role)"));
        }

        if (limits.DatabaseLimited)
        {
            yield return new HealthEvidence("Database connection limit", Count(limits.ThisDatabase) + " of " + Count(limits.DatabaseLimit));
        }

        if (PoolSentence(facts) is { } pool)
        {
            yield return new HealthEvidence("This process's pool", pool);
        }

        if (facts.Capacity?.Text is { } capacity)
        {
            yield return new HealthEvidence("Compute capacity", capacity);
        }
    }

    /// <summary>What the quiet row adds for a limit that applies, a pool that can pass the role's, and a capacity reading.</summary>
    private static string LimitsNote(ServerConnectionHeadroomFacts facts)
    {
        ServerConnectionLimits limits = facts.Limits;
        List<string> notes = [];

        if (limits.RoleLimited)
        {
            notes.Add("Role " + (limits.LoginRole ?? "(the login role)") + " has " + Count(limits.RoleSessions) + " of its "
                + Count(limits.RoleLimit) + " connections.");
        }

        if (limits.DatabaseLimited)
        {
            notes.Add("This database has " + Count(limits.ThisDatabase) + " of its " + Count(limits.DatabaseLimit) + " connections.");
        }

        if (PoolSentence(facts) is { } pool)
        {
            notes.Add(char.ToUpperInvariant(pool[0]) + pool[1..] + ".");
        }

        if (facts.Capacity?.Text is { } capacity)
        {
            notes.Add("Compute capacity " + capacity + ".");
        }

        return notes.Count == 0 ? string.Empty : " " + string.Join(" ", notes);
    }

    /// <summary>"this process alone may open 100 connections as role app, which may open 50" - when the pool can pass the role's limit.</summary>
    private static string? PoolSentence(ServerConnectionHeadroomFacts facts)
    {
        ServerConnectionLimits limits = facts.Limits;

        return limits.RoleLimited && facts.PoolCeiling is { } ceiling && ceiling > limits.RoleLimit
            ? "this process alone may open " + Count(ceiling) + " connections as role " + (limits.LoginRole ?? "(the login role)")
              + ", which may open " + Count(limits.RoleLimit)
            : null;
    }

    private static double Share(long used, int limit) => (double) used / Math.Max(1, limit);

    private static string Count(long value) => value.ToString("N0", CultureInfo.InvariantCulture);
}
