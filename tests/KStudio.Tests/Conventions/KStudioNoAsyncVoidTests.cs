using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace KStudio.Tests.Conventions;

/// <summary>
/// AGENTS.md hard rule 6 for KStudio: no <c>async void</c> anywhere in KStudio's sources (<c>src/KStudio.Core</c>, and <c>src/KStudio</c> once
/// M2 adds it). The source scan strips comments and string literals first (<see cref="SourceScanner" />, shared with the studios' scan); the
/// compiled-assembly scan is hard rule 6's other half (1.2-HEADER-FU, REVIEW-SCAF follow-up 3): an <c>async</c> lambda converted to an
/// <c>Action</c>, an <c>async</c> local function returning <c>void</c> or one added to an event is an <c>async void</c> with different
/// spelling, and the compiler turns every one of them into a <c>void</c> method carrying <c>[AsyncStateMachine]</c>.
/// </summary>
public class KStudioNoAsyncVoidTests
{
    private const BindingFlags AllDeclared =
        BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static readonly Regex AsyncVoid = new(@"\basync\s+void\b", RegexOptions.Compiled, TimeSpan.FromSeconds(5));

    [Fact]
    public void No_kstudio_source_file_declares_an_async_void()
    {
        string[] files = KStudioSourceFiles();

        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        string[] offenders =
        [
            .. files
                .Where(x => AsyncVoid.IsMatch(SourceScanner.StripCommentsAndStrings(File.ReadAllText(x))))
                .Select(SourceScanner.Relative)
                .Order(StringComparer.Ordinal),
        ];

        offenders.Should().BeEmpty("hard rule 6: an async void handler's exception kills the Blazor circuit silently. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void No_compiled_method_in_a_kstudio_assembly_is_an_async_void()
    {
        Assembly[] assemblies = [typeof(KafkaEngineOptions).Assembly, typeof(KafkaStudioOptions).Assembly];

        Type[] types = [.. assemblies.SelectMany(static x => x.GetTypes())];
        types.Should().HaveCountGreaterThan(50, "scanning nothing would pass for the wrong reason");

        string[] offenders = [.. VoidAsyncMethods(types)];

        offenders.Should().BeEmpty(
            "hard rule 6: a void-returning async method (an async lambda on an Action or an event handler, or an async void local function) is an async void "
            + "with different spelling, and its exception kills the circuit. Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>The anti-vacuity check for the compiled scan: the two async void shapes are found, a <c>Func&lt;Task&gt;</c> lambda and an async <c>Task</c> method are not.</summary>
    [Fact]
    public void The_compiled_scan_finds_an_async_lambda_on_an_Action_and_an_async_void_local_function_and_ignores_a_Func_of_Task()
    {
        Type[] fixture =
        [
            .. typeof(AsyncVoidFixture).Assembly.GetTypes().Where(static x => x == typeof(AsyncVoidFixture) || x.DeclaringType == typeof(AsyncVoidFixture)),
        ];

        string[] flagged = [.. VoidAsyncMethods(fixture)];

        flagged.Should().HaveCount(2, "exactly the async lambda on an Action and the async void local function: " + string.Join(", ", flagged));
        flagged.Should().NotContain(static x => x.Contains("Fine", StringComparison.Ordinal));
    }

    /// <summary>The anti-vacuity check for the source scan: it has to be able to fail.</summary>
    [Theory]
    [InlineData("class C { async void Handler() { } }", true)]
    [InlineData("class C { async  \n    void Wrapped() { } }", true)]
    [InlineData("class C { async Task Handler() { } }", false)]
    [InlineData("// never write async void here\nclass C { }", false)]
    [InlineData("class C { string s = \"async void\"; }", false)]
    public void The_scanner_finds_a_real_async_void_and_ignores_one_in_a_comment_or_a_string(string source, bool expected)
    {
        AsyncVoid.IsMatch(SourceScanner.StripCommentsAndStrings(source)).Should().Be(expected);
    }

    private static readonly string[] SourceProjects = ["KStudio.Core", "KStudio"];

    private static string[] KStudioSourceFiles() =>
        [
            .. SourceProjects
                .Select(static x => RepositoryRoot.Combine("src", x))
                .Where(Directory.Exists)
                .SelectMany(static x => Directory.EnumerateFiles(x, "*.*", SearchOption.AllDirectories))
                .Where(static x => (x.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
                    && !x.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    && !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
        ];

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
}
