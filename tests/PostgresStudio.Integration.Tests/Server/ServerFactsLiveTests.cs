using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Integration.Tests.Versions;
using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Integration.Tests.Server;

/// <summary>
/// The Server page's facts on PostgreSQL 14 to 18 as <c>app</c> and as <c>probe</c> (W5-S2): every section reads, with no
/// SQLSTATE; a role without <c>pg_read_all_settings</c> is told which settings it is not shown; the facts create nothing; and
/// <c>pg_stat_statements</c>' chip agrees with the statement checks about whether its library is loaded.
/// </summary>
/// <remarks>
/// The no-lock half is the version matrix's: the facts read is a <see cref="MatrixReads" /> entry, run behind an
/// <c>ACCESS EXCLUSIVE</c> lock under a 300 ms <c>lock_timeout</c> on every leg (<c>VersionMatrixLiveTests</c>).
/// </remarks>
public class ServerFactsLiveTests(PostgresVersionMatrix matrix, PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<int, MatrixRole> Legs
    {
        get
        {
            TheoryData<int, MatrixRole> legs = [];

            foreach (MatrixVersion version in PostgresVersionMatrix.Versions.Where(static x => !x.Preview))
            {
                legs.Add(version.Major, MatrixRole.App);
                legs.Add(version.Major, MatrixRole.Probe);
            }

            return legs;
        }
    }

    [PostgresTheory]
    [MemberData(nameof(Legs))]
    public async Task The_facts_read_on_every_version_say_what_the_role_is_not_shown_and_create_nothing(int major, MatrixRole role)
    {
        MatrixServer server = await matrix.ServerAsync(major);
        server.SkipIfUnavailable();

        IReadOnlySet<string> before = await server.SnapshotAsync(cancellationToken: Token);
        await using MatrixHost host = await MatrixHost.CreateAsync(server, role);

        ServerSettingsFacts facts = await host.Services.GetRequiredService<IServerSettingsFactsReader>().ReadAsync(host.Resolved, Token);

        facts.Failures.Should().BeEmpty("every section reads on " + major + " as " + role);
        facts.Server.VersionNumber.Should().Be(server.VersionNumber);

        IReadOnlyList<ServerSettingRow> rows = facts.CuratedSettings();
        List<string> hidden = [.. rows.Where(static x => x.State == ServerSettingState.Hidden).Select(static x => x.Name)];
        List<string> needs = [.. rows.Where(static x => x.State == ServerSettingState.NeedsVersion).Select(static x => x.Name)];

        TestContext.Current.TestOutputHelper?.WriteLine("PostgreSQL {0} as {1}: hidden [{2}]; needs a newer release [{3}]",
            server.ServerVersion, role, string.Join(", ", hidden), string.Join(", ", needs));

        hidden.Should().Contain("shared_preload_libraries", "only a superuser or pg_read_all_settings is shown it");
        rows.Should().NotContain(static x => x.State == ServerSettingState.Absent, "a role short of pg_read_all_settings calls a missing row hidden");
        rows.Where(static x => x.Group == "planner" && x.Name.StartsWith("enable_", StringComparison.Ordinal)).Should().NotBeEmpty("the family reads");
        rows.Where(static x => x.Name.StartsWith("autovacuum", StringComparison.Ordinal)).Should().NotBeEmpty("the family reads");

        needs.Should().BeEquivalentTo(ServerSettingsCatalog.Settings
            .Where(x => x.Since is { } since && !PostgresFeatures.Has(since, server.VersionNumber))
            .Select(static x => x.Name));

        // The three the studio's own transaction sets for itself are read as a new session starts with them.
        foreach (string name in ServerPostureQueries.StudioSetSettings)
        {
            ServerSettingRow row = rows.Single(x => x.Name == name);
            row.State.Should().Be(ServerSettingState.Read);
            row.Setting!.StudioSet.Should().BeTrue(name + " is set by the studio's read-only transaction");
            row.Setting.Value.Should().Be("0", name + "'s server default, not the studio's own value");
            row.Setting.Source.Should().Be("default");
        }

        ServerConnectionPosture posture = facts.Posture.Value!;
        posture.LoginRole.Should().Be(role == MatrixRole.App ? "app" : "probe");
        posture.StatementsLoaded.Should().BeTrue("the matrix preloads pg_stat_statements");
        facts.Chips().Single(x => x.Name == "pg_stat_statements").Usable.Should().BeTrue();

        (await server.SnapshotAsync(cancellationToken: Token)).Should().BeEquivalentTo(before, "a read path creates nothing");
    }

    /// <summary>
    /// On a server that does not preload <c>pg_stat_statements</c>, with the extension created in the database, the chip says
    /// its library is not loaded - and the statement checks' own read says the same, by its <c>55000</c>.
    /// </summary>
    [PostgresFact]
    public async Task The_chip_and_the_statement_checks_agree_the_library_is_not_loaded_when_it_is_not_preloaded()
    {
        string connectionString = await postgres.CreateDatabaseAsync("s2_unloaded", Token);

        await using (NpgsqlConnection connection = new(connectionString))
        {
            await connection.OpenAsync(Token);
            await using NpgsqlCommand command = new("create extension if not exists pg_stat_statements", connection);
            await command.ExecuteNonQueryAsync(Token);
        }

        await using PostureHost host = await PostureHost.CreateAsync(connectionString);

        ServerSettingsFacts facts = await host.FactsAsync();
        ServerExtensionStatus chip = facts.Chips().Single(x => x.Name == "pg_stat_statements");

        chip.InstalledVersion.Should().NotBeNull();
        chip.Usable.Should().BeFalse();
        chip.Note.Should().Contain("its library is not loaded");

        HealthSweep sweep = await host.SweepAsync(HealthSweepKind.Statements);
        HealthCheckResult statements = sweep.Results.Single(x => x.Check.Code == "T02");
        statements.Outcome.Should().Be(HealthCheckOutcome.Unavailable);
        statements.Problem!.Reason.Should().Be(StatementReads.NotPreloaded, "the statement checks tell the same state by the function's own 55000");
    }

    /// <summary>The settings <c>probe</c> is not shown, per version, for the report - and that they are the same on every leg.</summary>
    [PostgresFact]
    public async Task Probe_is_not_shown_the_same_settings_on_14_to_18()
    {
        StringBuilder report = new();
        HashSet<string>? first = null;

        foreach (MatrixVersion version in PostgresVersionMatrix.Versions.Where(static x => !x.Preview))
        {
            MatrixServer server = await matrix.ServerAsync(version.Major);
            server.SkipIfUnavailable();

            await using MatrixHost host = await MatrixHost.CreateAsync(server, MatrixRole.Probe);
            ServerSettingsFacts facts = await host.Services.GetRequiredService<IServerSettingsFactsReader>().ReadAsync(host.Resolved, Token);

            HashSet<string> hidden = [.. facts.CuratedSettings().Where(static x => x.State == ServerSettingState.Hidden).Select(static x => x.Name)];
            report.AppendLine(server.ServerVersion + ": " + string.Join(", ", hidden.Order(StringComparer.Ordinal)));

            first ??= hidden;
            hidden.Should().BeEquivalentTo(first, "probe is shown the same curated settings on every release");
        }

        TestContext.Current.TestOutputHelper?.WriteLine(report.ToString());
    }
}
