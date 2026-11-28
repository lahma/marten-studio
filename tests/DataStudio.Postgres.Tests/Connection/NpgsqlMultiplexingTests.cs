using Npgsql;

using DataStudio.Postgres.Services.Connection;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Sources;

namespace DataStudio.Postgres.Tests.Connection;

/// <summary>
/// 43bf: <c>Multiplexing</c> is read by keyword, so that one build runs on Npgsql 9, 10 and 11, which removed the property. The
/// tests that set the keyword skip where the loaded Npgsql does not know it; each has a twin that runs everywhere.
/// </summary>
public sealed class NpgsqlMultiplexingTests
{
    [Fact]
    public void A_connection_string_without_the_keyword_does_not_multiplex()
    {
        NpgsqlMultiplexing.Multiplexes(new NpgsqlConnectionStringBuilder("Host=db.example;Database=app")).Should().BeFalse();
    }

    [Fact]
    public void The_keyword_set_to_false_does_not_multiplex()
    {
        NpgsqlKeywords.SkipUnlessMultiplexingIsKnown();

        NpgsqlMultiplexing.Multiplexes(new NpgsqlConnectionStringBuilder("Host=db.example;Multiplexing=false")).Should().BeFalse();
    }

    [Fact]
    public void The_keyword_set_to_true_multiplexes()
    {
        NpgsqlKeywords.SkipUnlessMultiplexingIsKnown();

        NpgsqlMultiplexing.Multiplexes(new NpgsqlConnectionStringBuilder("Host=db.example;Multiplexing=true")).Should().BeTrue();
    }

    [Fact]
    public void A_keyword_the_loaded_Npgsql_does_not_know_is_false_and_never_throws()
    {
        // The twin of the two above: on Npgsql 11 the keyword is unknown, and TryGetValue answers false for it.
        NpgsqlConnectionStringBuilder builder = new("Host=db.example");

        builder.TryGetValue("No Such Keyword", out _).Should().BeFalse();
        NpgsqlMultiplexing.Multiplexes(builder).Should().BeFalse();
    }

    [Fact]
    public void The_data_sources_endpoints_say_no_multiplexing_without_the_keyword()
    {
        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=db.example;Port=5433;Database=app");

        DataSourceEndpoints? endpoints = DataSourceEndpoints.Of(new Database(dataSource));

        endpoints.Should().NotBeNull();
        endpoints!.Multiplexing.Should().BeFalse();
        endpoints.Endpoints.Should().Equal([("db.example", 5433)]);
    }

    [Fact]
    public void The_data_sources_endpoints_say_multiplexing_for_the_keyword()
    {
        NpgsqlKeywords.SkipUnlessMultiplexingIsKnown();

        using NpgsqlDataSource dataSource = NpgsqlDataSource.Create("Host=db.example;Multiplexing=true");

        DataSourceEndpoints.Of(new Database(dataSource))!.Multiplexing.Should().BeTrue();
    }

    private sealed class Database(NpgsqlDataSource dataSource) : IStudioDatabase
    {
        public string Identity => "default";

        public string Name => "app";

        public string Server => "db.example";

        public NpgsqlDataSource? DataSource => dataSource;

        public NpgsqlConnection CreateConnection(StudioConnectionUsage usage) => dataSource.CreateConnection();
    }
}
