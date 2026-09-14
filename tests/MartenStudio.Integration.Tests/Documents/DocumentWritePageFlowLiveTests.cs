using System.Text.Json.Nodes;

using MartenStudio.Services;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Json;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// The detail page's own sequence of service calls, against a real store.
/// </summary>
/// <remarks>
/// <para>
/// The bUnit suite proves the page calls these methods with these arguments; this proves the arguments
/// mean what the page thinks they mean. Between them there is no gap: every call here is made in the
/// order <c>DocumentDetail</c> and <c>DocumentEditPane</c> make it, carrying the value that component
/// would have carried — the probe's <c>CurrentToken</c> into the save, the preview's own
/// <c>EditedJson</c> rather than the textarea's text, <c>acknowledgeDrops</c> only where the dialog was
/// confirmed.
/// </para>
/// <para>
/// Rendering the page against a real Postgres would not be a better test of this. It would be a test of
/// bUnit's renderer, and it would still be these five methods underneath. What is worth proving with a
/// database attached is that the token round-trips, that a conflict really is detected, and that a
/// dropped property really is gone.
/// </para>
/// </remarks>
public class DocumentWritePageFlowLiveTests(PostgresFixture postgres) : DocumentWriteFixture(postgres)
{
    private const string CustomerTable = "mt_doc_customer";
    private const string OrderTable = "mt_doc_order";

    private const string CustomerAlias = "customer";
    private const string OrderAlias = "order";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The whole happy path as the page walks it: probe on Edit, preview on Save, save with the probe's
    /// token, then re-read the document the way the page reloads it.
    /// </summary>
    [PostgresFact]
    public async Task The_edit_flow_from_the_pages_point_of_view_round_trips()
    {
        Users.SignIn("ops");
        var service = CreateService();
        var id = await CustomerIdAsync("customer11@example.com");
        var idText = id.ToString();

        // 1. The page has the document from IDocumentDataService. Edit is pressed, and the pane probes
        //    the round trip with the document exactly as stored — which is what answers "can this type be
        //    constructed" and "what version am I editing".
        var stored = await StoredJsonAsync(CustomerTable, id);
        var probe = await service.PreviewAsync(ScopeFor(), CustomerAlias, idText, stored!, Token);

        probe.Status.Should().Be(WritePreviewStatus.Ready);
        probe.TypeIsConstructible.Should().BeTrue("the Edit button opens the editor only when this is true");
        probe.HasDrops.Should().BeFalse("the stored document round-trips: it was written by Marten");
        probe.CurrentToken.IsKnown.Should().BeTrue("this is the token the save will carry");

        // 2. Somebody types. Save runs a preview of the edited text first (D7), which is the same call.
        var edited = WithProperty(stored!, "Name", "Grace Hopper");
        var preview = await service.PreviewAsync(ScopeFor(), CustomerAlias, idText, edited, Token);

        preview.Status.Should().Be(WritePreviewStatus.Ready);
        preview.HasDrops.Should().BeFalse("nothing dropped, so the dialog does not open and the save goes straight through");
        preview.CurrentToken.Should().Be(probe.CurrentToken, "nobody else has written to the row");
        preview.EditedJson.Should().Be(edited);

        // 3. The pane saves the preview's own EditedJson with the preview's own token, which is what makes
        //    "what was shown" and "what was written" the same document.
        var saved = await service.SaveAsync(
            ScopeFor(),
            CustomerAlias,
            idText,
            preview.EditedJson!,
            preview.CurrentToken,
            acknowledgeDrops: preview.HasDrops,
            Token);

        saved.Status.Should().Be(WriteStatus.Saved);

        // 4. The page reloads. What it reads back is what the preview promised.
        var after = await StoredJsonAsync(CustomerTable, id);

        JsonNode.Parse(after!)!["Name"]!.GetValue<string>().Should().Be("Grace Hopper");
        RoundTripDiffer.Diff(after, preview.RoundTrippedJson).IsEmpty.Should().BeTrue();
        saved.CurrentToken.Should().NotBe(preview.CurrentToken, "the save moved the version on");
    }

