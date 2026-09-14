using System.Text.Json.Nodes;

using Marten;

using MartenStudio.Services.Documents;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// A subclass hierarchy, and a string id with a slash in it: the two shapes where writing a document
/// back can silently change what it is.
/// </summary>
/// <remarks>
/// <para>
/// <c>IReadOnlyStoreOptions.AllKnownDocumentTypes()</c> hands back root mappings only — a
/// <c>SubClassMapping</c> is not an <c>IDocumentType</c> — so the alias <c>asset</c> resolves to
/// <see cref="Asset" />, not to <see cref="Laptop" />. A write service that took that at face value would
/// deserialize a laptop row as an asset, store it back, and let Marten stamp
/// <c>mt_doc_type = 'BASE'</c> and <c>mt_dotnet_type = Asset</c> over it. The row would stop being a
/// laptop, <c>session.Query&lt;Laptop&gt;()</c> would stop returning it, and for
/// <see cref="Screen" /> — which has no members of its own — the round-trip dialog would have said
/// "nothing will change" on the way.
/// </para>
/// <para>
/// Every assertion about the row is a raw read against the table, never a second call to the write
/// service.
/// </para>
/// </remarks>
public class DocumentWriteHierarchyLiveTests(PostgresFixture postgres) : DocumentWriteFixture(postgres)
{
    private const string AssetTable = "mt_doc_asset";
    private const string NoteTable = "mt_doc_note";

    private const string AssetAlias = "asset";
    private const string NoteAlias = "note";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The demo data is irrelevant here and costs a second to seed.</summary>
    protected override bool SeedSampleData => false;

    /// <inheritdoc />
    protected override void ConfigureExtras(StoreOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        opts.Schema.For<Asset>()
            .AddSubClass<Laptop>()
            .AddSubClass<Screen>()
            .SoftDeleted();

        opts.Schema.For<Note>().SoftDeleted();
    }

    /// <summary>
    /// B1, the visible half: a subclass with members of its own survives an edit as itself.
    /// </summary>
    [PostgresFact]
    public async Task A_subclass_row_is_previewed_and_saved_as_the_subclass()
    {
        Users.SignIn("ops");
        var service = CreateService();

        var id = Guid.NewGuid();
        await StoreAsync(new Laptop { Id = id, Location = "Desk 4", Serial = "LT-0001" });

        var docTypeBefore = await DocTypeAsync(id);
        var dotNetTypeBefore = await DotNetTypeAsync(id);
        docTypeBefore.Should().Be("laptop", "Marten stamps the subclass alias on the row");

        var stored = await StoredJsonAsync(AssetTable, id);
        var edited = WithProperty(stored!, "Location", "Desk 9");

        var preview = await service.PreviewAsync(ScopeFor(), AssetAlias, id.ToString(), edited, Token);

        preview.Status.Should().Be(WritePreviewStatus.Ready);
        preview.DocumentTypeName.Should().Be(nameof(Laptop), "the row says what it is; the alias only says where it lives");
        preview.HasDrops.Should().BeFalse("the subclass has a member for Serial, so nothing is lost");

        var saved = await service.SaveAsync(
            ScopeFor(), AssetAlias, id.ToString(), edited, preview.CurrentToken, acknowledgeDrops: false, Token);

        saved.Status.Should().Be(WriteStatus.Saved);

        (await DocTypeAsync(id)).Should().Be(docTypeBefore, "a save must not change what the row is");
        (await DotNetTypeAsync(id)).Should().Be(dotNetTypeBefore);

        var after = JsonNode.Parse((await StoredJsonAsync(AssetTable, id))!)!;
        after["Location"]!.GetValue<string>().Should().Be("Desk 9");
        after["Serial"]!.GetValue<string>().Should().Be("LT-0001", "the subclass member came back too");

        (await CountAsync<Laptop>()).Should().Be(1, "Marten still finds it as a Laptop");
    }

