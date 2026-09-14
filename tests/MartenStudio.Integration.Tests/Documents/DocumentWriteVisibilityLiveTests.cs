using MartenStudio.SampleDomain.Documents;
using MartenStudio.Services.Documents;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// <see cref="MartenStudioOptions.IsDocumentTypeVisible" /> as a security gate rather than as a filter.
/// </summary>
/// <remarks>
/// <para>
/// A host that hides a document type from the studio has said "not this one" about the whole product,
/// not about the collection list. The write service is where that has to be enforced, because a page is
/// a suggestion and a URL is not: every one of the five mutating entry points is driven here against a
/// hidden alias, and every one of them has to refuse and to leave the row alone.
/// </para>
/// <para>
/// <b>The refusal is the same sentence an unmapped alias gets</b>, deliberately. A distinct "that type
/// is hidden" message would turn the gate into an oracle: a visitor could enumerate the types the host
/// went to the trouble of hiding by watching which refusal came back.
/// </para>
/// <para>
/// <b>It is not a 9202 and not a 9203.</b> Those two mean "this capability is off in this process" and
/// "this visitor may not have this scope"; neither is true here — the capability is on and the scope is
/// the visitor's. A hidden type is an ordinary failed action, which is event 9201, and the assertions
/// below pin that distinction in both directions so the Activity screen keeps telling the truth about
/// which of the three refusals happened.
/// </para>
/// </remarks>
public class DocumentWriteVisibilityLiveTests(PostgresFixture postgres) : DocumentWriteFixture(postgres)
{
    private const string CustomerTable = "mt_doc_customer";
    private const string CustomerAlias = "customer";
    private const string OrderAlias = "order";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task Every_write_to_a_hidden_type_is_refused_and_the_row_is_untouched()
    {
        Users.SignIn("ops");
        StudioOptions.IsDocumentTypeVisible = static type => type != typeof(Customer);

        var service = CreateService();
        var id = await CustomerIdAsync("customer09@example.com");
        var idText = id.ToString();

        var stored = await StoredJsonAsync(CustomerTable, id);
        var versionBefore = await VersionAsync(CustomerTable, id);

        var preview = await service.PreviewAsync(ScopeFor(), CustomerAlias, idText, stored!, Token);

        preview.Status.Should().Be(
            WritePreviewStatus.Refused,
            "a hidden type must be refused, not reported as a document that happens not to exist");
        preview.Status.Should().NotBe(WritePreviewStatus.NotFound);
        preview.Reason.Should().Contain("read-only");

        var save = await service.SaveAsync(
            ScopeFor(),
            CustomerAlias,
            idText,
            stored!,
            DocumentConcurrencyToken.ForVersion(versionBefore!.Value),
            acknowledgeDrops: true,
            Token);

        save.Status.Should().Be(WriteStatus.Refused);
        save.Reason.Should().Contain("read-only");

        var delete = await service.DeleteAsync(ScopeFor(), CustomerAlias, idText, Token);
        delete.Outcome.Should().Be(DeleteOutcome.Refused);
        delete.Reason.Should().Contain("read-only");

        var undelete = await service.UndeleteAsync(ScopeFor(), CustomerAlias, idText, Token);
        undelete.Outcome.Should().Be(DeleteOutcome.Refused);
        undelete.Reason.Should().Contain(
            "read-only",
            "the visibility gate runs before the soft-delete check, so the answer never says whether the " +
            "type is soft-deleted either");

        var bulk = await service.BulkDeleteAsync(ScopeFor(), CustomerAlias, [idText], Token);
        bulk.Accepted.Should().BeFalse();
        bulk.Reason.Should().Contain("read-only");

        (await StoredJsonAsync(CustomerTable, id)).Should().Be(stored, "nothing was written");
        (await VersionAsync(CustomerTable, id)).Should().Be(versionBefore);
        (await RowExistsAsync(CustomerTable, id)).Should().BeTrue();

        Ring.GetLatest().Where(x => x.Target.StartsWith(CustomerAlias, StringComparison.Ordinal))
            .Should().OnlyContain(x => !x.Succeeded);

        Logs.EventIds.Should().Contain(9201, "a refused write is a failed action");
        Logs.EventIds.Should().NotContain(9202, "the capability is on; it is the type that is hidden");
        Logs.EventIds.Should().NotContain(9203, "the scope was allowed; it is the type that is hidden");
    }

    /// <summary>
    /// Marten's own bookkeeping is not one of this application's collections, and no capability the
    /// documents browser owns answers for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The demo store registers async projections, so Marten maps <c>DeadLetterEvent</c> for free and the
    /// type arrives in <c>AllKnownDocumentTypes()</c> looking exactly like one of the host's own
    /// collections — alias <c>deadletterevent</c>, an ordinary <c>mt_doc_*</c> table, a CLR type the
    /// serializer can construct. The documents rail already leaves it out; without the same filter on the
    /// write side, a hand-typed URL would make those rows editable under <c>EditDocuments</c> and
    /// deletable under <c>DeleteDocuments</c>.
    /// </para>
    /// <para>
    /// That is not a smaller version of the dead-letter screen — it is a different capability answering
    /// for the same data, which is the whole failure mode D4 exists to prevent. Discarding a dead letter
    /// is <c>ManageDeadLetters</c>, and it stays that way.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task Martens_own_dead_letter_collection_is_not_writable_through_the_document_path()
    {
        Users.SignIn("ops");
        var service = CreateService();

        // Every capability is on and nothing is hidden: the refusal has to come from the type itself.
        StudioOptions.IsDocumentTypeVisible = null;

        const string alias = "deadletterevent";
        var id = Guid.NewGuid().ToString();

        // The premise, asserted rather than assumed: Marten really does map the type under really this
        // alias. Without it a refusal could just as well mean "no such alias", and the test would pass
        // while the filter did nothing.
        Store.Options.AllKnownDocumentTypes().Should().Contain(
            x => x.DocumentType == typeof(JasperFx.Events.Daemon.DeadLetterEvent) &&
                 x.Alias == alias,
            "excluding the table name from discovery is not enough: an async projection maps the type");

        var preview = await service.PreviewAsync(ScopeFor(), alias, id, "{}", Token);
        preview.Status.Should().Be(WritePreviewStatus.Refused);
        preview.Reason.Should().Contain("read-only");

        var save = await service.SaveAsync(
            ScopeFor(), alias, id, "{}", DocumentConcurrencyToken.None, acknowledgeDrops: true, Token);
        save.Status.Should().Be(WriteStatus.Refused);

        (await service.DeleteAsync(ScopeFor(), alias, id, Token)).Outcome.Should().Be(DeleteOutcome.Refused);
        (await service.UndeleteAsync(ScopeFor(), alias, id, Token)).Outcome.Should().Be(DeleteOutcome.Refused);
        (await service.BulkDeleteAsync(ScopeFor(), alias, [id], Token)).Accepted.Should().BeFalse();
    }

    /// <summary>
    /// The gate is about the type and nothing else: a collection the host did not hide is exactly as
    /// writable as it was.
    /// </summary>
    [PostgresFact]
    public async Task A_type_that_is_not_hidden_is_still_writable()
    {
        StudioOptions.IsDocumentTypeVisible = static type => type != typeof(Customer);

        var service = CreateService();
        var id = await OrderIdAsync("ORD-2026-0008");

        var deleted = await service.DeleteAsync(ScopeFor(), OrderAlias, id.ToString(), Token);

        deleted.Outcome.Should().Be(DeleteOutcome.SoftDeleted);
    }
}
