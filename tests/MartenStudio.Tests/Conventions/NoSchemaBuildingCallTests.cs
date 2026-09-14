using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Enforces the half of AGENTS.md hard rule 14 that is about the event store: nothing in <c>src/</c>
/// calls a Marten API that migrates before it reads.
/// </summary>
/// <remarks>
/// <para>
/// Thirteen members of <c>Marten.Storage.MartenDatabase</c> open with
/// <c>await EnsureStorageExistsAsync(typeof(IEvent), token)</c> - verified by reading
/// <c>src/Marten/Storage/MartenDatabase.EventStorage.cs</c> at tag <c>V9.35.0</c>, where they are the only
/// calls to it in the file - which applies the event store's Weasel migration under the database's own
/// <c>AutoCreate</c> before a row is read. Every caller of one of them was a read or a navigation path on
/// a timer, so each of them was a studio that created <c>mt_events</c>, <c>mt_streams</c>,
/// <c>mt_events_sequence</c> and <c>mt_event_progression</c> because somebody opened a tab, and applied
/// any pending event-store change on a database that already had them.
/// </para>
/// <para>
/// This is a grep and not a proof: the live <c>NoDdl</c> suites are what prove the studio creates
/// nothing, and they carry anti-vacuity theories showing that these calls still do. What a grep adds is
/// that the next person to want a high-water mark is stopped at the point of writing the call rather
/// than at the point of running Docker - and that reintroducing one cannot be a silent regression,
/// because the studio's own replacements
/// (<c>MartenStudio.Internal.Sql.ProjectionProgressQueries</c>) answer the same numbers and would look
/// like a working page.
/// </para>
/// <para>
/// The scan strips comments and string literals first (<see cref="SourceScanner"/>), because the reason
/// each of these is not called is documented at length in the doc comments of the files that used to
/// call them. That stripping is what makes the test capable of being silently vacuous, and it can go
/// vacuous two ways: for one <em>call</em>, which
/// <see cref="The_scanner_finds_a_real_call_and_ignores_one_in_a_comment_or_a_string"/> guards, and for
/// one whole <em>file</em> from an unclosed literal onwards, which
/// <see cref="Every_scanned_file_is_one_the_scanner_can_follow_to_the_end"/> guards.
/// </para>
/// </remarks>
public class NoSchemaBuildingCallTests
{
    /// <summary>
    /// Every member that migrates the event store before it does anything, as a <em>call</em>: a name
    /// followed by an argument list.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Eleven names for thirteen call sites - <c>FindEventStoreFloorAtTimeAsync</c> and
    /// <c>ReadProjectionProgressAsync</c> each have two overloads that open the same way, and
    /// <c>AllProjectionProgress</c>'s untenanted overload delegates to the tenanted one - plus
    /// <c>EnsureStorageExistsAsync</c> itself, because calling it directly is the same act without the
    /// wrapper. The previous version of this list named three of the thirteen, which was the three that
    /// had been removed from <c>src/</c> rather than the rule; nothing stopped the next person reaching
    /// for <c>ProjectionProgressFor</c> or <c>FetchMaxEventSequenceAsync</c>, which migrate exactly as
    /// much.
    /// </para>
    /// <para>
    /// Matching the call shape rather than the bare name is deliberate. A <c>&lt;see cref&gt;</c> or a
    /// sentence naming the member is exactly what the doc comments have to be able to say, and the
    /// stripping pass already removes those; requiring the parenthesis is the second line of defence, and
    /// it is also what keeps the rule readable - what is forbidden is calling them, not mentioning them.
    /// </para>
    /// </remarks>
    private static readonly Regex SchemaBuildingCall = new(
        @"\b(?<member>EnsureStorageExistsAsync"
        + @"|FetchHighestEventSequenceNumber"
        + @"|FetchMaxEventSequenceAsync"
        + @"|FetchEventStoreStatistics"
        + @"|FetchProjectionProgressFor"
        + @"|ProjectionProgressFor"
        + @"|AllProjectionProgress"
        + @"|ReadProjectionProgressAsync"
        + @"|DeleteProjectionProgressByShardNameAsync"
        + @"|FindEventStoreFloorAtTimeAsync"
        + @"|WriteExtendedProgressionAsync"
        + @"|MarkEventsAsSkipped)\s*\(",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// The one call in <c>src/</c> that is allowed to be on this list, and where.
    /// </summary>
    /// <remarks>
    /// Hard rule 14 is about read and navigation paths - the things that run on a timer because somebody
    /// opened a tab. <c>IMartenDatabase.MarkEventsAsSkipped</c> is reached only from
    /// <c>EventDataService.SkipEventAsync</c>, which is a capability-gated, audited write that a person
    /// asked for by name; a write that ensures the storage it is about to write to is not the failure the
    /// rule exists to prevent. It is allow-listed by file <em>and</em> member rather than by either alone,
    /// so allowing this one call does not quietly allow a progress read in the same file, and
    /// <see cref="The_allowed_call_is_still_there_so_the_allow_list_cannot_go_stale"/> fails if the call
    /// it excuses is gone.
    /// </remarks>
    private static readonly (string File, string Member)[] AllowedCalls =
    [
        ("src/MartenStudio/Services/Events/EventDataService.cs", "MarkEventsAsSkipped"),
    ];

    [Fact]
    public void No_source_file_calls_a_Marten_read_that_migrates_the_event_store()
    {
        var files = SourceScanner.ShippedSourceFiles();

        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        var offenders = files
            .SelectMany(Calls)
            .Where(x => !IsAllowed(x))
            .Select(x => $"{x.File}: {x.Member}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.Should().BeEmpty(
            "hard rule 14: every one of these Marten members begins with EnsureStorageExistsAsync(typeof(IEvent)), " +
            "so a page that calls one creates or alters the event store. Read them through " +
            "ProjectionProgressQueries instead. Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The allow-list is a statement about code that exists, not a permanent exemption.
    /// </summary>
    /// <remarks>
    /// Without this, deleting <c>SkipEventAsync</c> would leave an entry saying "a call to
    /// <c>MarkEventsAsSkipped</c> in <c>EventDataService</c> is fine" for whatever is written there next.
    /// </remarks>
    [Fact]
    public void The_allowed_call_is_still_there_so_the_allow_list_cannot_go_stale()
    {
        var found = SourceScanner.ShippedSourceFiles().SelectMany(Calls).ToArray();

        foreach ((string file, string member) in AllowedCalls)
        {
            found.Should().Contain(
                x => x.File == file && x.Member == member,
                "the allow-list excuses {0} in {1}, and that call is no longer there - prune the entry",
                member,
                file);
        }
    }

    /// <summary>The anti-vacuity check: the scanner has to be able to fail.</summary>
    [Theory]
    [InlineData("var x = await database.FetchHighestEventSequenceNumber(token);", true)]
    [InlineData("await resolved.Database.AllProjectionProgress(cancellationToken);", true)]
    [InlineData("_ = await store.Advanced.AllProjectionProgress(tenantId, token);", true)]
    [InlineData("await database.FetchEventStoreStatistics(token);", true)]
    [InlineData("await database\n    .FetchHighestEventSequenceNumber (token);", true)]
    [InlineData("var seq = await database.FetchMaxEventSequenceAsync(token);", true)]
    [InlineData("var at = await database.ProjectionProgressFor(shard, token);", true)]
    [InlineData("var all = await database.FetchProjectionProgressFor(names, token);", true)]
    [InlineData("var row = await database.ReadProjectionProgressAsync(shard, token);", true)]
    [InlineData("await database.DeleteProjectionProgressByShardNameAsync(identity, token);", true)]
    [InlineData("var floor = await database.FindEventStoreFloorAtTimeAsync(when, token);", true)]
    [InlineData("await database.WriteExtendedProgressionAsync(state, token);", true)]
    [InlineData("await database.MarkEventsAsSkipped([sequence], token);", true)]
    [InlineData("await database.EnsureStorageExistsAsync(typeof(IEvent), token);", true)]
    [InlineData("/// <c>IMartenDatabase.FetchHighestEventSequenceNumber</c> migrates before it reads.", false)]
    [InlineData("// AllProjectionProgress(tenantId, ct) is not a filter: it resolves the database.", false)]
    [InlineData("/* FetchEventStoreStatistics(token) */", false)]
    [InlineData("var title = \"IMartenDatabase.FetchHighestEventSequenceNumber\";", false)]
    [InlineData("@* the feed never calls FetchHighestEventSequenceNumber(token) *@", false)]
    [InlineData("var highWater = await ReadHighWaterMarkAsync(resolved, connection, token);", false)]
    [InlineData("var rows = await ReadProgressionRowsAsync(connection, catalog, schema, token);", false)]
    public void The_scanner_finds_a_real_call_and_ignores_one_in_a_comment_or_a_string(
        string source, bool expected)
    {
        SchemaBuildingCall.IsMatch(SourceScanner.StripCommentsAndStrings(source)).Should().Be(expected);
    }

    /// <summary>
    /// And the studio's own replacements are where those numbers come from now.
    /// </summary>
    /// <remarks>
    /// Without this, deleting the reads altogether would pass the rule above - and a projections page
    /// that reports nothing is a worse answer than one that migrates, because it looks like a healthy
    /// store with no projections.
    /// </remarks>
    [Fact]
    public void The_studios_own_progress_reads_are_the_ones_the_services_use()
    {
        // The name alone, not "the name and a dot": a call wrapped onto the next line is still a call,
        // and the stripping pass has already removed every mention of it in prose.
        var progressions = SourceScanner.ShippedSourceFiles()
            .Where(x => SourceScanner.StripCommentsAndStrings(File.ReadAllText(x))
                .Contains("ProjectionProgressQueries", StringComparison.Ordinal))
            .Select(SourceScanner.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        progressions.Should().Contain("src/MartenStudio/Services/Projections/ProjectionDataService.cs");
        progressions.Should().Contain("src/MartenStudio/Services/Events/EventDataService.cs");
    }

    /// <summary>
    /// Every file the conventions scan reads is one the stripping pass can follow all the way through.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other way a scanner goes vacuous, and the one the per-line theories cannot see.
    /// <see cref="SourceScanner"/> pairs quotes naively and knows nothing about raw string literals, of
    /// which <c>src/</c> has several with inner quotes in them. Their counts come out even today, so the
    /// pass resynchronises; one odd count would leave it "inside a string" to the end of the file, and
    /// this rule and <see cref="NoAsyncVoidTests"/> would both stop applying to everything after it with
    /// nothing failing.
    /// </para>
    /// <para>
    /// This is that condition as an assertion, over the same file set both rules scan, so the answer is
    /// "which file" rather than a rule that quietly covers less than it says.
    /// <see cref="A_file_the_scanner_cannot_follow_is_reported_rather_than_silently_exempted"/> is the
    /// proof that the flag can be true.
    /// </para>
    /// </remarks>
    [Fact]
    public void Every_scanned_file_is_one_the_scanner_can_follow_to_the_end()
    {
        var files = SourceScanner.ShippedSourceFiles();

        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        List<string> unfollowable = [];

        foreach (var file in files)
        {
            SourceScanner.StripCommentsAndStrings(File.ReadAllText(file), out bool ranOff);

            if (ranOff)
            {
                unfollowable.Add(SourceScanner.Relative(file));
            }
        }

        unfollowable.Should().BeEmpty(
            "the conventions scanners blank everything from an unclosed literal or block comment to the " +
            "end of the file, so a file they cannot follow silently exempts its own tail from every rule " +
            "they enforce. Files: " + string.Join(", ", unfollowable));
    }

    /// <summary>
    /// The anti-vacuity check for the check above: a file the pass cannot follow really does hide a
    /// forbidden call, and really does set the flag.
    /// </summary>
    /// <remarks>
    /// Written to a real file and read back through <c>File.ReadAllText</c>, which is the path the scan
    /// itself takes. The literal is left open and nothing after it contains a quote, so the pass blanks
    /// the rest of the file - including a <c>FetchHighestEventSequenceNumber</c> call that the rule above
    /// would otherwise have failed on. Both halves are asserted: that the call became invisible, which is
    /// the damage, and that the flag says so, which is the defence.
    /// </remarks>
    [Fact]
    public void A_file_the_scanner_cannot_follow_is_reported_rather_than_silently_exempted()
    {
        var path = Path.Combine(Path.GetTempPath(), $"martenstudio-scanner-{Guid.NewGuid():N}.cs");

        // An unterminated literal, and no further quote anywhere below it.
        File.WriteAllText(
            path,
            "namespace Broken;\n"
            + "\n"
            + "internal static class Half\n"
            + "{\n"
            + "    private const string Sql = \"select last_value from mt_events_sequence;\n"
            + "\n"
            + "    public static Task<long> Go(IMartenDatabase database, CancellationToken token) =>\n"
            + "        database.FetchHighestEventSequenceNumber(token);\n"
            + "}\n");

        try
        {
            var stripped = SourceScanner.StripCommentsAndStrings(File.ReadAllText(path), out bool ranOff);

            ranOff.Should().BeTrue("the literal on line five is never closed");

            SchemaBuildingCall.IsMatch(stripped).Should().BeFalse(
                "this is the damage the flag exists to report: the call is real, and the pass blanked it");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Whether this match is the one call the rule excuses, in the one file it excuses it in.</summary>
    private static bool IsAllowed((string File, string Member) call) =>
        AllowedCalls.Any(x =>
            string.Equals(x.File, call.File, StringComparison.Ordinal)
            && string.Equals(x.Member, call.Member, StringComparison.Ordinal));

    /// <summary>Every schema-building call one source file makes, by member name.</summary>
    private static IEnumerable<(string File, string Member)> Calls(string path)
    {
        var relative = SourceScanner.Relative(path);
        var code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(path));

        foreach (Match match in SchemaBuildingCall.Matches(code))
        {
            yield return (relative, match.Groups["member"].Value);
        }
    }
}
