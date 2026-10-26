using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Server;

namespace MartenStudio.Integration.Tests.Versions;

/// <summary>
/// A7's mechanism and the PostgreSQL 18 NOT NULL fix against real servers: the grant read answers for the role the reads
/// run as on every version; the managed-platform detector takes the Aurora shim for Aurora and the "not checked" sentence
/// names Aurora's grantor; the untrusted extensions are what <see cref="PostgresGrants.KnownExtensions" /> says; and the
/// database browser's constraint list carries no NOT NULL row on 18, and the same rows as before on 17.
/// </summary>
public class ServerGrantsLiveTests(PostgresVersionMatrix matrix)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// <c>stats</c> holds <c>pg_read_all_stats</c>, <c>mon</c> holds it through <c>pg_monitor</c>, <c>probe</c> holds
    /// nothing, the superuser everything - and <c>pg_maintain</c> exists exactly from 17, a provider's role nowhere, with no
    /// error for either.
    /// </summary>
    [PostgresTheory]
    [MemberData(nameof(PostgresVersionMatrix.All), MemberType = typeof(PostgresVersionMatrix))]
    public async Task The_grant_read_answers_for_the_reading_role_on_every_version(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        PostgresGrants stats = await GrantsAsync(server, MatrixRole.Stats);
        stats.ReadsAs.Should().Be("stats");
        stats.Superuser.Should().BeFalse();
        stats.Holds(PostgresGrant.ReadAllStats).Should().BeTrue();
        stats.Holds(PostgresGrant.Monitor).Should().BeFalse();

        PostgresGrants mon = await GrantsAsync(server, MatrixRole.Monitor);
        mon.Holds(PostgresGrant.ReadAllStats).Should().BeTrue("pg_monitor includes pg_read_all_stats");
        mon.Holds(PostgresGrant.StatScanTables).Should().BeTrue("pg_monitor includes pg_stat_scan_tables");
        mon.IsMember("pg_monitor").Should().BeTrue();

        PostgresGrants probe = await GrantsAsync(server, MatrixRole.Probe);
        probe.ReadsAs.Should().Be("probe");
        PostgresGrant.Roles.Should().OnlyContain(x => !probe.Holds(x));
        probe.IsProviderAdmin.Should().BeFalse();

        PostgresGrants superuser = await GrantsAsync(server, MatrixRole.Superuser);
        superuser.Superuser.Should().BeTrue();
        superuser.Holds(PostgresGrant.ReadAllStats).Should().BeTrue();

        probe.Roles.Select(static x => x.Name).Should().Equal(ServerReachQueries.Roles);
        probe.Roles.Single(static x => x.Name == "pg_maintain").Exists.Should().Be(server.VersionNumber >= 170000, "pg_maintain is 17's");
        probe.Roles.Where(x => PostgresGrants.ProviderAdminRoles.Contains(x.Name)).Should().OnlyContain(static x => !x.Exists,
            "a server of somebody's own has no provider's role, and asking about one is no error");
    }

    /// <summary>
    /// The Aurora shim, in a database of its own: the detector takes the server for Aurora, and a "not checked" sentence names
    /// Aurora's grantor - while the server's seeded <c>postgres</c> database is still a server of somebody's own.
    /// </summary>
    [PostgresFact]
    public async Task On_the_Aurora_shim_the_platform_is_Aurora_and_not_checked_names_its_grantor()
    {
        MatrixServer server = await matrix.ServerAsync(17);
        server.SkipIfUnavailable();

        string database = await AuroraShim.ApplyAsync(server);

        await using MatrixHost aurora = await MatrixHost.CreateAsync(server, MatrixRole.Probe, database);
        (HealthCheckContext context, HealthConnection connection) = aurora.Context();

        await using (connection)
        {
            PostgresServerFacts facts = await PostgresServerFacts.ReadAsync(context, Token);
            facts.Platform.Kind.Should().Be(PostgresPlatformKind.AmazonAurora);

            PostgresGrants grants = await PostgresGrants.ReadAsync(context, Token);

            PostgresGrants.NotChecked(PostgresGrant.ReadAllStats, facts.Platform, grants.ReadsAs).Should().Be(
                "Not checked: this needs pg_read_all_stats (pg_monitor includes it) for role probe. "
                + "On Amazon Aurora PostgreSQL the master user (a member of rds_superuser) can grant it.");
        }

        await using MatrixHost selfHosted = await MatrixHost.CreateAsync(server, MatrixRole.Probe);
        (HealthCheckContext plain, HealthConnection plainConnection) = selfHosted.Context();

        await using (plainConnection)
        {
            (await PostgresServerFacts.ReadAsync(plain, Token)).Platform.Kind.Should().Be(PostgresPlatformKind.SelfHosted,
                "the shim lives in its own database, and pg_proc is per database");
        }
    }

    /// <summary>The extensions the studio states the trust of are untrusted, by the server's own catalog (17.11).</summary>
    [PostgresFact]
    public async Task The_known_extensions_trust_is_the_servers()
    {
        MatrixServer server = await matrix.ServerAsync(17);
        server.SkipIfUnavailable();

        await using NpgsqlConnection connection = await server.OpenAsync(cancellationToken: Token);
        await using NpgsqlCommand command = new(
            "select v.name::text, pg_catalog.bool_or(v.trusted) from pg_catalog.pg_available_extension_versions v where v.name = any(@names) group by v.name",
            connection);
        command.Parameters.AddWithValue("names", PostgresGrants.KnownExtensions.Keys.ToArray());

        Dictionary<string, bool> available = [];
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                available[reader.GetString(0)] = reader.GetBoolean(1);
            }
        }

        available.Keys.Should().BeEquivalentTo(["pg_stat_statements", "pgstattuple"], "hypopg is not in the official image (B0 §2)");
        available.Should().OnlyContain(x => PostgresGrants.KnownExtensions[x.Key] == x.Value);
    }

    /// <summary>
    /// PostgreSQL 18 stores NOT NULL as constraints (<c>contype = 'n'</c>); the browser's constraint list leaves them to the
    /// Columns tab, so a table's constraints read the same on 17 and 18 - its key and its check.
    /// </summary>
    [PostgresTheory]
    [InlineData(17)]
    [InlineData(18)]
    public async Task The_constraint_list_carries_no_NOT_NULL_row_on_18_and_is_unchanged_on_17(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        await using NpgsqlConnection connection = await server.OpenAsync(MatrixRole.App, cancellationToken: Token);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Token);

        await using (NpgsqlCommand count = new("select pg_catalog.count(*) from pg_catalog.pg_constraint k where k.conrelid = 'store.child'::pg_catalog.regclass and k.contype = 'n'", connection, transaction))
        {
            long notNull = (long) (await count.ExecuteScalarAsync(Token))!;
            notNull.Should().Be(major >= 18 ? 3 : 0, "id (the key's column), n and label: 18 catalogs each NOT NULL, 17 none (the anti-vacuity half)");
        }

        await DatabaseCatalogQueries.PinSearchPathAsync(connection, transaction, 30, Token);
        CatalogRelationDetail detail = (await DatabaseCatalogQueries.ReadRelationDetailAsync(connection, transaction, "store", "child", 30, Token))!;

        detail.Constraints.Select(static x => (x.Name, x.Kind)).Should().Equal(
            [("child_pkey", "p"), ("child_n_check", "c")],
            "the key and the check - no foreign key (read with both ends named) and no NOT NULL (the Columns tab says it)");
    }

    /// <summary>
    /// A check whose statement Postgres refuses (42501) is not checked - Unavailable, the host's choice - and its row says
    /// which role was refused, that pg_monitor covers the advisor's reads, and who can grant it here.
    /// </summary>
    [PostgresTheory]
    [InlineData(14)]
    [InlineData(18)]
    public async Task A_refused_privilege_is_not_checked_and_names_the_role_the_grant_and_the_grantor(int major)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        await using MatrixHost host = await MatrixHost.CreateAsync(
            server,
            MatrixRole.Probe,
            services: static s => s.AddSingleton(HealthCheckRegistry.ForTypes([typeof(ReadsWhatItMayNotCheck)])));

        HealthCheckResult result = (await host.SweepAsync(HealthSweepKind.Regular)).Results.Single();

        result.Outcome.Should().Be(HealthCheckOutcome.Unavailable);
        result.Problem!.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        result.Problem.UnavailableReason.Should().Be(HealthUnavailableReason.HostChoice);
        result.Problem.Reason.Should().Be(
            "Not checked: Postgres refused role probe a read this check needs (SQLSTATE 42501); pg_monitor covers every statistics "
            + "view the advisor reads. On a self-hosted server a superuser can grant it.");
        result.Statements.Select(static x => x.Sql).Should().Contain(ServerReachQueries.Grants().Sql, "Show SQL carries the grant read the sentence came from");
    }

    /// <summary>A made-up check that reads a table the role has no privilege on.</summary>
    [HealthCheck("T95", HealthArea.Tables, HealthCostTier.Catalog, "Reads a table it may not")]
    private sealed class ReadsWhatItMayNotCheck : HealthCheck<int>
    {
        public override async Task<int> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken) =>
            (await context.Sql.QueryAsync(new HealthQuery("select pg_catalog.count(*)::int4 from store.child"), static r => r.GetInt32(0), cancellationToken)).Single();

        public override HealthVerdict Evaluate(int facts, HealthEvaluation evaluation) => HealthVerdict.Quiet();
    }

    private static async Task<PostgresGrants> GrantsAsync(MatrixServer server, MatrixRole role)
    {
        await using MatrixHost host = await MatrixHost.CreateAsync(server, role);
        (HealthCheckContext context, HealthConnection connection) = host.Context();

        await using (connection)
        {
            return await PostgresGrants.ReadAsync(context, Token);
        }
    }
}
