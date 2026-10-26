using System.Reflection;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services.Health;

namespace PostgresStudio.Integration.Tests.Health;

/// <summary>
/// Every health check Postgres Studio ships is accounted for: proven here, on a plain database, by a live test that fires
/// it and one that proves it quiet - or named, with the reason, as proven in <c>MartenStudio.Integration.Tests</c> on a
/// server set up for it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why two homes.</b> The checks are the shared studio's and run unchanged against both kinds of source; what PS-3b
/// gave Postgres Studio of its own is the table source - every table of the browsable schemas, each the host's, a
/// library's or somebody else's - and the remedies' migration SQL. So the checks that read the table source are proven
/// here, through it. The others read the server or the database as a whole, the same statements for either studio, and
/// each needs what a shared plain container cannot give: a server setting of its own, an extension preloaded, or a
/// condition that only the server's clock can age (a minute idle, five minutes running). The Marten suite starts those
/// servers, and its own convention holds every one of these codes to a fires and a quiet test there.
/// </para>
/// <para>
/// A plain <c>[Fact]</c>: it reads attributes and builds a container, so it runs - and fails - without Docker. A code
/// that is in neither list, in both, or not a shipped check fails; so does a mark that is not on a live test, or on a code
/// this suite does not claim. <see cref="The_scanner_finds_marks_and_holds_them_to_the_rules" /> proves it can fail.
/// </para>
/// </remarks>
public class HealthLiveTestConventionTests
{
    /// <summary>The checks this suite proves, through the table source, on a plain database.</summary>
    internal static readonly string[] ProvenHere = ["T01", "T03", "T04", "T05", "T06", "T07", "T08", "T12"];

