using System.Text.Json.Nodes;

using Marten;

using MartenStudio.Services.Documents;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// Numeric revisions: the other thing Marten keeps in <c>mt_version</c>.
/// </summary>
/// <remarks>
/// <para>
/// A type configured with <c>UseNumericRevisions(true)</c> — or, as here, one that declares it by
/// implementing <c>JasperFx.IRevisioned</c> — gets an <c>integer</c> <c>mt_version</c> instead of a
/// <c>uuid</c> one, and is written with <c>UpdateRevision&lt;T&gt;(document, next)</c> rather than with
/// <c>UpdateExpectedVersion&lt;T&gt;</c>. Both halves are easy to get wrong in a way nothing else
/// notices: read the column as the mapping's default type and the token is empty, so every save is
/// refused; write it with the wrong call and either every save fails or none of them is checked.
/// </para>
/// <para>
/// This class is also where <see cref="WritePreview" />'s remark about version members is pinned: Marten
/// writes the <em>new</em> revision into the document's own <c>Version</c> member before serializing it,
/// so the stored JSON and <see cref="WritePreview.RoundTrippedJson" /> disagree on exactly that one
/// value and on nothing else.
/// </para>
/// </remarks>
public class DocumentWriteRevisionLiveTests(PostgresFixture postgres) : DocumentWriteFixture(postgres)
{
    private const string TicketTable = "mt_doc_ticket";
    private const string TicketAlias = "ticket";

    private const string ManifestTable = "mt_doc_manifest";
    private const string ManifestAlias = "manifest";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    protected override bool SeedSampleData => false;

    /// <inheritdoc />
    protected override void ConfigureExtras(StoreOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        opts.Schema.For<Ticket>().UseNumericRevisions(true);
        opts.Schema.For<Manifest>();
    }

    /// <summary>A save against a numeric-revision type works, and moves the revision on by one.</summary>
    [PostgresFact]
    public async Task An_edit_to_a_revisioned_document_is_saved_and_the_revision_moves_on()
    {
        Users.SignIn("ops");
        var service = CreateService();

        var id = Guid.NewGuid();
        await StoreAsync(new Ticket { Id = id, Title = "Printer is on fire" });

        var revisionBefore = await RevisionAsync(id);
        revisionBefore.Should().Be(1, "Marten writes the first revision on insert");

        var stored = await StoredJsonAsync(TicketTable, id);
        var edited = WithProperty(stored!, "Title", "Printer is still on fire");

        var preview = await service.PreviewAsync(ScopeFor(), TicketAlias, id.ToString(), edited, Token);

        preview.Status.Should().Be(WritePreviewStatus.Ready);
        preview.CurrentToken.Revision.Should().Be(1, "the token is read for what the column holds, not for what a default mapping would hold");
        preview.CurrentToken.Version.Should().BeNull();
        preview.HasDrops.Should().BeFalse();

        var saved = await service.SaveAsync(
            ScopeFor(), TicketAlias, id.ToString(), edited, preview.CurrentToken, acknowledgeDrops: false, Token);

        saved.Status.Should().Be(WriteStatus.Saved);
        saved.CurrentToken.Revision.Should().Be(2);

        (await RevisionAsync(id)).Should().Be(2);

        var after = JsonNode.Parse((await StoredJsonAsync(TicketTable, id))!)!;
        after["Title"]!.GetValue<string>().Should().Be("Printer is still on fire");
    }

