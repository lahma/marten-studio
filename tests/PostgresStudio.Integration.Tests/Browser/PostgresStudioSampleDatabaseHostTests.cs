using System.Net;

using Npgsql;

using PostgresStudio.Integration.Tests.Database;
using PostgresStudio.Sample;
using PostgresStudio.SampleData;

namespace PostgresStudio.Integration.Tests.Browser;

/// <summary>
/// The Postgres Studio sample pointed at a database it did not start (SEC-fix F2): without <c>--allow-data-generation</c>
/// it writes nothing - no tenant databases on the server, no demo schemas in the admin database - says so in one line, and
/// still serves the studio; with the switch it writes the demo data there, as on its own throwaway container.
/// </summary>
/// <remarks>
/// In <see cref="TenantDatabases" />' collection, because the tenant databases have the sample's fixed names
/// (<see cref="TenantDemo.TenantNames" />) on the suite's shared server: xunit runs that collection apart from everything
/// else, so "no tenant database exists" is about this host alone.
/// </remarks>
/// <param name="postgres">The assembly's Postgres.</param>
[Collection(TenantDatabases.Name)]
public class PostgresStudioSampleDatabaseHostTests(PostgresFixture postgres)
{
    private const string ReadOnlyAdmin = "ps_sample_readonly_admin";

    private const string SeededAdmin = "ps_sample_seeded_admin";

    private static readonly string[] DemoSchemas =
        [RelationalDemoSchema.QuartzSchemaName, RelationalDemoSchema.LegacySchemaName, RelationalDemoSchema.SampleSchemaName];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task On_a_configured_database_without_the_switch_it_writes_nothing_says_so_and_still_serves_the_studio()
    {
        await TenantDatabases.DropAsync(postgres, Token);
        string admin = await postgres.CreateDatabaseAsync(ReadOnlyAdmin, Token);

        try
        {
            await using (SampleHost host = await SampleHost.StartAsync(admin, cancellationToken: Token))
            {
                host.Log.Should().Contain(SampleArguments.ReadOnlyNotice, "the sample says in one line that it wrote nothing");
                host.Log.Should().NotContain("throwaway PostgreSQL container was started", "a configured database starts no container");

                (await TenantDatabasesOnServerAsync()).Should().BeEmpty("no tenant database is created beside a configured one");
                (await DemoSchemasInAsync(admin)).Should().BeEmpty("no demo schema is created in a configured database");

                using HttpClient client = await PostgresStudioSampleHostTests.SignInAsync(host, "admin");

                // The configured database is the one source with a database: the tenants source lists none without the
                // directory, so the picker - drawn only for two or more listed sources - is not drawn at all.
                using (HttpResponseMessage page = await client.GetAsync(new Uri("/postgres", UriKind.Relative), Token))
                {
                    page.StatusCode.Should().Be(HttpStatusCode.OK, host.LogTail());

                    string html = await page.Content.ReadAsStringAsync(Token);
                    html.Should().Contain("<title>Postgres Studio</title>");
                    html.Should().NotContain("<option value=\"tenants\"", "a host source with no directory lists no database");
                }

                using (HttpResponseMessage tenants = await client.GetAsync(new Uri("/postgres?store=tenants", UriKind.Relative), Token))
                {
                    tenants.StatusCode.Should().Be(HttpStatusCode.OK, host.LogTail());
                    (await tenants.Content.ReadAsStringAsync(Token)).Should().NotContain("<option value=\"tenant_a\"");
                }

                using HttpResponseMessage landing = await client.GetAsync(new Uri("/", UriKind.Relative), Token);
                (await landing.Content.ReadAsStringAsync(Token)).Should().Contain("Read-only", "the landing page says it too");

                // No directory is an expected state: the host's list is empty, and nothing is logged as unreadable (9213).
                host.Log.Should().NotContain("[9213]", host.LogTail());
            }

            (await TenantDatabasesOnServerAsync()).Should().BeEmpty("reading the studio created nothing either");
            (await DemoSchemasInAsync(admin)).Should().BeEmpty();
        }
        finally
        {
            await TenantDatabases.DropAsync(postgres, CancellationToken.None);
            await postgres.DropDatabaseAsync(ReadOnlyAdmin, CancellationToken.None);
        }
    }

    [PostgresFact]
    public async Task On_a_configured_database_with_the_switch_it_writes_the_demo_data_there()
    {
        await TenantDatabases.DropAsync(postgres, Token);
        string admin = await postgres.CreateDatabaseAsync(SeededAdmin, Token);

        try
        {
            await using SampleHost host = await SampleHost.StartAsync(admin, allowDataGeneration: true, cancellationToken: Token);

            host.Log.Should().NotContain(SampleArguments.ReadOnlyNotice);
            host.BaseAddress.Host.Should().Be("127.0.0.1", "the switch before --urls did not swallow the URL after it");

            (await TenantDatabasesOnServerAsync()).Should().Equal(TenantDemo.TenantNames);
            (await DemoSchemasInAsync(admin)).Should().BeEquivalentTo(DemoSchemas);

            using HttpClient client = await PostgresStudioSampleHostTests.SignInAsync(host, "admin");
            using HttpResponseMessage tenants = await client.GetAsync(new Uri("/postgres?store=tenants", UriKind.Relative), Token);
            (await tenants.Content.ReadAsStringAsync(Token)).Should().Contain("<option value=\"tenant_a\"", host.LogTail());
        }
        finally
        {
            await TenantDatabases.DropAsync(postgres, CancellationToken.None);
            await postgres.DropDatabaseAsync(SeededAdmin, CancellationToken.None);
        }
    }

    /// <summary>The sample's tenant databases that exist on the server, by name, in order.</summary>
    private async Task<List<string>> TenantDatabasesOnServerAsync()
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);
        await using var command = new NpgsqlCommand(
            "select datname from pg_catalog.pg_database where datname = any(@names) order by datname", connection);
        command.Parameters.AddWithValue("names", TenantDemo.TenantNames.ToArray());

        List<string> found = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            found.Add(reader.GetString(0));
        }

        return found;
    }

    /// <summary>The demo's schemas that exist in <paramref name="database" />.</summary>
    private static async Task<List<string>> DemoSchemasInAsync(string database)
    {
        await using NpgsqlConnection connection = new(database);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(
            "select nspname from pg_catalog.pg_namespace where nspname = any(@names) order by nspname", connection);
        command.Parameters.AddWithValue("names", DemoSchemas);

        List<string> found = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            found.Add(reader.GetString(0));
        }

        return found;
    }
}
