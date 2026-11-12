using System.Text.RegularExpressions;

using MartenStudio.Tests.Conventions;

namespace KStudio.Tests.Components;

/// <summary>
/// What the related-records body must and must not do, read from its source because the thing proved is where a call is made, which a render cannot show:
/// what a record was found by is worked out once, when it arrives, and a render only reads it; and a change of cluster forgets what was read for the last one.
/// </summary>
public class KafkaRelatedBodyStructureTests
{
    private static string Source => File.ReadAllText(RepositoryRoot.Combine("src", "KStudio", "Components", "Pages", "KafkaRelatedBody.razor"));

    private static string Markup => Source[..Source.IndexOf("@code", StringComparison.Ordinal)];

    private static string Method(string name)
    {
        string source = Source;
        int start = source.IndexOf(name, StringComparison.Ordinal);
        start.Should().BeGreaterThan(0, name + " exists");
        Match next = Regex.Match(source[(start + name.Length)..], @"\n    (private|public|protected) ", RegexOptions.None, TimeSpan.FromSeconds(5));
        return next.Success ? source.Substring(start, name.Length + next.Index) : source[start..];
    }

    [Fact]
    public void The_markup_reads_what_a_record_was_found_by_and_never_works_it_out()
    {
        Markup.Should().Contain("MatchedByOf(match)");
        Markup.Should().NotContain("KafkaRelatedView.MatchedBy(", "a render must not run the extractor over every row");
        Markup.Should().NotContain("FieldsOf(");
        Method("private string MatchedByOf(").Should().NotContain("Extract", "it is a lookup");
    }

    [Fact]
    public void What_a_record_was_found_by_is_worked_out_once_in_the_batch_loop_and_cleared_with_the_matches()
    {
        Method("private async Task StartRunAsync(").Should().Contain("RecordMatchedBy(batch.NewMatches)").And.Contain("matchedBy.Clear()");
        Method("private void RecordMatchedBy(").Should().Contain("KafkaRelatedView.MatchedBy(");
    }

    [Fact]
    public void A_change_of_cluster_forgets_the_fields_and_what_was_found_by()
    {
        string reset = Method("private async Task ResetAndLoadAsync(");

        reset.Should().Contain("fieldsByTopic.Clear()").And.Contain("matchedBy.Clear()").And.Contain("matches.Clear()");
        Method("private Task OnClusterChangedAsync(").Should().Contain("ResetAndLoadAsync");
    }
}