    /// <summary>
    /// The dialog's confirm button, end to end: a preview that reports drops, a save refused without the
    /// acknowledgement, and the same save accepted with it.
    /// </summary>
    [PostgresFact]
    public async Task The_preview_dialogs_confirm_is_the_only_thing_that_lets_a_drop_through()
    {
        Users.SignIn("ops");
        var service = CreateService();
        var id = await CustomerIdAsync("customer12@example.com");
        var idText = id.ToString();

        var stored = await StoredJsonAsync(CustomerTable, id);
        var edited = WithProperty(stored!, "legacyNote", "written by the old application");

        var preview = await service.PreviewAsync(ScopeFor(), CustomerAlias, idText, edited, Token);

        preview.HasDrops.Should().BeTrue();
        preview.DroppedPaths.Should().Contain("$.legacyNote");

        // What the Save button would read while the dialog is closed.
        preview.DroppedPaths.Length.Should().Be(1);

        var refused = await service.SaveAsync(
            ScopeFor(), CustomerAlias, idText, preview.EditedJson!, preview.CurrentToken, acknowledgeDrops: false, Token);

        refused.Status.Should().Be(WriteStatus.Refused);
        (await StoredJsonAsync(CustomerTable, id)).Should().Be(stored, "a refused save writes nothing");

        var accepted = await service.SaveAsync(
            ScopeFor(), CustomerAlias, idText, preview.EditedJson!, preview.CurrentToken, acknowledgeDrops: true, Token);

        accepted.Status.Should().Be(WriteStatus.Saved);
        JsonNode.Parse((await StoredJsonAsync(CustomerTable, id))!)!.AsObject()
            .ContainsKey("legacyNote").Should().BeFalse();
    }

    /// <summary>
    /// Somebody else saves while the editor is open. The page gets a conflict carrying the current token,
    /// and "Overwrite" — re-preview, then save — is the one round trip that makes it go through.
    /// </summary>
    [PostgresFact]
    public async Task A_conflict_carries_the_current_token_and_overwrite_re_previews_against_it()
    {
        Users.SignIn("ops");
        var service = CreateService();
        var id = await CustomerIdAsync("customer13@example.com");
        var idText = id.ToString();

        var stored = await StoredJsonAsync(CustomerTable, id);

        // The editor opens and the page holds this token.
        var probe = await service.PreviewAsync(ScopeFor(), CustomerAlias, idText, stored!, Token);
        var openedWith = probe.CurrentToken;

        // Somebody else writes, through Marten rather than through the code under test.
        await ExecuteAsync(
            $"update \"{Schema}\".{CustomerTable} set data = jsonb_set(data, '{{Name}}', '\"Somebody else\"'), " +
            $"mt_version = gen_random_uuid() where id = '{id}'");

        var mine = WithProperty(stored!, "Name", "Mine");
        var preview = await service.PreviewAsync(ScopeFor(), CustomerAlias, idText, mine, Token);

        var conflict = await service.SaveAsync(
            ScopeFor(), CustomerAlias, idText, preview.EditedJson!, openedWith, acknowledgeDrops: false, Token);

        conflict.Status.Should().Be(WriteStatus.Conflict);
        conflict.CurrentToken.IsKnown.Should().BeTrue("the notice's Overwrite button needs somewhere to go");
        conflict.CurrentToken.Should().NotBe(openedWith);

        JsonNode.Parse((await StoredJsonAsync(CustomerTable, id))!)!["Name"]!.GetValue<string>()
            .Should().Be("Somebody else", "a conflict is never a lost update");

        // Overwrite: re-preview, which reads the row again and reports the token the other writer left,
        // then save with that. Exactly what DocumentEditPane.OverwriteAsync does.
        var again = await service.PreviewAsync(ScopeFor(), CustomerAlias, idText, mine, Token);

        again.CurrentToken.Should().Be(conflict.CurrentToken);

        var overwritten = await service.SaveAsync(
            ScopeFor(), CustomerAlias, idText, again.EditedJson!, again.CurrentToken, acknowledgeDrops: again.HasDrops, Token);

        overwritten.Status.Should().Be(WriteStatus.Saved);
        JsonNode.Parse((await StoredJsonAsync(CustomerTable, id))!)!["Name"]!.GetValue<string>().Should().Be("Mine");
    }

