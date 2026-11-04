using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Enforces AGENTS.md hard rule 6: no <c>async void</c> anywhere in <c>src/</c>.
/// </summary>
/// <remarks>
/// <para>
/// In Blazor Server an <c>async void</c> handler's exception has nowhere to go. It escapes the
/// synchronization context, is not observed by the renderer, and takes down the circuit - the user sees
/// the page stop responding with no error anywhere, and the server log gets an unhandled exception with
/// no component in the stack. Every component event in this codebase is a <c>Func&lt;Task&gt;</c> or a
/// <c>private async Task</c> handler with a <c>try</c>/<c>catch</c> that puts the failure on screen.
/// </para>
/// <para>
/// The scanner strips comments and string literals first (<see cref="SourceScanner"/>, shared with the
/// hard rule 14 scan), so prose that mentions the phrase - including this very rule, quoted in a doc
/// comment - does not fail the build. That stripping is exactly what makes the test capable of being
/// silently vacuous, so
/// <see cref="The_scanner_finds_a_real_async_void_and_ignores_one_in_a_comment_or_a_string"/> checks the
/// scanner against known-good and known-bad inputs before the real scan is trusted.
/// </para>
/// </remarks>
public class NoAsyncVoidTests
{
    private static readonly Regex AsyncVoid =
        new(@"\basync\s+void\b", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    [Fact]
    public void No_source_file_declares_an_async_void()
    {
        var files = SourceScanner.ShippedSourceFiles();

        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        var offenders = files
            .Where(x => AsyncVoid.IsMatch(SourceScanner.StripCommentsAndStrings(File.ReadAllText(x))))
            .Select(SourceScanner.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.Should().BeEmpty(
            "hard rule 6: an async void handler's exception kills the Blazor circuit silently. Offenders: " +
            string.Join(", ", offenders));
    }

    private const BindingFlags AllDeclared =
        BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>
    /// Hard rule 6's other half (1.2-HEADER-FU): the text scan above cannot see an <c>async</c> lambda converted to an <c>Action</c>, an
    /// <c>async</c> local function that returns <c>void</c> or one added to an <c>EventHandler</c> - each is an <c>async void</c>
    /// with different spelling. The compiler turns every one of them into a method that returns <c>void</c> and carries
    /// <c>[AsyncStateMachine]</c> (closure classes and local functions included), so the compiled assemblies are scanned for exactly that.
    /// <c>StudioState.LinkScopeChanged</c> is an <c>Action</c> event, which is only safe while this stays green.
    /// </summary>
    [Fact]
    public void No_compiled_method_in_a_shipped_assembly_is_an_async_void()
    {
        Assembly[] assemblies =
        [
            typeof(MartenStudioOptions).Assembly,
            typeof(PostgresStudio.PostgresStudioOptions).Assembly,
            typeof(PostgresStudio.PostgresDiagnosticsOptions).Assembly,
        ];

        assemblies.Select(static x => x.GetName().Name).Should().OnlyHaveUniqueItems("three different assemblies are scanned");

        Type[] types = [.. assemblies.SelectMany(static x => x.GetTypes())];
        types.Should().HaveCountGreaterThan(100, "scanning nothing would pass for the wrong reason");

        string[] offenders = [.. VoidAsyncMethods(types)];

        offenders.Should().BeEmpty(
            "hard rule 6: a void-returning async method (an async lambda converted to an Action or an event handler, or an async void local "
            + "function) is an async void with different spelling, and its exception kills the circuit. Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>The anti-vacuity check for the compiled scan: the two async void shapes are found, a <c>Func&lt;Task&gt;</c> lambda and an async <c>Task</c> method are not.</summary>
    [Fact]
    public void The_compiled_scan_finds_an_async_lambda_on_an_Action_and_an_async_void_local_function_and_ignores_a_Func_of_Task()
    {
        Type[] fixture = [.. typeof(AsyncVoidFixture).Assembly.GetTypes()
            .Where(static x => x == typeof(AsyncVoidFixture) || x.DeclaringType == typeof(AsyncVoidFixture))];

        string[] flagged = [.. VoidAsyncMethods(fixture)];

        flagged.Should().HaveCount(2, "exactly the async lambda on an Action and the async void local function: " + string.Join(", ", flagged));
        flagged.Should().Contain(static x => x.Contains("AsyncVoidFixture", StringComparison.Ordinal));
        flagged.Should().NotContain(static x => x.Contains("Fine", StringComparison.Ordinal));
    }

    private static IEnumerable<string> VoidAsyncMethods(IEnumerable<Type> types)
    {
        foreach (Type type in types)
        {
            foreach (MethodInfo method in type.GetMethods(AllDeclared))
            {
                if (method.ReturnType == typeof(void) && method.GetCustomAttribute<AsyncStateMachineAttribute>() is not null)
                {
                    yield return type.FullName + "::" + method.Name;
                }
            }
        }
    }

    /// <summary>What the compiled scan has to find (and not find). Never called.</summary>
    internal static class AsyncVoidFixture
    {
        public static Action Lambda()
        {
            Action action = async () => await Task.Yield();
            return action;
        }

        public static void LocalFunction()
        {
            async void Run() => await Task.Yield();
            Run();
        }

        public static Func<Task> Fine()
        {
            Func<Task> func = async () => await Task.Yield();
            return func;
        }

        public static async Task FineToo() => await Task.Yield();
    }

    /// <summary>The anti-vacuity check: the scanner has to be able to fail.</summary>
    [Theory]
    [InlineData("class C { async void Handler() { } }", true)]
    [InlineData("class C { public async void OnClick(object s, EventArgs e) { } }", true)]
    [InlineData("class C { async  \n    void Wrapped() { } }", true)]
    [InlineData("class C { async Task Handler() { } }", false)]
    [InlineData("// never write async void here\nclass C { }", false)]
    [InlineData("/* async void */\nclass C { }", false)]
    [InlineData("class C { string s = \"async void\"; }", false)]
    [InlineData("class C { string s = @\"async void\"; }", false)]
    [InlineData("@* Razor prose: never async void *@\n<p>ok</p>", false)]
    [InlineData("@* Marten's rule *@\n@code { async void Handler() { } }", true)]
    [InlineData("<p class='x'>it's fine</p>\n@code { async void Handler() { } }", true)]
    public void The_scanner_finds_a_real_async_void_and_ignores_one_in_a_comment_or_a_string(
        string source, bool expected)
    {
        AsyncVoid.IsMatch(SourceScanner.StripCommentsAndStrings(source)).Should().Be(expected);
    }
}