    /// <summary>
    /// B1, the half no diff can catch: a marker subclass has nothing in its JSON that a round trip
    /// through the root type would drop, so the loss is invisible in every bucket of the dialog.
    /// </summary>
    [PostgresFact]
    public async Task A_marker_subclass_with_no_members_of_its_own_keeps_its_type()
    {
        var service = CreateService();

        var id = Guid.NewGuid();
        await StoreAsync(new Screen { Id = id, Location = "Wall" });

        (await DocTypeAsync(id)).Should().Be("screen");

        var stored = await StoredJsonAsync(AssetTable, id);
        var edited = WithProperty(stored!, "Location", "Ceiling");

        var preview = await service.PreviewAsync(ScopeFor(), AssetAlias, id.ToString(), edited, Token);

        preview.Status.Should().Be(WritePreviewStatus.Ready);
        preview.DocumentTypeName.Should().Be(nameof(Screen));
        preview.HasDrops.Should().BeFalse(
            "there is nothing in the JSON a Screen has and an Asset does not - which is exactly why the " +
            "type has to come from mt_doc_type and not from the diff");

        var saved = await service.SaveAsync(
            ScopeFor(), AssetAlias, id.ToString(), edited, preview.CurrentToken, acknowledgeDrops: false, Token);

        saved.Status.Should().Be(WriteStatus.Saved);

        (await DocTypeAsync(id)).Should().Be("screen");
        (await CountAsync<Screen>()).Should().Be(1);
        (await CountAsync<Laptop>()).Should().Be(0);
    }

    /// <summary>
    /// A row stamped with a subclass this process does not map — an older deployment's type, or a
    /// hierarchy somebody trimmed — is a refusal that names the alias, not a row rewritten as its root.
    /// </summary>
    [PostgresFact]
    public async Task A_document_type_the_hierarchy_does_not_know_is_refused_and_named()
    {
        var service = CreateService();

        var id = Guid.NewGuid();
        await StoreAsync(new Laptop { Id = id, Location = "Desk 4", Serial = "LT-0002" });
        await ExecuteAsync($"update \"{Schema}\".{AssetTable} set mt_doc_type = 'forklift' where id = '{id}'");

        var stored = await StoredJsonAsync(AssetTable, id);
        var edited = WithProperty(stored!, "Location", "Nowhere");

        var preview = await service.PreviewAsync(ScopeFor(), AssetAlias, id.ToString(), edited, Token);

        preview.Status.Should().Be(WritePreviewStatus.Refused);
        preview.Reason.Should().Contain("forklift");

        var saved = await service.SaveAsync(
            ScopeFor(), AssetAlias, id.ToString(), edited, preview.CurrentToken, acknowledgeDrops: true, Token);

        saved.Status.Should().Be(WriteStatus.Refused);
        saved.Reason.Should().Contain("forklift");

        (await StoredJsonAsync(AssetTable, id)).Should().Be(stored, "a refused save writes nothing at all");
        (await DocTypeAsync(id)).Should().Be("forklift");
    }

    /// <summary>
    /// A hierarchy row with no discriminator at all under an abstract root: there is no type to read it
    /// as, so the answer is a sentence rather than a serializer exception.
    /// </summary>
    [PostgresFact]
    public async Task A_row_with_no_discriminator_under_an_abstract_root_is_refused()
    {
        var service = CreateService();

        var id = Guid.NewGuid();
        await StoreAsync(new Laptop { Id = id, Location = "Desk 4", Serial = "LT-0003" });
        await ExecuteAsync($"update \"{Schema}\".{AssetTable} set mt_doc_type = null where id = '{id}'");

        var stored = await StoredJsonAsync(AssetTable, id);

        var preview = await service.PreviewAsync(
            ScopeFor(), AssetAlias, id.ToString(), WithProperty(stored!, "Location", "Nowhere"), Token);

        preview.Status.Should().Be(WritePreviewStatus.Refused);
        preview.TypeIsConstructible.Should().BeFalse();
        preview.Reason.Should().Contain(nameof(Asset));

        (await StoredJsonAsync(AssetTable, id)).Should().Be(stored);
    }

