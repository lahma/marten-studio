using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Neither studio assembly creates a dependency-injection scope: <c>src/PostgresStudio</c> and <c>src/MartenStudio</c> contain no
/// <c>IServiceScopeFactory</c>, <c>CreateScope</c> or <c>CreateAsyncScope</c> (1.2-SEC-FIX).
/// </summary>
/// <remarks>
/// <para>
/// A studio service is scoped to the Blazor circuit, and it is the circuit that holds the visitor: the authenticated user
/// (<c>AuthenticationStateProvider</c>), the circuit's own <c>StudioState</c> and the per-circuit action ring. A scope made
/// anywhere else is a fresh container with <b>no circuit and no user</b> - an <c>IAuthorizationService</c> asked in it sees an
/// anonymous principal, a scoped service in it is a second instance with none of the circuit's state, and a policy a host wrote
/// against the visitor decides about nobody. A gate asked in such a scope would pass or fail for the wrong reason, and the studio
/// would believe the answer.
/// </para>
/// <para>
/// The diagnostics engine's scopes are the only ones: <c>src/PostgresStudio.Diagnostics</c> runs unattended (the sweep, the
/// connection history's sampler, the API's own calls), under no visitor's identity by design (D29, D31), and is not scanned
/// here. A studio that needs the engine's work asks the engine; it does not open a scope of its own.
/// </para>
/// </remarks>
public class NoServiceScopeInStudiosTests
{
    private static readonly Regex Scope = new(
        @"\b(?<what>IServiceScopeFactory|CreateAsyncScope|CreateScope|AsyncServiceScope)\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly string[] Scanned = [StudioProjects.Shared, StudioProjects.Marten];

    [Fact]
    public void Neither_studio_assembly_makes_or_asks_for_a_service_scope()
    {
        string[] files = StudioFiles();
        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        foreach (string project in Scanned)
        {
            files.Should().Contain(x => SourceScanner.Relative(x).StartsWith("src/" + project + "/", StringComparison.Ordinal), project + " is scanned");
        }

        string[] offenders = [.. files.SelectMany(OffendersIn).Order(StringComparer.Ordinal)];

        offenders.Should().BeEmpty(
            "a fresh DI scope has no circuit and no user: an authorization question asked in it sees nobody, and a scoped studio "
            + "service in it is a second instance with none of the circuit's state. Only the diagnostics engine's unattended work "
            + "makes scopes. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_scan_still_sees_the_diagnostics_engines_scopes_which_it_does_not_count()
    {
        // Anti-vacuity: the pattern finds real scopes, in the one assembly allowed to have them.
        string[] engine =
        [
            .. SourceScanner.ShippedSourceFiles()
                .Where(static x => SourceScanner.Relative(x).StartsWith("src/" + StudioProjects.Diagnostics + "/", StringComparison.Ordinal))
                .Where(static x => Offends(File.ReadAllText(x))),
        ];

        engine.Should().NotBeEmpty("the engine's sweep, sampler and API make scopes; a scan that finds none proves nothing");
    }

    [Theory]
    [InlineData("await using AsyncServiceScope scope = factory.CreateAsyncScope();", true)]
    [InlineData("using IServiceScope scope = services.CreateScope();", true)]
    [InlineData("private readonly IServiceScopeFactory scopes;", true)]
    [InlineData("@inject IServiceScopeFactory Scopes", true)]
    [InlineData("var x = provider\n    .CreateScope();", true)]
    [InlineData("// a CreateScope here would have no circuit and no user", false)]
    [InlineData("var why = \"IServiceScopeFactory has no user\";", false)]
    [InlineData("var scopeName = StudioScope.Of(key);", false)]
    public void The_scan_catches_a_scope_and_ignores_prose(string source, bool offends)
    {
        Offends(source).Should().Be(offends);
    }

    private static string[] StudioFiles() =>
    [
        .. SourceScanner.ShippedSourceFiles().Where(static x =>
        {
            string relative = SourceScanner.Relative(x);
            return Scanned.Any(project => relative.StartsWith("src/" + project + "/", StringComparison.Ordinal));
        }),
    ];

    private static IEnumerable<string> OffendersIn(string path)
    {
        if (Offends(File.ReadAllText(path)))
        {
            yield return SourceScanner.Relative(path);
        }
    }

    private static bool Offends(string source) => Scope.IsMatch(SourceScanner.StripCommentsAndStrings(source));
}
