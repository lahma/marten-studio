using System.Reflection;

namespace MartenStudio.Integration.Tests.Health;

/// <summary>
/// Every health check the studio ships has a live test that makes it fire and one that proves it quiet.
/// </summary>
/// <remarks>
/// <para>
/// A plain <c>[Fact]</c>: it reads attributes, not a database, so it runs - and fails - without Docker. It
/// lives here rather than in the fast suite because the fast suite cannot see this assembly.
/// </para>
/// <para>
/// A marked test must be a live one - a <c>[PostgresFact]</c> or <c>[PostgresTheory]</c> - because the point
/// of the rule is a real Postgres, and an attribute on a unit test would satisfy it vacuously. A code no
/// shipped check has is a stale mark and fails too. <see cref="The_scanner_finds_marks_and_holds_them_to_the_rules" />
/// proves the scanner can fail.
/// </para>
/// </remarks>
public class HealthLiveTestConventionTests
{
    /// <summary>
    /// The shared checks whose live proof is <c>tests/PostgresStudio.Integration.Tests</c>' - from W5, a live test that uses no
    /// Marten type goes there - and where: that suite's own convention (its <c>ProvenHere</c>) holds each to a fires and a
    /// quiet test, so here each is only named, never proven twice.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> ProvenInPostgresStudioSuite = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["R02"] = "archive commands that fail, hang and succeed - Replication/ArchiverAndCheckpointLiveTests",
        ["R03"] = "a primary and standby pair on 17 and 18 - Replication/StandbyLagLiveTests",
        ["R04"] = "a tiny max_wal_size under bulk writes on 17 and 18 - Replication/ArchiverAndCheckpointLiveTests",
        ["S04"] = "the matrix's superuser, app and a file-access login - Server/ServerPostureLiveTests",
        ["S05"] = "TCP without TLS, and a server given a self-signed certificate - Server/ConnectionTlsLiveTests",
        ["S06"] = "PUBLIC's CREATE on public on 14, none on 17 - Server/ServerPostureLiveTests",
        ["S07"] = "pgstattuple installed at 1.4, then updated - Server/ServerPostureLiveTests",
        ["S08"] = "a login whose password expires in three days - Server/ServerPostureLiveTests",
        ["S09"] = "a login whose own settings switch enable_seqscan off - Server/ServerPostureLiveTests",
        ["S10"] = "a login whose own settings start every transaction read-only - Server/ServerPostureLiveTests",
        ["K05"] = "a live host's options with and without SqlConsoleRole - Server/ServerPostureLiveTests",
        ["K06"] = "a live host's options with \"*\" and with and without SqlConsoleRole - Server/ServerPostureLiveTests",
        ["K07"] = "a studio mapped on Kestrel, anonymous and authorized - Server/AnonymousStudioLiveTests",
    };

    [Fact]
    public void Every_check_has_a_live_test_that_fires_it_and_one_that_proves_it_quiet()
    {
        IReadOnlyList<string> codes =
        [
            .. StudioHealthChecks.Registry.All.Select(static x => x.Info.Code).Where(static x => !ProvenInPostgresStudioSuite.ContainsKey(x)),
        ];
        codes.Should().NotBeEmpty();

        // Concrete classes only: the made-up ones below are abstract, and are the theory's, not the rule's.
        IReadOnlyList<string> problems = Problems(codes, typeof(HealthLiveTestConventionTests).Assembly.GetTypes().Where(static x => !x.IsAbstract));

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
    }

    /// <summary>A check proven in the other suite is a shared one this studio ships - never one of MartenStudio's own.</summary>
    [Fact]
    public void Every_check_proven_in_the_PostgresStudio_suite_is_a_shipped_shared_check()
    {
        foreach (string code in ProvenInPostgresStudioSuite.Keys)
        {
            PostgresStudio.Services.Health.HealthCheckDescriptor? check = StudioHealthChecks.Registry.Find(code);

            check.Should().NotBeNull(code + " is listed but is not a check MartenStudio ships");
            check!.Info.CheckType.Assembly.Should().BeSameAs(typeof(PostgresStudio.Services.Health.HealthCheckRegistry).Assembly,
                code + " is MartenStudio's own, whose live proof needs a Marten store and belongs here");
        }
    }

    /// <summary>The anti-vacuity half.</summary>
    [Theory]
    [InlineData(typeof(BothMarks), null)]
    [InlineData(typeof(OnlyFires), "K01 has no [HealthQuiet]")]
    [InlineData(typeof(OnlyQuiet), "K01 has no [HealthFires]")]
    [InlineData(typeof(MarkedUnitTest), "is not a [PostgresFact] or [PostgresTheory]")]
    [InlineData(typeof(StaleMark), "names Z99")]
    public void The_scanner_finds_marks_and_holds_them_to_the_rules(Type tests, string? problem)
    {
        IReadOnlyList<string> problems = Problems(["K01"], [tests]);

        if (problem is null)
        {
            problems.Should().BeEmpty();
        }
        else
        {
            problems.Should().Contain(x => x.Contains(problem, StringComparison.Ordinal));
        }
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
                problems.Add(where + " names " + code + ", which no shipped check has.");
            }
        }

        foreach (string code in codes)
        {
            if (!marks.Any(x => x.Fires && x.Code == code))
            {
                problems.Add(code + " has no [HealthFires] live test.");
            }

            if (!marks.Any(x => !x.Fires && x.Code == code))
            {
                problems.Add(code + " has no [HealthQuiet] live test.");
            }
        }

        return problems;
    }

    // -----------------------------------------------------------------------------------------------
    // Made-up test classes for the anti-vacuity theory. Never run: they are abstract, which xunit does not
    // instantiate, and public, which its analyzers require of a class holding test methods.
    // -----------------------------------------------------------------------------------------------

    public abstract class BothMarks
    {
        [PostgresFact, HealthFires("K01")]
        public void Fires()
        {
        }

        [PostgresFact, HealthQuiet("K01")]
        public void Quiet()
        {
        }
    }

    public abstract class OnlyFires
    {
        [PostgresFact, HealthFires("K01")]
        public void Fires()
        {
        }
    }

    public abstract class OnlyQuiet
    {
        [PostgresFact, HealthQuiet("K01")]
        public void Quiet()
        {
        }
    }

    public abstract class MarkedUnitTest
    {
        [Fact, HealthFires("K01")]
        public void Fires()
        {
        }

        [PostgresFact, HealthQuiet("K01")]
        public void Quiet()
        {
        }
    }

    public abstract class StaleMark
    {
        [PostgresFact, HealthFires("K01"), HealthFires("Z99")]
        public void Fires()
        {
        }

        [PostgresFact, HealthQuiet("K01")]
        public void Quiet()
        {
        }
    }
}
