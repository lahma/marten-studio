using System.Reflection;
using System.Text.RegularExpressions;

using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Postgres;

namespace DataStudio.Postgres.Tests.Conventions;

/// <summary>
/// The engine's own words (REVIEW-V2-D2, finding 2): a host with <c>AddPostgresDiagnostics()</c> and no studio has no <c>Capabilities</c> and no
/// visitor to grant one to, so no sentence a shared check says there may name an option that does not exist. Every shared check is evaluated
/// in <see cref="PostgresText.DiagnosticsInstance" /> and every <c>PostgresDiagnosticsOptions…</c> it prints is resolved against the real
/// type by reflection.
/// </summary>
public partial class PostgresWordsTests
{
    [GeneratedRegex(@"PostgresDiagnosticsOptions\.[A-Za-z]+(?:\.[A-Za-z]+)*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex DiagnosticsOptionName();

    /// <summary>Whether <paramref name="name" />, <c>PostgresDiagnosticsOptions.A.B</c>, is a chain of public members the real options type has.</summary>
    private static bool IsRealOption(string name)
    {
        string[] path = name.Split('.');
        Type? type = typeof(PostgresDiagnosticsOptions);

        foreach (string member in path.Skip(1))
        {
            PropertyInfo? property = type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);

            if (property is null)
            {
                return false;
            }

            type = property.PropertyType;
        }

        return true;
    }

    /// <summary>What every shared check renders in the engine's own words, with where.</summary>
    private static List<(string Where, string Text)> RenderedInTheEnginesWords()
    {
        List<(string Where, string Text)> rendered = [];

        // The engine's checks only: a host with no studio runs these, and the checks of the studio's own assembly (the console, the browser,
        // the anonymous mapping) are about options it does not have.
        foreach (CheckWordCase checkCase in CheckWordCases.All.Where(static x => x.Check.Assembly == typeof(PostgresDiagnosticsOptions).Assembly))
        {
            HealthEvaluation evaluation = checkCase.Evaluation with { Text = PostgresText.DiagnosticsInstance };
            HealthCheckInfo info = HealthCheckRegistry.Describe(checkCase.Check, text: PostgresText.DiagnosticsInstance);
            HealthVerdict verdict = ((IHealthCheck) Activator.CreateInstance(checkCase.Check)!).Evaluate(checkCase.Facts, evaluation);

            rendered.AddRange(Rendered(info, verdict).Select(x => (checkCase + ": " + x.Where, x.Text)));
        }

        return rendered;
    }

    [Fact]
    public void An_engine_only_host_is_never_told_to_set_an_option_that_does_not_exist()
    {
        List<(string Where, string Text)> rendered = RenderedInTheEnginesWords();
        rendered.Should().NotBeEmpty();

        string[] names = [.. rendered.SelectMany(x => DiagnosticsOptionName().Matches(x.Text).Select(match => x.Where + " says " + match.Value))];

        names.Where(x => !IsRealOption(x[(x.IndexOf("says ", StringComparison.Ordinal) + 5)..]))
            .Should().BeEmpty("PostgresDiagnosticsOptions has no Capabilities: a sentence that names one tells a host to set what is not there");

        rendered.Should().NotContain(
            static x => x.Text.Contains("Capabilities", StringComparison.Ordinal) || x.Text.Contains("PostgresOptions", StringComparison.Ordinal),
            "there is no studio, so no studio option is named");

        rendered.Should().NotContain(
            static x => x.Text.Contains("ViewDatabaseActivity", StringComparison.Ordinal),
            "an engine-only host has no such capability; its call's IncludeActivity is the switch (R03's primary view said it bare)");
    }

    /// <summary>The anti-vacuity half: the cases do reach the activity sentences, and say what is true of the host's own call.</summary>
    [Fact]
    public void The_engines_activity_sentences_say_the_call_returns_them_and_the_cases_reach_them()
    {
        List<(string Where, string Text)> rendered = RenderedInTheEnginesWords();

        string[] codes = ["C04", "C05", "C06", "C07", "R01"];

        foreach (string code in codes)
        {
            rendered.Where(x => x.Where.StartsWith(code + " ", StringComparison.Ordinal))
                .Should().Contain(
                    static x => x.Text.Contains("returned only when the call sets IncludeActivity = true", StringComparison.Ordinal),
                    code + " says what an engine-only host does to see its activity-grade evidence");
        }

        PostgresText.Instance.StatementsOnSessionsPage.Should().Contain("Database › Sessions").And.Contain("PostgresOptions.Capabilities.ViewDatabaseActivity");
        PostgresText.DiagnosticsInstance.StatementsOnSessionsPage.Should().NotContain("Sessions").And.NotContain("Capabilities");
    }

    /// <summary>The fields an engine caveat may name of a session, each with what its evidence line holds when it has it (REVIEW-V2-E, finding 1).</summary>
    private static readonly (string Field, string Token)[] SessionFields =
    [
        ("process id", "pid "),
        ("role", ", role "),
        ("application", "application '"),
        ("client", ", from "),
    ];

    /// <summary>
    /// C04, C05 and C06 name sessions, never statements: what <c>IncludeActivity</c> returns of them is the session's process id, role,
    /// application and client. The caveat an engine-only host reads names exactly those fields and each is in that check's activity evidence.
    /// </summary>
    [Theory]
    [InlineData("C04")]
    [InlineData("C05")]
    [InlineData("C06")]
    public void A_session_checks_engine_caveat_names_only_fields_its_activity_evidence_holds(string code)
    {
        HealthFinding[] findings =
        [
            .. CheckWordCases.All
                .Where(x => x.Code == code)
                .SelectMany(x => ((IHealthCheck) Activator.CreateInstance(x.Check)!)
                    .Evaluate(x.Facts, x.Evaluation with { Text = PostgresText.DiagnosticsInstance }).Findings),
        ];

        string[] activity = [.. findings.SelectMany(static x => x.Evidence).Where(static x => x.Gate == HealthGate.Activity).Select(static x => x.Value)];
        activity.Should().NotBeEmpty(code + "'s cases reach activity-grade evidence");

        string[] caveats = [.. findings.Select(static x => x.Caveat).OfType<string>().Where(static x => x.Contains("returned only when", StringComparison.Ordinal))];
        caveats.Should().NotBeEmpty(code + " says what the call returns");

        foreach (string caveat in caveats)
        {
            string sentence = caveat[(caveat.IndexOf("Each session's", StringComparison.Ordinal))..];
            sentence.Should().StartWith("Each session's process id, role, application and client are returned only when the call sets IncludeActivity = true.");
            sentence.Should().NotContain("statements themselves", "no finding of this check carries a statement");

            foreach ((string field, string token) in SessionFields.Where(x => sentence.Contains(x.Field, StringComparison.Ordinal)))
            {
                activity.Should().Contain(x => x.Contains(token, StringComparison.Ordinal), "the caveat names the " + field + " and the evidence holds it");
            }
        }
    }

    /// <summary>
    /// The members of <see cref="DataStudio.Postgres.Services.Settings.StudioText" /> whose sentences name <c>…Options.Capabilities.*</c>, the scope
    /// policy or the studio's registration: the page refusals, the capability and scope-policy names, the registration line and the read-only reach (not <c>OptionName</c>, which the engine's own options also use). The engine's words
    /// hold them because the studio's one wording is one type, and no engine-only host may print one (REVIEW-V2-E, finding 6).
    /// </summary>
    private static string[] StudioOnlyMembers() =>
    [
        .. typeof(DataStudio.Postgres.Services.Settings.StudioText)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(static x => x.MemberType is MemberTypes.Property or MemberTypes.Method)
            .Select(static x => x.Name)
            .Where(static x => x.Contains("Denial", StringComparison.Ordinal)
                || x is "ScopePolicyRefused" or "ScopePolicyOption" or "ScopePolicyName" or "ScopePolicyNoun" or "CapabilityOption"
                    or "EveryCapabilityOff" or "OptionsRegistration")
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>The engine's source that reads one of <see cref="StudioOnlyMembers" />, as <c>file: member</c>; the two files that define the words are not readers.</summary>
    private static List<string> EngineReadsOfStudioOnlyMembers(IEnumerable<(string File, string Source)> sources)
    {
        System.Text.RegularExpressions.Regex read = new(
            @"\.(" + string.Join('|', StudioOnlyMembers().Select(System.Text.RegularExpressions.Regex.Escape)) + @")\b",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));
        List<string> reads = [];

        foreach ((string file, string source) in sources)
        {
            reads.AddRange(read.Matches(DataStudio.Marten.Tests.Conventions.SourceScanner.StripCommentsAndStrings(source)).Select(x => file + ": " + x.Groups[1].Value));
        }

        return reads;
    }

    /// <summary>
    /// No engine check, and not the diagnostics service, reads a page-refusal sentence or a name only a studio has: the reflection test above
    /// sees only what <c>CheckWordCases</c> happens to reach, and this holds the whole assembly when a check grows a branch the cases miss.
    /// </summary>
    [Fact]
    public void The_engine_assembly_never_reads_a_sentence_only_a_studio_host_may_print()
    {
        string[] studioOnly = StudioOnlyMembers();
        studioOnly.Should().Contain(["BrowseReadOnlyDenial", "SessionsCapabilityDenial", "StatementsWritePolicyDenial", "ScopePolicyRefused", "CapabilityOption", "EveryCapabilityOff"]);

        string[] defining = ["Services/Settings/StudioText.cs", "Services/Postgres/PostgresText.cs"];
        string engine = DataStudio.Marten.Tests.Conventions.RepositoryRoot.Combine("src", "DataStudio.Postgres.Diagnostics");

        (string File, string Source)[] sources =
        [
            .. Directory.EnumerateFiles(engine, "*.cs", SearchOption.AllDirectories)
                .Where(static x => !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !x.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                .Select(x => (File: Path.GetRelativePath(engine, x).Replace('\\', '/'), Source: File.ReadAllText(x)))
                .Where(x => !defining.Contains(x.File)),
        ];

        sources.Should().HaveCountGreaterThan(100, "the scan reads the engine's source, not an empty folder");
        EngineReadsOfStudioOnlyMembers(sources).Should().BeEmpty(
            "an engine-only host has no Capabilities, no scope policy and no studio registration to be told about; the page refusals are the studio's");
    }

    /// <summary>The scan can fail: a check that reads a refusal, a capability name or the read-only reach is found, and a comment or a string is not.</summary>
    [Theory]
    [InlineData("var x = evaluation.Text.BrowseReadOnlyDenial(option);", true)]
    [InlineData("string s = text.CapabilityOption(\"RunSql\");", true)]
    [InlineData("return settings.Text.EveryCapabilityOff(name);", true)]
    [InlineData("// text.CapabilityOption(\"RunSql\")\nvar y = 1;", false)]
    [InlineData("var s = \"text.CapabilityOption\";", false)]
    [InlineData("var o = settings.OptionName(\"SqlConsoleRole\");", false)]
    [InlineData("var z = evaluation.Text.ActivityShownOnlyWith;", false)]
    [InlineData("var z = evaluation.Text.HealthCheckRegistration;", false)]
    public void The_engine_scan_finds_a_planted_read_and_ignores_a_comment_a_string_and_the_words_the_engine_may_read(string source, bool found) =>
        EngineReadsOfStudioOnlyMembers([("Planted.cs", source)]).Count.Should().Be(found ? 1 : 0);

    /// <summary>The reflection check can fail: the option the review found, and a made-up one, are not real; the real ones are.</summary>
    [Theory]
    [InlineData("PostgresDiagnosticsOptions.Capabilities.ViewDatabaseActivity", false)]
    [InlineData("PostgresDiagnosticsOptions.NotAnOption", false)]
    [InlineData("PostgresDiagnosticsOptions.SqlConsoleRole", true)]
    [InlineData("PostgresDiagnosticsOptions.BrowsableSchemas", true)]
    [InlineData("PostgresDiagnosticsOptions.QueryTimeout", true)]
    public void The_option_check_resolves_real_options_and_refuses_the_rest(string name, bool real) =>
        IsRealOption(name).Should().Be(real);
}
