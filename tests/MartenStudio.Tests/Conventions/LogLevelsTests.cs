using System.Globalization;
using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Every Warning, Error and Critical the studio can write, named, with the reason it is not Debug.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a roster.</b> A host that runs no async daemon here on purpose - or has Wolverine run its
/// projections - got "could not reach the async daemon" every <c>RefreshInterval</c> in every open tab,
/// and the only response available to an operator was to mute the category, which also mutes the next
/// warning that matters. The rule since DB-0 is that an expected, configuration-driven state is a value
/// the page renders and is Debug at most; a real anomaly on a polled path goes through
/// <c>StudioLogThrottle</c>; and a Warning is kept only where somebody can say why. This test is where
/// they say it. A new <c>LogWarning</c>, <c>LogError</c>, <c>LogCritical</c>, <c>Log(level, …)</c> or
/// Warning-or-higher <c>[LoggerMessage]</c> anywhere in <c>src/</c> fails the build until it is added
/// below with a verdict and one line of justification - the same idea as
/// <see cref="CapabilityGatingMatrixTests" />, for the same reason: the gap it closes is silent.
/// </para>
/// <para>
/// <b>What a site is.</b> A call is keyed by its file and its message template, read out of the original
/// text at the position the comment-stripped scan found it; a <c>[LoggerMessage]</c> is keyed by its file,
/// event id and method name. A <c>[LoggerMessage]</c> with no <c>Level</c> - the level is a parameter -
/// and a bare <c>Log(level, …)</c> are sites too, because they can write a Warning; they are exactly the
/// throttled ones, and <see cref="Every_throttled_site_takes_its_level_from_the_throttle" /> holds the two
/// lists to each other.
/// </para>
/// <para>
/// The scanner strips comments and string literals first (<see cref="SourceScanner" />), which is what lets
/// the prose in this codebase quote a log call without failing, and is also what could make it silently
/// vacuous - so <see cref="The_scanner_finds_a_real_site_and_ignores_one_in_prose" /> proves it can still
/// fail.
/// </para>
/// </remarks>
public class LogLevelsTests
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    private static readonly Regex DirectCall = new(
        @"\.Log(?<level>Warning|Error|Critical)\s*\(", RegexOptions.None, RegexTimeout);

    private static readonly Regex DynamicCall = new(@"\.Log\s*\(", RegexOptions.None, RegexTimeout);

    private static readonly Regex LoggerMessage = new(
        @"\[\s*LoggerMessage\s*\((?<args>[^\]]*?)\)\s*\]", RegexOptions.None, RegexTimeout);

    private static readonly Regex EventIdArgument = new(@"(?:EventId\s*=\s*)?(?<id>\d+)", RegexOptions.None, RegexTimeout);

    private static readonly Regex LevelArgument = new(@"LogLevel\s*\.\s*(?<level>\w+)", RegexOptions.None, RegexTimeout);

    private static readonly Regex MethodName = new(@"\G\s*(?:public|internal|private|static|partial|\s)*void\s+(?<name>\w+)", RegexOptions.None, RegexTimeout);

    /// <summary>Why a site is allowed to be above Debug.</summary>
    public enum Verdict
    {
        /// <summary>
        /// A security-relevant refusal. Always a Warning: only a client driving a control the page did not
        /// offer produces one, and an operator wants to see that.
        /// </summary>
        Refusal,

        /// <summary>
        /// A real anomaly on a path the studio polls or repeats. The level is a parameter, Warning once
        /// per store, database and exception type per ten minutes, Debug in between.
        /// </summary>
        Throttled,

        /// <summary>
        /// A real anomaly on a path a visitor started - a tab, a button, a page load - and nothing polls.
        /// One line per action, and the page says the same thing on screen.
        /// </summary>
        VisitorAction,

        /// <summary>A fault in the studio itself, or an operation that ended half-done. Error.</summary>
        Fault,

        /// <summary>A record that data was lost on the visitor's say-so. Always a Warning.</summary>
        DataLoss,
    }

    /// <summary>
    /// Every site above Debug, and why. Keyed by file and message template (or event id and method).
    /// </summary>
    /// <remarks>
    /// Curated deliberately rather than derived: "is this worth waking somebody for" is a judgement, and a
    /// test that inferred it from the code would agree with whatever the code happens to do.
    /// </remarks>
    private static readonly Dictionary<string, (Verdict Verdict, string Why)> Roster = new(StringComparer.Ordinal)
    {
        // ---- StudioLog: the audit events kept at Warning ----------------------------------------------
        ["src/MartenStudio/Services/StudioLog.cs : 9202 CapabilityDenied"] = (Verdict.Refusal,
            "A capability refusal reaches the service only when a client drives a control the page disabled or hid."),
        ["src/MartenStudio/Services/StudioLog.cs : 9203 ScopeAuthorizationDenied"] = (Verdict.Refusal,
            "A policy refusal on a write or a query run; listings are filtered (FilterAsync logs nothing) and a refused scope's page body is never rendered."),
        ["src/MartenStudio/Services/StudioLog.cs : 9211 DocumentWriteRoundTripDropped"] = (Verdict.DataLoss,
            "A save the visitor confirmed dropped properties the CLR type does not have; the only durable record of the loss."),

        // ---- StudioLog: the throttled anomalies (level is a parameter) --------------------------------
        ["src/MartenStudio/Services/StudioLog.cs : 9210 StoreUnavailable"] = (Verdict.Throttled,
            "A registered store will not build; the registry's ten-second cache expires under every polling page."),
        ["src/MartenStudio/Services/StudioLog.cs : 9212 DaemonUnreachable"] = (Verdict.Throttled,
            "A registered coordinator answered with neither a daemon nor NotSupported; polled by the Overview, projections and nav."),
        ["src/MartenStudio/Services/StudioLog.cs : 9213 StoreDatabasesUnreadable"] = (Verdict.Throttled,
            "A store's databases could not be listed; asked by every Overview load and every scope listing in every circuit."),
        ["src/MartenStudio/Services/StudioLog.cs : 9214 PostgresVersionUnreadable"] = (Verdict.Throttled,
            "The server version could not be read; asked once per circuit per store, and every circuit asks."),
        ["src/MartenStudio/Services/StudioLog.cs : 9215 ShardTrackerUnobservable"] = (Verdict.Throttled,
            "The in-process tracker refused an observer; asked again by every visit to the projections page."),
        ["src/MartenStudio/Services/StudioLog.cs : 9216 ProjectionSummaryUnreadable"] = (Verdict.Throttled,
            "The projection summary failed for a reason that is not a refusal or a stale link; polled by the Overview and nav."),
        ["src/MartenStudio/Services/StudioLog.cs : 9217 EventReadFailed"] = (Verdict.Throttled,
            "An event-store read failed other than by timeout or a missing table; the Overview, feed and nav poll these."),
        ["src/MartenStudio/Services/StudioLog.cs : 9218 StreamTimestampMissing"] = (Verdict.Throttled,
            "A hand-migrated mt_streams no Marten schema can have; once is enough, not once per page of streams."),
        ["src/MartenStudio/Services/StudioLog.cs : 9219 TenantDiscoveryFailed"] = (Verdict.Throttled,
            "Tenant discovery failed; the header asks on every scope change in every circuit."),
        ["src/MartenStudio/Services/StudioLog.cs : 9220 LiveUpdateHandlerFailed"] = (Verdict.Throttled,
            "A page's own refresh-failure handler threw: a studio bug, on a loop that runs every RefreshInterval."),

        // ---- Visitor-started reads and writes, one line per action ------------------------------------
        ["src/MartenStudio/Services/Configuration/ConfigurationService.cs : Marten Studio could not describe the databases of store {StoreKey}"] = (Verdict.VisitorAction,
            "The configuration screen's own load; not polled, and the page shows the failure."),
        ["src/MartenStudio/Services/Projections/ProjectionDataService.cs : Marten Studio could not enumerate the databases of store {StoreKey} before a coordinator control"] = (Verdict.VisitorAction,
            "A pause or resume the visitor pressed is refused because the store's databases cannot be enumerated."),
        ["src/MartenStudio/Services/Relationships/RelationshipDataService.cs : Marten Studio could not read the relationships of {StoreKey}"] = (Verdict.VisitorAction,
            "The relationships screen's load failed on a catalog read; not polled."),
        ["src/MartenStudio/Services/Relationships/RelationshipDataService.cs : Marten Studio could not read what references '{Id}' of '{Alias}'"] = (Verdict.VisitorAction,
            "A document detail's referenced-by panel failed as a whole; the per-edge counts inside it are Debug."),
        ["src/MartenStudio/Services/Schema/SchemaDataService.cs : Marten Studio could not create a schema migration for database {DatabaseId}"] = (Verdict.VisitorAction,
            "The drift check behind a button failed to build a migration."),
        ["src/MartenStudio/Services/Schema/SchemaDataService.cs : Marten Studio could not preview a schema migration for database {DatabaseId}"] = (Verdict.VisitorAction,
            "The migration preview behind a button failed."),
        ["src/MartenStudio/Services/Schema/SchemaDataService.cs : Marten Studio's schema apply on {DatabaseId} was cancelled"] = (Verdict.VisitorAction,
            "An apply was cancelled mid-way by the process stopping; the schema may be half migrated."),
        ["src/MartenStudio/Services/Schema/SchemaDataService.cs : Marten Studio could not read table statistics for database {DatabaseId}"] = (Verdict.VisitorAction,
            "The schema screen's tables tab failed to load."),
        ["src/MartenStudio/Services/Schema/SchemaDataService.cs : Marten Studio could not read index statistics for database {DatabaseId}"] = (Verdict.VisitorAction,
            "The schema screen's indexes tab failed to load."),
        ["src/MartenStudio/Services/Schema/SchemaDataService.cs : Marten Studio could not read functions for database {DatabaseId}"] = (Verdict.VisitorAction,
            "The schema screen's functions tab failed to load."),
        ["src/MartenStudio/Services/Schema/SchemaDataService.cs : Marten Studio could not produce a database script for database {DatabaseId}"] = (Verdict.VisitorAction,
            "The DDL script behind a button failed."),
        ["src/MartenStudio/Services/Documents/DocumentDataService.cs : Marten Studio could not list the document collections of {StoreKey}"] = (Verdict.VisitorAction,
            "The documents rail failed as a whole; not polled, and the rail says so."),
        ["src/MartenStudio/Services/Documents/DocumentDataService.cs : Marten Studio could not count '{Alias}' exactly"] = (Verdict.VisitorAction,
            "An exact count the visitor pressed failed for a reason other than its timeout (which is Debug)."),
        ["src/MartenStudio/Services/Documents/DocumentDataService.cs : Marten Studio could not read the recent documents of {StoreKey}"] = (Verdict.VisitorAction,
            "The recent-documents region failed other than by its timeout (which is Debug)."),
        ["src/MartenStudio/Services/Documents/DocumentDataService.cs : Marten Studio could not list '{Alias}'"] = (Verdict.VisitorAction,
            "A collection's list failed with a non-Postgres exception; Postgres errors are rendered without a log."),
        ["src/MartenStudio/Services/Documents/DocumentDataService.cs : Marten Studio could not count '{Alias}'"] = (Verdict.VisitorAction,
            "A list header's count failed with an unexpected SQLSTATE; its timeout is Debug."),
        ["src/MartenStudio/Services/Documents/DocumentDataService.cs : Marten Studio could not list the document tables of the database"] = (Verdict.VisitorAction,
            "The rail's discovery of unregistered mt_doc tables failed on a catalog read."),
        ["src/MartenStudio/Services/Documents/DocumentDataService.cs : Marten Studio could not count '{Alias}' #2"] = (Verdict.VisitorAction,
            "A rail count failed with an unexpected SQLSTATE; running out of its two-second budget is Debug."),
        ["src/MartenStudio/Services/Documents/DocumentDataService.Detail.cs : Marten Studio could not read document '{Id}' of '{Alias}'"] = (Verdict.VisitorAction,
            "A document the visitor opened could not be read, other than by a Postgres error it renders."),
        ["src/MartenStudio/Services/Documents/DocumentDataService.Detail.cs : Marten Studio could not read the related documents of '{Alias}'"] = (Verdict.VisitorAction,
            "A document detail's related-documents panel failed."),
        ["src/MartenStudio/Services/Documents/DocumentDataService.Detail.cs : Marten Studio could not probe the collections for id '{Id}'"] = (Verdict.VisitorAction,
            "The find-by-id probe the visitor ran failed; per-collection probe failures inside it are Debug."),

        // ---- Faults ---------------------------------------------------------------------------------
        ["src/MartenStudio/Services/StudioState.cs : A Marten Studio page failed to handle a scope change"] = (Verdict.Fault,
            "A page's scope-change handler threw: a studio bug, and a page that missed the switch can show another scope's data."),
        ["src/MartenStudio/Services/Projections/StudioOperationTracker.cs : Marten Studio operation {Kind} on {Target} failed"] = (Verdict.Fault,
            "A rebuild the visitor started failed after tearing the projection's tables down; they stay empty until rebuilt."),
    };

    [Fact]
    public void Every_log_site_above_Debug_is_classified_and_every_classified_site_exists()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        List<LogSite> sites = [.. files.SelectMany(static file => Sites(SourceScanner.Relative(file), File.ReadAllText(file)))];

        sites.Should().NotBeEmpty("src/ does log above Debug, and a scan that finds nothing has stopped working");

        List<string> problems = [];

        foreach (LogSite site in sites.Where(static x => !Roster.ContainsKey(x.Key)))
        {
            problems.Add($"'{site.Key}' ({site.Level}) is not classified. Make it Debug if the page already shows it and nothing is wrong; otherwise add it to Roster with a verdict and one line of why.");
        }

        HashSet<string> found = [.. sites.Select(static x => x.Key)];

        foreach (string stale in Roster.Keys.Where(x => !found.Contains(x)))
        {
            problems.Add($"'{stale}' is in the roster and no longer logs above Debug; remove it.");
        }

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_throttled_site_takes_its_level_from_the_throttle()
    {
        List<LogSite> sites = [.. SourceScanner.ShippedSourceFiles()
            .SelectMany(static file => Sites(SourceScanner.Relative(file), File.ReadAllText(file)))];

        List<string> problems = [];

        foreach (LogSite site in sites.Where(static x => Roster.ContainsKey(x.Key)))
        {
            Verdict verdict = Roster[site.Key].Verdict;

            string? problem = (verdict, site.Level) switch
            {
                (Verdict.Throttled, not SiteLevel.Dynamic) =>
                    "is filed as throttled but writes a fixed level; take the level from StudioLogThrottle",
                (not Verdict.Throttled, SiteLevel.Dynamic) =>
                    "takes its level as a parameter but is not filed as throttled",
                (Verdict.Fault, not (SiteLevel.Error or SiteLevel.Critical)) => "is filed as a fault but is not an Error",
                (Verdict.Refusal or Verdict.VisitorAction or Verdict.DataLoss, not SiteLevel.Warning) =>
                    "is filed as a Warning-level site but writes " + site.Level,
                _ => null,
            };

            if (problem is not null)
            {
                problems.Add($"'{site.Key}' {problem}.");
            }
        }

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_justification_is_one_line_of_prose()
    {
        foreach ((string key, (Verdict _, string why)) in Roster)
        {
            why.Should().NotBeNullOrWhiteSpace(key);
            why.Should().NotContain("\n", "one line, for {0}", key);
            why.Length.Should().BeGreaterThan(20, "a reason, for {0}", key);
        }
    }

    /// <summary>The anti-vacuity check: the scanner has to be able to fail.</summary>
    [Theory]
    [InlineData("class C { void M() { logger.LogWarning(exception, \"x {A}\", a); } }", "f.cs : x {A}")]
    [InlineData("class C { void M() { logger.LogError(\"boom\"); } }", "f.cs : boom")]
    [InlineData("class C { void M() { logger.LogCritical ( \"boom\" ); } }", "f.cs : boom")]
    [InlineData("class C { void M() { logger.Log(level, exception, \"dyn\"); } }", "f.cs : dyn")]
    [InlineData("static partial class L { [LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = \"m\")] public static partial void Warned(this ILogger l); }", "f.cs : 7 Warned")]
    [InlineData("static partial class L { [LoggerMessage(EventId = 8, Message = \"m\")] public static partial void Levelled(this ILogger l, LogLevel level); }", "f.cs : 8 Levelled")]
    [InlineData("static partial class L { [LoggerMessage(9, LogLevel.Error, \"m\")] public static partial void Positional(this ILogger l); }", "f.cs : 9 Positional")]
    [InlineData("@code { void M() { Logger.LogWarning(\"in razor\"); } }", "f.cs : in razor")]
    [InlineData("class C { void M() { logger.LogDebug(\"quiet\"); logger.LogInformation(\"said\"); } }", null)]
    [InlineData("class C { void M() { logger.LogTrace(\"quiet\"); } }", null)]
    [InlineData("// logger.LogWarning(\"in a comment\");\nclass C { }", null)]
    [InlineData("/* logger.LogError(\"in a block\") */\nclass C { }", null)]
    [InlineData("/// <c>logger.LogWarning(\"in a doc\")</c>\nclass C { }", null)]
    [InlineData("class C { string s = \"logger.LogWarning(x)\"; }", null)]
    [InlineData("@* Logger.LogWarning(\"razor prose\") *@\n<p>fine</p>", null)]
    [InlineData("static partial class L { [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = \"m\")] public static partial void Quiet(this ILogger l); }", null)]
    [InlineData("static partial class L { [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = \"m\")] public static partial void Said(this ILogger l); }", null)]
    [InlineData("/// [LoggerMessage(EventId = 3, Level = LogLevel.Error)]\nclass C { }", null)]
    public void The_scanner_finds_a_real_site_and_ignores_one_in_prose(string source, string? expectedKey)
    {
        List<LogSite> sites = [.. Sites("f.cs", source)];

        if (expectedKey is null)
        {
            sites.Should().BeEmpty();
        }
        else
        {
            sites.Should().ContainSingle().Which.Key.Should().Be(expectedKey);
        }
    }

    [Fact]
    public void Two_sites_with_the_same_message_in_one_file_are_told_apart()
    {
        const string source = "class C { void M() { logger.LogWarning(\"same\"); logger.LogWarning(\"same\"); } }";

        Sites("f.cs", source).Select(static x => x.Key).Should().Equal("f.cs : same", "f.cs : same #2");
    }

    // ------------------------------------------------------------------------------------------------
    // The scanner
    // ------------------------------------------------------------------------------------------------

    /// <summary>The level a site writes at, as far as its text says.</summary>
    public enum SiteLevel
    {
        /// <summary><c>LogWarning</c> or <c>Level = LogLevel.Warning</c>.</summary>
        Warning,

        /// <summary><c>LogError</c> or <c>Level = LogLevel.Error</c>.</summary>
        Error,

        /// <summary><c>LogCritical</c> or <c>Level = LogLevel.Critical</c>.</summary>
        Critical,

        /// <summary><c>Log(level, …)</c>, or a <c>[LoggerMessage]</c> whose level is a parameter.</summary>
        Dynamic,
    }

    /// <summary>One place in <c>src/</c> that can write above Debug.</summary>
    /// <param name="Key">The roster key: the file, then the message template or the event id and method.</param>
    /// <param name="Level">What the text says it writes at.</param>
    public sealed record LogSite(string Key, SiteLevel Level);

    /// <summary>Every site in one file, in the order they appear, with repeated keys numbered.</summary>
    private static IEnumerable<LogSite> Sites(string relativePath, string source)
    {
        string code = SourceScanner.StripCommentsAndStrings(source);

        List<(int Index, string Label, SiteLevel Level)> found = [];

        foreach (Match match in DirectCall.Matches(code))
        {
            found.Add((match.Index, MessageAfter(source, match.Index + match.Length), Enum.Parse<SiteLevel>(match.Groups["level"].Value)));
        }

        foreach (Match match in DynamicCall.Matches(code))
        {
            found.Add((match.Index, MessageAfter(source, match.Index + match.Length), SiteLevel.Dynamic));
        }

        foreach (Match match in LoggerMessage.Matches(code))
        {
            string arguments = match.Groups["args"].Value;
            Match level = LevelArgument.Match(arguments);

            SiteLevel? siteLevel = level.Success
                ? level.Groups["level"].Value switch
                {
                    "Warning" => SiteLevel.Warning,
                    "Error" => SiteLevel.Error,
                    "Critical" => SiteLevel.Critical,
                    _ => null,
                }
                : SiteLevel.Dynamic;

            if (siteLevel is null)
            {
                continue;
            }

            string id = EventIdArgument.Match(arguments) is { Success: true } eventId ? eventId.Groups["id"].Value : "?";
            Match method = MethodName.Match(code, match.Index + match.Length);
            string name = method.Success ? method.Groups["name"].Value : "?";

            found.Add((match.Index, id + " " + name, siteLevel.Value));
        }

        Dictionary<string, int> seen = new(StringComparer.Ordinal);

        foreach ((int _, string label, SiteLevel siteLevel) in found.OrderBy(static x => x.Index))
        {
            string key = relativePath + " : " + label;
            int count = seen[key] = seen.GetValueOrDefault(key) + 1;

            yield return new LogSite(count == 1 ? key : key + " #" + count.ToString(CultureInfo.InvariantCulture), siteLevel);
        }
    }

    /// <summary>
    /// The first string literal after <paramref name="index" /> in the original text - the message
    /// template, which is what a person searching the log would search for.
    /// </summary>
    /// <remarks>
    /// Read from the original rather than the stripped text, because the stripped text has every literal
    /// blanked; the two have the same length, so a position in one is the same position in the other.
    /// </remarks>
    private static string MessageAfter(string source, int index)
    {
        int open = source.IndexOf('"', index);
        if (open < 0)
        {
            return "(no message)";
        }

        int close = open + 1;
        while (close < source.Length && source[close] != '"')
        {
            close += source[close] == '\\' ? 2 : 1;
        }

        return close < source.Length ? source[(open + 1)..close] : "(no message)";
    }
}