    /// <summary>Every other shipped check, and why its live proof is the Marten suite's.</summary>
    internal static readonly IReadOnlyDictionary<string, string> ProvenElsewhere = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["C01"] = "a pool saturated across two samples - ConnectionPoolLiveTests; its quiet half on Npgsql 9 and 10 is NpgsqlPoolLiveTests here",
        ["C03"] = "a server whose max_connections is lowered - ConnectionServerLiveTests",
        ["C04"] = "a real idle transaction, gathered and judged past a minute on - SessionActivityLiveTests",
        ["C05"] = "a real running statement, gathered and judged five minutes on - SessionActivityLiveTests",
        ["C06"] = "a real lock wait, gathered and judged past thirty seconds on - SessionActivityLiveTests",
        ["C07"] = "a server with max_prepared_transactions above zero - ConnectionServerLiveTests",
        ["C08"] = "a thousand transactions' worth of database-wide counters - DatabaseCounterLiveTests",
        ["R01"] = "a server with wal_level=logical - ServerSafetyLiveTests",
        ["S01"] = "a server version past its end of life - ServerSettingsLiveTests",
        ["S02"] = "a server with fsync and full_page_writes on, which the Testcontainers default is not - ServerSettingsLiveTests and ServerSafetyLiveTests",
        ["S03"] = "a server with data checksums - ServerSafetyLiveTests",
        ["T02"] = "a server with pg_stat_statements preloaded - StatementChecksLiveTests",
        ["T13"] = "a server with a lowered autovacuum_multixact_freeze_max_age - HorizonLiveTests",
        ["T14"] = "a horizon held for an hour of the server's clock - HorizonLiveTests",
        ["T23"] = "a server with pg_stat_statements preloaded and JIT on - StatementChecksLiveTests",
    };

    [Fact]
    public void Every_shipped_check_is_proven_here_or_named_with_where_it_is_proven()
    {
        IReadOnlyList<string> codes = ShippedCodes();
        codes.Should().NotBeEmpty();

        List<string> problems = [];

        foreach (string code in codes)
        {
            bool here = ProvenHere.Contains(code, StringComparer.Ordinal);
            bool elsewhere = ProvenElsewhere.ContainsKey(code);

            if (here == elsewhere)
            {
                problems.Add(code + (here ? " is claimed both here and elsewhere." : " is a shipped check this suite neither proves nor names."));
            }
        }

        problems.AddRange(ProvenHere.Concat(ProvenElsewhere.Keys).Where(x => !codes.Contains(x, StringComparer.Ordinal))
            .Select(static x => x + " is listed but is not a check Postgres Studio ships."));

        problems.AddRange(Problems(ProvenHere, typeof(HealthLiveTestConventionTests).Assembly.GetTypes().Where(static x => !x.IsAbstract)));

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
    }

    /// <summary>The anti-vacuity half.</summary>
    [Theory]
    [InlineData(typeof(BothMarks), null)]
    [InlineData(typeof(OnlyFires), "T01 has no [HealthQuiet]")]
    [InlineData(typeof(OnlyQuiet), "T01 has no [HealthFires]")]
    [InlineData(typeof(MarkedUnitTest), "is not a [PostgresFact] or [PostgresTheory]")]
    [InlineData(typeof(StaleMark), "names Z99")]
    public void The_scanner_finds_marks_and_holds_them_to_the_rules(Type tests, string? problem)
    {
        IReadOnlyList<string> problems = Problems(["T01"], [tests]);

        if (problem is null)
        {
            problems.Should().BeEmpty();
        }
        else
        {
            problems.Should().Contain(x => x.Contains(problem, StringComparison.Ordinal));
        }
    }

    /// <summary>The codes of the checks a Postgres Studio container registers, as a host's builds it.</summary>
    private static IReadOnlyList<string> ShippedCodes()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAuthorization();
        services.AddPostgresStudio();
        services.AddScoped<AuthenticationStateProvider, MartenStudio.Tests.Support.TestAuthenticationStateProvider>();

        using ServiceProvider provider = services.BuildServiceProvider();
        return [.. provider.GetRequiredService<HealthCheckRegistry>().All.Select(static x => x.Info.Code)];
    }

    /// <summary>Everything wrong with how <paramref name="types" /> cover <paramref name="codes" />.</summary>
    internal static IReadOnlyList<string> Problems(IReadOnlyList<string> codes, IEnumerable<Type> types)
    {
        List<(MethodInfo Method, string Code, bool Fires)> marks = [];

        foreach (Type type in types)
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                marks.AddRange(method.GetCustomAttributes<HealthFiresAttribute>().Select(x => (method, x.Code, true)));
                marks.AddRange(method.GetCustomAttributes<HealthQuietAttribute>().Select(x => (method, x.Code, false)));
            }
        }

        List<string> problems = [];

        foreach ((MethodInfo method, string code, bool _) in marks)
        {
            string where = method.DeclaringType?.Name + "." + method.Name;

            if (method.GetCustomAttribute<PostgresFactAttribute>() is null && method.GetCustomAttribute<PostgresTheoryAttribute>() is null)
            {
                problems.Add(where + " is marked for " + code + " but is not a [PostgresFact] or [PostgresTheory].");
            }

            if (!codes.Contains(code, StringComparer.Ordinal))
            {
                problems.Add(where + " names " + code + ", which this suite does not prove.");
            }
        }

        foreach (string code in codes)
        {
            if (!marks.Any(x => x.Code == code && x.Fires))
            {
                problems.Add(code + " has no [HealthFires] live test.");
            }

            if (!marks.Any(x => x.Code == code && !x.Fires))
            {
                problems.Add(code + " has no [HealthQuiet] live test.");
            }
        }

        return problems;
    }

    public abstract class BothMarks
    {
        [PostgresFact]
        [HealthFires("T01")]
        public void Fires()
        {
        }

        [PostgresFact]
        [HealthQuiet("T01")]
        public void Quiet()
        {
        }
    }

    public abstract class OnlyFires
    {
        [PostgresFact]
        [HealthFires("T01")]
        public void Fires()
        {
        }
    }

    public abstract class OnlyQuiet
    {
        [PostgresFact]
        [HealthQuiet("T01")]
        public void Quiet()
        {
        }
    }

    public abstract class MarkedUnitTest
    {
        [Fact]
        [HealthFires("T01")]
        [HealthQuiet("T01")]
        public void Both()
        {
        }
    }

    public abstract class StaleMark
    {
        [PostgresFact]
        [HealthFires("T01")]
        [HealthQuiet("T01")]
        [HealthFires("Z99")]
        public void Stale()
        {
        }
    }
}
