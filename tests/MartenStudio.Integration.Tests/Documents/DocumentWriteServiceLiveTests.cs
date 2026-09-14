using System.Globalization;
using System.Text.Json.Nodes;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.SampleDomain.Documents;
using MartenStudio.Services;
using MartenStudio.Services.Documents;
using MartenStudio.Services.Json;

using Npgsql;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// The write path against a real Marten store over the demo domain: the round-trip preview, the save and
/// its concurrency check, delete, undelete, bulk delete, and the two refusals that must reach the
/// database as nothing at all.
/// </summary>
/// <remarks>
/// <para>
/// Every assertion about what is in the database is a raw read against the table, never a second call to
/// the code under test. A write service that is wrong in the same way twice would otherwise agree with
/// itself.
/// </para>
/// <para>
/// The three sample types are chosen for what makes them awkward, not for variety: <c>Customer</c> is the
/// plain case with a unique duplicated column, <c>Order</c> has a strong-typed id, soft deletion, a
/// foreign key and optimistic concurrency, and <c>Invoice</c> is conjoined multi-tenant with an int
/// HiLo id. Between them they cover every branch the write service has.
/// </para>
/// </remarks>
public class DocumentWriteServiceLiveTests(PostgresFixture postgres) : DocumentWriteFixture(postgres)
{
    private const string CustomerTable = "mt_doc_customer";
    private const string OrderTable = "mt_doc_order";
    private const string InvoiceTable = "mt_doc_invoice";

    private const string CustomerAlias = "customer";
    private const string OrderAlias = "order";
    private const string InvoiceAlias = "invoice";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---------------------------------------------------------------------------------------------
    // The ordinary edit.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// What is stored afterwards is exactly what the preview said would be stored — the round-tripped
    /// edit, not the edit — and the row's own bookkeeping moved with it.
    /// </summary>
    [PostgresFact]
    public async Task An_edited_customer_is_saved_as_the_round_trip_said_it_would_be()
    {
        Users.SignIn("ops");
        var service = CreateService();
        var id = await CustomerIdAsync("customer05@example.com");

        var stored = await StoredJsonAsync(CustomerTable, id);
        var versionBefore = await VersionAsync(CustomerTable, id);
        var modifiedBefore = await LastModifiedAsync(CustomerTable, id);

        var edited = WithProperty(stored!, "Name", "Ada Lovelace");

        var preview = await service.PreviewAsync(ScopeFor(), CustomerAlias, id.ToString(), edited, Token);

        preview.Status.Should().Be(WritePreviewStatus.Ready);
        preview.HasDrops.Should().BeFalse();
        preview.CurrentToken.Version.Should().Be(versionBefore);
        preview.EditDiff.Changed.Select(x => x.Path).Should().Contain("$.Name");

        var result = await service.SaveAsync(
            ScopeFor(), CustomerAlias, id.ToString(), edited, preview.CurrentToken, acknowledgeDrops: false, Token);

        result.Status.Should().Be(WriteStatus.Saved);

        var after = await StoredJsonAsync(CustomerTable, id);
        RoundTripDiffer.Diff(after, preview.RoundTrippedJson).IsEmpty.Should()
            .BeTrue("what is stored has to be exactly what the preview said would be stored");
        JsonNode.Parse(after!)!["Name"]!.GetValue<string>().Should().Be("Ada Lovelace");

        var versionAfter = await VersionAsync(CustomerTable, id);
        versionAfter.Should().NotBe(versionBefore!.Value);
        result.CurrentToken.Version.Should().Be(versionAfter);

        (await LastModifiedAsync(CustomerTable, id)).Should().BeAfter(modifiedBefore!.Value);

        Ring.GetLatest().Should().Contain(x =>
            x.Action == "EditDocument" && x.Target == CustomerAlias + "/" + id && x.Succeeded && x.User == "ops");
        Logs.EventIds.Should().Contain(9200);
    }

    /// <summary>
    /// D7, end to end: a property the CLR type has no member for is named in the preview, the save is
    /// refused until somebody says they have seen it, and the property really is gone afterwards.
    /// </summary>
    [PostgresFact]
    public async Task A_property_the_type_cannot_carry_is_named_refused_and_then_dropped_on_purpose()
    {
        Users.SignIn("ops");
        var service = CreateService();
        var id = await CustomerIdAsync("customer06@example.com");

        var stored = await StoredJsonAsync(CustomerTable, id);
        var versionBefore = await VersionAsync(CustomerTable, id);
        var edited = WithProperty(stored!, "legacyField", 1);

        var preview = await service.PreviewAsync(ScopeFor(), CustomerAlias, id.ToString(), edited, Token);

        preview.Status.Should().Be(WritePreviewStatus.Ready);
        preview.DroppedPaths.Should().Contain("$.legacyField");

        var refused = await service.SaveAsync(
            ScopeFor(), CustomerAlias, id.ToString(), edited, preview.CurrentToken, acknowledgeDrops: false, Token);

        refused.Status.Should().Be(WriteStatus.Refused);
        refused.Reason.Should().Contain("$.legacyField");
        (await VersionAsync(CustomerTable, id)).Should().Be(versionBefore, "a refused save writes nothing at all");

        var accepted = await service.SaveAsync(
            ScopeFor(), CustomerAlias, id.ToString(), edited, preview.CurrentToken, acknowledgeDrops: true, Token);

        accepted.Status.Should().Be(WriteStatus.Saved);

        var after = await StoredJsonAsync(CustomerTable, id);
        JsonNode.Parse(after!)!.AsObject().ContainsKey("legacyField").Should()
            .BeFalse("the type has no member for it, so the round trip could only drop it");

        Logs.EventIds.Should().Contain(9211);
        Logs.Lines.Should().Contain(x => x.EventId.Id == 9211 && x.Message.Contains("$.legacyField", StringComparison.Ordinal));
    }

