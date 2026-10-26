using PostgresStudio.Internal.Sql;

namespace PostgresStudio.Tests;

/// <summary>
/// Every statement the console offers as an example is one its own guard sends, and none spells a function that does not
/// exist in the form it is written (PS-3a's <c>pg_catalog.current_user</c>, which is a keyword and not a function).
/// PS-5 runs them against a real server.
/// </summary>
public class ConsoleExamplesTests
{
    public static TheoryData<string> Titles() => [.. ConsoleExamples.All.Select(static x => x.Title)];

    [Theory]
    [MemberData(nameof(Titles))]
    public void Every_example_is_one_statement_the_console_guard_allows(string title)
    {
        string snippet = ConsoleExamples.All.Single(x => x.Title == title).Snippet;

        SqlGuardResult result = ReadOnlySqlGuard.Check(snippet);

        result.Allowed.Should().BeTrue(result.Message);
    }

    [Fact]
    public void There_are_examples_and_none_qualifies_a_keyword_as_a_function()
    {
        ConsoleExamples.All.Should().NotBeEmpty();

        foreach ((string _, string snippet, string? alias) in ConsoleExamples.All)
        {
            alias.Should().BeNull("no example is about a document type");
            snippet.Should().NotContainAny("pg_catalog.current_user", "pg_catalog.session_user", "pg_catalog.current_schema ");
        }
    }
}
