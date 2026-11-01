using System.Text.Json.Nodes;

using JasperFx.Events.Projections;

using Marten;

using MartenStudio.Integration.Tests.Projections;
using MartenStudio.Services.Documents;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>An event that opens a gauge stream.</summary>
/// <param name="Label">What the gauge is called.</param>
public record GaugeOpened(string Label);

/// <summary>An event that raises a gauge by one.</summary>
public record GaugeRaised;

/// <summary>A projection-owned document, kept up to date inline.</summary>
public class InlineGauge
{
    /// <summary>The stream id.</summary>
    public Guid Id { get; set; }

    /// <summary>Set by the first event; the studio edit changes it.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>How many times it was raised.</summary>
    public int Count { get; set; }

    /// <summary>Starts the document.</summary>
    public static InlineGauge Create(GaugeOpened opened) => new() { Label = opened.Label };

    /// <summary>Raises it.</summary>
    public void Apply(GaugeRaised raised) => Count++;
}

/// <summary>The same shape as <see cref="InlineGauge" />, kept up to date by the async daemon.</summary>
public class AsyncGauge
{
    /// <summary>The stream id.</summary>
    public Guid Id { get; set; }

    /// <summary>Set by the first event; the studio edit changes it.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>How many times it was raised.</summary>
    public int Count { get; set; }

    /// <summary>Starts the document.</summary>
    public static AsyncGauge Create(GaugeOpened opened) => new() { Label = opened.Label };

    /// <summary>Raises it.</summary>
    public void Apply(GaugeRaised raised) => Count++;
}

/// <summary>An inline projection document with soft delete, for the undelete path.</summary>
public class SoftGauge
{
    /// <summary>The stream id.</summary>
    public Guid Id { get; set; }

    /// <summary>Set by the first event.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>How many times it was raised.</summary>
    public int Count { get; set; }

    /// <summary>Starts the document.</summary>
    public static SoftGauge Create(GaugeOpened opened) => new() { Label = opened.Label };

    /// <summary>Raises it.</summary>
    public void Apply(GaugeRaised raised) => Count++;
}

