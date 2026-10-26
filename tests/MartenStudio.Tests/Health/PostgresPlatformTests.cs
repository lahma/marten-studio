using PostgresStudio;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The managed-platform detector, marker by marker: each provider's documented marker names that provider, the order
/// they are read in holds where two coexist, a host alone names the providers whose hosts are documented, and nothing
/// recognised means self-hosted.
/// </summary>
public class PostgresPlatformTests
{
    private static readonly PostgresPlatformMarkers None = PostgresPlatformMarkers.None;

    [Fact]
    public void A_server_with_no_marker_and_an_ordinary_host_is_self_hosted()
    {
        PostgresPlatform platform = PostgresPlatform.Detect(None, ["db.internal.example", "10.0.0.7"]);

        platform.Should().BeSameAs(PostgresPlatform.SelfHosted);
        platform.IsManaged.Should().BeFalse();
        platform.Marker.Should().BeNull();
        platform.WhereSettingsChange.Should().Contain("postgresql.conf");
    }

    [Fact]
    public void A_server_with_no_marker_and_no_host_at_all_is_self_hosted()
    {
        PostgresPlatform.Detect(None, []).Kind.Should().Be(PostgresPlatformKind.SelfHosted);
    }

    [Fact]
    public void The_rds_settings_name_Amazon_RDS()
    {
        PostgresPlatform platform = PostgresPlatform.Detect(None with { RdsSettings = true }, ["mydb.abc123.eu-west-1.rds.amazonaws.com"]);

        platform.Kind.Should().Be(PostgresPlatformKind.AmazonRds);
        platform.IsManaged.Should().BeTrue();
        platform.Marker.Should().Contain("rds.extensions");
        platform.WhereSettingsChange.Should().Contain("DB parameter group");
    }

    [Fact]
    public void Aurora_s_function_names_Aurora_even_beside_the_rds_settings_an_Aurora_cluster_also_has()
    {
        PostgresPlatform platform = PostgresPlatform.Detect(None with { RdsSettings = true, AuroraVersionFunction = true }, []);

        platform.Kind.Should().Be(PostgresPlatformKind.AmazonAurora);
        platform.Marker.Should().Contain("aurora_version()");
        platform.WhereSettingsChange.Should().Contain("DB cluster parameter group");
    }

    [Fact]
    public void The_azure_extensions_setting_names_Azure()
    {
        PostgresPlatform platform = PostgresPlatform.Detect(None with { AzureExtensions = true }, ["studio.postgres.database.azure.com"]);

        platform.Kind.Should().Be(PostgresPlatformKind.Azure);
        platform.Marker.Should().Contain("azure.extensions");
        platform.WhereSettingsChange.Should().Contain("Server parameters");
    }

    [Fact]
    public void A_cloudsql_flag_names_Cloud_SQL()
    {
        PostgresPlatform platform = PostgresPlatform.Detect(None with { CloudSqlSettings = true }, ["10.20.0.3"]);

        platform.Kind.Should().Be(PostgresPlatformKind.CloudSql);
        platform.Marker.Should().Contain("cloudsql.");
        platform.WhereSettingsChange.Should().Contain("database flags");
    }

    [Fact]
    public void The_neon_superuser_role_names_Neon()
    {
        PostgresPlatform platform = PostgresPlatform.Detect(None with { NeonSuperuserRole = true }, ["my-proxy.internal"]);

        platform.Kind.Should().Be(PostgresPlatformKind.Neon);
        platform.Marker.Should().Contain("neon_superuser");
        platform.WhereSettingsChange.Should().Contain("Neon Support");
    }

    [Theory]
    [InlineData("ep-cool-darkness-123456.us-east-2.aws.neon.tech")]
    [InlineData("ep-cool-darkness-123456-pooler.us-east-2.aws.neon.tech")]
    [InlineData("EP-Cool-Darkness-123456.US-EAST-2.AWS.NEON.TECH.")]
    public void A_neon_endpoint_host_names_Neon_without_a_server_marker(string host)
    {
        PostgresPlatform platform = PostgresPlatform.Detect(None, [host]);

        platform.Kind.Should().Be(PostgresPlatformKind.Neon);
        platform.Marker.Should().Contain("*.neon.tech");
    }

    [Theory]
    [InlineData("db.apbkobhfnmcqqzqeeqss.supabase.co")]
    [InlineData("aws-0-eu-central-1.pooler.supabase.com")]
    public void A_supabase_host_names_Supabase(string host)
    {
        PostgresPlatform platform = PostgresPlatform.Detect(None, ["localhost", host]);

        platform.Kind.Should().Be(PostgresPlatformKind.Supabase);
        platform.Marker.Should().Contain("supabase");
    }

    /// <summary>A host that merely mentions a provider is not that provider's: the match is on the domain's end.</summary>
    [Theory]
    [InlineData("neon.tech.example.com")]
    [InlineData("supabase.co.internal")]
    [InlineData("my-supabase-mirror.local")]
    [InlineData("rds.amazonaws.com.evil")]
    public void A_host_that_only_mentions_a_provider_is_self_hosted(string host)
    {
        PostgresPlatform.Detect(None, [host]).Kind.Should().Be(PostgresPlatformKind.SelfHosted);
    }

    [Fact]
    public void A_server_marker_wins_over_a_host()
    {
        // Somebody's own proxy, named after Neon, in front of an RDS server: the server says what it is.
        PostgresPlatform.Detect(None with { RdsSettings = true }, ["ep-x.neon.tech"]).Kind.Should().Be(PostgresPlatformKind.AmazonRds);
    }

    [Fact]
    public void Every_platform_has_a_name_and_a_place_settings_change()
    {
        foreach (PostgresPlatformKind kind in Enum.GetValues<PostgresPlatformKind>())
        {
            PostgresPlatform platform = new(kind, kind == PostgresPlatformKind.SelfHosted ? null : "a marker");

            platform.Name.Should().NotBeNullOrWhiteSpace();
            platform.WhereSettingsChange.Should().NotBeNullOrWhiteSpace();
            platform.IsManaged.Should().Be(kind != PostgresPlatformKind.SelfHosted);
        }
    }

    // ---------------------------------------------------------------------------------------------------
    // The data source's endpoints
    // ---------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("localhost", 5432, "localhost:5432")]
    [InlineData("a,b:6432", 5432, "a:5432|b:6432")]
    [InlineData(" primary.example , replica.example:5433 ", 5432, "primary.example:5432|replica.example:5433")]
    [InlineData("::1", 5432, "::1:5432")]
    [InlineData("[::1]:6543", 5432, "::1:6543")]
    [InlineData("/var/run/postgresql", 5432, "/var/run/postgresql:5432")]
    [InlineData("", 5432, "")]
    public void A_connection_strings_hosts_are_read_with_their_own_ports(string hosts, int port, string expected)
    {
        string actual = string.Join("|", DataSourceEndpoints.Parse(hosts, port).Select(static x => x.Host + ":" + x.Port));

        actual.Should().Be(expected);
    }
}
