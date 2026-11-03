using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PostgresStudio;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Tests.Health;

/// <summary>
/// 1.1-NP2: the six connection-settings checks (C16, C17, C18, S11, S12, S13) as both studios carry them - registered with the
/// area, tier, gate, variants and source kinds the design fixes; accepted by a host in either studio like any check; and muted by
/// keys that are pinned, because a key that moves loses every browser's mute of it.
/// </summary>
public class ConnectionChecksSeamTests
{
    private static readonly (string Code, HealthArea Area, string[] Variants)[] Checks =
    [
        ("C16", HealthArea.Connections, ["idle-session-timeout", "proxy-idle-timeout"]),
        ("C17", HealthArea.Connections, []),
        ("C18", HealthArea.Connections, []),
        ("S11", HealthArea.Server, ["prefer", "require"]),
        ("S12", HealthArea.Server, ["persist-security-info", "ssl-password", "pgpassword-env"]),
        ("S13", HealthArea.Server, ["log-parameters", "include-error-detail", "include-failed-batched-command"]),
    ];

    [Fact]
    public void Each_check_is_registered_in_both_studios_at_the_database_gate_in_the_regular_sweep_with_its_variants()
    {
        foreach (HealthCheckRegistry registry in (HealthCheckRegistry[]) [StudioHealthChecks.Registry, new(PostgresStudio.Services.Postgres.PostgresStudioDescriptor.Instance)])
        {
            foreach ((string code, HealthArea area, string[] variants) in Checks)
            {
                HealthCheckDescriptor? found = registry.Find(code);
                found.Should().NotBeNull(code + " is registered");
                HealthCheckDescriptor check = found!;

                check.Info.Area.Should().Be(area);
                check.Info.Gate.Should().Be(HealthGate.Database, code + " reads whole-database numbers");
                check.Info.Tier.Should().Be(HealthCostTier.Catalog);
                check.Info.Variants.Should().Equal(variants);
                check.RunsAgainst(StudioSourceKind.DataSource).Should().BeTrue(code + " runs on a plain data source");
            }
        }
    }

    [Fact]
    public void A_host_accepts_each_check_in_Marten_Studio_and_in_Postgres_Studio()
    {
        ServiceCollection marten = new();
        marten.AddMartenStudio(options =>
        {
            foreach ((string code, _, _) in Checks)
            {
                options.AcceptedChecks[code] = "judged elsewhere";
            }
        });

        using ServiceProvider martenProvider = marten.BuildServiceProvider();
        martenProvider.GetRequiredService<IOptions<MartenStudioOptions>>().Value.AcceptedChecks.Keys.Should().BeEquivalentTo(Checks.Select(static x => x.Code));

        ServiceCollection postgres = new();
        postgres.AddPostgresStudio(options =>
        {
            foreach ((string code, _, _) in Checks)
            {
                options.AcceptedChecks[code] = "judged elsewhere";
            }
        });

        using ServiceProvider postgresProvider = postgres.BuildServiceProvider();
        postgresProvider.GetRequiredService<IOptions<PostgresStudioOptions>>().Value.AcceptedChecks.Keys.Should().BeEquivalentTo(Checks.Select(static x => x.Code));
    }

    [Theory]
    [InlineData("C16", "idle-session-timeout", "default|db|C16|idle-session-timeout")]
    [InlineData("C16", "proxy-idle-timeout", "default|db|C16|proxy-idle-timeout")]
    [InlineData("C17", null, "default|db|C17")]
    [InlineData("C18", null, "default|db|C18")]
    [InlineData("S11", "prefer", "default|db|S11|prefer")]
    [InlineData("S11", "require", "default|db|S11|require")]
    [InlineData("S12", "persist-security-info", "default|db|S12|persist-security-info")]
    [InlineData("S12", "ssl-password", "default|db|S12|ssl-password")]
    [InlineData("S12", "pgpassword-env", "default|db|S12|pgpassword-env")]
    [InlineData("S13", "log-parameters", "default|db|S13|log-parameters")]
    [InlineData("S13", "include-error-detail", "default|db|S13|include-error-detail")]
    [InlineData("S13", "include-failed-batched-command", "default|db|S13|include-failed-batched-command")]
    public void The_mute_key_of_each_finding_is_pinned(string code, string? variant, string muteKey)
    {
        HealthFinding finding = new()
        {
            Code = code,
            Variant = variant,
            Severity = HealthSeverity.Info,
            Title = "A finding",
            WhyItMatters = "Because.",
        };

        HealthService.MuteKeyOf("default", "db", finding).Should().Be(muteKey);
    }
}