    /// <summary>
    /// A subclass row is deleted by its id, without the document ever being read — and brought back the
    /// same way, with its body and its discriminator untouched.
    /// </summary>
    /// <remarks>
    /// The Guid-id half of B2, and the one that also exercises an abstract hierarchy root:
    /// <c>UndoDeleteWhere&lt;Asset&gt;(x =&gt; x.Id == id)</c> is built against a type that cannot be
    /// instantiated, which is fine because it is never instantiated — that is the point of not
    /// round-tripping the document.
    /// </remarks>
    [PostgresFact]
    public async Task A_subclass_row_is_deleted_and_undeleted_by_id_alone()
    {
        var service = CreateService();

        var id = Guid.NewGuid();
        await StoreAsync(new Laptop { Id = id, Location = "Desk 4", Serial = "LT-0004" });
        await ExecuteAsync(
            $"update \"{Schema}\".{AssetTable} set data = jsonb_set(data, '{{legacyAsset}}', '\"keep\"') where id = '{id}'");

        var before = await StoredJsonAsync(AssetTable, id);

        var deleted = await service.DeleteAsync(ScopeFor(), AssetAlias, id.ToString(), Token);

        deleted.Outcome.Should().Be(DeleteOutcome.SoftDeleted);
        (await IsDeletedAsync(AssetTable, id)).Should().BeTrue();
        (await RowExistsAsync(AssetTable, id)).Should().BeTrue();

        var undeleted = await service.UndeleteAsync(ScopeFor(), AssetAlias, id.ToString(), Token);

        undeleted.Outcome.Should().Be(DeleteOutcome.Undeleted);
        (await IsDeletedAsync(AssetTable, id)).Should().BeFalse();
        (await StoredJsonAsync(AssetTable, id)).Should().Be(before, "nothing but the two delete columns moved");
        (await DocTypeAsync(id)).Should().Be("laptop");
        (await CountAsync<Laptop>()).Should().Be(1);
    }

    // ---------------------------------------------------------------------------------------------
    // String ids, and the undelete that does not rewrite the body.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// B2: an undelete flips <c>mt_deleted</c> and nothing else, so a property no CLR member could carry
    /// is exactly where it was afterwards — byte for byte.
    /// </summary>
    /// <remarks>
    /// The id is a string containing <c>/</c>, which is both the D9 case and the one that would break a
    /// path-segment route or a naively built LINQ expression.
    /// </remarks>
    [PostgresFact]
    public async Task Undeleting_a_string_id_document_keeps_its_unknown_properties_byte_for_byte()
    {
        Users.SignIn("ops");
        var service = CreateService();

        const string id = "notes/2026/alpha";
        await StoreAsync(new Note { Id = id, Body = "Keep me" });

        // A property the CLR type has no member for: the thing a round-trip undelete would have eaten.
        await ExecuteAsync(
            $"update \"{Schema}\".{NoteTable} set data = jsonb_set(data, '{{legacyNote}}', '\"do not lose this\"') " +
            $"where id = '{id}'");

        var before = await StoredJsonAsync(NoteTable, id);
        before.Should().Contain("legacyNote");

        var deleted = await service.DeleteAsync(ScopeFor(), NoteAlias, id, Token);
        deleted.Outcome.Should().Be(DeleteOutcome.SoftDeleted);
        (await IsDeletedAsync(NoteTable, id)).Should().BeTrue();

        var undeleted = await service.UndeleteAsync(ScopeFor(), NoteAlias, id, Token);

        undeleted.Outcome.Should().Be(DeleteOutcome.Undeleted);
        (await IsDeletedAsync(NoteTable, id)).Should().BeFalse();
        (await ScalarAsync($"select mt_deleted_at from \"{Schema}\".{NoteTable} where id = '{id}'"))
            .Should().BeNull("Marten's UnSoftDelete clears the timestamp as well as the flag");

        (await StoredJsonAsync(NoteTable, id)).Should().Be(
            before, "an undelete that touches `data` at all is an undelete that can lose data");

        Ring.GetLatest().Should().Contain(x => x.Action == "UndeleteDocument" && x.Succeeded && x.User == "ops");
    }

