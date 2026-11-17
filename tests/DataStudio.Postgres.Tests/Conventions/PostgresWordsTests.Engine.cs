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