    /// <summary>
    /// Two people with the same document open. The second save is told what happened rather than
    /// quietly overwriting the first — the lost update this whole mechanism exists to prevent.
    /// </summary>
    [PostgresFact]
    public async Task A_stale_version_is_a_conflict_and_the_document_is_left_alone()
    {
        var service = CreateService();
        var id = await CustomerIdAsync("customer07@example.com");
        var stored = await StoredJsonAsync(CustomerTable, id);

        var first = await service.PreviewAsync(
            ScopeFor(), CustomerAlias, id.ToString(), WithProperty(stored!, "Name", "Mine"), Token);

        // Somebody else saves in between, which is what makes the token above stale.
        var theirs = WithProperty(stored!, "Name", "Theirs");
        var theirSave = await service.SaveAsync(
            ScopeFor(), CustomerAlias, id.ToString(), theirs, first.CurrentToken, acknowledgeDrops: false, Token);
        theirSave.Status.Should().Be(WriteStatus.Saved);

        var conflict = await service.SaveAsync(
            ScopeFor(),
            CustomerAlias,
            id.ToString(),
            WithProperty(stored!, "Name", "Mine"),
            first.CurrentToken,
            acknowledgeDrops: false,
            Token);

        conflict.Status.Should().Be(WriteStatus.Conflict);
        conflict.CurrentToken.Should().Be(theirSave.CurrentToken);
        conflict.Reason.Should().Contain("changed while it was open");

        var after = await StoredJsonAsync(CustomerTable, id);
        JsonNode.Parse(after!)!["Name"]!.GetValue<string>().Should().Be("Theirs", "the loser of the race changes nothing");

        Ring.GetLatest().Should().Contain(x => x.Action == "EditDocument" && !x.Succeeded);
        Logs.EventIds.Should().Contain(9201);
    }

    /// <summary>
    /// Two circuits saving the same document at the same moment, which is the case the version check on
    /// its own cannot decide.
    /// </summary>
    /// <remarks>
    /// This is what the <c>for update</c> in the write transaction buys. Both saves read the same version
    /// and both pass their own check; the row lock is what makes the second one wait for the first to
    /// commit and then see the version it left behind. Without the lock, read committed lets both read
    /// the old version, both decide they are safe, and the second silently overwrites the first — a lost
    /// update that no assertion about a stale token would ever catch.
    /// </remarks>
    [PostgresFact]
    public async Task Two_saves_racing_on_one_document_produce_one_save_and_one_conflict()
    {
        var first = CreateService();
        var second = CreateService();

        var id = await CustomerIdAsync("customer19@example.com");
        var stored = await StoredJsonAsync(CustomerTable, id);
        var token = DocumentConcurrencyToken.ForVersion((await VersionAsync(CustomerTable, id))!.Value);

        var results = await Task.WhenAll(
            first.SaveAsync(
                ScopeFor(), CustomerAlias, id.ToString(), WithProperty(stored!, "Name", "First"), token, false, Token),
            second.SaveAsync(
                ScopeFor(), CustomerAlias, id.ToString(), WithProperty(stored!, "Name", "Second"), token, false, Token));

        results.Count(x => x.Status == WriteStatus.Saved).Should().Be(1, "exactly one of them held the row");
        results.Count(x => x.Status == WriteStatus.Conflict).Should().Be(1, "the other has to be told, not ignored");

        var winner = results.Single(x => x.Status == WriteStatus.Saved);
        var loser = results.Single(x => x.Status == WriteStatus.Conflict);

        loser.CurrentToken.Should().Be(winner.CurrentToken, "the conflict reports the version that is actually there");
        (await VersionAsync(CustomerTable, id)).Should().Be(winner.CurrentToken.Version!.Value);
    }