    /// <summary>
    /// There is only one undelete now, and it never rewrites the body.
    /// </summary>
    /// <remarks>
    /// The studio used to carry a second overload that allowed a body rewrite for a document type whose
    /// id it could not turn into <c>x =&gt; x.Id == id</c> — an F# discriminated-union id, say. That path
    /// deserialized the document and stored it back, rewriting <c>data</c> wholesale to clear one
    /// boolean, and a type the studio cannot address by id is exactly the type most likely to lose
    /// something on the way through. It was deleted; such a type is refused outright and told to go
    /// through Marten. This test pins the remaining behaviour: repeated undeletes of a document carrying
    /// a member the CLR type has no property for leave <c>data</c> byte-for-byte alone.
    /// </remarks>
    [PostgresFact]
    public async Task An_undelete_never_rewrites_the_body_even_across_repeats()
    {
        var service = CreateService();

        const string id = "notes/2026/beta";
        await StoreAsync(new Note { Id = id, Body = "Keep me" });
        await ExecuteAsync(
            $"update \"{Schema}\".{NoteTable} set data = jsonb_set(data, '{{legacyNote}}', '1') where id = '{id}'");

        (await service.DeleteAsync(ScopeFor(), NoteAlias, id, Token)).Outcome.Should().Be(DeleteOutcome.SoftDeleted);

        var before = await StoredJsonAsync(NoteTable, id);

        (await service.UndeleteAsync(ScopeFor(), NoteAlias, id, Token)).Outcome.Should().Be(DeleteOutcome.Undeleted);
        (await service.UndeleteAsync(ScopeFor(), NoteAlias, id, Token)).Outcome.Should().Be(DeleteOutcome.Undeleted);

        (await IsDeletedAsync(NoteTable, id)).Should().BeFalse();
        (await StoredJsonAsync(NoteTable, id)).Should().Be(
            before, "undelete is two columns; it is never a rewrite of the document");
    }

    /// <summary>A string id that is not there is not found, and nothing is written.</summary>
    [PostgresFact]
    public async Task An_undelete_of_a_string_id_that_is_not_there_is_not_found()
    {
        var service = CreateService();

        // Marten creates a document table the first time something is written to it, so there has to be
        // a note for there to be a notes table to fail to find one in.
        await StoreAsync(new Note { Id = "notes/2026/present", Body = "Here" });

        var result = await service.UndeleteAsync(ScopeFor(), NoteAlias, "notes/2026/missing", Token);

        result.Outcome.Should().Be(DeleteOutcome.NotFound);
    }

    /// <summary>Undeleting a document that was never deleted changes nothing and says so.</summary>
    [PostgresFact]
    public async Task An_undelete_of_a_live_document_changes_nothing()
    {
        var service = CreateService();

        const string id = "notes/2026/live";
        await StoreAsync(new Note { Id = id, Body = "Here" });

        var before = await StoredJsonAsync(NoteTable, id);

        var result = await service.UndeleteAsync(ScopeFor(), NoteAlias, id, Token);

        result.Outcome.Should().Be(DeleteOutcome.Undeleted);
        result.Reason.Should().Contain("not deleted");
        (await StoredJsonAsync(NoteTable, id)).Should().Be(before);
    }

    private async Task<string?> DocTypeAsync(Guid id) =>
        (string?) await ScalarAsync($"select mt_doc_type from \"{Schema}\".{AssetTable} where id = '{id}'");

    private async Task<string?> DotNetTypeAsync(Guid id) =>
        (string?) await ScalarAsync($"select mt_dotnet_type from \"{Schema}\".{AssetTable} where id = '{id}'");

    private async Task<int> CountAsync<T>() where T : notnull
    {
        await using var session = Store.QuerySession();
        return await session.Query<T>().CountAsync(Token);
    }

    private static string WithProperty(string json, string name, JsonNode? value)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node[name] = value;
        return node.ToJsonString();
    }
}
