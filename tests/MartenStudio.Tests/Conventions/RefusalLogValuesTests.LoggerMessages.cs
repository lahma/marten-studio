using System.Text.RegularExpressions;

using MartenStudio.Services;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.Logging;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// H1: the scans above hold the audit dispatchers and every direct <c>LogWarning</c> to <see cref="StudioLogValue" />; this
/// part holds every <c>[LoggerMessage]</c> template to it as well, at whatever level - the throttled Warnings among them
/// (9210-9222, 9237, 9246, 9251, 9410, 9430, 9440, 9451, 9500, 9501), 9320, 9420 and 9431 - and the Marten wrappers' targets.
/// </summary>
public partial class RefusalLogValuesTests
{
    /// <summary>
    /// The two spellings a template call may pass bare that look like route values and are not: a health check's context is
    /// the resolved source's, so its <c>StoreKey</c> is the registered key and its <c>DatabaseId</c> the database's identity
    /// (<see cref="The_canonical_spellings_a_template_may_be_passed_are_the_resolved_sources" /> pins both).
    /// </summary>
    private static readonly string[] CanonicalRouteValues = ["context.StoreKey", "context.DatabaseId"];

    /// <summary>
    /// Every call of a <c>[LoggerMessage]</c> template passes nothing from the address bare: no document id, collection alias,
    /// or scope's store, database or tenant, except through <see cref="StudioLogValue.Cap" /> or as the resolved source's own
    /// name. A per-product dispatcher (first argument a <c>StudioProduct</c>) caps inside, and is held to that above.
    /// </summary>
    [Fact]
    public void Every_LoggerMessage_template_is_fed_route_values_capped()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty();

        HashSet<string> templates = new(StringComparer.Ordinal);

        foreach (string file in files)
        {
            foreach (Match declaration in LoggerMessageDeclaration().Matches(SourceScanner.StripCommentsAndStrings(File.ReadAllText(file))))
            {
                templates.Add(declaration.Groups["name"].Value);
            }
        }

        templates.Should().HaveCountGreaterThan(80, "src/ declares its events as [LoggerMessage] templates, and a scan that finds none has stopped working")
            .And.Contain(["DaemonUnreachable", "StoreUnavailable", "SubscriptionRewindRefused", "AuroraReadFailed", "DaemonControlRequestedRaw"]);

        Regex call = new(
            @"\.\s*(?<name>" + string.Join('|', templates.OrderByDescending(static x => x.Length)) + @")\s*\(",
            RegexOptions.CultureInvariant);

        List<string> uncapped = [];
        int calls = 0;

        foreach (string file in files)
        {
            string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(file));

