using Npgsql;

using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Database;
using DataStudio.Postgres.Services.Sources;

namespace DataStudio.Postgres.Tests.Connection;

/// <summary>
/// Issue #86: Sessions' pool panel and the Connection page match a pool to a database by the data source's <c>Name</c> when the
/// host gave it one - Npgsql reports a named data source's pool to its meter under that name, not under its connection string - and
/// by connection string otherwise, as C18 identifies a named pool since #56. Read from the real Npgsql meter, through data sources
/// that never connect.
/// </summary>
public sealed class NamedDataSourcePoolTests
{
    private static string ConnectionString() =>
        "Host=127.0.0.1;Port=1;Username=none;Password=secret;Maximum Pool Size=7;Timeout=1;Database=named_pool_" + Guid.NewGuid().ToString("N");

    private static NpgsqlDataSource Named(string connectionString, string name) =>
        new NpgsqlDataSourceBuilder(connectionString) { Name = name }.Build();

    private static ResolvedSource Resolved(NpgsqlDataSource dataSource) =>
        new(new StudioScope("default", "default", null), new StudioSourceRegistration("default", "Default", StudioSourceKind.DataSource), new DataSourceOnly(dataSource));

    /// <summary>The pool summary Sessions and the Connection page show, read while a lease is held, as a page holds one.</summary>
    private static SessionPoolSummary? Summary(NpgsqlDataSource dataSource, out IReadOnlyList<string> poolNames)
    {
        using SessionPoolObserver observer = new();
        using SessionPoolLease lease = observer.Lease();

        poolNames = [.. lease.Read().Select(static x => x.PoolName)];
        return SessionActivityService.PoolOf(observer, Resolved(dataSource));
    }

    [Fact]
    public async Task A_named_data_sources_pool_is_matched_by_its_name_and_not_by_its_connection_string()
    {
        string connectionString = ConnectionString();
        string name = "named-pool-" + Guid.NewGuid().ToString("N");
        await using NpgsqlDataSource source = Named(connectionString, name);

        SessionPoolSummary? summary = Summary(source, out IReadOnlyList<string> poolNames);

        poolNames.Should().Contain(name, "Npgsql reports a named data source's pool under its name");
        poolNames.Should().NotContain(source.ConnectionString);
        summary.Should().Be(new SessionPoolSummary(true, 0, 0, 7, true), "Sessions says \"n of max in use\", not \"not matched to this database\"");

        DatabasePoolSettings.IsPoolOf(new DataSourceOnly(source), name).Should().BeTrue();
        DatabasePoolSettings.IsPoolOf(new DataSourceOnly(source), source.ConnectionString).Should().BeFalse("a named data source has no pool under its connection string");
    }

    [Fact]
    public async Task An_unnamed_data_sources_pool_is_still_matched_by_its_connection_string()
    {
        await using NpgsqlDataSource source = NpgsqlDataSource.Create(ConnectionString());

        SessionPoolSummary? summary = Summary(source, out IReadOnlyList<string> poolNames);

        poolNames.Should().Contain(source.ConnectionString);
        summary.Should().Be(new SessionPoolSummary(true, 0, 0, 7, true));
        DatabasePoolSettings.IsPoolOf(new DataSourceOnly(source), source.ConnectionString).Should().BeTrue();
    }

    [Fact]
    public async Task Two_data_sources_with_one_connection_string_and_different_names_are_told_apart()
    {
        string connectionString = ConnectionString();
        string first = "first-" + Guid.NewGuid().ToString("N");
        string second = "second-" + Guid.NewGuid().ToString("N");
        await using NpgsqlDataSource a = Named(connectionString, first);
        await using NpgsqlDataSource b = Named(connectionString, second);

        DataSourceOnly databaseA = new(a);
        DataSourceOnly databaseB = new(b);

        DatabasePoolSettings.IsPoolOf(databaseA, first).Should().BeTrue();
        DatabasePoolSettings.IsPoolOf(databaseA, second).Should().BeFalse("the other data source's pool is not this one's");
        DatabasePoolSettings.IsPoolOf(databaseB, second).Should().BeTrue();
        DatabasePoolSettings.IsPoolOf(databaseB, first).Should().BeFalse();

        // And neither is mistaken for the other when the meter reports both: each is one pool, never "several share this connection string".
        using SessionPoolObserver observer = new();
        using SessionPoolLease lease = observer.Lease();

        SessionActivityService.PoolOf(observer, Resolved(a)).Should().Be(new SessionPoolSummary(true, 0, 0, 7, true));
        SessionActivityService.PoolOf(observer, Resolved(b)).Should().Be(new SessionPoolSummary(true, 0, 0, 7, true));
    }

    /// <summary>The database a read resolves to, as little of one as the pool match reads: its data source.</summary>
    private sealed class DataSourceOnly(NpgsqlDataSource dataSource) : IStudioDatabase
    {
        public string Identity => "default";

        public string Name => "named-pool";

        public string Server => "127.0.0.1:1";

        public NpgsqlDataSource? DataSource => dataSource;

        public NpgsqlConnection CreateConnection(StudioConnectionUsage usage) => dataSource.CreateConnection();
    }
}
