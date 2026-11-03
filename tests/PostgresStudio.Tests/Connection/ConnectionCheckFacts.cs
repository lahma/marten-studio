using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Tests.Connection;

/// <summary>Stated <see cref="ConnectionFacts" /> for the connection-settings checks (1.1-NP2): a connection string, what the server saw, and nothing else.</summary>
internal static class ConnectionCheckFacts
{
    public static readonly DateTimeOffset At = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    public const string ProxyHost = "the-proxy.proxy-demo.us-east-1.rds.amazonaws.com";

    /// <summary>What the server saw of a connection: only the fields the connection checks read are stated.</summary>
    public static ConnectionObserved Observed(long? idleSessionMs = 0, bool? ssl = false, string? sslVersion = null) =>
        new("app", "app", "orders", null, null, false, null, null, null, null, idleSessionMs, 0, 0, null, "scram-sha-256", ssl, sslVersion, null, null, null, null, new HashSet<string>());

    public static PostgresPlatform Platform(PostgresPlatformKind kind) =>
        kind == PostgresPlatformKind.SelfHosted ? PostgresPlatform.SelfHosted : new PostgresPlatform(kind, "marker");

    /// <summary>The facts of <paramref name="connectionString" /> as the sweep would read them, with the stated server-side values.</summary>
    public static ConnectionFacts Facts(
        string connectionString,
        ConnectionObserved? observed = null,
        PostgresPlatformKind platform = PostgresPlatformKind.SelfHosted,
        ConnectionSessionStatistics? sessions = null,
        ConnectionPoolSiblings? siblings = null,
        FakeEnvironment? environment = null,
        string? notRead = null)
    {
        ConnectionSettings settings = NpgsqlSettingsReader.Read(
            connectionString,
            new ConnectionReadContext
            {
                Environment = environment ?? new FakeEnvironment(),
                NpgsqlMajor = 10,
                Platform = platform,
                Observed = observed,
            });

        return new ConnectionFacts
        {
            Settings = settings,
            Observed = observed,
            ObservedNotRead = observed is null ? notRead ?? "stated" : null,
            Platform = Platform(platform),
            Sessions = sessions,
            Siblings = siblings,
        };
    }

    /// <summary>
    /// Facts whose effective values are stated directly, for a keyword the loaded Npgsql may not have
    /// (<c>Include Failed Batched Command</c> is Npgsql 10's, and the floor run loads 9.0.4).
    /// </summary>
    public static ConnectionFacts ValuesFacts(params (string Keyword, object Value)[] values) => new()
    {
        Settings = new ConnectionSettings
        {
            NpgsqlVersion = "10.0.3",
            NpgsqlMajor = 10,
            Values = new ConnectionValues(values.ToDictionary(static x => x.Keyword, static x => (object?) x.Value)),
        },
    };

    /// <summary>The text of everything a finding says, for the checks that hold no sentinel and no host in it.</summary>
    public static string Everything(HealthFinding finding) =>
        string.Join('\n', new[] { finding.Title, finding.WhyItMatters, finding.Caveat, finding.Remedy?.Code, finding.Remedy?.Explanation, finding.Subject }
            .Concat(finding.Evidence.Select(static x => x.Label + ": " + x.Value))
            .Where(static x => x is not null));
}
