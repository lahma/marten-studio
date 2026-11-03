using MartenStudio.Tests.Health;

using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Tests.Connection;

/// <summary>Stated facts, reports and views for the Connection page's tests (NP3): everything the page is built from, said outright.</summary>
internal static class ConnectionTestFacts
{
    /// <summary>A connection string with a remote host, a database and a role - nothing a test does not name.</summary>
    public const string Remote = "Host=db.example;Port=5432;Database=app;Username=svc;Persist Security Info=false";

    private static readonly DateTimeOffset Now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    /// <summary>What the server saw, with the connection's TLS state stated.</summary>
    public static ConnectionObserved Observed(
        bool? ssl = true,
        string? sslVersion = "TLSv1.3",
        bool? gssEncrypted = null,
        string sessionUser = "svc",
        string currentUser = "svc",
        long? idleSessionTimeoutMs = 0,
        long? statementTimeoutMs = 0,
        long? idleInTransactionMs = 0,
        long? transactionMs = null,
        string? passwordEncryption = "scram-sha-256",
        bool inRecovery = false) => new(
        CurrentUser: currentUser,
        SessionUser: sessionUser,
        Database: "app",
        ServerAddress: "10.0.0.5",
        ServerPort: 5432,
        InRecovery: inRecovery,
        TimeZone: "UTC",
        ClientEncoding: "UTF8",
        SearchPath: "\"$user\", public",
        ApplicationName: "PostgresStudio",
        IdleSessionTimeoutMs: idleSessionTimeoutMs,
        StatementTimeoutMs: statementTimeoutMs,
        IdleInTransactionTimeoutMs: idleInTransactionMs,
        TransactionTimeoutMs: transactionMs,
        PasswordEncryption: passwordEncryption,
        Ssl: ssl,
        SslVersion: ssl == true ? sslVersion : null,
        SslCipher: ssl == true ? "TLS_AES_256_GCM_SHA384" : null,
        SslBits: ssl == true ? 256 : null,
        GssAuthenticated: gssEncrypted is null ? null : false,
        GssEncrypted: gssEncrypted,
        KnownOptionNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase))
    {
        ReportedParameters = new Dictionary<string, string> { ["server_version"] = "17.4", ["is_superuser"] = "off", ["scram_iterations"] = "4096" },
    };

    /// <summary>The facts of a connection string read the way the engine reads it, with a stated environment and platform.</summary>
    public static ConnectionFacts Facts(
        string connectionString,
        ConnectionObserved? observed = null,
        PostgresPlatformKind platform = PostgresPlatformKind.SelfHosted,
        FakeEnvironment? environment = null,
        string? observedNotRead = null)
    {
        ConnectionSettings settings = NpgsqlSettingsReader.Read(connectionString, new ConnectionReadContext
        {
            Environment = environment ?? new FakeEnvironment(),
            ServerSettingNames = observed?.KnownOptionNames,
            Platform = platform,
            Observed = observed,
        });

        return new ConnectionFacts
        {
            Settings = settings,
            Observed = observed,
            ObservedNotRead = observed is null ? observedNotRead ?? "no server in this test" : null,
            Platform = new PostgresPlatform(platform, null),
        };
    }

    /// <summary>A finding the way the Health report hands it over, with the connection's code.</summary>
    public static HealthReportFinding Finding(string code, HealthSeverity severity, string title, bool accepted = false) =>
        new(
            FakeHealthService.Finding(code, severity, title, HealthArea.Connections) with { Accepted = accepted },
            [new HealthEvidence("Setting", "value")],
            "default|app|" + code,
            "https://example.test/health/codes#" + code.ToLowerInvariant(),
            RemedyAvailable: true,
            HostRuns: null)
        {
            Anchor = "finding-" + code.ToLowerInvariant(),
        };

    /// <summary>A report with these findings.</summary>
    public static HealthReport Report(int withheld = 0, string? withheldReason = null, params HealthReportFinding[] findings) => new()
    {
        StoreKey = "default",
        DatabaseId = "app",
        MeasuredAt = Now,
        Findings = findings,
        WithheldCount = withheld,
        WithheldReason = withheldReason,
    };

    /// <summary>The activity gate open, the way a visitor with <c>ViewDatabaseActivity</c> has it.</summary>
    public static SessionDetailsAccess GateOpen { get; } = new(true, false, true, DatabaseRefusal.None, null);

    /// <summary>The activity gate shut because the capability is off.</summary>
    public static SessionDetailsAccess GateClosed { get; } = new(false, false, null, DatabaseRefusal.CapabilityOff, "Process ids need PostgresStudioOptions.Capabilities.ViewDatabaseActivity.");

    /// <summary>The page's model for these facts.</summary>
    public static ConnectionPageView View(
        ConnectionFacts facts,
        HealthReport? report = null,
        SessionDetailsAccess? activity = null,
        SessionPoolSummary? pool = null,
        ConnectionPoolPeak? peak = null,
        params ConnectionContribution[] contributions) =>
        ConnectionPageBuilder.Build(new ConnectionPageInputs
        {
            Facts = facts,
            Report = report ?? Report(),
            Activity = activity ?? GateClosed,
            MeasuredAt = Now,
            Pool = pool,
            PoolPeak = peak,
            Contributions = contributions,
        });
}