    /// <summary>
    /// A save with no version at all is refused rather than run unguarded, because the collection has a
    /// version column and an unguarded write to it would be a silent overwrite.
    /// </summary>
    [PostgresFact]
    public async Task A_save_with_no_version_at_all_is_refused()
    {
        var service = CreateService();
        var id = await CustomerIdAsync("customer08@example.com");
        var stored = await StoredJsonAsync(CustomerTable, id);
        var versionBefore = await VersionAsync(CustomerTable, id);

        var result = await service.SaveAsync(
            ScopeFor(),
            CustomerAlias,
            id.ToString(),
            WithProperty(stored!, "Name", "Unversioned"),
            DocumentConcurrencyToken.None,
            acknowledgeDrops: false,
            Token);

        result.Status.Should().Be(WriteStatus.Refused);
        result.Reason.Should().Contain("version");
        (await VersionAsync(CustomerTable, id)).Should().Be(versionBefore);
    }

    // ---------------------------------------------------------------------------------------------
    // The awkward document: strong-typed id, optimistic concurrency, soft delete, foreign key.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// An <c>Order</c> has a strong-typed id and Marten's own optimistic concurrency, which means a plain
    /// <c>StoreObjects</c> would fail every save of it — the expected version has to be handed to Marten
    /// as well as checked by the studio.
    /// </summary>
    [PostgresFact]
    public async Task An_order_with_a_strong_typed_id_and_optimistic_concurrency_round_trips()
    {
        var service = CreateService();
        var id = await OrderIdAsync("ORD-2026-0005");

        var stored = await StoredJsonAsync(OrderTable, id);
        var edited = WithProperty(stored!, "Status", "Shipped");

        var preview = await service.PreviewAsync(ScopeFor(), OrderAlias, id.ToString(), edited, Token);

        preview.Status.Should().Be(WritePreviewStatus.Ready);
        preview.HasDrops.Should().BeFalse("the strong-typed id and the nested lines all survive the round trip");

        var result = await service.SaveAsync(
            ScopeFor(), OrderAlias, id.ToString(), edited, preview.CurrentToken, acknowledgeDrops: false, Token);

        result.Status.Should().Be(WriteStatus.Saved);

        var after = await StoredJsonAsync(OrderTable, id);
        JsonNode.Parse(after!)!["Status"]!.GetValue<string>().Should().Be("Shipped");
        RoundTripDiffer.Diff(after, preview.RoundTrippedJson).IsEmpty.Should().BeTrue();
        (await VersionAsync(OrderTable, id)).Should().NotBe(preview.CurrentToken.Version!.Value);
    }

    [PostgresFact]
    public async Task A_concurrent_change_to_an_order_is_detected()
    {
        var service = CreateService();
        var id = await OrderIdAsync("ORD-2026-0006");
        var stored = await StoredJsonAsync(OrderTable, id);

        var stale = await service.PreviewAsync(
            ScopeFor(), OrderAlias, id.ToString(), WithProperty(stored!, "Status", "Mine"), Token);

        var theirs = await service.SaveAsync(
            ScopeFor(),
            OrderAlias,
            id.ToString(),
            WithProperty(stored!, "Status", "Theirs"),
            stale.CurrentToken,
            acknowledgeDrops: false,
            Token);
        theirs.Status.Should().Be(WriteStatus.Saved);

        var conflict = await service.SaveAsync(
            ScopeFor(),
            OrderAlias,
            id.ToString(),
            WithProperty(stored!, "Status", "Mine"),
            stale.CurrentToken,
            acknowledgeDrops: false,
            Token);

        conflict.Status.Should().Be(WriteStatus.Conflict);
        JsonNode.Parse((await StoredJsonAsync(OrderTable, id))!)!["Status"]!.GetValue<string>().Should().Be("Theirs");
    }

    // ---------------------------------------------------------------------------------------------
    // Tenancy.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A conjoined collection is exactly as reachable as the tenant in scope says it is: the wrong tenant
    /// cannot see the row, the right one can write it, and no tenant at all is refused rather than
    /// written into Marten's <c>*DEFAULT*</c> tenant.
    /// </summary>
    [PostgresFact]
    public async Task An_invoice_belongs_to_its_tenant_and_a_write_without_one_is_refused()
    {
        var service = CreateService();
        var id = await InvoiceIdAsync("acme", "ACME-INV-001");
        var idText = id.ToString(CultureInfo.InvariantCulture);
        var stored = await StoredJsonAsync(InvoiceTable, id, "acme");
        var edited = WithProperty(stored!, "Paid", true);

        var wrongTenant = await service.PreviewAsync(ScopeFor("globex"), InvoiceAlias, idText, edited, Token);
        wrongTenant.Status.Should().Be(WritePreviewStatus.NotFound, "another tenant's row is not this tenant's row");

        var noTenant = await service.PreviewAsync(ScopeFor(), InvoiceAlias, idText, edited, Token);
        noTenant.Status.Should().Be(WritePreviewStatus.Refused);
        noTenant.Reason.Should().Contain("multi-tenanted");

        var rightTenant = await service.PreviewAsync(ScopeFor("acme"), InvoiceAlias, idText, edited, Token);
        rightTenant.Status.Should().Be(WritePreviewStatus.Ready);

        var saved = await service.SaveAsync(
            ScopeFor("acme"),
            InvoiceAlias,
            idText,
            edited,
            rightTenant.CurrentToken,
            acknowledgeDrops: false,
            Token);

        saved.Status.Should().Be(WriteStatus.Saved);
        JsonNode.Parse((await StoredJsonAsync(InvoiceTable, id, "acme"))!)!["Paid"]!.GetValue<bool>().Should().BeTrue();

        (await ScalarAsync(string.Create(
                CultureInfo.InvariantCulture,
                $"select count(*) from \"{Schema}\".{InvoiceTable} where id = {id}")))
            .Should().Be(1L, "a tenanted write must never leave a second row behind in another tenant");
    }

