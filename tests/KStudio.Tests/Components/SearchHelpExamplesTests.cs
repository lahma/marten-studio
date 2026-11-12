using KStudio.Components.Pages;
using KStudio.Core.Predicates;

namespace KStudio.Tests.Components;

/// <summary>The syntax help's examples (D-UI decision 12): each one parses, so the help cannot drift from the grammar.</summary>
public class SearchHelpExamplesTests
{
    public static TheoryData<string> Examples => new(KafkaSearchBody.SyntaxExamples);

    [Theory]
    [MemberData(nameof(Examples))]
    public void Every_example_parses(string example)
    {
        PredicateParseResult parsed = PredicateParser.Parse(example);

        parsed.IsValid.Should().BeTrue(string.Join("; ", parsed.Errors.Select(static x => x.Message)));
    }

    [Fact]
    public void There_are_five_examples()
    {
        KafkaSearchBody.SyntaxExamples.Should().HaveCount(5);
    }
}
