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
    /// The seeding runs on every database the sample is pointed at, so its grants hold only for a role the login is a member
    /// of - one the host made here. A role that is not there is granted nothing, and is not made either.
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
