using Marten;
using Marten.Schema;

using MartenStudio.Services.Documents;

using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// M5: a save whose JSON carries another id than the document's own is refused, at every Marten.
/// </summary>
/// <remarks>
/// <para>
/// <b>What it was.</b> A save deserializes the edit into the CLR type and hands the instance to Marten, which writes it
/// under the id <em>in the instance</em>. The studio checked the version of the row it had opened and never compared that
/// row's id with the edit's, so an edit that changed <c>Id</c> on a type without Marten's optimistic concurrency passed the
/// check and overwrote another document - or created one - while the document on screen stayed as it was. Proved here
/// against the floor before the fix (this class's first test failed with the other document overwritten), and found by
/// reading the save path while laying Marten 9.53's own preview beside it, which refuses the same edit.
/// </para>
/// <para>
/// The types are this class's own: a Guid id with the default metadata (an <c>mt_version</c> column the studio checks, and
/// no optimistic concurrency of Marten's, so the write is a plain upsert), and a string id.
/// </para>
/// </remarks>
public class DocumentIdChangeLiveTests(PostgresFixture postgres) : DocumentWriteFixture(postgres)
{
    private const string NoteAlias = "id_change_note";
    private const string NoteTable = "mt_doc_id_change_note";
    private const string KeyedAlias = "id_change_keyed";
    private const string KeyedTable = "mt_doc_id_change_keyed";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    protected override bool SeedSampleData => false;

    protected override void ConfigureExtras(StoreOptions opts)
    {
        opts.Schema.For<IdChangeNote>();
        opts.Schema.For<IdChangeKeyed>();
    }

    [PostgresFact]
    public async Task An_edit_that_changes_the_id_is_refused_and_overwrites_no_other_document()
    {
        Users.SignIn("ops");
        Guid opened = Guid.NewGuid();
        Guid other = Guid.NewGuid();
        await StoreAsync(new IdChangeNote { Id = opened, Text = "opened" }, new IdChangeNote { Id = other, Text = "other" });

        string storedOpened = (await StoredJsonAsync(NoteTable, opened))!;
        string storedOther = (await StoredJsonAsync(NoteTable, other))!;
        string edited = storedOpened
            .Replace(opened.ToString(), other.ToString(), StringComparison.OrdinalIgnoreCase)
            .Replace("\"opened\"", "\"hijacked\"", StringComparison.Ordinal);

        IDocumentWriteService service = CreateService();
        WritePreview probe = await service.PreviewAsync(ScopeFor(), NoteAlias, opened.ToString(), storedOpened, Token);
        probe.Status.Should().Be(WritePreviewStatus.Ready, "the document as stored is editable: {0}", probe.Reason);

        WriteResult result = await service.SaveAsync(
            ScopeFor(), NoteAlias, opened.ToString(), edited, probe.CurrentToken, acknowledgeDrops: true, Token);

        (await StoredJsonAsync(NoteTable, other)).Should().Be(storedOther,
            "a save of one document must never write another one - the id in the edit is not where the save goes");
        (await StoredJsonAsync(NoteTable, opened)).Should().Be(storedOpened, "a refused save changes nothing");

        result.IsSaved.Should().BeFalse();
        result.Reason.Should().Contain("changes the document's id")
            .And.Contain(opened.ToString())
            .And.Contain(other.ToString());

        StudioActionLogEntry entry = Ring.GetLatest().Should().ContainSingle(x => x.Action == "EditDocument").Subject;
        entry.Succeeded.Should().BeFalse("a refused save is audited as one");
        entry.Message.Should().Be("The edit changes the document's id, so it was refused.",
            "the audit names the refusal and not the ids a visitor typed");
    }

