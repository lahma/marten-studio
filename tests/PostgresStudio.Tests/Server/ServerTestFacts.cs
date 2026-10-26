using PostgresStudio.Services.Health;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Tests.Server;

/// <summary>The facts the W5-S2 tests state: a connection, settings, extensions and grants, each with a healthy default.</summary>
internal static class ServerTestFacts
{
    public static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    public static PostgresPlatform SelfHosted => PostgresPlatform.SelfHosted;

    public static PostgresPlatform Platform(PostgresPlatformKind kind) => new(kind, kind == PostgresPlatformKind.SelfHosted ? null : "a marker");

    /// <summary>A connection as <c>app</c>, not a superuser, encrypted, from another machine, on a primary.</summary>
    public static ServerConnectionPosture Posture(
        string login = "app",
        bool superuser = false,
        string[]? memberOf = null,
        string? reading = null,
        bool? ssl = true,
        string? client = "10.1.2.3",
        DateTimeOffset? validUntil = null,
        bool inRecovery = false,
        bool statementsLoaded = true) =>
        new(
            login,
            superuser,
            memberOf ?? [],
            reading ?? login,
            false,
            [],
            ssl,
            ssl == true ? "TLSv1.3" : null,
            client,
            validUntil,
            inRecovery,
            Now.AddDays(-3),
            "off",
            statementsLoaded);

    public static ServerSettingValue Setting(string name, string value, string? unit = null, string? source = "default", string? boot = null, string? context = "user") =>
        new(name, value, unit, source, false, context, boot ?? value);

    public static ServerSettingsRead Settings(params ServerSettingValue[] rows) => new(rows);

    public static HealthEvaluation Evaluation(bool postgres = false) =>
        HealthEvaluation.At(Now, text: postgres ? PostgresStudio.Services.Postgres.PostgresStudioText.Instance : null);

    /// <summary>The reading role's grants: <paramref name="roles" /> held, the others not.</summary>
    public static PostgresGrants Grants(string readsAs = "app", bool superuser = false, params string[] roles) =>
        new(
            readsAs,
            superuser,
            [.. PostgresStudio.Internal.Sql.ServerReachQueries.Roles.Select(x => new PostgresRoleMembership(x, true, roles.Contains(x)))])
        {
            LoginRole = "app",
        };

    /// <summary>
    /// Every finding of <paramref name="verdict" /> is one <typeparamref name="TCheck" /> may make: its own code, a listed
    /// variant, a key of its own - the rules the sweep refuses a verdict for.
    /// </summary>
    public static HealthVerdict ShouldBeWellFormedFor<TCheck>(this HealthVerdict verdict)
        where TCheck : IHealthCheck
    {
        HealthCheckInfo info = HealthCheckRegistry.Describe(typeof(TCheck));

        foreach (HealthFinding finding in verdict.Findings)
        {
            finding.Code.Should().Be(info.Code);
            HealthCheckRegistry.FindingProblems(info, finding).Should().BeEmpty();
        }

        verdict.Findings.Select(static x => x.Key).Should().OnlyHaveUniqueItems();
        return verdict;
    }
}
