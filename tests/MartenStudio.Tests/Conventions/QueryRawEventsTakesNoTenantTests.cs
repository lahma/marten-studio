using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Every source call of <c>QueryRawEvents</c> in <c>src/</c> has an empty argument list (1.1).
/// </summary>
/// <remarks>
/// <para>
/// At Marten 9.57.0 <c>QueryRawEvents(tenantId)</c> for a tenant other than the session's returns
/// <c>_store.QuerySession(tenantId).Events.QueryAllRawEvents()</c>: a session hard rule 15 forbids, which ignores the
/// database the studio resolved, throws <c>DefaultTenantUsageDisabledException</c> on a multi-tenanted-databases store, and
/// is never disposed because "an <c>IQueryable</c> exposes no disposal". With no argument it is the session's own
/// <c>Query&lt;IEvent&gt;()</c>. The studio therefore never passes one, and tenancy is the session's own
/// (<c>SessionOptions.ForDatabase(tenant, database)</c>) plus Marten's tenant filter.
/// </para>
/// </remarks>
public class QueryRawEventsTakesNoTenantTests
{
    private static readonly Regex Call = new(@"\bQueryRawEvents\s*(?:<[^>()]*>)?\s*\(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    [Fact]
    public void Every_call_of_QueryRawEvents_in_src_passes_no_argument()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        string[] offenders = [.. files.SelectMany(OffendersIn).Order(StringComparer.Ordinal)];

        offenders.Should().BeEmpty(
            "QueryRawEvents(tenantId) opens an unpinned, undisposed QuerySession(tenantId); the studio passes no argument. Offenders: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void The_scan_sees_the_one_call_the_studio_makes()
    {
        string[] files = SourceScanner.ShippedSourceFiles();

        int calls = files
            .Where(static x => x.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Sum(static x => Call.Count(SourceScanner.StripCommentsAndStrings(File.ReadAllText(x))));

        calls.Should().Be(1, "LinqQueryComposer.ComposeEvents is the only caller; a scan that finds none proves nothing");
    }

    [Theory]
    [InlineData("var raw = ((IReadOnlyEventStore) session.Events).QueryRawEvents();", false)]
    [InlineData("var raw = ((IReadOnlyEventStore) session.Events).QueryRawEvents( );", false)]
    [InlineData("var raw = events.QueryRawEvents(\"b\");", true)]
    [InlineData("var raw = events.QueryRawEvents(tenant);", true)]
    [InlineData("var raw = events.QueryRawEvents(tenantId: null);", true)]
    [InlineData("var raw = events\n    .QueryRawEvents(\n        tenant);", true)]
    [InlineData("// events.QueryRawEvents(\"b\") is why the argument is never passed", false)]
    [InlineData("var why = \"QueryRawEvents(tenant) opens a session\";", false)]
    public void The_scan_catches_an_argument_and_ignores_prose(string source, bool offends)
    {
        Offends(source).Should().Be(offends);
    }

    private static IEnumerable<string> OffendersIn(string path)
    {
        if (Offends(File.ReadAllText(path)))
        {
            yield return SourceScanner.Relative(path);
        }
    }

    /// <summary>
    /// The call is found in the text with comments and strings blanked (so prose is not a call), and its argument list is
    /// read from the original at the same offsets - the pass keeps the length - because a string literal argument is
    /// exactly what the blanking would hide.
    /// </summary>
    private static bool Offends(string source)
    {
        string code = SourceScanner.StripCommentsAndStrings(source);

        foreach (Match match in Call.Matches(code))
        {
            int open = match.Index + match.Length;
            int close = code.IndexOf(')', open);

            if (close < 0 || source[open..close].Any(static x => !char.IsWhiteSpace(x)))
            {
                return true;
            }
        }

        return false;
    }
}
