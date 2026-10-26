using System.Globalization;

using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What C02 reads.</summary>
/// <param name="DatabasesOnServer">How many of the store's databases point at this database's server.</param>
/// <param name="PooledOnServer">How many of those pool.</param>
/// <param name="CeilingsOnServer">Their <c>Max Pool Size</c>, summed.</param>
/// <param name="UnpooledOnServer">How many do not pool, and so have no ceiling at all.</param>
/// <param name="DatabasesInStore">How many databases the store has, as far as it could be told without I/O.</param>
/// <param name="Server">The server's own budget.</param>
/// <param name="Estimated">
/// Whether the sum is an estimate: a master-table or sharded tenancy is counted from the databases it holds,
/// each taken to have this database's ceiling and to share its server, because asking it for its databases is
/// not a read.
/// </param>
internal sealed record ConnectionBudgetFacts(
    int DatabasesOnServer,
    int PooledOnServer,
    long CeilingsOnServer,
    int UnpooledOnServer,
    int DatabasesInStore,
    ServerConnectionLimits Server,
    bool Estimated = false)
{
    /// <summary>
    /// The <c>Max Pool Size</c> of the pooled databases on this server that log in as the login role, summed - what the role's
    /// <c>CONNECTION LIMIT</c> is spent by (<see cref="ConnectionBudgetCheck.SameLogin" />). Zero when none is known to.
    /// </summary>
    public long CeilingsAsLogin { get; init; }
}

/// <summary>What C02 counts of a store's databases on one server, read from their connection strings without I/O.</summary>
/// <param name="OnServer">How many point at the server.</param>
/// <param name="Pooled">How many of those pool.</param>
/// <param name="Ceilings">Their <c>Max Pool Size</c>, summed.</param>
/// <param name="Unpooled">How many do not pool.</param>
/// <param name="CeilingsAsLogin">The <c>Max Pool Size</c> of the pooled ones that log in as the login role, summed.</param>
internal readonly record struct ConnectionBudgetTally(int OnServer, int Pooled, long Ceilings, int Unpooled, long CeilingsAsLogin);

/// <summary>
/// C02: a store with a database per tenant whose pools, together, can open more connections to one server than
/// that server accepts.
/// </summary>
/// <remarks>
/// <para>
/// Each of a store's databases has a data source - and a pool - of its own, and every connection Marten makes
/// to that database, the async daemon's leadership lock included, comes out of it. So the most this process can
/// hold open to one server is the sum of the ceilings of the store's databases on it. When that is more than
/// <c>max_connections</c> less what is reserved, enough concurrent tenants exhaust the server for everybody -
/// this application's other tenants, and every other application on it.
/// </para>
/// <para>
/// <b>Listed without making a tenancy do anything</b> (<see cref="HeldDatabases" />, shared with the health
/// export and the daemon card): a single or static tenancy's databases are configuration; a single-server
/// tenancy's are the ones it has built, from memory; a master-table or sharded tenancy's are counted from the
/// ones it holds, each found in its own cache, because <c>AllDatabases()</c> on those reads the master table on
/// every call and applies it on the first. Databases are grouped by host and port as their connection strings
/// spell them, read from each data source without a connection.
/// </para>
/// <para>
/// A store with one database on the server is C03's to watch: one pool against the server is the ordinary case,
/// and a check that fired on Npgsql's default of 100 against Postgres' default of 100 would fire for everybody.
/// </para>
/// </remarks>
[HealthCheck("C02", HealthArea.Connections, HealthCostTier.Catalog, "Connection budget across databases")]
internal sealed class ConnectionBudgetCheck : HealthCheck<ConnectionBudgetFacts>
{
    /// <inheritdoc />
    public override async Task<ConnectionBudgetFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DatabasePoolSettings mine = DatabasePoolSettings.Of(context.Database)
            ?? throw new HealthCheckUnavailableException(
                "This database's data source is not one the studio can read pool settings from, so the store's budget is not judged.");

        HeldDatabaseList listed = await HeldDatabases.ListAsync(context.Store).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (!listed.IsKnown)
        {
            throw new HealthCheckUnavailableException(
                "This store's tenancy can list its databases only by building or querying them, so its connection budget is not judged.");
        }

        ServerConnectionLimits server = await ServerConnectionLimits.SharedAsync(context).ConfigureAwait(false);