    /// <summary>
    /// The delete button and the banner's undelete button, in the order somebody presses them.
    /// </summary>
    [PostgresFact]
    public async Task Soft_delete_then_undelete_is_what_the_banner_and_its_button_do()
    {
        Users.SignIn("ops");
        var service = CreateService();
        var id = await OrderIdAsync("ORD-2026-0009");
        var idText = id.ToString();

        var before = await StoredJsonAsync(OrderTable, id);

        var deleted = await service.DeleteAsync(ScopeFor(), OrderAlias, idText, Token);

        deleted.Outcome.Should().Be(
            DeleteOutcome.SoftDeleted,
            "which is the outcome the toast names and the reason the page stays put rather than navigating away");
        (await IsDeletedAsync(OrderTable, id)).Should().BeTrue("the reloaded page draws the deleted banner from this");

        var undeleted = await service.UndeleteAsync(ScopeFor(), OrderAlias, idText, Token);

        undeleted.Outcome.Should().Be(DeleteOutcome.Undeleted);
        (await IsDeletedAsync(OrderTable, id)).Should().BeFalse();
        (await StoredJsonAsync(OrderTable, id)).Should().Be(
            before, "the undelete button must not be a way to lose a property the CLR type has no member for");
    }

    /// <summary>The list page's action bar: three ticked rows, one call, one summary.</summary>
    [PostgresFact]
    public async Task Bulk_deleting_the_three_selected_rows_reports_each_of_them()
    {
        Users.SignIn("ops");
        var service = CreateService();

        var first = await OrderIdAsync("ORD-2026-0010");
        var second = await OrderIdAsync("ORD-2026-0011");
        var missing = Guid.NewGuid();

        var result = await service.BulkDeleteAsync(
            ScopeFor(),
            OrderAlias,
            [first.ToString(), second.ToString(), missing.ToString()],
            Token);

        result.Accepted.Should().BeTrue();
        result.Results.Should().HaveCount(3, "the bar reports per id, not a total");
        result.SoftDeletedCount.Should().Be(2);
        result.NotFoundCount.Should().Be(1);

        // What the toast says.
        result.Summary().Should().Contain("2 deleted").And.Contain("1 not found");

        (await IsDeletedAsync(OrderTable, first)).Should().BeTrue();
        (await IsDeletedAsync(OrderTable, second)).Should().BeTrue();

        Ring.GetLatest().Should().Contain(x => x.Action == "BulkDeleteDocuments" && x.Succeeded && x.User == "ops");
    }

    /// <summary>
    /// The cap the action bar states, enforced where it has to be. The bar disables its button past 500;
    /// this is the service refusing the request that gets past the bar anyway.
    /// </summary>
    [PostgresFact]
    public async Task More_ids_than_the_cap_are_refused_before_anything_is_read()
    {
        Users.SignIn("ops");
        var service = CreateService();

        string[] ids = [.. Enumerable.Range(0, DocumentWriteService.MaxBulkDeleteIds + 1)
            .Select(_ => Guid.NewGuid().ToString())];

        var result = await service.BulkDeleteAsync(ScopeFor(), OrderAlias, ids, Token);

        result.Accepted.Should().BeFalse();
        result.Reason.Should().Contain(DocumentWriteService.MaxBulkDeleteIds.ToString(
            System.Globalization.CultureInfo.InvariantCulture));
        result.Results.Should().BeEmpty();
    }

    /// <summary>
    /// A viewer is refused at the service, and the page renders that refusal as a message rather than
    /// dying on it. The exception type is the contract: the page catches it and shows
    /// <see cref="Exception.Message" />.
    /// </summary>
    [PostgresFact]
    public async Task A_visitor_the_write_policy_refuses_gets_an_exception_the_page_can_render()
    {
        Users.SignIn("viewer");
        Authorization.DenyWrites();

        var service = CreateService();
        var id = await CustomerIdAsync("customer14@example.com");
        var stored = await StoredJsonAsync(CustomerTable, id);

        var probe = async () => await service.PreviewAsync(ScopeFor(), CustomerAlias, id.ToString(), stored!, Token);

        (await probe.Should().ThrowAsync<StudioNotAuthorizedException>())
            .Which.Message.Should().NotBeNullOrWhiteSpace("the page puts this on screen verbatim");

        (await StoredJsonAsync(CustomerTable, id)).Should().Be(stored);
    }

    private static string WithProperty(string json, string name, JsonNode? value)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node[name] = value;
        return node.ToJsonString();
    }
}
