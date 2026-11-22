using System.Collections.Immutable;
using System.Globalization;
using System.Xml.Linq;

using DataStudio.Analyzers;

using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace DataStudio.Marten.Tests.Conventions;

/// <summary>
/// DS1305, the culture guard every project under <c>src/</c> compiles with (<c>analyzers/DataStudio.Analyzers</c>; testing.md, <em>The fast
/// suites run in fi-FI</em>): what it flags, what it lets through, and that the build of <c>src/</c> still runs it.
/// </summary>
/// <remarks>
/// <para>
/// The real guard is the build: the analyzer reports an error there, so <c>src/</c> holds no number or date written in the server's culture
/// that it can see. These tests hold what it can see. Each case compiles a probe in process and runs the analyzer over it; a probe that does
/// not compile fails the test, because a case that passes against code with an error in it proves nothing.
/// </para>
/// <para>
/// The <c>.razor</c> cases are the C# the Razor compiler writes, not a <c>.razor</c> file: the shapes in <see cref="RazorProbe" /> are copied
/// from what SDK 10.0.404's Razor compiler generated for those lines (<c>EmitCompilerGeneratedFiles</c>, 2026-11-22), with their
/// <c>#line</c> directives, because the analyzer decides from them which generated code a <c>.razor</c> file wrote. If a later Razor
/// compiler writes another shape, the build stops seeing it and these tests do not notice; the fi-FI suites remain behind it.
/// </para>
/// </remarks>
public class CurrentCultureTextAnalyzerTests
{
    private const string Parameters =
        "long lag, double share, int count, DateTime at, TimeSpan span, bool focused, IEnumerable<double> shares, StringBuilder builder, TextWriter writer";

