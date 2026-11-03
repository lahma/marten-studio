using System.ComponentModel;
using System.Reflection;
using System.Text.Json;

using Npgsql;

using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Health.Export;

using static PostgresStudio.Tests.Connection.ConnectionCheckFacts;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// 1.1-NP2's sentinel test (NP-DESIGN §2.6) for the six checks: data sources whose every string keyword holds
/// <c>S3NT1NEL-&lt;keyword&gt;</c>, both passwords, a custom <c>Options</c> value and <c>PGPASSWORD</c> - and not one of them, nor a
/// host, a path or a connection string, in any finding, however it is dumped, nor in what the opt-in health check exports.
/// </summary>
public sealed class ConnectionChecksSentinelTests
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = true, MaxDepth = 64 };

    private static string Sentinel(string keyword) => "S3NT1NEL-" + keyword;

    /// <summary>What must never appear: the secrets, the custom option's value, the environment's password.</summary>
    private static readonly string[] Withheld = [Sentinel("Password"), Sentinel("SSL Password"), Sentinel("opt"), "S3NT1NEL-env-pgpassword"];

    private static string ConnectionString(string host, bool pooling, bool persist)
    {
        NpgsqlConnectionStringBuilder builder = new() { PersistSecurityInfo = persist };

        foreach (PropertyInfo property in typeof(NpgsqlConnectionStringBuilder).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (property.PropertyType == typeof(string)
                && property.GetCustomAttribute<DisplayNameAttribute>()?.DisplayName is { } keyword
                && property.GetCustomAttribute<ObsoleteAttribute>() is null
                && keyword is not ("Options" or "Host" or "Target Session Attributes" or "Require Auth"))
            {
                builder[keyword] = Sentinel(keyword);
            }
        }

        builder.Host = host;
        builder.Options = "-c app.secret=" + Sentinel("opt");
        builder.Pooling = pooling;
        builder.MinPoolSize = pooling ? 1 : 0;
        builder.LogParameters = true;
        builder.IncludeErrorDetail = true;
        builder.SslMode = SslMode.Require;
        return builder.ConnectionString;
    }

    private static FakeEnvironment Environment() => new(("PGPASSWORD", "S3NT1NEL-env-pgpassword"), ("PGSSLKEY", "S3NT1NEL-env-sslkey"));

    /// <summary>Every finding the six checks make over the sentinel data sources, with each check's finding fired.</summary>
    private static List<(string Code, HealthFinding Finding)> Findings()
    {
        ConnectionObserved observed = Observed(idleSessionMs: 600_000, ssl: true, sslVersion: "TLSv1.3");
        ConnectionPoolSiblings siblings = new(3, 300, 1);

        // A proxy: C16's both variants, C18, S11, S12 and S13 (the pool outlasts both cuts: a floor of one and a long idle lifetime).
        ConnectionFacts proxy = Facts(ConnectionString(ProxyHost, pooling: true, persist: true) + ";Connection Idle Lifetime=3600", observed, PostgresPlatformKind.AmazonRds, siblings: siblings, environment: Environment());

        // A data source of its own with pooling off: C17.
        ConnectionFacts unpooled = Facts(
            ConnectionString("db.example", pooling: false, persist: false),
            observed,
            sessions: new ConnectionSessionStatistics(50, 2_500, At.AddDays(-1)),
            siblings: siblings,
            environment: Environment());

        HealthEvaluation evaluation = PostgresStudio.Tests.Server.ServerTestFacts.Evaluation();
        List<(string, HealthFinding)> all = [];

        void Add(string code, HealthVerdict verdict) => all.AddRange(verdict.Findings.Select(x => (code, x)));

        Add("C16", new PoolIdleCutCheck().Evaluate(proxy, evaluation));
        Add("C17", new PoolingOffCheck().Evaluate(unpooled, evaluation));
        Add("C18", new SiblingPoolsCheck().Evaluate(proxy, evaluation));
        Add("S11", new ServerCertificateCheck().Evaluate(proxy, evaluation));
        Add("S12", new ReadableCredentialCheck().Evaluate(proxy, evaluation));

        // Persist Security Info off: the password is not visible, and PGPASSWORD is what the finding is about.
        Add("S12", new ReadableCredentialCheck().Evaluate(unpooled, evaluation));
        Add("S13", new DriverLoggingSettingsCheck().Evaluate(new DriverLoggingFacts(proxy, "Production"), evaluation));

        return all;
    }

    [Fact]
    public void No_sentinel_host_path_or_connection_string_is_in_any_finding_however_it_is_dumped()
    {
        List<(string Code, HealthFinding Finding)> findings = Findings();

        // Non-vacuous: every check made a finding, and the variants that carry the redaction rules are among them.
        findings.Select(static x => x.Code).Distinct().Should().BeEquivalentTo("C16", "C17", "C18", "S11", "S12", "S13");
        findings.Select(static x => x.Finding.Key).Should().Contain(
        [
            "C16|idle-session-timeout",
            "C16|proxy-idle-timeout",
            "S11|require",
            "S12|persist-security-info",
            "S12|ssl-password",
            "S12|pgpassword-env",
            "S13|log-parameters",
            "S13|include-error-detail",
        ]);

        string dump = string.Join(
            '\n',
            findings.Select(static x => JsonSerializer.Serialize(x.Finding, Json) + "\n" + Everything(x.Finding)));

        foreach (string sentinel in Withheld)
        {
            dump.Should().NotContain(sentinel);
        }

        // Stronger than the rule asks: these checks show no keyword's value at all, no path, and no host.
        dump.Should().NotContain("S3NT1NEL");
        dump.Should().NotContain("amazonaws").And.NotContain("the-proxy").And.NotContain("db.example");
        dump.Should().NotContain("Host=").And.NotContain("Username=");

        // And it is not empty of what the checks do say.
        dump.Should().Contain("Persist Security Info").And.Contain("PGPASSWORD").And.Contain("SSL Password");
    }

    [Fact]
    public void The_health_checks_export_of_those_findings_holds_codes_and_counts_only()
    {
        List<(string Code, HealthFinding Finding)> findings = Findings();

        HealthCheckResult[] results = [.. findings.GroupBy(static x => x.Code).Select(group => Result(group.Key, [.. group.Select(static x => x.Finding)]))];
        HealthSweep sweep = new("default", "db", HealthSweepKind.Regular, At, TimeSpan.Zero, results);
        HealthExportSummary summary = HealthExportSummary.From(At, [sweep]);

        string exported = summary.Describe(TimeSpan.FromSeconds(60)) + "\n" + JsonSerializer.Serialize(summary.ToData(), Json) + "\n" + JsonSerializer.Serialize(summary, Json);

        foreach (string sentinel in Withheld)
        {
            exported.Should().NotContain(sentinel);
        }

        exported.Should().NotContain("S3NT1NEL").And.NotContain("amazonaws").And.NotContain("db.example");
        summary.Warning.Should().Contain(["C16", "S12"], "the findings are in it, by code");
        summary.Info.Should().Contain(["C18", "S13"]);
    }

    private static HealthCheckResult Result(string code, HealthFinding[] findings) =>
        new(
            new HealthCheckInfo(code, HealthCheckRegistry.AreaOf(code[0])!.Value, HealthCostTier.Catalog, HealthGate.Database, "A check", null, typeof(object)),
            HealthCheckOutcome.Checked,
            findings,
            null,
            null,
            [],
            TimeSpan.Zero);
}
