using MartenStudio.SampleDomain;

using Npgsql;

namespace MartenStudio.Integration.Tests;

/// <summary>
/// The sample's narrowed console role (wave 6) is made and granted safely by hosts that start together, and granted nothing by
/// a login it was not made for.
/// </summary>
/// <remarks>
/// <para>
/// A role of the test's own name, never <see cref="SampleConsoleRole.Name" />: the race is in creating a role the server does
/// not have yet, and the sample's own may already be there, made by another class on this container, and in use by it.
/// </para>
/// <para>
/// The W6 review reproduced the race on Postgres 17.11 before the advisory lock: eight concurrent <c>CREATE ROLE</c>s of one
/// name lost with 23505 as well as <c>duplicate_object</c>, and concurrent grants on one schema with XX000 (<c>tuple
/// concurrently updated</c>).
/// </para>
/// </remarks>
public class SampleConsoleRoleLiveTests(PostgresFixture postgres)
{
    private const int Hosts = 8;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task Hosts_that_start_together_make_the_role_and_grant_to_it_one_after_the_other()
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string role = "studio_role_race_" + suffix;
        string schema = "sample_role_race_" + suffix;

        await ExecuteAsync("create schema " + schema + "; create table " + schema + ".kept (id int primary key);");

        try
        {
            bool[] made = await Task.WhenAll(
                Enumerable.Range(0, Hosts).Select(_ => SampleConsoleRole.EnsureAsync(postgres.ConnectionString, role, null, Token)));

            made.Should().AllSatisfy(static x => x.Should().BeTrue(), "every host that starts together finds the role usable");

            await Task.WhenAll(Enumerable.Range(0, Hosts).Select(async _ =>
            {
                await using NpgsqlConnection connection = new(postgres.ConnectionString);
                await connection.OpenAsync(Token);
                await SampleConsoleRole.GrantAsync(connection, schemas: [schema], role: role, cancellationToken: Token);
            }));

            (await ScalarAsync<long>("select count(*) from pg_catalog.pg_roles where rolname = @role", role)).Should().Be(1);
            (await ScalarAsync<bool>("select pg_catalog.pg_has_role(current_user, @role, 'MEMBER')", role)).Should().BeTrue(
                "the login is a member, so SET LOCAL ROLE reaches it");
            (await ScalarAsync<bool>("select pg_catalog.pg_has_role(@role, 'pg_monitor', 'MEMBER')", role)).Should().BeTrue();
            (await ScalarAsync<bool>("select pg_catalog.has_table_privilege(@role, '" + schema + ".kept', 'SELECT')", role)).Should().BeTrue(
                "the grants all landed, one after the other");
        }
        finally
        {
            await ExecuteAsync("drop schema if exists " + schema + " cascade;");
            await ExecuteAsync("drop owned by " + role + "; drop role if exists " + role + ";", tolerateMissingRole: true);
        }
    }

    /// <summary>
    /// The grants hold only for a role the login is a member of: a role that is not there is granted nothing, and is not made
    /// either. Membership is the server's, though, not the database's - which is why the seeding asks the host first
    /// (<see cref="The_seeding_grants_the_role_nothing_on_a_database_the_host_did_not_say_is_the_demos" />).
    /// </summary>
    [PostgresFact]
    public async Task A_role_the_host_did_not_make_is_granted_nothing()
    {
        string suffix = Guid.NewGuid().ToString("N")[..12];
        string role = "studio_role_absent_" + suffix;
        string schema = "sample_role_absent_" + suffix;

        await ExecuteAsync("create schema " + schema + ";");

        try
        {
            await using NpgsqlConnection connection = new(postgres.ConnectionString);
            await connection.OpenAsync(Token);

            await SampleConsoleRole.GrantAsync(connection, schemas: [schema], role: role, cancellationToken: Token);

            (await ScalarAsync<long>("select count(*) from pg_catalog.pg_roles where rolname = @role", role)).Should().Be(0);
        }
        finally
        {
            await ExecuteAsync("drop schema if exists " + schema + " cascade;");
        }
    }

    /// <summary>
    /// SEC-fix F3: the seeding runs on every database the sample is pointed at, and once a start has made the login a member
    /// of the sample's role, it is a member on every database of that server - so <see cref="SampleConsoleRole.GrantAsync" />'s
    /// own membership check would grant the role the demo's schemas in any database. The seeder grants only when the host said
    /// the database is the demo's (<see cref="SampleSeeding.GrantConsoleRole" />), and the granted half proves the check reads
    /// the grants it is looking for.
    /// </summary>
    /// <remarks>
    /// The sample's own role, <see cref="SampleConsoleRole.Name" />, because that is the one the seeder grants; making it is
    /// idempotent and other classes on this container make it the same way. Each half has a database of its own, because the
    /// seeder grants in the sample's fixed schemas.
    /// </remarks>
    [PostgresFact]
    public async Task The_seeding_grants_the_role_nothing_on_a_database_the_host_did_not_say_is_the_demos()
    {
        (await SampleConsoleRole.EnsureAsync(postgres.ConnectionString, cancellationToken: Token)).Should().BeTrue(
            "the premise: the server has the role and the login is a member, as after a start on the demo's database");

        (long Schemas, long Granted, bool Member) withheld = await SeedAsync("sample_seed_role_withheld", grant: false);
        (long Schemas, long Granted, bool Member) granted = await SeedAsync("sample_seed_role_granted", grant: true);

        withheld.Member.Should().BeTrue("the login is a member on a database nobody made the role for: membership is cluster-wide");
        withheld.Schemas.Should().Be(SampleConsoleRole.Schemas.Count, "the seeding made every demo schema there");
        withheld.Granted.Should().Be(0, "a database the host did not call the demo's grants the role nothing");

        granted.Schemas.Should().Be(SampleConsoleRole.Schemas.Count);
        granted.Granted.Should().Be(SampleConsoleRole.Schemas.Count, "where the host allowed it, every demo schema is granted");
    }

    /// <summary>
    /// A database of its own, the sample's store in it seeded with <paramref name="grant" /> as the host's answer, and then
    /// what the role holds there; the database is dropped again.
    /// </summary>
    private async Task<(long Schemas, long Granted, bool Member)> SeedAsync(string database, bool grant)
    {
        string connectionString = await Browser.BrowserSuiteFixture.CreateDatabaseAsync(postgres, database);

        try
        {
            await using (MartenFixture marten = await MartenFixture.CreateAsync(connectionString, SampleStore.DocumentSchema))
            {
                await new SampleDataSeeder(seeding: new SampleSeeding(GrantConsoleRole: grant)).Populate(marten.Store, Token);
            }

            await using NpgsqlConnection connection = new(connectionString);
            await connection.OpenAsync(Token);
            await using NpgsqlCommand command = new(
                "select count(*), count(*) filter (where pg_catalog.has_schema_privilege(@role, n.oid, 'USAGE')), "
                + "pg_catalog.pg_has_role(current_user, @role, 'MEMBER') "
                + "from pg_catalog.pg_namespace n where n.nspname = any(@schemas)",
                connection);
            command.Parameters.AddWithValue("role", SampleConsoleRole.Name);
            command.Parameters.AddWithValue("schemas", SampleConsoleRole.Schemas.ToArray());

            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
            (await reader.ReadAsync(Token)).Should().BeTrue();
            return (reader.GetInt64(0), reader.GetInt64(1), reader.GetBoolean(2));
        }
        finally
        {
            // `with (force)` ends the sessions the pools above still hold; the name is this test's own, so none is reused.
            await ExecuteAsync("drop database if exists " + database + " with (force);");
        }
    }

    private async Task ExecuteAsync(string sql, bool tolerateMissingRole = false)
    {
        await using NpgsqlConnection connection = new(postgres.ConnectionString);
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new(sql, connection);

        try
        {
            await command.ExecuteNonQueryAsync(Token);
        }
        catch (PostgresException exception) when (tolerateMissingRole && exception.SqlState == PostgresErrorCodes.UndefinedObject)
        {
        }
    }

    private async Task<T> ScalarAsync<T>(string sql, string role)
    {
        await using NpgsqlConnection connection = new(postgres.ConnectionString);
        await connection.OpenAsync(Token);
        await using NpgsqlCommand command = new(sql, connection);
        command.Parameters.AddWithValue("role", role);
        return (T) (await command.ExecuteScalarAsync(Token))!;
    }
}