    /// <summary>
    /// What a version member does to the round-trip preview, pinned rather than assumed.
    /// </summary>
    /// <remarks>
    /// <see cref="WritePreview" /> says in its own remarks that a version member and
    /// <see cref="WritePreview.RoundTrippedJson" /> disagree after a save. This is where that claim is
    /// checked against Marten 9.35 rather than repeated: it runs one save of a
    /// <see cref="JasperFx.Metadata.IVersioned" /> document and one of an
    /// <see cref="JasperFx.IRevisioned" /> one, and asserts the relationship between the stored JSON, the
    /// version column and what the preview said would be stored, for both.
    /// </remarks>
    [PostgresFact]
    public async Task A_version_member_travels_in_the_json_and_lags_the_column_by_one_write()
    {
        var service = CreateService();

        var id = Guid.NewGuid();
        await StoreAsync(new Manifest { Id = id, Title = "Cargo" });

        // --- what the host's own Marten round trip does -----------------------------------------------
        // Marten fills the member in when it reads a document and serializes whatever the member then
        // holds, so `data` ends up carrying the version the row had *before* this write.
        var columnBeforeHostSave = await GuidVersionAsync(id);

        await using (var session = Store.LightweightSession())
        {
            var loaded = await session.LoadAsync<Manifest>(id, Token);
            loaded!.Title = "Ballast";
            session.Store(loaded);
            await session.SaveChangesAsync(Token);
        }

        var afterHostSave = JsonNode.Parse((await StoredJsonAsync(ManifestTable, id))!)!;
        var columnAfterHostSave = await GuidVersionAsync(id);

        columnAfterHostSave.Should().NotBe(columnBeforeHostSave, "the column moves on every write");
        afterHostSave["Version"]!.GetValue<Guid>().Should().Be(
            columnBeforeHostSave,
            "Marten populates the member when it reads the document and does not re-stamp it before " +
            "serializing, so the version inside `data` is one write behind mt_version");

        // --- what the studio's save does --------------------------------------------------------------
        // The studio never reads through Marten, so the member is whatever the edited JSON carried. It is
        // therefore a member a person can edit, and the concurrency check does not consult it: the column
        // is the authority, and this is the one member where `data` and mt_version routinely disagree.
        var stored = await StoredJsonAsync(ManifestTable, id);

        var preview = await service.PreviewAsync(
            ScopeFor(), ManifestAlias, id.ToString(), WithProperty(stored!, "Title", "Ballast II"), Token);

        preview.Status.Should().Be(WritePreviewStatus.Ready);
        preview.HasDrops.Should().BeFalse();
        JsonNode.Parse(preview.RoundTrippedJson!)!["Version"]!.GetValue<Guid>().Should().Be(
            columnBeforeHostSave, "the round trip carries the member through untouched");

        var saved = await service.SaveAsync(
            ScopeFor(),
            ManifestAlias,
            id.ToString(),
            WithProperty(stored!, "Title", "Ballast II"),
            preview.CurrentToken,
            acknowledgeDrops: false,
            Token);

        saved.Status.Should().Be(WriteStatus.Saved);

        var afterStudioSave = JsonNode.Parse((await StoredJsonAsync(ManifestTable, id))!)!;

        afterStudioSave["Version"]!.GetValue<Guid>().Should().Be(
            columnBeforeHostSave,
            "the studio stores exactly what the round trip produced, version member included");
        (await GuidVersionAsync(id)).Should().Be(
            saved.CurrentToken.Version!.Value, "mt_version is the one the concurrency check uses, and it moved");
    }

    /// <summary>The numeric revision is the same story with an integer in it.</summary>
    [PostgresFact]
    public async Task A_revision_member_also_travels_in_the_json_and_does_not_move_with_the_column()
    {
        var service = CreateService();

        var id = Guid.NewGuid();
        await StoreAsync(new Ticket { Id = id, Title = "Lift is stuck" });

        var stored = await StoredJsonAsync(TicketTable, id);
        var edited = WithProperty(stored!, "Title", "Lift is fine");

        var preview = await service.PreviewAsync(ScopeFor(), TicketAlias, id.ToString(), edited, Token);

        var saved = await service.SaveAsync(
            ScopeFor(), TicketAlias, id.ToString(), edited, preview.CurrentToken, acknowledgeDrops: false, Token);

        saved.Status.Should().Be(WriteStatus.Saved);

        var storedVersion = JsonNode.Parse(stored!)!["Version"]!.GetValue<int>();

        JsonNode.Parse((await StoredJsonAsync(TicketTable, id))!)!["Version"]!.GetValue<int>().Should().Be(
            storedVersion,
            "the member is carried through the round trip unchanged; only mt_version is authoritative");

        (await RevisionAsync(id)).Should().Be(2, "the column moved on even though the member did not");
    }

    private async Task<Guid> GuidVersionAsync(Guid id) =>
        (Guid) (await ScalarAsync($"select mt_version from \"{Schema}\".{ManifestTable} where id = '{id}'"))!;

    /// <summary>
    /// A revision that is behind the row is a conflict and not a write, exactly as a stale Guid version
    /// is.
    /// </summary>
    [PostgresFact]
    public async Task A_stale_revision_is_a_conflict_and_the_document_is_left_alone()
    {
        var service = CreateService();

        var id = Guid.NewGuid();
        await StoreAsync(new Ticket { Id = id, Title = "Mine" });

        var stored = await StoredJsonAsync(TicketTable, id);
        var stale = DocumentConcurrencyToken.ForRevision(1);

        var theirs = await service.SaveAsync(
            ScopeFor(), TicketAlias, id.ToString(), WithProperty(stored!, "Title", "Theirs"), stale,
            acknowledgeDrops: false, Token);

        theirs.Status.Should().Be(WriteStatus.Saved);
        (await RevisionAsync(id)).Should().Be(2);

        var conflict = await service.SaveAsync(
            ScopeFor(), TicketAlias, id.ToString(), WithProperty(stored!, "Title", "Mine again"), stale,
            acknowledgeDrops: false, Token);

        conflict.Status.Should().Be(WriteStatus.Conflict);
        conflict.CurrentToken.Revision.Should().Be(2);

        JsonNode.Parse((await StoredJsonAsync(TicketTable, id))!)!["Title"]!.GetValue<string>().Should()
            .Be("Theirs", "the loser of the race changes nothing");
        (await RevisionAsync(id)).Should().Be(2);
    }

    private async Task<int?> RevisionAsync(Guid id) =>
        (int?) await ScalarAsync($"select mt_version from \"{Schema}\".{TicketTable} where id = '{id}'");

    private static string WithProperty(string json, string name, JsonNode? value)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node[name] = value;
        return node.ToJsonString();
    }
}
