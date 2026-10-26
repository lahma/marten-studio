using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// <c>Publish</c> pushes PostgresStudio and then MartenStudio, one <c>dotnet nuget push</c> at a time, and the first
/// failure ends the run - so a PostgresStudio push that fails never leaves behind a MartenStudio that depends on exactly
/// the version that is not there.
/// </summary>
/// <remarks>
/// <para>
/// Read from <c>build/Build.Publish.cs</c>, because nothing references the build project and its push only ever runs
/// on a tag with a minted key. The shape it refuses is the one it replaced: Fallout's combinatorial
/// <c>DotNetNuGetPush(… .CombineWith(packages, …), degreeOfParallelism: 1, completeOnFailure: true)</c>, which runs its
/// items through PLINQ (no order promised, even at a degree of parallelism of 1) and, with <c>completeOnFailure</c>,
/// pushes the rest after a failure and throws only at the end. A single, non-combinatorial <c>DotNetNuGetPush</c>
/// throws on the non-zero exit of its own push, which is what makes a <c>foreach</c> stop at the first failure.
/// </para>
/// </remarks>
public class ReleasePushOrderTests
{
    private static string PublishSource => File.ReadAllText(RepositoryRoot.Combine("build", "Build.Publish.cs"));

    [Fact]
    public void Publish_pushes_one_package_at_a_time_and_stops_at_the_first_failure()
    {
        PushProblems(PublishSource).Should().BeEmpty();
    }

    [Fact]
    public void The_release_packages_are_PostgresStudio_then_MartenStudio_and_nothing_else()
    {
        Match list = Regex.Match(PublishSource, @"AbsolutePath\[\] ReleasePackages =>\s*\[(?<items>[^\]]*)\]", RegexOptions.CultureInvariant);

        list.Success.Should().BeTrue("Build.Publish.cs declares the release packages as one list");

        string[] items = [.. list.Groups["items"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)];

        items.Should().HaveCount(2);
        items[0].Should().Contain("PostgresStudioPackageId", "MartenStudio depends on exactly PostgresStudio, so PostgresStudio is pushed first");
        items[1].Should().Contain("MartenStudioPackageId");
    }

    [Fact]
    public void No_other_build_file_pushes_a_package()
    {
        string[] pushers = [.. Directory.EnumerateFiles(RepositoryRoot.Combine("build"), "*.cs", SearchOption.TopDirectoryOnly)
            .Where(static x => File.ReadAllText(x).Contains("DotNetNuGetPush(", StringComparison.Ordinal))
            .Select(static x => Path.GetFileName(x))];

        pushers.Should().Equal("Build.Publish.cs");
    }