            foreach (Match site in call.Matches(code))
            {
                List<string> arguments = Arguments(code, site.Index + site.Length);

                if (arguments.Count > 0 && arguments[0].Contains("Product", StringComparison.Ordinal))
                {
                    continue;
                }

                calls++;
                uncapped.AddRange(arguments
                    .SelectMany(RouteValuesIn)
                    .Where(static x => !CanonicalRouteValues.Contains(x, StringComparer.Ordinal))
                    .Select(x => SourceScanner.Relative(file) + ": " + site.Groups["name"].Value + "(" + x + ")"));
            }
        }

        calls.Should().BeGreaterThan(60, "src/ calls its templates from many sites, and a scan that finds none has stopped working");
        uncapped.Should().BeEmpty("a value from the address reaches a log template only escaped and bounded");
    }

    /// <summary>The scan's anti-vacuity half: it catches a bare route value anywhere in an argument, and passes a capped one.</summary>
    [Theory]
    [InlineData("logger.DaemonUnreachable(level, exception, scope.StoreKey, scope.DatabaseId);", "scope.StoreKey|scope.DatabaseId")]
    [InlineData("logger.SubscriptionRewindRefused(exception, name, scope.TenantId ?? AllTenants, storeKey, databaseId);", "scope.TenantId")]
    [InlineData("logger.EventReadFailed(level, exception, \"x\", run.Request.Scope.StoreKey + \"!\", d, null);", "run.Request.Scope.StoreKey")]
    [InlineData("logger.DocumentRead(level, id, alias);", "id|alias")]
    [InlineData("logger.DaemonUnreachable(level, exception, StudioLogValue.Cap(scope.StoreKey), resolved.Database.Id.Identity);", "")]
    [InlineData("logger.ShardTrackerUnobservable(level, exception, resolved.Registration.Key, StudioLogValue.Cap(scope.DatabaseId ?? x.Id));", "")]
    [InlineData("logger.AuroraReadFailed(level, exception, section, context.StoreKey, context.DatabaseId, null);", "context.StoreKey|context.DatabaseId")]
    public void The_template_scan_catches_a_bare_route_value_and_passes_a_capped_one(string source, string caught)
    {
        string code = SourceScanner.StripCommentsAndStrings(source);
        int open = code.IndexOf('(', StringComparison.Ordinal);

        Arguments(code, open + 1).SelectMany(RouteValuesIn).Should()
            .Equal(caught.Length == 0 ? [] : caught.Split('|'));
    }

    /// <summary>
    /// The two bare spellings the scan lets through are what they claim: a health check context's store and database are
    /// the resolved source's registered key and its database's identity, never the visitor's spelling.
    /// </summary>
    [Fact]
    public void The_canonical_spellings_a_template_may_be_passed_are_the_resolved_sources()
    {
        string context = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "Services", "Health", "HealthCheckContext.cs"));

        context.Should().Contain("public string StoreKey => Source.Source.Key;")
            .And.Contain("public string DatabaseId => Source.Target.Identity;");
    }

    /// <summary>
    /// 9209 is written before the daemon looks the shard up, so a shard name a circuit typed reached the log raw: the Marten
    /// wrappers cap the operation, its target and 9207's projection as they cap the user and the scope (H1).
    /// </summary>
    [Fact]
    public void The_Marten_wrappers_write_a_target_the_circuit_named_escaped_and_bounded()
    {
        CapturingLoggerProvider captured = new();
        ILogger logger = captured.CreateFactory().CreateLogger("marten");

        logger.DaemonControlRequested("ops", "StopAgent", HostileStatement, "default", "db");
        logger.ProjectionRebuildStarted("ops", HostileStatement, "default", "db");

        captured.Entries.Select(static x => x.EventId.Id).Should().Equal(9209, 9207);

        foreach (CapturedLogEntry entry in captured.Entries)
        {
            HasRawControl(entry.Message).Should().BeFalse("event {0} writes the target escaped", entry.EventId.Id);
            entry.Message.Should().Contain(StudioLogValue.Cap(HostileStatement)).And.NotContain(HostileStatement);
            entry.Message.Length.Should().BeLessThan(2 * StudioLogValue.MaxLength + 200, "event {0} is bounded whatever was named", entry.EventId.Id);
        }

        string martenLog = SourceScanner.StripCommentsAndStrings(File.ReadAllText(RepositoryRoot.Combine("src", "MartenStudio", "Services", "MartenLog.cs")));
        Regex.IsMatch(martenLog, @"public static void DaemonControlRequested\([^{]*\{[^}]*StudioLogValue\.Cap\(operation\)[^}]*StudioLogValue\.Cap\(target\)")
            .Should().BeTrue("9209's wrapper caps the operation and the target");
        Regex.IsMatch(martenLog, @"public static void ProjectionRebuildStarted\([^{]*\{[^}]*StudioLogValue\.Cap\(projection\)")
            .Should().BeTrue("9207's wrapper caps the projection");
    }

    /// <summary>The top-level arguments of the call whose list starts at <paramref name="start" />.</summary>
    private static List<string> Arguments(string code, int start)
    {
        List<string> arguments = [];
        int depth = 0;
        int from = start;

        for (int index = start; index < code.Length; index++)
        {
            char c = code[index];

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    if (code[from..index].Trim() is { Length: > 0 } last)
                    {
                        arguments.Add(last);
                    }

                    break;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(code[from..index].Trim());
                from = index + 1;
            }
        }

        return arguments;
    }

    /// <summary>
    /// The route values <paramref name="argument" /> names bare, anywhere in it: what is left once every
    /// <c>StudioLogValue.Cap…(…)</c> call - and everything inside it - is taken out.
    /// </summary>
    private static IEnumerable<string> RouteValuesIn(string argument)
    {
        string bare = argument;

        for (Match capped = CappedCall().Match(bare); capped.Success; capped = CappedCall().Match(bare))
        {
            int open = capped.Index + capped.Length - 1;
            int depth = 0;
            int end = open;

            for (; end < bare.Length; end++)
            {
                if (bare[end] == '(')
                {
                    depth++;
                }
                else if (bare[end] == ')' && --depth == 0)
                {
                    break;
                }
            }

            bare = bare[..capped.Index] + bare[Math.Min(end + 1, bare.Length)..];
        }

        return RouteValueToken().Matches(bare).Select(static x => x.Value);
    }

    /// <summary>A <c>[LoggerMessage]</c> template's declaration, as the scanner leaves it: the attribute, then the method's name.</summary>
    [GeneratedRegex(@"\[LoggerMessage\([^\]]*\)\]\s*(?:public|internal|private)\s+static\s+partial\s+void\s+(?<name>\w+)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex LoggerMessageDeclaration();

    /// <summary>
    /// The head of a capping call, up to its opening parenthesis: <see cref="StudioLogValue" />'s, or 9211's own sanitizer
    /// for a document's type and id (<c>WriteAuditText.Sanitize</c>, which <see cref="Only_the_Marten_wrappers_call_the_raw_templates" /> pins).
    /// </summary>
    [GeneratedRegex(@"(?:StudioLogValue\s*\.\s*Cap\w*|WriteAuditText\s*\.\s*Sanitize)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex CappedCall();

    /// <summary>A bare document id or collection alias, or a scope's, request's or table's store, database, tenant or alias.</summary>
    [GeneratedRegex(@"(?<![\w.])(?:id|alias)(?![\w(])|(?<![\w.])(?:[A-Za-z_]\w*\.)+(?:Alias|StoreKey|DatabaseId|TenantId)\b", RegexOptions.CultureInvariant)]
    private static partial Regex RouteValueToken();
}
