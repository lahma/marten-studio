using System.Globalization;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

using Npgsql;

namespace PostgresStudio.Integration.Tests.Health;

/// <summary>
/// The opt-in ASP.NET Core health check (<c>AddHealthChecks().AddPostgresStudio()</c>) over a host's database source that
/// lists more databases than one run checks: the first twenty-five are swept, the rest are counted and never opened, and
/// what the probe reports is codes and counts - never a name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thirty names, and only twenty-five real databases behind them.</b> The first twenty-five names the host lists are
/// opened on this class's own database; the last five on a port nothing listens on. A run that swept any of those five
/// would report it timed out, failed or not reached, so a clean twenty-five and five skipped is the proof the budget was
/// kept - in the order the host listed them - and that the enumeration past it names databases and connects to none.
/// </para>
/// <para>
/// The probe is <see cref="HealthCheckService" /> from the container, the way a host's health endpoint probes it: no
/// visitor, no circuit, no policy.
/// </para>
/// </remarks>
public class HealthExportLiveTests(PostgresFixture postgres)
{
    private const int Listed = 30;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_host_source_of_thirty_databases_is_checked_twenty_five_deep_and_reported_without_a_name_or_a_write()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ps_export"], Token);
        await OwnedDatabases.ExecuteAsync(owned[0], "create table public.orders (id int primary key); insert into public.orders values (1); analyze public.orders", Token);

        ServerSnapshot before = await ServerSnapshot.ReadAsync(owned.ConnectionStrings, "ps_export", Token);
        // The application's, so disposed by the test rather than by the container it was handed to.
        await using ThirtyDatabases host = new(owned[0]);

        await using StudioHost studio = StudioHost.Create(services =>
        {
            services.AddSingleton(host);
            services.AddHealthChecks().AddPostgresStudio();
        }, static options => options.AddDatabaseSource("tenants", static services => services.GetRequiredService<ThirtyDatabases>()));

        HealthReport report = await studio.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync(Token);
        HealthReportEntry entry = report.Entries["postgres-studio"];
        TestContext.Current.TestOutputHelper?.WriteLine(entry.Status + ": " + entry.Description);

        entry.Data["databases"].Should().Be(25, entry.Description);
        entry.Data["databasesSkipped"].Should().Be(Listed - 25, "the five names past the budget are counted, never checked");
        entry.Data["databasesTimedOut"].Should().Be(0, "nothing past the budget was tried: " + entry.Description);
        entry.Data["storesNotReached"].Should().Be(0);
        ((string[]) entry.Data["timedOut"]).Should().BeEmpty();
        entry.Exception.Should().BeNull();

        // W5-API: listing builds no data source (G11), so the host is asked only for the databases this run checks - the
        // first twenty-five, in the order it lists them, once each; the rotation asks for the other five on the next run.
        host.Asked.Should().Equal(host.Names.Take(25), "the host is asked for each name this run checks, in the order it lists them, and asked once");

        string everything = entry.Description + " " + string.Join(" ", entry.Data.Values.Select(static x => x is string[] codes ? string.Join(",", codes) : Convert.ToString(x, CultureInfo.InvariantCulture)));
        everything.Should().NotContain("tenant_", "the export reports codes and counts, never a database's name")
            .And.NotContain("ps_export").And.NotContain("orders");

        before.ShouldBeUnchangedIn(await ServerSnapshot.ReadAsync(owned.ConnectionStrings, "ps_export", Token));
        studio.Logs.WarningsOrWorse.Should().BeEmpty("a probe of healthy databases is nothing to warn about");
    }

    /// <summary>
    /// The host's tenant directory: thirty names, the first twenty-five on the class's database and the rest on a port no
    /// server listens on, each data source built once and disposed with the source.
    /// </summary>
    private sealed class ThirtyDatabases(string database) : IPostgresDatabaseSource, IAsyncDisposable
    {
        private readonly Dictionary<string, NpgsqlDataSource> built = new(StringComparer.Ordinal);
        private readonly List<string> asked = [];
        private readonly Lock gate = new();

        public IReadOnlyList<string> Names { get; } =
            [.. Enumerable.Range(1, Listed).Select(static x => "tenant_" + x.ToString("00", CultureInfo.InvariantCulture))];

        public IReadOnlyList<string> Asked
        {
            get
            {
                lock (gate)
                {
                    return [.. asked];
                }
            }
        }

        public async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            foreach (string name in Names)
            {
                await Task.Yield();
                yield return name;
            }
        }

        public ValueTask<NpgsqlDataSource?> GetDataSourceAsync(string name, CancellationToken cancellationToken)
        {
            int index = Names.ToList().IndexOf(name);

            lock (gate)
            {
                asked.Add(name);

                if (index < 0)
                {
                    return ValueTask.FromResult<NpgsqlDataSource?>(null);
                }

                if (!built.TryGetValue(name, out NpgsqlDataSource? source))
                {
                    string connection = index < 25
                        ? new NpgsqlConnectionStringBuilder(database) { ApplicationName = name }.ConnectionString
                        : new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 1, Database = name, Username = "nobody", Timeout = 2 }.ConnectionString;

                    source = NpgsqlDataSource.Create(connection);
                    built[name] = source;
                }

                return ValueTask.FromResult<NpgsqlDataSource?>(source);
            }
        }

        public async ValueTask DisposeAsync()
        {
            foreach (NpgsqlDataSource source in built.Values)
            {
                await source.DisposeAsync();
            }
        }
    }
}