    [PostgresFact]
    public async Task The_preview_refuses_an_edit_that_changes_the_id_before_anything_is_saved()
    {
        Guid opened = Guid.NewGuid();
        Guid elsewhere = Guid.NewGuid();
        await StoreAsync(new IdChangeNote { Id = opened, Text = "opened" });

        string stored = (await StoredJsonAsync(NoteTable, opened))!;
        string edited = stored.Replace(opened.ToString(), elsewhere.ToString(), StringComparison.OrdinalIgnoreCase);

        WritePreview preview = await CreateService().PreviewAsync(ScopeFor(), NoteAlias, opened.ToString(), edited, Token);

        preview.Status.Should().Be(WritePreviewStatus.Refused);
        preview.TypeIsConstructible.Should().BeTrue("putting the id back is an edit the person can make");
        preview.Reason.Should().Contain("changes the document's id").And.Contain("Change the id back");
        preview.MartenCheck.Should().BeNull("Marten's own preview is laid beside a ready preview only");

        (await RowExistsAsync(NoteTable, elsewhere)).Should().BeFalse("nothing was written, under either id");
    }

    /// <summary>A string id, changed, is refused the same way - compared as the id member's own value.</summary>
    [PostgresFact]
    public async Task A_changed_string_id_is_refused()
    {
        await StoreAsync(new IdChangeKeyed { Id = "orders/1", Text = "first" });

        string stored = (await StoredJsonAsync(KeyedTable, "orders/1"))!;
        string edited = stored.Replace("orders/1", "orders/2", StringComparison.Ordinal);

        WritePreview preview = await CreateService().PreviewAsync(ScopeFor(), KeyedAlias, "orders/1", edited, Token);

        preview.Status.Should().Be(WritePreviewStatus.Refused);
        preview.Reason.Should().Contain("'orders/1'").And.Contain("'orders/2'");
    }

    /// <summary>
    /// Anti-vacuity: an edit that keeps the id saves, for both id types, so the check refuses a changed id and not every
    /// edit.
    /// </summary>
    [PostgresFact]
    public async Task An_edit_that_keeps_the_id_still_saves()
    {
        Users.SignIn("ops");
        Guid id = Guid.NewGuid();
        await StoreAsync(new IdChangeNote { Id = id, Text = "before" });
        await StoreAsync(new IdChangeKeyed { Id = "orders/7", Text = "before" });

        IDocumentWriteService service = CreateService();

        string note = (await StoredJsonAsync(NoteTable, id))!;
        WritePreview notePreview = await service.PreviewAsync(ScopeFor(), NoteAlias, id.ToString(), note, Token);
        WriteResult noteSaved = await service.SaveAsync(
            ScopeFor(), NoteAlias, id.ToString(), note.Replace("before", "after", StringComparison.Ordinal),
            notePreview.CurrentToken, acknowledgeDrops: false, Token);

        noteSaved.IsSaved.Should().BeTrue(noteSaved.Reason);
        (await StoredJsonAsync(NoteTable, id)).Should().Contain("after");

        // The id in the URL in another case than the JSON's: a Guid is a value, not its text.
        string upper = id.ToString().ToUpperInvariant();
        WritePreview upperPreview = await service.PreviewAsync(ScopeFor(), NoteAlias, upper, note, Token);
        upperPreview.Status.Should().Be(WritePreviewStatus.Ready, upperPreview.Reason);

        string keyed = (await StoredJsonAsync(KeyedTable, "orders/7"))!;
        WritePreview keyedPreview = await service.PreviewAsync(ScopeFor(), KeyedAlias, "orders/7", keyed, Token);
        WriteResult keyedSaved = await service.SaveAsync(
            ScopeFor(), KeyedAlias, "orders/7", keyed.Replace("before", "after", StringComparison.Ordinal),
            keyedPreview.CurrentToken, acknowledgeDrops: false, Token);

        keyedSaved.IsSaved.Should().BeTrue(keyedSaved.Reason);
        (await StoredJsonAsync(KeyedTable, "orders/7")).Should().Contain("after");
    }
}

/// <summary>A Guid-keyed document with Marten's default metadata and no optimistic concurrency of its own.</summary>
[DocumentAlias("id_change_note")]
public sealed class IdChangeNote
{
    /// <summary>The identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Something to edit.</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>A string-keyed document, whose id may carry a slash (D9).</summary>
[DocumentAlias("id_change_keyed")]
public sealed class IdChangeKeyed
{
    /// <summary>The identity, assigned by the caller.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Something to edit.</summary>
    public string Text { get; set; } = string.Empty;
}