    /// <summary>A delete cannot reach another tenant's row either.</summary>
    [PostgresFact]
    public async Task An_invoice_cannot_be_deleted_from_the_wrong_tenant()
    {
        var service = CreateService();
        var id = await InvoiceIdAsync("acme", "ACME-INV-002");

        var wrongTenant = await service.DeleteAsync(
            ScopeFor("globex"), InvoiceAlias, id.ToString(CultureInfo.InvariantCulture), Token);

        wrongTenant.Outcome.Should().Be(DeleteOutcome.NotFound);
        (await RowExistsAsync(InvoiceTable, id)).Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------------
    // Delete, undelete, bulk delete.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Soft and hard are reported apart, and both are what the mapping asked for rather than what the
    /// caller assumed.
    /// </summary>
    [PostgresFact]
    public async Task Deleting_a_soft_deleted_type_marks_the_row_and_undeleting_brings_it_back()
    {
        Users.SignIn("ops");
        var service = CreateService();
        var id = await OrderIdAsync("ORD-2026-0007");

        var deleted = await service.DeleteAsync(ScopeFor(), OrderAlias, id.ToString(), Token);

        deleted.Outcome.Should().Be(DeleteOutcome.SoftDeleted);
        (await IsDeletedAsync(OrderTable, id)).Should().BeTrue();
        (await RowExistsAsync(OrderTable, id)).Should().BeTrue("a soft delete keeps the row");

        var undeleted = await service.UndeleteAsync(ScopeFor(), OrderAlias, id.ToString(), Token);

        undeleted.Outcome.Should().Be(DeleteOutcome.Undeleted);
        (await IsDeletedAsync(OrderTable, id)).Should().BeFalse();

        Ring.GetLatest().Should().Contain(x => x.Action == "UndeleteDocument" && x.Succeeded);
    }

    /// <summary>A document the seeder soft-deleted is exactly as undeletable as one the studio deleted.</summary>
    [PostgresFact]
    public async Task A_document_that_was_already_deleted_can_be_undeleted()
    {
        var service = CreateService();
        var id = await OrderIdAsync("ORD-2026-0001");

        (await IsDeletedAsync(OrderTable, id)).Should().BeTrue("the seeder deletes the first three orders");

        var undeleted = await service.UndeleteAsync(ScopeFor(), OrderAlias, id.ToString(), Token);

        undeleted.Outcome.Should().Be(DeleteOutcome.Undeleted);
        (await IsDeletedAsync(OrderTable, id)).Should().BeFalse();
    }

    [PostgresFact]
    public async Task Undeleting_a_type_that_is_hard_deleted_is_refused()
    {
        var service = CreateService();
        var id = await CustomerIdAsync("customer17@example.com");

        var result = await service.UndeleteAsync(ScopeFor(), CustomerAlias, id.ToString(), Token);

        result.Outcome.Should().Be(DeleteOutcome.Refused);
        result.Reason.Should().Contain("not soft-deleted");
        (await RowExistsAsync(CustomerTable, id)).Should().BeTrue();
    }

    [PostgresFact]
    public async Task Deleting_a_hard_deleted_type_removes_the_row()
    {
        var service = CreateService();
        var id = await CustomerIdAsync("customer20@example.com");

        var result = await service.DeleteAsync(ScopeFor(), CustomerAlias, id.ToString(), Token);

        result.Outcome.Should().Be(DeleteOutcome.HardDeleted);
        (await RowExistsAsync(CustomerTable, id)).Should().BeFalse();

        var again = await service.DeleteAsync(ScopeFor(), CustomerAlias, id.ToString(), Token);
        again.Outcome.Should().Be(DeleteOutcome.NotFound);
    }

    /// <summary>
    /// One transaction, one answer per id — including for the id that was not there, which is the whole
    /// reason the result is a list rather than a count.
    /// </summary>
    [PostgresFact]
    public async Task A_bulk_delete_reports_every_id_including_the_one_that_was_not_there()
    {
        var service = CreateService();
        var first = await CustomerIdAsync("customer21@example.com");
        var second = await CustomerIdAsync("customer22@example.com");
        var missing = Guid.NewGuid();

        var result = await service.BulkDeleteAsync(
            ScopeFor(),
            CustomerAlias,
            [first.ToString(), missing.ToString(), second.ToString()],
            Token);

        result.Accepted.Should().BeTrue();
        result.Results.Should().HaveCount(3);
        result.Results[0].Outcome.Should().Be(DeleteOutcome.HardDeleted);
        result.Results[1].Outcome.Should().Be(DeleteOutcome.NotFound);
        result.Results[2].Outcome.Should().Be(DeleteOutcome.HardDeleted);
        result.DeletedCount.Should().Be(2);
        result.NotFoundCount.Should().Be(1);

        (await RowExistsAsync(CustomerTable, first)).Should().BeFalse();
        (await RowExistsAsync(CustomerTable, second)).Should().BeFalse();
    }

    [PostgresFact]
    public async Task A_bulk_delete_past_the_cap_is_refused_and_deletes_nothing()
    {
        var service = CreateService();
        var id = await CustomerIdAsync("customer23@example.com");

        var ids = new List<string> { id.ToString() };
        while (ids.Count <= DocumentWriteService.MaxBulkDeleteIds)
        {
            ids.Add(Guid.NewGuid().ToString());
        }

        var result = await service.BulkDeleteAsync(ScopeFor(), CustomerAlias, ids, Token);

        result.Accepted.Should().BeFalse();
        (await RowExistsAsync(CustomerTable, id)).Should().BeTrue();
    }

    // ---------------------------------------------------------------------------------------------
    // The two refusals that have to reach the database as nothing at all.
    // ---------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_capability_that_is_off_refuses_the_write_and_changes_nothing()
    {
        Users.SignIn("viewer");
        StudioOptions.Capabilities = new MartenStudioCapabilities();

        try
        {
            var service = CreateService();
            var id = await CustomerIdAsync("customer24@example.com");
            var stored = await StoredJsonAsync(CustomerTable, id);
            var versionBefore = await VersionAsync(CustomerTable, id);

            await service
                .Invoking(x => x.SaveAsync(
                    ScopeFor(),
                    CustomerAlias,
                    id.ToString(),
                    WithProperty(stored!, "Name", "Nope"),
                    DocumentConcurrencyToken.ForVersion(versionBefore!.Value),
                    acknowledgeDrops: true,
                    Token))
                .Should().ThrowAsync<StudioCapabilityDeniedException>();

            await service
                .Invoking(x => x.DeleteAsync(ScopeFor(), CustomerAlias, id.ToString(), Token))
                .Should().ThrowAsync<StudioCapabilityDeniedException>();

            (await VersionAsync(CustomerTable, id)).Should().Be(versionBefore);
            (await RowExistsAsync(CustomerTable, id)).Should().BeTrue();
            Logs.EventIds.Should().Contain(9202);
        }
        finally
        {
            StudioOptions.Capabilities = MartenStudioCapabilities.All();
        }
    }

    [PostgresFact]
    public async Task A_write_policy_that_refuses_stops_the_write_and_changes_nothing()
    {
        Users.SignIn("viewer");
        Authorization.DenyWrites();

        try
        {
            var service = CreateService();
            var id = await CustomerIdAsync("customer25@example.com");
            var stored = await StoredJsonAsync(CustomerTable, id);
            var versionBefore = await VersionAsync(CustomerTable, id);

            await service
                .Invoking(x => x.SaveAsync(
                    ScopeFor(),
                    CustomerAlias,
                    id.ToString(),
                    WithProperty(stored!, "Name", "Nope"),
                    DocumentConcurrencyToken.ForVersion(versionBefore!.Value),
                    acknowledgeDrops: true,
                    Token))
                .Should().ThrowAsync<StudioNotAuthorizedException>();

            await service
                .Invoking(x => x.BulkDeleteAsync(ScopeFor(), CustomerAlias, [id.ToString()], Token))
                .Should().ThrowAsync<StudioNotAuthorizedException>();

            (await VersionAsync(CustomerTable, id)).Should().Be(versionBefore);
            (await RowExistsAsync(CustomerTable, id)).Should().BeTrue();
            Logs.EventIds.Should().Contain(9203);
        }
        finally
        {
            Authorization.AllowEverything();
        }
    }

    /// <summary>
    /// An id that cannot be what the column is, and a collection nobody mapped: both are answers, not
    /// exceptions, because both arrive from a URL somebody pasted.
    /// </summary>
    [PostgresFact]
    public async Task A_malformed_id_and_an_unmapped_collection_are_refusals_rather_than_failures()
    {
        var service = CreateService();

        var badId = await service.PreviewAsync(ScopeFor(), CustomerAlias, "not-a-guid", "{}", Token);
        badId.Status.Should().Be(WritePreviewStatus.Refused);
        badId.Reason.Should().Contain("uuid");

        var unmapped = await service.DeleteAsync(ScopeFor(), "mt_doc_something_else", Guid.NewGuid().ToString(), Token);
        unmapped.Outcome.Should().Be(DeleteOutcome.Refused);
        unmapped.Reason.Should().Contain("read-only");
    }

    /// <summary>Invalid JSON never reaches the database, and the message says where it went wrong.</summary>
    [PostgresFact]
    public async Task Invalid_json_is_refused_with_a_position()
    {
        var service = CreateService();
        var id = await CustomerIdAsync("customer18@example.com");
        var versionBefore = await VersionAsync(CustomerTable, id);

        var preview = await service.PreviewAsync(ScopeFor(), CustomerAlias, id.ToString(), "{ \"Name\": }", Token);

        preview.Status.Should().Be(WritePreviewStatus.Refused);
        preview.Reason.Should().Contain("line").And.Contain("column");

        var save = await service.SaveAsync(
            ScopeFor(),
            CustomerAlias,
            id.ToString(),
            "{ \"Name\": }",
            DocumentConcurrencyToken.ForVersion(versionBefore!.Value),
            acknowledgeDrops: true,
            Token);

        save.Status.Should().Be(WriteStatus.Refused);
        (await VersionAsync(CustomerTable, id)).Should().Be(versionBefore);
    }

    // ---------------------------------------------------------------------------------------------
    // The row lock, and the two ways a version column can stop a save from being decidable.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The proof that the <c>for update</c> is load-bearing, without touching production code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Two_saves_racing_on_one_document_produce_one_save_and_one_conflict</c> above shows what the
    /// write service does; it cannot show that the lock is <em>why</em>, because a passing test proves
    /// nothing about the counterfactual. This one runs the same algorithm — read the version, compare it
    /// with what the editor was opened on, write — with <see cref="DocumentRowLock.None" /> instead, out
    /// of the studio's own builder, and shows the lost update that read committed allows: both readers
    /// see the same version, both conclude they are safe, and one of the two edits disappears with
    /// nobody told.
    /// </para>
    /// <para>
    /// Deterministic rather than racy: both reads happen before either write, which is precisely the
    /// interleaving the lock makes impossible.
    /// </para>
    /// </remarks>
    [PostgresFact]
    public async Task Without_the_row_lock_two_saves_lose_one_of_the_edits()
    {
        var id = await CustomerIdAsync("customer01@example.com");
        var table = CustomerTableInfo();
        var before = (await VersionAsync(CustomerTable, id))!.Value;

        await using var first = Store.LightweightSession();
        await using var second = Store.LightweightSession();

        await first.BeginTransactionAsync(Token);
        await second.BeginTransactionAsync(Token);

        // Step one of the save, twice, with no lock: this is the only line that differs from what the
        // write service does.
        var firstSeen = await ReadVersionAsync(first, table, id);
        var secondSeen = await ReadVersionAsync(second, table, id);

        firstSeen.Should().Be(before);
        secondSeen.Should().Be(before, "neither reader locked anything, so both still see the version the other is about to replace");

        // Step two: both compare what they read with what their editor was opened on. Both pass.
        firstSeen.Should().Be(before);
        secondSeen.Should().Be(before);

        var mine = await first.LoadAsync<Customer>(id, Token);
        mine!.Name = "First";
        first.Store(mine);
        await first.SaveChangesAsync(Token);

        var theirs = await second.LoadAsync<Customer>(id, Token);
        theirs!.Name = "Second";
        second.Store(theirs);
        await second.SaveChangesAsync(Token);

        var after = JsonNode.Parse((await StoredJsonAsync(CustomerTable, id))!)!;

        after["Name"]!.GetValue<string>().Should().Be(
            "Second",
            "without the row lock the second writer's guard passed against a version the first writer had " +
            "already replaced - a lost update, which is exactly what `for update` prevents");

        (await VersionAsync(CustomerTable, id)).Should().NotBe(before);
    }

    /// <summary>
    /// A row somebody else is holding produces an answer, not a page that never finishes loading.
    /// </summary>
    /// <remarks>
    /// The competing transaction is a plain connection holding <c>for update</c> on the row, which is
    /// what a forgotten <c>begin;</c> in a psql window looks like from the studio's side. Without
    /// <c>SET LOCAL lock_timeout</c> the save would wait for as long as that transaction lives.
    /// </remarks>
    [PostgresFact]
    public async Task A_row_another_session_is_holding_is_refused_rather_than_waited_on()
    {
        // Halved and capped, so the lock gives up after a second and the command timeout never races it.
        StudioOptions.QueryTimeout = TimeSpan.FromSeconds(2);

        try
        {
            var service = CreateService();
            var id = await CustomerIdAsync("customer02@example.com");
            var stored = await StoredJsonAsync(CustomerTable, id);
            var versionBefore = (await VersionAsync(CustomerTable, id))!.Value;

            await using var competitor = await Postgres.OpenAsync(Token);
            await using var transaction = await competitor.BeginTransactionAsync(Token);

            await using (var hold = new NpgsqlCommand(
                             $"select id from \"{Schema}\".{CustomerTable} where id = '{id}' for update",
                             competitor,
                             transaction))
            {
                await hold.ExecuteScalarAsync(Token);
            }

            var result = await service.SaveAsync(
                ScopeFor(),
                CustomerAlias,
                id.ToString(),
                WithProperty(stored!, "Name", "Blocked"),
                DocumentConcurrencyToken.ForVersion(versionBefore),
                acknowledgeDrops: false,
                Token);

            result.Status.Should().Be(WriteStatus.Refused);
            result.Reason.Should().Contain("another session");

            await transaction.RollbackAsync(Token);

            (await VersionAsync(CustomerTable, id)).Should().Be(versionBefore, "nothing was written");
            JsonNode.Parse((await StoredJsonAsync(CustomerTable, id))!)!["Name"]!.GetValue<string>()
                .Should().NotBe("Blocked");
        }
        finally
        {
            StudioOptions.QueryTimeout = TimeSpan.FromSeconds(30);
        }
    }

    /// <summary>
    /// A version column that is there and empty is a refusal with its own reason — never a conflict,
    /// which would be a loop: reload, get the same nothing, save, be told it conflicts again.
    /// </summary>
    [PostgresFact]
    public async Task A_row_whose_version_column_is_null_is_refused_with_its_own_reason()
    {
        var service = CreateService();
        var id = await NewCustomerAsync("nullversion@example.com");

        // Marten's own schema has mt_version NOT NULL, which is exactly why this state only ever arrives
        // from somebody's hand-written migration - and why the studio has to have an answer for it.
        await ExecuteAsync($"alter table \"{Schema}\".{CustomerTable} alter column mt_version drop not null");
        await ExecuteAsync($"update \"{Schema}\".{CustomerTable} set mt_version = null where id = '{id}'");

        var stored = await StoredJsonAsync(CustomerTable, id);
        var edited = WithProperty(stored!, "Name", "Unversionable");

        var preview = await service.PreviewAsync(ScopeFor(), CustomerAlias, id.ToString(), edited, Token);

        preview.Status.Should().Be(WritePreviewStatus.Ready, "the document can still be read and shown");
        preview.CurrentToken.IsKnown.Should().BeFalse();
        preview.Reason.Should().Contain("empty", "the dialog has to say the save will be refused before the click");

        var result = await service.SaveAsync(
            ScopeFor(),
            CustomerAlias,
            id.ToString(),
            edited,
            DocumentConcurrencyToken.ForVersion(Guid.NewGuid()),
            acknowledgeDrops: true,
            Token);

        result.Status.Should().Be(WriteStatus.Refused);
        result.Status.Should().NotBe(WriteStatus.Conflict);
        result.Reason.Should().Contain("version column is empty");

        (await StoredJsonAsync(CustomerTable, id)).Should().Be(stored, "nothing was written");
    }

    // ---------------------------------------------------------------------------------------------
    // Delete by id: no deserialization, and one read for a whole selection.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A row whose JSON no CLR type can read is still a row somebody has to be able to get rid of —
    /// which is only true if the delete never materialises the document.
    /// </summary>
    [PostgresFact]
    public async Task A_row_that_cannot_be_deserialized_can_still_be_deleted()
    {
        var service = CreateService();
        var id = Guid.NewGuid();

        await InsertUnreadableCustomerAsync(id, "unreadable@example.com");

        // It really is unreadable: the preview, which does need the document, says so.
        var preview = await service.PreviewAsync(
            ScopeFor(), CustomerAlias, id.ToString(), (await StoredJsonAsync(CustomerTable, id))!, Token);

        preview.Status.Should().Be(WritePreviewStatus.Refused);
        preview.TypeIsConstructible.Should().BeFalse();

        var result = await service.DeleteAsync(ScopeFor(), CustomerAlias, id.ToString(), Token);

        result.Outcome.Should().Be(DeleteOutcome.HardDeleted);
        (await RowExistsAsync(CustomerTable, id)).Should().BeFalse();
    }

    /// <summary>A selection may name the same document twice; it gets two answers and one delete.</summary>
    [PostgresFact]
    public async Task A_bulk_delete_answers_every_id_even_when_one_is_repeated()
    {
        var service = CreateService();
        var id = await NewCustomerAsync("bulk-one@example.com");
        var other = await NewCustomerAsync("bulk-two@example.com");

        var result = await service.BulkDeleteAsync(
            ScopeFor(), CustomerAlias, [id.ToString(), other.ToString(), id.ToString()], Token);

        result.Accepted.Should().BeTrue();
        result.Results.Should().HaveCount(3);
        result.Results.Should().OnlyContain(x => x.Outcome == DeleteOutcome.HardDeleted);

        (await RowExistsAsync(CustomerTable, id)).Should().BeFalse();
        (await RowExistsAsync(CustomerTable, other)).Should().BeFalse();
    }

    /// <summary>An id that cannot be what the column is refuses only itself; the rest of the batch runs.</summary>
    [PostgresFact]
    public async Task A_bulk_delete_refuses_the_unusable_id_and_deletes_the_rest()
    {
        var service = CreateService();
        var id = await NewCustomerAsync("bulk-three@example.com");

        var result = await service.BulkDeleteAsync(
            ScopeFor(), CustomerAlias, ["not-a-guid", id.ToString()], Token);

        result.Accepted.Should().BeTrue();
        result.Results[0].Outcome.Should().Be(DeleteOutcome.Refused);
        result.Results[0].Reason.Should().Contain("uuid");
        result.Results[1].Outcome.Should().Be(DeleteOutcome.HardDeleted);

        (await RowExistsAsync(CustomerTable, id)).Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------------------
    // Undelete without a body rewrite.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// B2: bringing an order back changes <c>mt_deleted</c> and <c>mt_deleted_at</c> and nothing else —
    /// the JSON is the same bytes afterwards, unknown properties included.
    /// </summary>
    /// <remarks>
    /// The order's id is a strong-typed <c>OrderId</c>, which is the identity shape the
    /// <c>x =&gt; x.Id == id</c> expression has to be built for; an undelete that fell back to
    /// deserializing and storing would drop <c>legacyFlag</c> on the way and the assertion below is what
    /// would catch it.
    /// </remarks>
    [PostgresFact]
    public async Task Undeleting_an_order_keeps_its_unknown_properties_byte_for_byte()
    {
        var service = CreateService();
        var id = await OrderIdAsync("ORD-2026-0009");

        await ExecuteAsync(
            $"update \"{Schema}\".{OrderTable} set data = jsonb_set(data, '{{legacyFlag}}', 'true') where id = '{id}'");

        var deleted = await service.DeleteAsync(ScopeFor(), OrderAlias, id.ToString(), Token);
        deleted.Outcome.Should().Be(DeleteOutcome.SoftDeleted);

        var before = await StoredJsonAsync(OrderTable, id);
        var versionBefore = await VersionAsync(OrderTable, id);
        before.Should().Contain("legacyFlag");

        var undeleted = await service.UndeleteAsync(ScopeFor(), OrderAlias, id.ToString(), Token);

        undeleted.Outcome.Should().Be(DeleteOutcome.Undeleted);
        (await IsDeletedAsync(OrderTable, id)).Should().BeFalse();
        (await ScalarAsync($"select mt_deleted_at from \"{Schema}\".{OrderTable} where id = '{id}'"))
            .Should().BeNull();

        (await StoredJsonAsync(OrderTable, id)).Should().Be(
            before, "the undelete is an update of two columns and must not touch `data`");
        (await VersionAsync(OrderTable, id)).Should().Be(
            versionBefore, "nor mt_version: nobody changed the document");
    }

    /// <summary>
    /// A customer of this test's own, so a delete cannot trip over the demo orders' foreign key and two
    /// tests can never disagree about whose row they were using.
    /// </summary>
    private async Task<Guid> NewCustomerAsync(string email)
    {
        var customer = new Customer { Id = Guid.NewGuid(), Name = "Ad hoc", Email = email };

        await using var session = Store.LightweightSession();
        session.Store(customer);
        await session.SaveChangesAsync(Token);

        return customer.Id;
    }

    private DocumentTableInfo CustomerTableInfo() =>
        DocumentTableInfo.FromDocumentType(Store.Options.FindOrResolveDocumentType(typeof(Customer)));

    /// <summary>
    /// The write service's version read, run through the studio's own builder with the lock left off.
    /// </summary>
    private static async Task<Guid> ReadVersionAsync(IDocumentSession session, DocumentTableInfo table, Guid id)
    {
        DocumentQueryBuilder.TryBuildSingle(table, id.ToString(), null, DocumentRowLock.None, out var command, out _)
            .Should().BeTrue();

        await using (command)
        {
            await using var reader = await session.ExecuteReaderAsync(command!, Token);
            (await reader.ReadAsync(Token)).Should().BeTrue();

            return reader.GetGuid(reader.GetOrdinal("mt_version"));
        }
    }

    /// <summary>Puts a row into the customer table whose JSON no <c>Customer</c> can be built from.</summary>
    private Task InsertUnreadableCustomerAsync(Guid id, string email) =>
        ExecuteAsync(
            $$"""
              insert into "{{Schema}}".{{CustomerTable}} (id, data, email, mt_dotnet_type)
              values (
                  '{{id}}'::uuid,
                  '{"Id":"{{id}}","Name":"Broken","Email":"{{email}}","Tags":"not-an-array"}'::jsonb,
                  '{{email}}',
                  'MartenStudio.SampleDomain.Documents.Customer')
              """);

    private static string WithProperty(string json, string name, JsonNode? value)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node[name] = value;
        return node.ToJsonString();
    }
}
