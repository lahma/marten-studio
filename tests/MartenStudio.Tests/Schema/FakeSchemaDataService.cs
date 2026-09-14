using MartenStudio.Services;
using MartenStudio.Services.Schema;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// What the Schema page is given, said outright by a test.
/// </summary>
/// <remarks>
/// Hand-written rather than a mocking framework's call matcher (AGENTS.md's package budget says so): the
/// data seam is our own interface, the whole fake fits on a screen, and what it records is exactly the
/// two things the page tests are about - whether the page called the service at all, and what it did
/// with the answer.
/// </remarks>
internal sealed class FakeSchemaDataService : ISchemaDataService
{
    /// <summary>What <see cref="CheckAsync" /> answers.</summary>
    public SchemaCheck Check { get; set; } = SchemaCheck.Matches(["studio"], "Marten reports a match.");

    /// <summary>What <see cref="PreviewAsync" /> answers.</summary>
    public MigrationPreview Preview { get; set; } = MigrationPreview.None;

    /// <summary>What <see cref="TablesAsync" /> answers.</summary>
    public SchemaTables Tables { get; set; } = SchemaTables.Empty;

    /// <summary>What <see cref="IndexesAsync" /> answers.</summary>
    public SchemaIndexes Indexes { get; set; } = SchemaIndexes.Empty;

    /// <summary>What <see cref="FunctionsAsync" /> answers.</summary>
    public SchemaFunctions Functions { get; set; } = SchemaFunctions.Empty;

    /// <summary>What <see cref="DdlAsync" /> answers.</summary>
    public DdlScript Ddl { get; set; } = DdlScript.Empty;

    /// <summary>The database identity the apply dialog makes a person type.</summary>
    public string DatabaseIdentity { get; set; } = "localhost.marten";

    /// <summary>What <see cref="ApplyAsync" /> throws, when a test is about a refusal.</summary>
    public Exception? ApplyFailure { get; set; }

    /// <summary>What <see cref="ApplyAsync" /> answers when it does not throw.</summary>
    public SchemaApplyResult ApplyResult { get; set; } =
        new(Succeeded: true, "Update", 2, "Applied to localhost.marten. Weasel reported Update.", null);

    /// <summary>How many times each method was called.</summary>
    public int Checks { get; private set; }

    /// <inheritdoc cref="Checks" />
    public int Previews { get; private set; }

    /// <inheritdoc cref="Checks" />
    public int Applies { get; private set; }

    /// <summary>
    /// How many times the DDL script was asked for.
    /// </summary>
    /// <remarks>
    /// Counted because producing the script walks <c>AllObjects()</c>, which can create Marten's own HiLo
    /// objects - so "nobody asked for it on navigation" is a fact a test has to be able to assert.
    /// </remarks>
    public int Ddls { get; private set; }

    /// <summary>What the last apply was given as the typed confirmation.</summary>
    public string? LastConfirmation { get; private set; }

    public Task<SchemaCheck> CheckAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        Checks++;
        return Task.FromResult(Check);
    }

    public Task<MigrationPreview> PreviewAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        Previews++;
        return Task.FromResult(Preview);
    }

    public Task<SchemaApplyResult> ApplyAsync(
        StudioScope scope,
        string confirmation,
        CancellationToken cancellationToken = default)
    {
        Applies++;
        LastConfirmation = confirmation;

        return ApplyFailure is null
            ? Task.FromResult(ApplyResult)
            : Task.FromException<SchemaApplyResult>(ApplyFailure);
    }

    public Task<SchemaTables> TablesAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(Tables);

    public Task<SchemaIndexes> IndexesAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(Indexes);

    public Task<SchemaFunctions> FunctionsAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(Functions);

    public Task<DdlScript> DdlAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        Ddls++;
        return Task.FromResult(Ddl);
    }

    public Task<string> DatabaseIdentityAsync(StudioScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(DatabaseIdentity);
}