    private static readonly Lazy<ImmutableArray<MetadataReference>> References = new(() =>
    {
        // The two shared frameworks this suite runs on, and nothing of its own output, so no type is defined twice.
        string[] frameworks = [Path.GetDirectoryName(typeof(object).Assembly.Location)!, Path.GetDirectoryName(typeof(RenderTreeBuilder).Assembly.Location)!];
        string assemblies = (string) AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;

        return [.. assemblies.Split(Path.PathSeparator)
            .Where(x => frameworks.Contains(Path.GetDirectoryName(x) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            .Select(static x => (MetadataReference) MetadataReference.CreateFromFile(x))];
    });

    [Theory]
    [InlineData("_ = $\"{lag:N0} events behind\";", "This long written as \"N0\"")]
    [InlineData("_ = $\"every {share:0.#} s\";", "This double written as \"0.#\"")]
    [InlineData("_ = $\"{count:#,0} rows\";", "This int written as \"#,0\"")]
    [InlineData("_ = $\"{count:P0}\";", "This int written as \"P0\"")]
    [InlineData("_ = $\"{share}\";", "This double")]
    [InlineData("_ = $\"{(double?) share}\";", "This double")]
    [InlineData("_ = $\"read at {at}\";", "This DateTime")]
    [InlineData("_ = $\"{at:yyyy-MM-dd HH:mm}\";", "This DateTime written as \"yyyy-MM-dd HH:mm\"")]
    [InlineData("_ = $\"{span:g}\";", "This TimeSpan written as \"g\"")]
    [InlineData("_ = $\"{(focused ? 0 : -1)}\";", "This negative int")]
    [InlineData("_ = $\"{-count}\";", "This negative int")]
    [InlineData("_ = \"every \" + share + \" s\";", "This double")]
    [InlineData("string text = \"every \"; text += share;", "This double")]
    [InlineData("builder.Append(share);", "This double")]
    [InlineData("builder.Insert(0, at);", "This DateTime")]
    [InlineData("writer.Write(share);", "This double")]
    [InlineData("_ = string.Concat(\"every \", share);", "This double")]
    [InlineData("_ = string.Join(\", \", shares);", "This double")]
    [InlineData("System.Diagnostics.Debug.Assert(focused, $\"at {share}\");", "This double")]
    public async Task A_number_or_a_date_written_in_the_current_culture_is_flagged(string statement, string expected)
    {
        IReadOnlyList<Finding> findings = await DiagnoseAsync(("Probe.cs", Probe(statement)));

        findings.Should().ContainSingle(statement).Which.Message.Should().Be(expected + " becomes text in the server's culture here; write it with CultureInfo.InvariantCulture");
    }

    [Theory]
    [InlineData("_ = string.Create(CultureInfo.InvariantCulture, $\"{lag:N0} events behind, {share} at {at}\");")]
    [InlineData("_ = FormattableString.Invariant($\"{share} at {at}\");")]
    [InlineData("builder.Append(CultureInfo.InvariantCulture, $\"{share}\");")]
    [InlineData("_ = share.ToString(\"0.#\", CultureInfo.InvariantCulture) + at.ToString(CultureInfo.InvariantCulture);")]
    [InlineData("_ = $\"{count} of {lag} {count:D} {count:X} {count:000}\";")]
    [InlineData("_ = \"page \" + count + \" of \" + lag;")]
    [InlineData("_ = $\"{at:O} {at:o} {at:R} {at:s} {at:u}\";")]
    [InlineData("_ = $\"{span} {span:c}\";")]
    [InlineData("_ = $\"{focused} {'c'} {DayOfWeek.Monday} {Guid.Empty}\";")]
    [InlineData("_ = $\"{(focused ? \"0\" : \"-1\")}\";")]
    [InlineData("builder.Append(count).Append(\"rows\", 0, 4).Insert(0, lag);")]
    // CA1305's own ground in a .cs file: DS1305 leaves it to CA1305 and does not report it twice.
    [InlineData("_ = share.ToString();")]
    // The documented gap: a difference is not taken for negative (every one in src/ is guarded by a comparison of the same two values).
    [InlineData("_ = $\"and {count - 3} more\";")]
    public async Task What_is_invariant_or_is_CA1305s_passes(string statement)
    {
        IReadOnlyList<Finding> findings = await DiagnoseAsync(("Probe.cs", Probe(statement)));

        findings.Should().BeEmpty(statement);
    }

    [Fact]
    public async Task In_a_razor_file_it_reads_what_the_file_wrote_and_nothing_Razor_wrapped_it_in()
    {
        IReadOnlyList<Finding> findings = await DiagnoseAsync(("Probe_razor.g.cs", RazorProbe));

        findings.Select(static x => (x.Path, x.Line, x.Message[..x.Message.IndexOf(" becomes", StringComparison.Ordinal)])).Should().Equal(
            ("Probe.razor", 1, "This negative int"),
            ("Probe.razor", 2, "This double"),
            ("Probe.razor", 3, "This double"),
            ("Probe.razor", 4, "This int written as \"N0\""),
            ("Probe.razor", 5, "This double"),
            ("Probe.razor", 6, "This double written as \"F1\""));
    }

    [Fact]
    public async Task Generated_code_no_razor_file_wrote_is_not_read()
    {
        string generated = "// <auto-generated/>\n" + Probe("_ = $\"{share} at {at}\";");

        IReadOnlyList<Finding> findings = await DiagnoseAsync(("Logging.g.cs", generated));

        findings.Should().BeEmpty("a source generator's text is its own: only the code a .razor file wrote is the studio's");
    }

    [Fact]
    public void Every_project_under_src_compiles_with_it()
    {
        XElement reference = XDocument.Load(RepositoryRoot.Combine("src", "Directory.Build.props")).Descendants()
            .Should().ContainSingle(static x => x.Name.LocalName == "ProjectReference").Which;

        ((string?) reference.Attribute("Include")).Should().EndWith("../analyzers/DataStudio.Analyzers/DataStudio.Analyzers.csproj");
        ((string?) reference.Attribute("OutputItemType")).Should().Be("Analyzer");
        ((string?) reference.Attribute("ReferenceOutputAssembly")).Should().Be("false", "no package of ours may depend on it");
        ((string?) reference.Attribute("PrivateAssets")).Should().Be("all");

        string source = RepositoryRoot.Combine("src");
        string[] projects = [.. Directory.EnumerateFiles(source, "*.csproj", SearchOption.AllDirectories)
            .Where(static x => !SourceScanner.Relative(x).Split('/').Any(static segment => segment is "bin" or "obj"))];

        projects.Should().HaveCount(7, "the seven packages");

        foreach (string project in projects)
        {
            File.ReadAllText(project).Should().NotContain("ImportDirectoryBuildProps", SourceScanner.Relative(project) + " must import src/Directory.Build.props");

            // MSBuild imports the nearest Directory.Build.props only, so one closer to the project would hide src/'s.
            for (string? folder = Path.GetDirectoryName(project); !string.Equals(folder, source, StringComparison.OrdinalIgnoreCase); folder = Path.GetDirectoryName(folder))
            {
                File.Exists(Path.Combine(folder!, "Directory.Build.props")).Should().BeFalse(SourceScanner.Relative(folder!) + " would hide src/Directory.Build.props");
            }
        }
    }

    /// <summary>
    /// The C# the Razor compiler wrote for six lines of a <c>.razor</c> file, one finding a line, and five that pass: an attribute that is
    /// a string, a positive count, <c>@bind</c> on a number input (which Razor gives the invariant culture), <c>ToString</c> with the
    /// invariant culture, and an interpolation in Razor's own hidden code.
    /// </summary>
    /// <remarks>
    /// The lines: <c>&lt;div tabindex="@(Focused ? 0 : -1)"&gt;</c>, <c>style="width: @Share%"</c>, <c>@Share</c>,
    /// <c>@Count.ToString("N0")</c>, <c>&lt;input @bind="Share" /&gt;</c> and <c>@($"{Share:F1}")</c>; then
    /// <c>tabindex="@(Focused ? "0" : "-1")"</c>, <c>@Count</c>, <c>&lt;input type="number" @bind="Share" /&gt;</c> and
    /// <c>@Share.ToString(CultureInfo.InvariantCulture)</c>.
    /// </remarks>
    private const string RazorProbe = """
        // <auto-generated/>
        #pragma warning disable 1591
        namespace Probe
        {
            using System.Globalization;
            using Microsoft.AspNetCore.Components;

            public partial class Probe : ComponentBase
            {
                protected override void BuildRenderTree(global::Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder __builder)
                {
                    __string = $"{Share}";
                    __builder.OpenElement(0, "div");
                    __builder.AddAttribute(1, "tabindex",
        #nullable restore
        #line (1,18)-(1,34) "Probe.razor"
        Focused ? 0 : -1

        #line default
        #line hidden
        #nullable disable
                    );
                    __builder.AddAttribute(2, "style", "width:" + " " + (
        #nullable restore
        #line (2,15)-(2,20) "Probe.razor"
        Share

        #line default
        #line hidden
        #nullable disable
                    ) + "%");
        #nullable restore
        #line (3,2)-(3,7) 24 "Probe.razor"
        __builder.AddContent(3, Share

        #line default
        #line hidden
        #nullable disable
                    );
        #nullable restore
        #line (4,2)-(4,22) 24 "Probe.razor"
        __builder.AddContent(4, Count.ToString("N0")

        #line default
        #line hidden
        #nullable disable
                    );
                    __builder.AddAttribute(5, "value", global::Microsoft.AspNetCore.Components.BindConverter.FormatValue(
        #nullable restore
        #line (5,15)-(5,20) "Probe.razor"
        Share

        #line default
        #line hidden
        #nullable disable
                    ));
        #nullable restore
        #line (6,3)-(6,16) 24 "Probe.razor"
        __builder.AddContent(6, $"{Share:F1}"

        #line default
        #line hidden
        #nullable disable
                    );
                    __builder.AddAttribute(7, "tabindex",
        #nullable restore
        #line (7,18)-(7,38) "Probe.razor"
        Focused ? "0" : "-1"

        #line default
        #line hidden
        #nullable disable
                    );
        #nullable restore
        #line (8,2)-(8,7) 24 "Probe.razor"
        __builder.AddContent(8, Count

        #line default
        #line hidden
        #nullable disable
                    );
                    __builder.AddAttribute(9, "value", global::Microsoft.AspNetCore.Components.BindConverter.FormatValue(
        #nullable restore
        #line (9,29)-(9,34) "Probe.razor"
        Share

        #line default
        #line hidden
        #nullable disable
                    , culture: global::System.Globalization.CultureInfo.InvariantCulture));
        #nullable restore
        #line (10,2)-(10,43) 25 "Probe.razor"
        __builder.AddContent(10, Share.ToString(CultureInfo.InvariantCulture)

        #line default
        #line hidden
        #nullable disable
                    );
                    __builder.CloseElement();
                }

        #nullable restore
        #line (11,8)-(16,1) "Probe.razor"
            private string __string = "";
            private bool Focused = true;
            private double Share = 12.5;
            private int Count = 3;

        #line default
        #line hidden
        #nullable disable
            }
        }
        """;

    private static string Probe(string statement) => $$"""
        using System;
        using System.Collections.Generic;
        using System.Globalization;
        using System.IO;
        using System.Text;

        public static class Probe
        {
            public static void Run({{Parameters}})
            {
                {{statement}}
            }
        }
        """;

    private static async Task<IReadOnlyList<Finding>> DiagnoseAsync(params (string Path, string Source)[] files)
    {
        CSharpParseOptions parse = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "Probe",
            files.Select(x => CSharpSyntaxTree.ParseText(x.Source, parse, x.Path, cancellationToken: TestContext.Current.CancellationToken)),
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        compilation.GetDiagnostics(TestContext.Current.CancellationToken).Where(static x => x.Severity == DiagnosticSeverity.Error)
            .Should().BeEmpty("the probe compiles, or a case that passes proves nothing");

        ImmutableArray<Diagnostic> diagnostics = await compilation
            .WithAnalyzers([new CurrentCultureTextAnalyzer()])
            .GetAnalyzerDiagnosticsAsync(TestContext.Current.CancellationToken);

        diagnostics.Where(static x => x.Id != CurrentCultureTextAnalyzer.DiagnosticId).Should().BeEmpty("the analyzer reports DS1305 and never fails (AD0001)");

        return [.. diagnostics
            .Select(static x => (Span: x.Location.GetMappedLineSpan(), Message: x.GetMessage(CultureInfo.InvariantCulture)))
            .Select(static x => new Finding(x.Span.Path, x.Span.StartLinePosition.Line + 1, x.Message))
            .OrderBy(static x => x.Line)];
    }

    private sealed record Finding(string Path, int Line, string Message);
}
