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
/// <b>And a throttled declaration is only as good as its calls.</b> A <c>[LoggerMessage]</c> whose level
/// is a parameter can still be called with <c>LogLevel.Warning</c>, which puts it back on every poll - and
/// nothing about the declaration would say so.
/// <see cref="Every_call_of_a_throttled_event_takes_its_level_from_the_throttle" /> reads every call of
/// one, and of <c>Log(level, …)</c>, and follows the level argument back to where it was computed: it must
/// come from <c>StudioLogThrottle.WarningOrDebug</c> or from a helper on
/// <see cref="ThrottleLessHelpers" />, with no literal level above Debug on the way.
/// </para>
/// <para>
/// The scanner strips comments and string literals first (<see cref="SourceScanner" />), which is what lets
/// the prose in this codebase quote a log call without failing, and is also what could make it silently
/// vacuous - so <see cref="The_scanner_finds_a_real_site_and_ignores_one_in_prose" /> and
/// <see cref="The_call_site_scanner_follows_the_level_and_ignores_prose" /> prove they can still fail.
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

    private static readonly Regex LiteralLevelAboveDebug = new(
        @"LogLevel\s*\.\s*(?:Information|Warning|Error|Critical)\b", RegexOptions.None, RegexTimeout);

    private static readonly Regex ThrottleCall = new(@"\.\s*WarningOrDebug\s*\(", RegexOptions.None, RegexTimeout);

    private static readonly Regex Identifier = new(@"^[A-Za-z_]\w*$", RegexOptions.None, RegexTimeout);

    /// <summary>
    /// The only helpers besides <c>StudioLogThrottle.WarningOrDebug</c> a throttled event may take its level
    /// from, spelled as they are called, and why each is allowed.
    /// </summary>
    /// <remarks>
    /// A helper that routes a throttled event through a fixed level is the whole thing this rule exists to
    /// catch, so a new one fails until somebody writes down here why it does not.
    /// </remarks>
    private static readonly Dictionary<string, string> ThrottleLessHelpers = new(StringComparer.Ordinal)
    {
        ["StudioLogThrottle.LevelOrWarning"] =
            "The throttle's own answer, or Warning for an owner a test built without one (StudioScopeCatalog, MartenStoreRegistry, StudioLiveUpdates); the container always supplies a throttle.",
    };

    /// <summary>Why a site is allowed to be above Debug.</summary>
    public enum Verdict
    {
        /// <summary>
        /// A security-relevant refusal. Always a Warning: a capability or a policy refused an action. A
        /// client driving a control the page did not offer is one way; a per-database policy refusing a
        /// pause or a store-wide correction the page did offer - because it reaches a database the visitor
        /// may not - is the other. An operator wants to see either.
        /// </summary>
        Refusal,

        /// <summary>
        /// A real anomaly on a path the studio polls or repeats. The level is a parameter, Warning once
        /// per store, database and kind of failure per ten minutes, Debug in between.
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

        /// <summary>
        /// A risky configuration the operator chose, said once per host start where the startup log is
        /// read - never on a page, never polled. Always a Warning.
        /// </summary>
        Configuration,
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
            "A policy refusal on a write or a query run, including an offered pause or correction that reaches a database the visitor may not; listings are filtered (FilterAsync logs nothing)."),
        ["src/MartenStudio/Services/StudioLog.cs : 9211 DocumentWriteRoundTripDropped"] = (Verdict.DataLoss,
            "A save the visitor confirmed dropped properties the CLR type does not have; the only durable record of the loss."),
        ["src/MartenStudio/Services/StudioLog.cs : 9230 DatabaseBrowserOpenToEverySchemaWithoutRole"] = (Verdict.Configuration,
            "BrowsableSchemas contains \"*\" and SqlConsoleRole is unset: every table the store's role can read is browsable. Once per host start."),

        // ---- StudioLog: the throttled anomalies (level is a parameter) --------------------------------
        ["src/MartenStudio/Services/StudioLog.cs : 9210 StoreUnavailable"] = (Verdict.Throttled,
            "A registered store will not build; the registry's ten-second cache expires under every polling page."),
        ["src/MartenStudio/Services/StudioLog.cs : 9212 DaemonUnreachable"] = (Verdict.Throttled,
            "A registered coordinator answered with neither a daemon nor NotSupported, or would not build other than by Wolverine's unknown-store shape; polled."),
        ["src/MartenStudio/Services/StudioLog.cs : 9213 StoreDatabasesUnreadable"] = (Verdict.Throttled,
            "A store's databases could not be listed; asked by every Overview load and every scope listing in every circuit."),
        ["src/MartenStudio/Services/StudioLog.cs : 9214 PostgresVersionUnreadable"] = (Verdict.Throttled,
            "The server version could not be read; asked once per circuit per store, and every circuit asks."),
        ["src/MartenStudio/Services/StudioLog.cs : 9215 ShardTrackerUnobservable"] = (Verdict.Throttled,
            "The in-process tracker refused an observer; asked again by every visit to the projections page."),
        ["src/MartenStudio/Services/StudioLog.cs : 9216 ProjectionSummaryUnreadable"] = (Verdict.Throttled,
            "The projection summary failed for a reason that is not a refusal or a stale link; polled by the Overview and nav."),
        ["src/MartenStudio/Services/StudioLog.cs : 9217 EventReadFailed"] = (Verdict.Throttled,
            "An event-store read failed other than by a statement timeout (a missing table is here: every read asks the catalog first); polled."),
        ["src/MartenStudio/Services/StudioLog.cs : 9218 StreamTimestampMissing"] = (Verdict.Throttled,
            "A hand-migrated mt_streams no Marten schema can have; once is enough, not once per page of streams."),
        ["src/MartenStudio/Services/StudioLog.cs : 9219 TenantDiscoveryFailed"] = (Verdict.Throttled,
            "Tenant discovery failed; the header asks on every scope change in every circuit."),
        ["src/MartenStudio/Services/StudioLog.cs : 9220 LiveUpdateHandlerFailed"] = (Verdict.Throttled,
            "A page's own refresh-failure handler threw: a studio bug, on a loop that runs every RefreshInterval."),

        // ---- Visitor-started reads and writes, one line per action ------------------------------------
        ["src/MartenStudio/Services/Configuration/ConfigurationService.cs : Marten Studio could not describe the databases of store {StoreKey}"] = (Verdict.VisitorAction,
            "The configuration screen's own load; not polled, and the page shows the failure."),
        ["src/MartenStudio/Services/Projections/ProjectionDataService.cs : Marten Studio could not enumerate the databases of store {StoreKey} before {Action}, so it refused it"] = (Verdict.VisitorAction,
            "A pause, resume or store-wide correction the visitor pressed is refused because the store's databases cannot be enumerated."),
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
                (Verdict.Refusal or Verdict.VisitorAction or Verdict.DataLoss or Verdict.Configuration, not SiteLevel.Warning) =>
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
    // The calls of the throttled events (DB-0-fix, item 6)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every call of a throttled <c>StudioLog</c> event - and of <c>Log(level, …)</c> - takes its level from
    /// the throttle, never from a literal.
    /// </summary>
    [Fact]
    public void Every_call_of_a_throttled_event_takes_its_level_from_the_throttle()
    {
        HashSet<string> throttled = ThrottledEventMethods();

        string[] files = SourceScanner.ShippedSourceFiles();
        List<ThrottledCall> calls = [.. files.SelectMany(file => ThrottledCalls(SourceScanner.Relative(file), File.ReadAllText(file), throttled))];

        calls.Should().HaveCountGreaterThanOrEqualTo(
            throttled.Count, "every throttled event has at least one caller, and a scan that finds fewer has stopped seeing them");

        calls.Select(static x => x.Method).Where(static x => x != "Log").Distinct().Should().BeEquivalentTo(
            throttled, "an event nobody calls is dead, and a caller the scan cannot see is a caller it cannot check");

        List<string> problems = [.. calls.Where(static x => x.Problem is not null).Select(static x => $"{x.Location}: {x.Problem}")];

        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// The events the rule is about are exactly the ones the roster files as throttled in
    /// <c>StudioLog.cs</c> - 9210 and 9212-9220 - so neither list can drift from the other.
    /// </summary>
    [Fact]
    public void The_throttled_events_are_the_ones_the_roster_files_as_throttled()
    {
        IEnumerable<string> fromRoster = Roster
            .Where(static x => x.Value.Verdict == Verdict.Throttled && x.Key.StartsWith(StudioLogFile + " : ", StringComparison.Ordinal))
            .Select(static x => x.Key[(x.Key.LastIndexOf(' ') + 1)..]);

        ThrottledEventMethods().Should().BeEquivalentTo(fromRoster);
        ThrottledEventMethods().Should().Contain(["StoreUnavailable", "DaemonUnreachable", "LiveUpdateHandlerFailed"]);
    }

    /// <summary>Every listed throttle-less helper exists, as a static method of that name on that type.</summary>
    [Fact]
    public void Every_throttle_less_helper_is_real_and_says_why()
    {
        string[] files = SourceScanner.ShippedSourceFiles();

        foreach ((string helper, string why) in ThrottleLessHelpers)
        {
            why.Length.Should().BeGreaterThan(20, "a reason, for {0}", helper);

            string type = helper[..helper.IndexOf('.', StringComparison.Ordinal)];
            string method = helper[(helper.IndexOf('.', StringComparison.Ordinal) + 1)..];

            files.Should().Contain(
                file => Path.GetFileName(file) == type + ".cs"
                    && Regex.IsMatch(
                        SourceScanner.StripCommentsAndStrings(File.ReadAllText(file)),
                        $@"\bstatic\s+LogLevel\s+{method}\s*\(",
                        RegexOptions.None,
                        RegexTimeout),
                "{0} is listed as a helper and has to exist", helper);
        }
    }

    /// <summary>
    /// The anti-vacuity check for the call rule: it has to catch a literal Warning however it is spelled
    /// and wherever it hides, accept the shapes <c>src/</c> really uses, and ignore a call in prose.
    /// </summary>
    [Theory]
    // Accepted: the throttle, directly or through a local, and the one listed helper.
    [InlineData("LogLevel level = throttle.WarningOrDebug(\"s\", a, b, StudioLogThrottle.KindOf(e)); logger.DaemonUnreachable(level, e, a, b);", null)]
    [InlineData("logger.DaemonUnreachable(throttle.WarningOrDebug(\"s\", a, b, null), e, a, b);", null)]
    [InlineData("LogLevel level = StudioLogThrottle.LevelOrWarning(throttle, \"s\", a, null, k); logger.StoreUnavailable(level, a, b, c);", null)]
    [InlineData("LogLevel level = expected\n    ? LogLevel.Debug\n    : throttle.WarningOrDebug(\"s\", a, b, k);\nlogger.DaemonUnreachable(level, e, a, b);", null)]
    [InlineData("LogLevel level = throttle.WarningOrDebug(\"s\", a, b, k); logger.Log(level, e, \"dynamic\");", null)]
    // Refused: a literal, however it arrives.
    [InlineData("logger.DaemonUnreachable(LogLevel.Warning, e, a, b);", "literal")]
    [InlineData("LogLevel level = LogLevel.Warning; logger.DaemonUnreachable(level, e, a, b);", "literal")]
    [InlineData("LogLevel level = throttle?.WarningOrDebug(\"s\", a, b, k) ?? LogLevel.Warning; logger.DaemonUnreachable(level, e, a, b);", "literal")]
    [InlineData("LogLevel level = throttle.WarningOrDebug(\"s\", a, b, k); level = LogLevel.Error; logger.DaemonUnreachable(level, e, a, b);", "literal")]
    [InlineData("logger.Log(LogLevel.Warning, e, \"dynamic\");", "literal")]
    // Refused: a level the throttle did not decide.
    [InlineData("LogLevel level = LevelFor(\"s\", a, e); logger.TenantDiscoveryFailed(level, e, a);", "throttle")]
    [InlineData("void M(LogLevel level) { logger.DaemonUnreachable(level, e, a, b); }", "follow")]
    [InlineData("logger.DaemonUnreachable(options.Level, e, a, b);", "throttle")]
    // Ignored: prose.
    [InlineData("// logger.DaemonUnreachable(LogLevel.Warning, e, a, b);\nclass C { }", "none")]
    [InlineData("/// <c>logger.DaemonUnreachable(LogLevel.Warning, e, a, b)</c>\nclass C { }", "none")]
    [InlineData("class C { string s = \"logger.DaemonUnreachable(LogLevel.Warning, e, a, b)\"; }", "none")]
    public void The_call_site_scanner_follows_the_level_and_ignores_prose(string source, string? expected)
    {
        List<ThrottledCall> calls = [.. ThrottledCalls("f.cs", source, ThrottledEventMethods())];

        if (expected == "none")
        {
            calls.Should().BeEmpty();
            return;
        }

        ThrottledCall call = calls.Should().ContainSingle().Subject;

        if (expected is null)
        {
            call.Problem.Should().BeNull();
        }
        else
        {
            call.Problem.Should().NotBeNull().And.Contain(expected);
        }
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

    /// <summary>One call of a throttled event, and what is wrong with where its level came from.</summary>
    /// <param name="Location">File and line, for the failure message.</param>
    /// <param name="Method">The event method called, or <c>Log</c>.</param>
    /// <param name="Problem">What is wrong, or <see langword="null" /> when the level is the throttle's.</param>
    public sealed record ThrottledCall(string Location, string Method, string? Problem);

    private const string StudioLogFile = "src/MartenStudio/Services/StudioLog.cs";

    /// <summary>
    /// The <c>StudioLog</c> methods whose level is a parameter - read out of <c>StudioLog.cs</c> by the
    /// same scanner the roster uses, so a new throttled event is covered the moment it is declared.
    /// </summary>
    private static HashSet<string> ThrottledEventMethods() =>
        Sites(StudioLogFile, File.ReadAllText(RepositoryRoot.Combine(StudioLogFile)))
            .Where(static x => x.Level == SiteLevel.Dynamic)
            .Select(static x => x.Key[(x.Key.LastIndexOf(' ') + 1)..])
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Every call of one of <paramref name="methods" />, or of <c>Log(level, …)</c>, with its level argument
    /// followed back to where it was computed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The level argument is the call's first. When it is an expression, it is judged as it stands; when it
    /// is a local, the nearest assignment to that name before the call <em>that is still in scope at the
    /// call</em> is judged instead - "in scope" meaning no brace between the two closes the assignment's
    /// block, which is what keeps another method's <c>LogLevel level = …</c> from vouching for this one.
    /// </para>
    /// <para>
    /// Judged means: no literal level above Debug anywhere in it (a <c>LogLevel.Debug</c> branch for an
    /// expected state is fine), and a call of <c>WarningOrDebug</c> or of a helper on
    /// <see cref="ThrottleLessHelpers" />. It does not reason about branches - an <c>if</c> that assigns a
    /// literal on one arm and the throttle on the other is judged by whichever assignment comes last - which
    /// is a limit, not a licence: the rule is for the shape every site here has, one computed level per
    /// call.
    /// </para>
    /// </remarks>
    private static IEnumerable<ThrottledCall> ThrottledCalls(string relativePath, string source, IReadOnlySet<string> methods)
    {
        string code = SourceScanner.StripCommentsAndStrings(source);

        var call = new Regex(
            @"\.\s*(?<name>" + string.Join("|", methods.Append("Log").Select(Regex.Escape)) + @")\s*\(",
            RegexOptions.None,
            RegexTimeout);

        foreach (Match match in call.Matches(code))
        {
            string argument = FirstArgument(code, match.Index + match.Length).Trim();
            string location = relativePath + ":" + (code.AsSpan(0, match.Index).Count('\n') + 1).ToString(CultureInfo.InvariantCulture);

            yield return new ThrottledCall(location, match.Groups["name"].Value, ProblemWith(code, match.Index, argument));
        }
    }

    private static string? ProblemWith(string code, int callIndex, string argument)
    {
        if (LiteralLevelAboveDebug.IsMatch(argument))
        {
            return $"passes a literal level above Debug ({argument}); take it from StudioLogThrottle.WarningOrDebug";
        }

        if (ComesFromTheThrottle(argument))
        {
            return null;
        }

        if (!Identifier.IsMatch(argument))
        {
            return $"takes its level from '{argument}', which is not the throttle";
        }

        string? assigned = LastAssignmentInScope(code, callIndex, argument);

        if (assigned is null)
        {
            return $"takes its level from '{argument}', and the scanner cannot follow it to an assignment in scope - compute it from the throttle next to the call";
        }

        if (LiteralLevelAboveDebug.IsMatch(assigned))
        {
            return $"computes its level with a literal level above Debug ({assigned.Trim()}); a throttled event must not be pinned to Warning";
        }

        return ComesFromTheThrottle(assigned)
            ? null
            : $"computes its level without the throttle ({assigned.Trim()}); use StudioLogThrottle.WarningOrDebug or one of {string.Join(", ", ThrottleLessHelpers.Keys)}";
    }

    private static bool ComesFromTheThrottle(string expression) =>
        ThrottleCall.IsMatch(expression)
        || ThrottleLessHelpers.Keys.Any(helper => Regex.IsMatch(
            expression,
            Regex.Escape(helper).Replace(@"\.", @"\s*\.\s*", StringComparison.Ordinal) + @"\s*\(",
            RegexOptions.None,
            RegexTimeout));

    /// <summary>The text of the first argument of the call whose argument list starts at <paramref name="start" />.</summary>
    private static string FirstArgument(string code, int start)
    {
        int depth = 0;

        for (int index = start; index < code.Length; index++)
        {
            switch (code[index])
            {
                case '(' or '[' or '{':
                    depth++;
                    break;

                case ')' or ']' or '}' when depth == 0:
                    return code[start..index];

                case ')' or ']' or '}':
                    depth--;
                    break;

                case ',' when depth == 0:
                    return code[start..index];
            }
        }

        return code[start..];
    }

    /// <summary>
    /// The right-hand side of the last assignment to <paramref name="name" /> before
    /// <paramref name="callIndex" /> whose block has not closed by the call, or <see langword="null" />.
    /// </summary>
    private static string? LastAssignmentInScope(string code, int callIndex, string name)
    {
        var assignment = new Regex(
            @"(?<![\w.])" + Regex.Escape(name) + @"\s*=(?![=>])", RegexOptions.None, RegexTimeout);

        foreach (Match match in assignment.Matches(code[..callIndex]).Reverse())
        {
            if (!StaysInScope(code, match.Index, callIndex))
            {
                continue;
            }

            int start = match.Index + match.Length;
            int end = code.IndexOf(';', start);

            return code[start..(end < 0 || end > callIndex ? callIndex : end)];
        }

        return null;
    }

    private static bool StaysInScope(string code, int from, int to)
    {
        int depth = 0;

        for (int index = from; index < to; index++)
        {
            if (code[index] == '{')
            {
                depth++;
            }
            else if (code[index] == '}' && --depth < 0)
            {
                return false;
            }
        }

        return true;
    }

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
