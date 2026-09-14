using MartenStudio.Services;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Json;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// What the documents pages are answered with when they write, said outright by a test.
/// </summary>
/// <remarks>
/// Hand-written rather than a mocking framework (AGENTS.md package budget). It records every call, which
/// is the half that matters: the questions these tests are about are "did the page pass the token the
/// preview gave it", "did it acknowledge the drops only after the dialog", and "did it send the ids it
/// showed as selected" — none of which is visible in the markup.
/// </remarks>
internal sealed class FakeDocumentWriteService : IDocumentWriteService
{
    /// <summary>One recorded call.</summary>
    /// <param name="Method">Which method was called.</param>
    /// <param name="Alias">The alias it was given.</param>
    /// <param name="Id">The id it was given, or the id count for a bulk delete.</param>
    /// <param name="Json">The edited JSON, for a preview or a save.</param>
    /// <param name="ExpectedToken">The token a save carried.</param>
    /// <param name="AcknowledgeDrops">Whether a save acknowledged the round-trip loss.</param>
    /// <param name="Ids">The ids a bulk delete was given.</param>
    internal sealed record Call(
        string Method,
        string Alias,
        string? Id,
        string? Json = null,
        DocumentConcurrencyToken ExpectedToken = default,
        bool AcknowledgeDrops = false,
        IReadOnlyList<string>? Ids = null);

    /// <summary>Every call the pages made, in order.</summary>
    public List<Call> Calls { get; } = [];

    /// <summary>What the next preview answers. Queued answers are used first, oldest first.</summary>
    public Queue<WritePreview> Previews { get; } = new();

    /// <summary>What a preview answers once the queue is empty.</summary>
    public WritePreview Preview { get; set; } = Ready();

    /// <summary>What the next save answers. Queued answers are used first.</summary>
    public Queue<WriteResult> Saves { get; } = new();

    /// <summary>What a save answers once the queue is empty.</summary>
    public WriteResult Save { get; set; } = WriteResult.Saved(Token(2), Ready());

    /// <summary>What a delete answers.</summary>
    public DeleteResult Delete { get; set; } = DeleteResult.SoftDeleted("customer", "id");

    /// <summary>What an undelete answers.</summary>
    public DeleteResult Undelete { get; set; } = DeleteResult.Undeleted("customer", "id");

    /// <summary>What a bulk delete answers.</summary>
    public BulkDeleteResult Bulk { get; set; } = BulkDeleteResult.Completed([]);

    /// <summary>What every method throws, when a test is about the refusal frame.</summary>
    public Exception? Failure { get; set; }

    /// <summary>A version token, spelled the way a test can compare it.</summary>
    public static DocumentConcurrencyToken Token(int n) =>
        DocumentConcurrencyToken.ForVersion(new Guid($"00000000-0000-0000-0000-00000000000{n}"));

    /// <summary>A ready preview whose round trip drops <paramref name="dropped" /> members.</summary>
    /// <remarks>
    /// The dropped members are really in the edited document and really absent from what the round trip
    /// produces, and both diffs come from the real <see cref="RoundTripDiffer" />. A fake that handed the
    /// page a diff result with the right <em>counts</em> and JSON that did not match it would let the
    /// dialog render an empty diff and still pass, which is the one thing these tests are about.
    /// </remarks>
    public static WritePreview Ready(
        string stored = """{"Name":"Before"}""",
        string edited = """{"Name":"After"}""",
        params string[] dropped)
    {
        string editedWithExtras = WithExtras(edited, dropped);

        return WritePreview.Ready(
            "customer",
            "id",
            "Customer",
            stored,
            editedWithExtras,
            edited,
            RoundTripDiffer.Diff(editedWithExtras, edited),
            RoundTripDiffer.Diff(stored, editedWithExtras),
            Token(1));
    }

    /// <summary>Adds members to a flat JSON object, which is what the round trip will then eat.</summary>
    private static string WithExtras(string json, string[] names)
    {
        if (names.Length == 0)
        {
            return json;
        }

        string extras = string.Join(',', names.Select(static name => $"\"{name}\":\"legacy\""));
        return json.TrimEnd().TrimEnd('}') + ',' + extras + '}';
    }

    public Task<WritePreview> PreviewAsync(
        StudioScope scope,
        string alias,
        string id,
        string editedJson,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(new Call(nameof(PreviewAsync), alias, id, editedJson));

        if (Failure is not null)
        {
            return Task.FromException<WritePreview>(Failure);
        }

        return Task.FromResult(Previews.Count > 0 ? Previews.Dequeue() : Preview);
    }

    public Task<WriteResult> SaveAsync(
        StudioScope scope,
        string alias,
        string id,
        string editedJson,
        DocumentConcurrencyToken expectedToken,
        bool acknowledgeDrops,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(new Call(nameof(SaveAsync), alias, id, editedJson, expectedToken, acknowledgeDrops));

        if (Failure is not null)
        {
            return Task.FromException<WriteResult>(Failure);
        }

        return Task.FromResult(Saves.Count > 0 ? Saves.Dequeue() : Save);
    }

    public Task<DeleteResult> DeleteAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(new Call(nameof(DeleteAsync), alias, id));

        return Failure is null
            ? Task.FromResult(Delete)
            : Task.FromException<DeleteResult>(Failure);
    }

    public Task<DeleteResult> UndeleteAsync(
        StudioScope scope,
        string alias,
        string id,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(new Call(nameof(UndeleteAsync), alias, id));

        return Failure is null
            ? Task.FromResult(Undelete)
            : Task.FromException<DeleteResult>(Failure);
    }

    public Task<BulkDeleteResult> BulkDeleteAsync(
        StudioScope scope,
        string alias,
        IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(new Call(nameof(BulkDeleteAsync), alias, null, Ids: [.. ids]));

        return Failure is null
            ? Task.FromResult(Bulk)
            : Task.FromException<BulkDeleteResult>(Failure);
    }

    /// <summary>Every call to one method, in order.</summary>
    public List<Call> CallsTo(string method) =>
        [.. Calls.Where(x => string.Equals(x.Method, method, StringComparison.Ordinal))];
}
