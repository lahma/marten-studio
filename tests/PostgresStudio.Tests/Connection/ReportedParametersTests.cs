using PostgresStudio.Services.Health;

namespace PostgresStudio.Tests.Connection;

/// <summary>1.1-NP1-FIX: the parameters the server reported are a copy, not Npgsql's live table.</summary>
public sealed class ReportedParametersTests
{
    [Fact]
    public void The_copy_survives_Npgsql_clearing_and_rewriting_its_own_table()
    {
        // NpgsqlConnector.Cleanup() empties PostgresParameters when the physical connection is closed or broken (not when it goes
        // back to the pool), and the connector's next session writes to the same dictionary.
        Dictionary<string, string> live = new() { ["server_version"] = "17.2", ["is_superuser"] = "off" };

        IReadOnlyDictionary<string, string> copy = HealthSqlRunner.CopyReported(live);

        live.Clear();
        live["server_version"] = "another session";

        copy.Should().HaveCount(2);
        copy["server_version"].Should().Be("17.2");
        copy["is_superuser"].Should().Be("off");
        copy.Should().NotBeSameAs(live);
    }

    [Fact]
    public void The_copy_keys_are_compared_exactly()
    {
        HealthSqlRunner.CopyReported(new Dictionary<string, string> { ["TimeZone"] = "UTC" }).Should().NotContainKey("timezone");
    }
}