        if (listed.Shape == TenancyShape.Held)
        {
            // Counted, as C02 has always counted a master-table or sharded tenancy: each held database is taken
            // to be like this one - those the helper could not name from the cache included, since they are held.
            int count = Math.Max(listed.Databases.Count + listed.Unnamed, 1);

            long estimate = mine.Pooling ? (long) count * mine.MaxPoolSize : 0;

            return new ConnectionBudgetFacts(
                count,
                mine.Pooling ? count : 0,
                estimate,
                mine.Pooling ? 0 : count,
                count,
                server,
                Estimated: true)
            {
                // Taken to be like this one in its login too: this database is the one the role was read through.
                CeilingsAsLogin = estimate,
            };
        }

        ConnectionBudgetTally tally = Tally([.. listed.Databases.Select(static database => DatabasePoolSettings.Of(database))], mine, server.LoginRole);

        return new ConnectionBudgetFacts(tally.OnServer, tally.Pooled, tally.Ceilings, tally.Unpooled, listed.Databases.Count, server)
        {
            CeilingsAsLogin = tally.CeilingsAsLogin,
        };
    }

    /// <summary>
    /// Counts <paramref name="databases" /> on <paramref name="mine" />'s server - a database whose pool settings could not be
    /// read (<see langword="null" />) is not counted - and sums the ceilings of those that log in as <paramref name="loginRole" />.
    /// </summary>
    /// <param name="databases">The store's databases' pool settings.</param>
    /// <param name="mine">This database's, the one the server was read through.</param>
    /// <param name="loginRole">The role this database's sessions log in as (<c>session_user</c>).</param>
    internal static ConnectionBudgetTally Tally(IReadOnlyList<DatabasePoolSettings?> databases, DatabasePoolSettings mine, string? loginRole)
    {
        ArgumentNullException.ThrowIfNull(databases);
        ArgumentNullException.ThrowIfNull(mine);

        int onServer = 0;
        int pooled = 0;
        int unpooled = 0;
        long ceilings = 0;
        long asLogin = 0;

        foreach (DatabasePoolSettings? settings in databases)
        {
            if (settings is null || !string.Equals(settings.ServerKey, mine.ServerKey, StringComparison.Ordinal))
            {
                continue;
            }

            onServer++;

            if (settings.Pooling)
            {
                pooled++;
                ceilings += settings.MaxPoolSize;

                if (SameLogin(settings, mine, loginRole))
                {
                    asLogin += settings.MaxPoolSize;
                }
            }
            else
            {
                unpooled++;
            }
        }

        return new ConnectionBudgetTally(onServer, pooled, ceilings, unpooled, asLogin);
    }

    /// <summary>
    /// Whether <paramref name="database" />'s pool logs in as the login role, as far as connection strings say: it names the
    /// same <c>Username</c> as <paramref name="mine" /> - the database the role was read through, both naming none included,
    /// since Npgsql then takes the user from the process's own environment - or names <paramref name="loginRole" /> itself.
    /// Postgres compares a role name exactly, so this does too. A pool that names no user while this one names one is not
    /// counted: what it logs in as is not known without connecting, and the role sentence errs towards a smaller sum.
    /// </summary>
    internal static bool SameLogin(DatabasePoolSettings database, DatabasePoolSettings mine, string? loginRole)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(mine);

        return string.Equals(database.Username, mine.Username, StringComparison.Ordinal)
            || (database.Username is { } user && loginRole is not null && string.Equals(user, loginRole, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(ConnectionBudgetFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        int available = facts.Server.Available;
        string estimated = facts.Estimated ? ", by estimate," : string.Empty;

        if (facts.PooledOnServer < 2)
        {
            return HealthVerdict.Quiet(
                "This store has " + (facts.DatabasesOnServer <= 1 ? "one database" : SessionText.Count(facts.DatabasesOnServer, "database"))
                + " on this server" + (facts.PooledOnServer == 1 ? " with a pool" : string.Empty)
                + "; one pool against the server's " + available.ToString("N0", CultureInfo.InvariantCulture)
                + " connections is what C03 watches.");
        }

        if (facts.CeilingsOnServer <= available)
        {
            return HealthVerdict.Quiet(
                SessionText.Count(facts.PooledOnServer, "database") + " on this server can open" + estimated + " at most "
                + SessionText.Number(facts.CeilingsOnServer) + " connections between them, within the server's "
                + available.ToString("N0", CultureInfo.InvariantCulture) + "."
                + (RoleSentence(facts) is { } role ? " " + char.ToUpperInvariant(role[0]) + role[1..] + "." : string.Empty));
        }

        List<HealthEvidence> evidence =
        [
            new("Databases on this server", SessionText.Number(facts.DatabasesOnServer)
                + (facts.DatabasesInStore > facts.DatabasesOnServer
                    ? " of the store's " + SessionText.Number(facts.DatabasesInStore)
                    : string.Empty)
                + (facts.Estimated ? " (the ones this process holds)" : string.Empty)),
            new("Max Pool Size, summed", SessionText.Number(facts.CeilingsOnServer) + (facts.Estimated ? " (estimated)" : string.Empty)),
            new("The server accepts", facts.Server.AvailableText),
            new("Client connections now", SessionText.Number(facts.Server.ClientBackends)),
        ];

        if (facts.UnpooledOnServer > 0)
        {
            evidence.Add(new HealthEvidence("Without a pool", SessionText.Count(facts.UnpooledOnServer, "database") + ", which have no ceiling at all"));
        }

        if (RoleSentence(facts) is { } roleLimit)
        {
            evidence.Add(new HealthEvidence("The login role's limit", roleLimit));
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "C02",

            // An estimate assumes every held database shares this one's server and ceiling; said as advice, not alarm.
            Severity = facts.Estimated ? HealthSeverity.Info : HealthSeverity.Warning,
            Title = facts.Estimated
                ? "This store's pools may be able to open more connections to this server than the server accepts"
                : "This store's pools can open more connections to this server than the server accepts",
            WhyItMatters =
                "Each of the store's databases has a pool of its own, and every connection Marten makes to it - the async daemon's "
                + "included - comes out of that pool. Together these pools may open " + SessionText.Number(facts.CeilingsOnServer)
                + " connections from this process alone, and the server takes " + available.ToString("N0", CultureInfo.InvariantCulture)
                + " from ordinary roles. When enough tenants are busy at once, the server refuses new connections with "
                + "\"sorry, too many clients already\" - for this application's other tenants, and for every other application on the server.",
            Evidence = evidence,
            Caveat =
                "This process only: every other process that uses the server - another instance of this application, another "
                + "application - spends the same budget, so the real margin is smaller. Databases are grouped by host and port as "
                + "their connection strings spell them, so one server reached under two names is counted as two."
                + (facts.Estimated
                    ? " This store's tenancy is master-table or sharded, and listing its databases would query its master table, so "
                      + "they are counted from the ones this process holds, each taken to have this database's Max Pool Size and to "
                      + "share its server."
                    : string.Empty),
            Remedy = new HealthRemedy(
                """
                // A ceiling per database that fits the server:
                //   databases on the server x Maximum Pool Size (x instances) <= max_connections - reserved
                opts.ConfigureNpgsqlDataSourceBuilder(b => b.ConnectionStringBuilder.MaxPoolSize = 20);
                """,
                "It applies to every data source Marten builds from a connection string, every tenant database's included. A pooler "
                + "such as PgBouncer in front of the server is the other way out; raising max_connections is the server's own "
                + "setting, and the studio never changes it."),
        });
    }

    /// <summary>
    /// "this process alone may open 300 connections as role app, which may open 50" - when the login role has a
    /// <c>CONNECTION LIMIT</c> that the pools on this server logging in as that role can pass together (W5-AUR, C03's role
    /// limit; <see cref="ConnectionBudgetFacts.CeilingsAsLogin" />, so a tenant database that logs in as a role of its own
    /// is not charged to this one), or <see langword="null" />. Not enforced against a superuser, so never said for one.
    /// </summary>
    internal static string? RoleSentence(ConnectionBudgetFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);

        ServerConnectionLimits server = facts.Server;

        return server.RoleLimited && facts.CeilingsAsLogin > server.RoleLimit
            ? "this process alone may open " + SessionText.Number(facts.CeilingsAsLogin) + " connections as role "
              + (server.LoginRole ?? "(the login role)") + ", which may open " + SessionText.Number(server.RoleLimit)
            : null;
    }
}
