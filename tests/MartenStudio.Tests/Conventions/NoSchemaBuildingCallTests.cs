using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Enforces the half of AGENTS.md hard rule 14 that is about the event store: nothing in <c>src/</c>
/// calls a Marten API that migrates before it reads.
/// </summary>
/// <remarks>
/// <para>
/// <c>IMartenDatabase.FetchHighestEventSequenceNumber</c>, <c>AllProjectionProgress</c> (both overloads,
/// on <c>IMartenDatabase</c> and on <c>AdvancedOperations</c>) and <c>FetchEventStoreStatistics</c> each
/// open with <c>await EnsureStorageExistsAsync(typeof(IEvent), token)</c> - verified in Marten 9.35,
/// <c>src/Marten/Storage/MartenDatabase.EventStorage.cs</c> - which applies the event store's Weasel
/// migration under the database's own <c>AutoCreate</c> before a row is read. Every caller of the three
/// was a read or a navigation path on a timer, so each of them was a studio that created
/// <c>mt_events</c>, <c>mt_streams</c>, <c>mt_events_sequence</c> and <c>mt_event_progression</c> because
/// somebody opened a tab, and applied any pending event-store change on a database that already had
/// them.
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
/// call them. That stripping is what makes the test capable of being silently vacuous, so
/// <see cref="The_scanner_finds_a_real_call_and_ignores_one_in_a_comment_or_a_string"/> checks it against
/// known-good and known-bad inputs first.
/// </para>
/// </remarks>
public class NoSchemaBuildingCallTests
{
    /// <summary>
    /// The three event-store reads that migrate, as <em>calls</em>: a name followed by an argument list.
    /// </summary>
    /// <remarks>
    /// Matching the call shape rather than the bare name is deliberate. A <c>&lt;see cref&gt;</c> or a
    /// sentence naming the member is exactly what the doc comments have to be able to say, and the
    /// stripping pass already removes those; requiring the parenthesis is the second line of defence, and
    /// it is also what keeps the rule readable - what is forbidden is calling them, not mentioning them.
    /// </remarks>
    private static readonly Regex SchemaBuildingCall = new(
        @"\b(FetchHighestEventSequenceNumber|AllProjectionProgress|FetchEventStoreStatistics)\s*\(",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    [Fact]
    public void No_source_file_calls_a_Marten_read_that_migrates_the_event_store()
    {
        var files = SourceScanner.ShippedSourceFiles();

        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        var offenders = files
            .Where(x => SchemaBuildingCall.IsMatch(SourceScanner.StripCommentsAndStrings(File.ReadAllText(x))))
            .Select(SourceScanner.Relative)
            .Order(StringComparer.Ordinal)
            .ToArray();

        offenders.Should().BeEmpty(
            "hard rule 14: these three Marten reads each begin with EnsureStorageExistsAsync(typeof(IEvent)), " +
            "so a page that calls one creates or alters the event store. Read them through " +
            "ProjectionProgressQueries instead. Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>The anti-vacuity check: the scanner has to be able to fail.</summary>
    [Theory]
    [InlineData("var x = await database.FetchHighestEventSequenceNumber(token);", true)]
    [InlineData("await resolved.Database.AllProjectionProgress(cancellationToken);", true)]
    [InlineData("_ = await store.Advanced.AllProjectionProgress(tenantId, token);", true)]
    [InlineData("await database.FetchEventStoreStatistics(token);", true)]
    [InlineData("await database\n    .FetchHighestEventSequenceNumber (token);", true)]
    [InlineData("/// <c>IMartenDatabase.FetchHighestEventSequenceNumber</c> migrates before it reads.", false)]
    [InlineData("// AllProjectionProgress(tenantId, ct) is not a filter: it resolves the database.", false)]
    [InlineData("/* FetchEventStoreStatistics(token) */", false)]
    [InlineData("var title = \"IMartenDatabase.FetchHighestEventSequenceNumber\";", false)]
    [InlineData("@* the feed never calls FetchHighestEventSequenceNumber(token) *@", false)]
    [InlineData("var highWater = await ReadHighWaterMarkAsync(resolved, connection, token);", false)]
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
}
