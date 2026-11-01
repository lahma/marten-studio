using System.Globalization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using PostgresStudio.Services.Server;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>The host's environment as the posture checks read it: K01's way, <c>IHostEnvironment.EnvironmentName</c>.</summary>
internal static class HostEnvironmentName
{
    /// <summary>The environment's name, or <see langword="null" /> when the host registers none or names none.</summary>
    public static string? Of(HealthCheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        string? name = context.Services.GetService<IHostEnvironment>()?.EnvironmentName;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>Whether <paramref name="name" /> is Production, as <c>IHostEnvironment</c> spells it, in any case.</summary>
    public static bool IsProduction(string? name) => string.Equals(name, Environments.Production, StringComparison.OrdinalIgnoreCase);

    /// <summary>The evidence value: the name, or that the host names none.</summary>
    public static string Describe(string? name) => name ?? "not named by the host";
}

/// <summary>How the posture checks name the connection they read on, in each studio's words.</summary>
internal static class SourceWords
{
    /// <summary>"the store's data source" in MartenStudio, "the data source" in Postgres Studio, whose source is one.</summary>
    public static string DataSource(Settings.StudioText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Product == Settings.StudioProduct.PostgresStudio ? "the data source" : text.SourcePossessive + " data source";
    }
}

/// <summary>What S04 reads.</summary>
/// <param name="Posture">The source's connection: its login, and the login's roles.</param>
/// <param name="EnvironmentName">The host's environment, or <see langword="null" />.</param>
/// <param name="Platform">Who runs the server, for the admin role's name.</param>
internal sealed record SuperuserConnectionFacts(ServerConnectionPosture Posture, string? EnvironmentName, PostgresPlatform Platform)
{
    /// <summary>Whether the host says this is Production.</summary>
    public bool IsProduction => HostEnvironmentName.IsProduction(EnvironmentName);
}

/// <summary>
/// S04: the source's data source logs in as a superuser, a managed platform's admin role, or a member of a role that reaches
/// the server's files and programs.
/// </summary>
/// <remarks>
/// <para>
/// <b>The login, not the reading role.</b> <c>session_user</c> on a connection of the source's own data source is the role the
/// application's connections log in as, before any <c>SET ROLE</c> - and <c>SqlConsoleRole</c>, which the studio's own reads
/// switch to, does not change it. A role the application switches to later is not seen.
/// </para>
/// <para>
/// <b>Three kinds, worst first</b> (B0 §5): a true superuser (<c>rolsuper</c>, which no managed provider hands out); a
/// provider admin - a member of <c>rds_superuser</c>, <c>azure_pg_admin</c>, <c>cloudsqlsuperuser</c> or
/// <c>neon_superuser</c>, which is never called a superuser; or a member of <c>pg_read_server_files</c>,
/// <c>pg_write_server_files</c> or <c>pg_execute_server_program</c>. Membership is <c>MEMBER</c>: a role that may
/// <c>SET ROLE</c> to one holds its power whether or not it inherits it.
/// </para>
/// <para>
/// <b>Warning in Production, Info anywhere else</b>, read as S02 and K01 read the environment: a demo or a test on a throwaway
/// server's superuser is common and costs nothing. No remedy line: the change is a role that owns the store's schema and
/// nothing more, which is the DBA's to make, and the finding says so in words.
/// </para>
/// </remarks>
[HealthCheck("S04", HealthArea.Server, HealthCostTier.Catalog, "Connecting as a superuser", Variants = ["superuser", "provider-admin", "file-access"])]
internal sealed class SuperuserConnectionCheck : HealthCheck<SuperuserConnectionFacts>
{
    /// <inheritdoc />
    public override async Task<SuperuserConnectionFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        ServerConnectionPosture posture = await ServerConnectionPosture.SharedAsync(context).ConfigureAwait(false);
        PostgresServerFacts server = await PostgresServerFacts.SharedAsync(context).ConfigureAwait(false);

        return new SuperuserConnectionFacts(posture, HostEnvironmentName.Of(context), server.Platform);
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(SuperuserConnectionFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        ServerConnectionPosture posture = facts.Posture;
        IReadOnlyList<string> admin = posture.LoginProviderAdminRoles;
        IReadOnlyList<string> files = posture.LoginFileAccessRoles;
        string source = evaluation.Text.SourcePossessive;

        if (!posture.LoginSuperuser && admin.Count == 0 && files.Count == 0)
        {
            return HealthVerdict.Quiet(
                Capitalised(SourceWords.DataSource(evaluation.Text)) + " logs in as role " + posture.LoginRole + ", which is not a superuser, a provider admin role "
                + "or a member of a role that reaches the server's files.");
        }

        HealthSeverity severity = facts.IsProduction ? HealthSeverity.Warning : HealthSeverity.Info;
        string ownerAdvice =
            " The application needs a role that owns its schema and nothing more: create one for it, give it the schema, and "
            + "point the data source at it - a DBA's change, which the studio never makes.";

        (string variant, string title, string why) = posture switch
        {
            { LoginSuperuser: true } => (
                "superuser",
                "The application connects to Postgres as a superuser",
                "A superuser passes every permission check. One SQL injection in the application, or one mistaken statement, can read "
                + "and change every database on the server, drop any table, read and write the server's files and run programs on it "
                + "(COPY … PROGRAM) - and a bug that would be refused with a permission error is not." + ownerAdvice),
            _ when admin.Count > 0 => (
                "provider-admin",
                "The application connects as a provider admin role (" + admin[0] + ")",
                "It is not a superuser - " + facts.Platform.Name + " does not hand one out - but it is the platform's administrator: it "
                + "creates roles and databases, can grant itself the roles that read every table and every session, and owns what it "
                + "creates. One SQL injection in the application reaches all of that." + ownerAdvice),
            _ => (
                "file-access",
                "The application connects as a role that may read or write the server's files",
                "A member of " + string.Join(", ", files) + " can make COPY read and write any file the server's operating-system user "
                + "can - its configuration, its data files, the server log - or run programs as that user (pg_execute_server_program). "
                + "One SQL injection in the application reaches the server itself." + ownerAdvice),
        };

        List<HealthEvidence> evidence =
        [
            new("Logs in as", posture.LoginRole, HealthGate.Database),
            new("Superuser", posture.LoginSuperuser ? "yes" : "no"),
        ];

        if (!posture.LoginSuperuser)
        {
            evidence.Add(new HealthEvidence("Member of", string.Join(", ", admin.Concat(files))));
        }

        evidence.Add(new HealthEvidence("Environment", HostEnvironmentName.Describe(facts.EnvironmentName)));
        evidence.Add(new HealthEvidence("Platform", facts.Platform.Name));

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "S04",
            Severity = severity,
            Variant = variant,
            Title = title,
            WhyItMatters = why,
            Evidence = evidence,
            Caveat =
                "Read as session_user on a connection from " + SourceWords.DataSource(evaluation.Text) + " - the role it logs in as, before any SET ROLE. "
                + (facts.IsProduction
                    ? "This is Production."
                    : "Info rather than Warning: the environment is " + HostEnvironmentName.Describe(facts.EnvironmentName)
                      + ", and a throwaway server's superuser is common outside Production."),
        });
    }

    private static string Capitalised(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.InvariantCulture) + text[1..];
}
