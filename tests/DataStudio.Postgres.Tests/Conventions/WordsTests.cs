using DataStudio.Marten.Tests.Conventions;
using DataStudio.Postgres.Services.Postgres;

namespace DataStudio.Postgres.Tests.Conventions;

/// <summary>
/// Every sentence the Postgres module's words hold, in one baseline (v2 step 6), so that a change of wording is read as a diff.
/// <see cref="PostgresWordsTests" /> holds the other half: that none of them names Marten's configuration.
/// </summary>
public class WordsTests
{
    [Fact]
    public Task The_Postgres_modules_words_are_pinned() =>
        Verify(WordsSnapshot.OfStudioText(PostgresText.Instance, "PostgresOptions.ReadOnly"), extension: "txt")
            .UseDirectory("../Verify")
            .UseFileName("WordsTests_DataStudioPostgres");

    /// <summary>
    /// The engine's own words, for a host with <c>AddPostgresDiagnostics()</c> and no studio: the Postgres module's, over
    /// <c>PostgresDiagnosticsOptions</c>, with no visitor to grant a capability to. A member that only a visitor reads (a page's refusal) is
    /// still written, in the options type the engine has; <see cref="PostgresWordsTests" /> holds that no sentence a check says names an
    /// option that type lacks.
    /// </summary>
    [Fact]
    public Task The_engines_own_words_are_pinned() =>
        Verify(WordsSnapshot.OfStudioText(PostgresText.DiagnosticsInstance, "PostgresDiagnosticsOptions.ReadOnly"), extension: "txt")
            .UseDirectory("../Verify")
            .UseFileName("WordsTests_DataStudioPostgresDiagnostics");
}