    /// <summary>Anti-vacuity: the check refuses the combinatorial shape it replaced, and the near misses of the loop.</summary>
    [Theory]
    [InlineData(Loop, "")]
    [InlineData("""
        Target Publish => _ => _
            .Executes(() =>
            {
                AssertTheReleasePackagesAndNothingElse();
                var packages = ReleasePackages;
                var apiKey = GetTrustedPublishingApiKey();
                DotNetNuGetPush(_ => _
                        .SetSource(NuGetSource)
                        .SetApiKey(apiKey)
                        .EnableSkipDuplicate()
                        .CombineWith(packages, (_, v) => _.SetTargetPath(v)),
                    degreeOfParallelism: 1,
                    completeOnFailure: true);
            });
        """, "combinatorial")]
    [InlineData("""
        Target Publish => _ => _
            .Executes(() =>
            {
                AssertTheReleasePackagesAndNothingElse();
                var packages = ReleasePackages;
                var apiKey = GetTrustedPublishingApiKey();
                foreach (var package in packages)
                {
                    DotNetNuGetPush(_ => _.SetSource(NuGetSource).SetApiKey(apiKey).SetTargetPath(package));
                }
            });
        """, "skip-duplicate")]
    [InlineData("""
        Target Publish => _ => _
            .Executes(() =>
            {
                var packages = PackagesDirectory.GlobFiles("*.nupkg");
                var apiKey = GetTrustedPublishingApiKey();
                foreach (var package in packages)
                {
                    DotNetNuGetPush(_ => _.SetSource(NuGetSource).SetApiKey(apiKey).EnableSkipDuplicate().SetTargetPath(package));
                }
            });
        """, "ReleasePackages")]
    public void The_push_check_refuses_every_shape_but_the_ordered_loop(string source, string problem)
    {
        IReadOnlyList<string> problems = PushProblems(source);

        if (problem.Length == 0)
        {
            problems.Should().BeEmpty();
        }
        else
        {
            problems.Should().Contain(x => x.Contains(problem, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Anti-vacuity for the loop's body, one case per forbidden token: each is the ordered loop with only its body
    /// changed, so the token is the only thing wrong with it. <c>PUSH</c> stands for the canonical push call.
    /// </summary>
    [Theory]
    [InlineData("PUSH", "")]
    [InlineData("try\n{\n    PUSH\n}\ncatch (ProcessException)\n{\n}", "catch")]
    [InlineData("DotNetNuGetPush(_ => _.SetSource(NuGetSource).SetApiKey(apiKey).EnableSkipDuplicate().SetProcessExitHandler(null).SetTargetPath(package));", "ExitHandl")]
    [InlineData("DotNetNuGetPush(_ => _.SetSource(NuGetSource).SetApiKey(apiKey).EnableSkipDuplicate().SetProcessExitHandling(false).SetTargetPath(package));", "ExitHandl")]
    [InlineData("Parallel.Invoke(() => DotNetNuGetPush(_ => _.SetSource(NuGetSource).SetApiKey(apiKey).EnableSkipDuplicate().SetTargetPath(package)));", "Parallel")]
    [InlineData("_ = Task.Run(() => DotNetNuGetPush(_ => _.SetSource(NuGetSource).SetApiKey(apiKey).EnableSkipDuplicate().SetTargetPath(package)));", "Task.")]
    public void The_loop_check_refuses_a_body_that_could_carry_on_after_a_failed_push(string loopBody, string forbidden)
    {
        const string Push = "DotNetNuGetPush(_ => _.SetSource(NuGetSource).SetApiKey(apiKey).EnableSkipDuplicate().SetTargetPath(package));";

        string source =
            "Target Publish => _ => _\n" +
            "    .Executes(() =>\n" +
            "    {\n" +
            "        AssertTheReleasePackagesAndNothingElse();\n" +
            "        var packages = ReleasePackages;\n" +
            "        var apiKey = GetTrustedPublishingApiKey();\n" +
            "        foreach (var package in packages)\n" +
            "        {\n" +
            "            " + loopBody.Replace("PUSH", Push, StringComparison.Ordinal) + "\n" +
            "        }\n" +
            "    });\n";

        IReadOnlyList<string> problems = PushProblems(source);

        if (forbidden.Length == 0)
        {
            problems.Should().BeEmpty();
        }
        else
        {
            problems.Should().ContainSingle().Which.Should().Contain($"'{forbidden}'");
        }
    }

    private const string Loop = """
        Target Publish => _ => _
            .Executes(() =>
            {
                AssertTheReleasePackagesAndNothingElse();
                var packages = ReleasePackages;
                var apiKey = GetTrustedPublishingApiKey();
                // Not .CombineWith(...) with completeOnFailure: a comment naming the old shape is not the old shape.
                foreach (var package in packages)
                {
                    DotNetNuGetPush(_ => _
                        .SetSource(NuGetSource)
                        .SetApiKey(apiKey)
                        .EnableSkipDuplicate()
                        .SetTargetPath(package));
                }
            });
        """;

    /// <summary>What is wrong with the <c>Publish</c> target in <paramref name="source" />; empty when nothing is.</summary>
    private static List<string> PushProblems(string source)
    {
        List<string> problems = [];
        int start = source.IndexOf("Target Publish =>", StringComparison.Ordinal);

        if (start < 0)
        {
            return ["there is no Publish target"];
        }

        int end = source.IndexOf("});", start, StringComparison.Ordinal);

        // Comments out: the target's own comment explains the combinatorial shape it does not use.
        string body = Regex.Replace(end < 0 ? source[start..] : source[start..end], @"//[^\n]*", string.Empty, RegexOptions.CultureInvariant);

        int pushes = Regex.Count(body, @"DotNetNuGetPush\(", RegexOptions.CultureInvariant);
        int push = body.IndexOf("DotNetNuGetPush(", StringComparison.Ordinal);
        int loop = body.IndexOf("foreach (var package in packages)", StringComparison.Ordinal);

        if (body.Contains("CombineWith", StringComparison.Ordinal)
            || body.Contains("completeOnFailure", StringComparison.Ordinal)
            || body.Contains("degreeOfParallelism", StringComparison.Ordinal))
        {
            problems.Add("the push is combinatorial: PLINQ promises no order, and completeOnFailure pushes on after a failure");
        }

        if (pushes != 1)
        {
            problems.Add($"there are {pushes} DotNetNuGetPush calls, not one inside the loop");
        }

        if (loop < 0 || push < loop)
        {
            problems.Add("the push is not inside `foreach (var package in packages)`, one package per call");
        }

        if (!Regex.IsMatch(body, @"var packages = ReleasePackages;", RegexOptions.CultureInvariant))
        {
            problems.Add("the packages are not ReleasePackages, the list that puts PostgresStudio first");
        }

        int guard = body.IndexOf("AssertTheReleasePackagesAndNothingElse();", StringComparison.Ordinal);
        int key = body.IndexOf("GetTrustedPublishingApiKey()", StringComparison.Ordinal);

        if (guard < 0 || key < 0 || guard > key || (loop >= 0 && key > loop))
        {
            problems.Add("the two release packages are not checked before the key is minted, or the key after the loop");
        }

        if (!body.Contains("EnableSkipDuplicate()", StringComparison.Ordinal))
        {
            problems.Add("the push has no --skip-duplicate, so re-running after a partial failure fails on the package that landed");
        }

        // What would make the loop carry on after a failed push, or run its pushes out of order, while still looking like
        // the loop: a catch around the push, a process exit handler that does not assert the exit code (SetProcessExitHandler,
        // ProcessExitHandling), or the push handed to Parallel or to a Task.
        if (loop >= 0)
        {
            string loopBody = LoopBody(body, loop);

            foreach (string forbidden in ForbiddenInTheLoop.Where(x => loopBody.Contains(x, StringComparison.Ordinal)))
            {
                problems.Add($"the push loop's body contains '{forbidden}', which can carry on after a failed push or push out of order");
            }
        }

        return problems;
    }

    private static readonly string[] ForbiddenInTheLoop = ["catch", "ExitHandl", "Parallel", "Task."];

    /// <summary>The body of the <c>foreach</c> starting at <paramref name="loop" />: its braces, matched, or its one statement.</summary>
    private static string LoopBody(string body, int loop)
    {
        int open = body.IndexOf('{', loop);
        int statementEnd = body.IndexOf(';', loop);

        if (open < 0 || (statementEnd >= 0 && statementEnd < open))
        {
            return statementEnd < 0 ? body[loop..] : body[loop..(statementEnd + 1)];
        }

        int depth = 0;

        for (int i = open; i < body.Length; i++)
        {
            depth += body[i] switch
            {
                '{' => 1,
                '}' => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return body[open..(i + 1)];
            }
        }

        return body[open..];
    }
}
