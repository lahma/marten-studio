using DataStudio.Marten.Tests.Conventions;
using DataStudio.Postgres.Services.Postgres;

namespace DataStudio.Postgres.Tests.Conventions;

/// <summary>
/// Every sentence the Postgres module's words hold, in one baseline (v2 step 6), so that a change of wording is read as a diff.
/// <see cref="PostgresStudioWordsTests" /> holds the other half: that none of them names Marten's configuration.
/// </summary>
public class WordsTests
{
    [Fact]
    public Task The_Postgres_modules_words_are_pinned() =>
        Verify(WordsSnapshot.OfStudioText(PostgresStudioText.Instance, "PostgresOptions.ReadOnly"), extension: "txt")
            .UseDirectory("../Verify")
            .UseFileName("WordsTests_DataStudioPostgres");
}
