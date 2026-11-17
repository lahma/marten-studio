using DataStudio.Marten.Services;
using DataStudio.Postgres.Services.Postgres;

namespace DataStudio.Marten.Tests.Conventions;

/// <summary>
/// Every sentence the Marten module's words hold, in one baseline (v2 step 6), so that a change of wording is read as a diff. The Postgres
/// module's sentences are pinned the same way in its own suite and Kafka's in its own; <c>WordsTests</c> is the one name all three share.
/// </summary>
public class WordsTests
{
    [Fact]
    public Task The_Marten_modules_words_are_pinned() =>
        Verify(WordsSnapshot.OfStudioText(MartenStudioText.Instance, "MartenOptions.ReadOnly"), extension: "txt")
            .UseDirectory("../Verify")
            .UseFileName("WordsTests_DataStudioMarten");

    /// <summary>The snapshot is the module's words and not a second copy of the Postgres ones.</summary>
    [Fact]
    public void The_snapshot_reads_the_modules_own_words()
    {
        string marten = WordsSnapshot.OfStudioText(MartenStudioText.Instance, "MartenOptions.ReadOnly");
        string postgres = WordsSnapshot.OfStudioText(PostgresStudioText.Instance, "PostgresOptions.ReadOnly");

        marten.Should().Contain("MartenOptions.Capabilities.BrowseDatabase").And.NotContain("PostgresOptions");
        postgres.Should().Contain("PostgresOptions.Capabilities.BrowseDatabase").And.NotContain("MartenOptions");
    }
}
