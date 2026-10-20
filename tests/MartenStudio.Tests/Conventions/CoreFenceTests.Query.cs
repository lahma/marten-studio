namespace MartenStudio.Tests.Conventions;

public partial class CoreFenceTests
{
    /// <summary>
    /// PS-1d's shell and console files - the SQL console and its panel, the shared root and query pages, the query
    /// editor, grid, stats, examples and saved queries, Activity, the live-update helpers and the indicator service -
    /// name no Marten-family type and no type MartenStudio keeps.
    /// </summary>
    [Fact]
    public void Query() => AssertFenced(CoreManifest.Of(CoreManifest.Packet.PS1d).Where(static x => !IsRelationships(x)));

    /// <summary>
    /// PS-1d's Relationships files - the plain foreign-key graph, its layout, its shared reads and its screens - name
    /// no Marten-family type and no type MartenStudio keeps. The document half is MartenStudio's node source.
    /// </summary>
    [Fact]
    public void Relationships() => AssertFenced(CoreManifest.Of(CoreManifest.Packet.PS1d).Where(IsRelationships));

    /// <summary>Whether a PS-1d row is one of the Relationships screen's files rather than the shell's or the console's.</summary>
    private static bool IsRelationships(CoreManifest.Entry row) =>
        row.Path.Contains("Relationship", StringComparison.Ordinal) || row.Path.Contains("Graph", StringComparison.Ordinal);

    /// <summary>
    /// <see cref="AssertFenced(CoreManifest.Packet)" /> over a subset of one packet's rows, so one packet's areas fence
    /// separately and a failure names its area.
    /// </summary>
    private static void AssertFenced(IEnumerable<CoreManifest.Entry> subset)
    {
        IReadOnlyList<CoreManifest.Entry> rows = [.. subset];
        rows.Should().NotBeEmpty("a fence over nothing passes for the wrong reason");

        StayBehindNames stayBehind = StayBehind.Value;
        List<string> problems = [];

        foreach (CoreManifest.Entry row in rows)
        {
            string source = File.ReadAllText(RepositoryRoot.Combine(row.RepositoryPath.Split('/')));

            SourceScanner.StripCommentsAndStrings(source, out bool unterminated);
            if (unterminated)
            {
                problems.Add(row.RepositoryPath + ": the scanner could not follow the file to its end");
            }

            bool razor = row.Path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);
            foreach (string lean in Leans(source, stayBehind, razor))
            {
                problems.Add(row.RepositoryPath + ": " + lean);
            }
        }

        problems.Should().BeEmpty(
            "a file bound for the shared assembly names no Marten, JasperFx or Weasel type and no type MartenStudio keeps:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }
}