/// <summary>
/// F1: the studio's edit of a document a projection owns must not make the projection skip the next event.
/// </summary>
/// <remarks>
/// Marten 9.55 (#5773): for a mapping with <c>UseVersionFromMatchingStream</c> and numeric revisions the revision is the
/// version of the stream the snapshot reflects, and the studio's save moves it with <c>UpdateRevision(revision + 1)</c>.
/// These tests edit through <see cref="IDocumentWriteService" />, append one more event and ask whether the document
/// reflects it, for an inline projection, an async one, and an undelete. Only the async fetch path is affected:
/// <see cref="An_async_fetch_after_a_studio_edit_still_sees_the_event_the_daemon_has_not_yet_projected" /> is the test
/// that fails without the refusal (measured on the base commit at the floor: Count 2, not 3). The inline and
/// daemon-running variants pass on the base too; they stay as guards for those paths, not as proof of the fix.
/// </remarks>
public class ProjectionDocumentEditLiveTests(PostgresFixture postgres) : DocumentWriteFixture(postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    protected override bool SeedSampleData => false;

    /// <inheritdoc />
    protected override void ConfigureExtras(StoreOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        opts.Projections.Snapshot<InlineGauge>(SnapshotLifecycle.Inline);
        opts.Projections.Snapshot<AsyncGauge>(SnapshotLifecycle.Async);
        opts.Projections.Snapshot<SoftGauge>(SnapshotLifecycle.Inline);
        opts.Schema.For<SoftGauge>().SoftDeleted();
    }

    /// <summary>The mapping flag the guard keys on is set for all three projection documents.</summary>
    [PostgresFact]
    public void Projection_documents_use_the_version_of_the_matching_stream()
    {
        foreach (Type type in new[] { typeof(InlineGauge), typeof(AsyncGauge), typeof(SoftGauge) })
        {
            var mapping = (Marten.Schema.DocumentMapping) Store.Options.AllKnownDocumentTypes().Single(x => x.DocumentType == type);
            mapping.UseVersionFromMatchingStream.Should().BeTrue(type.Name);
            mapping.UseNumericRevisions.Should().BeTrue(type.Name);
        }
    }

    /// <summary>
    /// Edit an inline projection's document, then append an event: the document must reflect it. This variant is not
    /// affected by #5773 and passes without the refusal; it guards the inline path. The fix's proof is
    /// <see cref="An_async_fetch_after_a_studio_edit_still_sees_the_event_the_daemon_has_not_yet_projected" />.
    /// </summary>
    [PostgresFact]
    public async Task An_inline_projection_document_still_reflects_the_next_event_after_a_studio_edit()
    {
        Users.SignIn("ops");
        Guid id = await OpenStreamAsync();

        await TryEditLabelAsync("inlinegauge", "mt_doc_inlinegauge", id, "edited");
        Func<Task> append = () => AppendRaisedAsync(id);
        await append.Should().NotThrowAsync("the inline projection must apply the next event to the edited document");

        JsonNode.Parse((await StoredJsonAsync("mt_doc_inlinegauge", id))!)!["Count"]!.GetValue<int>().Should().Be(3);
    }

    /// <summary>
    /// Edit an async projection's document while the daemon runs, then append an event. This variant is not affected
    /// by #5773 and passes without the refusal; it guards the running-daemon path. The fix's proof is
    /// <see cref="An_async_fetch_after_a_studio_edit_still_sees_the_event_the_daemon_has_not_yet_projected" />.
    /// </summary>
    [PostgresFact]
    public async Task An_async_projection_document_still_reflects_the_next_event_after_a_studio_edit()
    {
        Users.SignIn("ops");
        using var daemon = await Store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        Guid id = await OpenStreamAsync();
        await WaitForCountAsync("mt_doc_asyncgauge", id, 2);

        await TryEditLabelAsync("asyncgauge", "mt_doc_asyncgauge", id, "edited");
        await AppendRaisedAsync(id);

        await WaitForCountAsync("mt_doc_asyncgauge", id, 3);

        await using var query = Store.LightweightSession();
        var latest = await query.Events.FetchLatest<AsyncGauge>(id, Token);
        latest!.Count.Should().Be(3);

        await daemon.StopAllAsync();
    }

    /// <summary>
    /// Edit an async projection's document while the daemon is stopped, then append: the async fetch (FetchLatest)
    /// reads the events above the document's revision, which is where #5773 says the next event is skipped.
    /// </summary>
    [PostgresFact]
    public async Task An_async_fetch_after_a_studio_edit_still_sees_the_event_the_daemon_has_not_yet_projected()
    {
        Users.SignIn("ops");
        using var daemon = await Store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        Guid id = await OpenStreamAsync();
        await WaitForCountAsync("mt_doc_asyncgauge", id, 2);
        await daemon.StopAllAsync();

        await TryEditLabelAsync("asyncgauge", "mt_doc_asyncgauge", id, "edited");
        await AppendRaisedAsync(id);

        await using var query = Store.LightweightSession();
        var latest = await query.Events.FetchLatest<AsyncGauge>(id, Token);
        latest!.Count.Should().Be(3, "FetchLatest folds the events above the stored revision onto the stored document");
    }

    /// <summary>Delete then undelete a soft-deleted projection document, then append: the document reflects it.</summary>
    [PostgresFact]
    public async Task An_undeleted_inline_projection_document_still_reflects_the_next_event()
    {
        Users.SignIn("ops");
        Guid id = await OpenStreamAsync();

        var service = CreateService();
        (await service.DeleteAsync(ScopeFor(), "softgauge", id.ToString(), Token)).Changed.Should().BeTrue();
        long? revisionDeleted = await RevisionAsync("mt_doc_softgauge", id);
        (await service.UndeleteAsync(ScopeFor(), "softgauge", id.ToString(), Token)).Changed.Should().BeTrue();
        (await RevisionAsync("mt_doc_softgauge", id)).Should().Be(revisionDeleted, "undelete does not move the revision");

        await AppendRaisedAsync(id);

        JsonNode.Parse((await StoredJsonAsync("mt_doc_softgauge", id))!)!["Count"]!.GetValue<int>().Should().Be(3);
    }

    /// <summary>The preview and the save of a projection-owned document are refused, audited, and write nothing.</summary>
    [PostgresTheory]
    [InlineData("inlinegauge", "mt_doc_inlinegauge")]
    [InlineData("asyncgauge", "mt_doc_asyncgauge")]
    [InlineData("softgauge", "mt_doc_softgauge")]
    public async Task The_edit_of_a_projection_owned_document_is_refused_before_anything_is_written(string alias, string table)
    {
        Users.SignIn("ops");

        // The async document's table is the daemon's to create, so it runs for that case.
        using var daemon = alias == "asyncgauge" ? await Store.BuildProjectionDaemonAsync() : null;
        if (daemon is not null)
        {
            await daemon.StartAllAsync();
        }

        Guid id = await OpenStreamAsync();
        if (daemon is not null)
        {
            await WaitForCountAsync(table, id, 2);
        }

        string before = (await StoredJsonAsync(table, id))!;
        long? revision = await RevisionAsync(table, id);

        var (preview, saved) = await TryEditLabelAsync(alias, table, id, "edited");

        preview.Status.Should().Be(WritePreviewStatus.Refused);
        preview.Reason.Should().Contain("written by its projection").And.Contain(id.ToString());
        saved.Status.Should().Be(WriteStatus.Refused);
        saved.Reason.Should().Be(preview.Reason);

        (await StoredJsonAsync(table, id)).Should().Be(before);
        (await RevisionAsync(table, id)).Should().Be(revision);

        Ring.GetLatest().Should().Contain(x => x.Action == "EditDocument" && !x.Succeeded)
            .Which.Message.Should().NotContain(id.ToString(), "the audit line never carries the id the visitor typed");

        if (daemon is not null)
        {
            await daemon.StopAllAsync();
        }
    }

    /// <summary>A delete stays allowed: a rebuild recreates the document.</summary>
    [PostgresFact]
    public async Task A_projection_owned_document_can_still_be_deleted()
    {
        Users.SignIn("ops");
        Guid id = await OpenStreamAsync();

        var result = await CreateService().DeleteAsync(ScopeFor(), "inlinegauge", id.ToString(), Token);

        result.Changed.Should().BeTrue();
        (await RowExistsAsync("mt_doc_inlinegauge", id)).Should().BeFalse();
    }

    private async Task<Guid> OpenStreamAsync()
    {
        Guid id = Guid.NewGuid();
        await using IDocumentSession session = Store.LightweightSession();
        session.Events.StartStream(id, new GaugeOpened("gauge"), new GaugeRaised());
        session.Events.Append(id, new GaugeRaised());
        await session.SaveChangesAsync(Token);
        return id;
    }

    private async Task AppendRaisedAsync(Guid id)
    {
        await using IDocumentSession session = Store.LightweightSession();
        session.Events.Append(id, new GaugeRaised());
        await session.SaveChangesAsync(Token);
    }

    /// <summary>
    /// Previews and saves an edit exactly as the page does, and reports what came back. It asserts nothing about the
    /// outcome: the tests that are about a later event must hold whether the studio saved the edit or refused it.
    /// </summary>
    private async Task<(WritePreview Preview, WriteResult Saved)> TryEditLabelAsync(string alias, string table, Guid id, string label)
    {
        var service = CreateService();
        string edited = WithLabel((await StoredJsonAsync(table, id))!, label);

        var preview = await service.PreviewAsync(ScopeFor(), alias, id.ToString(), edited, Token);
        var saved = await service.SaveAsync(
            ScopeFor(), alias, id.ToString(), edited, preview.CurrentToken, acknowledgeDrops: false, Token);

        return (preview, saved);
    }

    private async Task<long?> RevisionAsync(string table, Guid id) =>
        Convert.ToInt64(await ScalarAsync($"select mt_version from \"{Schema}\".{table} where id = '{id}'"), System.Globalization.CultureInfo.InvariantCulture);

    private Task WaitForCountAsync(string table, Guid id, int count) =>
        ProjectionsFixture.WaitForAsync(
            async () =>
            {
                try
                {
                    string? json = await StoredJsonAsync(table, id);
                    return json is not null && JsonNode.Parse(json)!["Count"]!.GetValue<int>() == count;
                }
                catch (Npgsql.PostgresException ex) when (ex.SqlState == "42P01")
                {
                    // The daemon has not created the table yet.
                    return false;
                }
            },
            $"{table} to reach Count {count}",
            TimeSpan.FromSeconds(60),
            Token);

    private static string WithLabel(string json, string label)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        node["Label"] = label;
        return node.ToJsonString();
    }
}
