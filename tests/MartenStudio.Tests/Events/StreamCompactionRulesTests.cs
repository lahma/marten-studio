using JasperFx.Events;
using JasperFx.Events.Projections;

using Marten;

using MartenStudio.Services.Events;
using MartenStudio.Tests.Sql;

using StreamState = MartenStudio.Services.Events.StreamState;

namespace MartenStudio.Tests.Events;

/// <summary>
/// F3's rules as pure functions: what is offered, what is refused, and what the dialog says - no Postgres.
/// </summary>
public class StreamCompactionRulesTests
{
    private const string StreamId = "11111111-1111-1111-1111-111111111111";

    private static readonly CompactionAggregate Order = new(typeof(TimeTravelOrder), "time_travel_order");

    // ------------------------------------------------------------------------------------------------
    // How many events a stream holds
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(7, 0, 7)]
    [InlineData(7, 7, 1)]
    [InlineData(9, 7, 3)]
    [InlineData(3, 9, 1)]
    public void A_stream_holds_its_version_or_the_snapshot_and_what_came_after(long version, long compacted, long expected) =>
        StreamCompactionRules.EventsInStream(version, compacted).Should().Be(expected);

    // ------------------------------------------------------------------------------------------------
    // What is offered
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void An_aggregate_stream_with_events_to_fold_is_offered_with_its_facts()
    {
        CompactionPlan plan = Plan(Stream(version: 12));

        plan.IsOffered.Should().BeTrue();
        plan.AggregateName.Should().Be(nameof(TimeTravelOrder));
        plan.Version.Should().Be(12);
        plan.EventsToFold.Should().Be(12);
        plan.NeedsTenant.Should().BeFalse();
        plan.TagTablesMayRefuse.Should().BeFalse();
    }

    [Fact]
    public void A_missing_stream_is_not_offered()
    {
        CompactionPlan plan = StreamCompactionRules.Plan(
            StreamState.Missing(StreamId), StreamIdentity.AsGuid, Order, CompactionUnavailableReason.None, false, null, EventTagStorage.None, false);

        plan.Reason.Should().Be(CompactionUnavailableReason.StreamMissing);
        plan.IsOffered.Should().BeFalse();
    }

    /// <summary>Marten's stream fetch never returns archived events, so compacting one would do nothing.</summary>
    [Fact]
    public void An_archived_stream_is_not_offered_because_compaction_would_read_nothing()
    {
        CompactionPlan plan = Plan(Stream(version: 12, archived: true));

        plan.Reason.Should().Be(CompactionUnavailableReason.Archived);
        plan.Explanation.Should().Contain("never returns archived events");
    }

    [Theory]
    [InlineData(nameof(CompactionUnavailableReason.Untyped))]
    [InlineData(nameof(CompactionUnavailableReason.AggregateNotRegistered))]
    [InlineData(nameof(CompactionUnavailableReason.AggregateAmbiguous))]
    public void A_stream_whose_type_does_not_resolve_says_why(string reasonName)
    {
        CompactionUnavailableReason reason = Enum.Parse<CompactionUnavailableReason>(reasonName);

        CompactionPlan plan = StreamCompactionRules.Plan(
            Stream(version: 12), StreamIdentity.AsGuid, aggregate: null, reason, false, null, EventTagStorage.None, false);

        plan.Reason.Should().Be(reason);
        plan.Explanation.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(5, 5)]
    public void A_stream_with_nothing_to_fold_is_not_offered(long version, long compacted)
    {
        CompactionPlan plan = Plan(Stream(version, compacted));

        plan.Reason.Should().Be(CompactionUnavailableReason.NothingToFold);
    }

    [Fact]
    public void A_stream_compacted_before_with_new_events_since_is_offered_again()
    {
        CompactionPlan plan = Plan(Stream(version: 9, compacted: 7));

        plan.IsOffered.Should().BeTrue();
        plan.WasCompacted.Should().BeTrue();
        plan.EventsToFold.Should().Be(3, "the Compacted<T> at version 7 and the two events after it");
    }

    /// <summary>
    /// A conjoined store's session always has one tenant, and the "all tenants" scope's is <c>*DEFAULT*</c> -
    /// the default tenant's stream of the same id, which is what must never be compacted in its place.
    /// </summary>
    [Fact]
    public void A_conjoined_stream_from_an_all_tenants_scope_needs_its_tenant_picked_first()
    {
        CompactionPlan plan = StreamCompactionRules.Plan(
            Stream(version: 4, tenantId: "acme"), StreamIdentity.AsGuid, Order, CompactionUnavailableReason.None,
            isConjoined: true, scopeTenantId: null, EventTagStorage.None, skipping: false);

        plan.IsOffered.Should().BeTrue("the stream can be compacted, once its tenant is chosen");
        plan.NeedsTenant.Should().BeTrue();
        StreamCompactionRules.TenantRequired(plan.StreamTenantId).Should().Contain("tenant acme");
    }

    [Fact]
    public void A_conjoined_stream_in_its_own_tenants_scope_needs_nothing_more()
    {
        CompactionPlan plan = StreamCompactionRules.Plan(
            Stream(version: 4, tenantId: "acme"), StreamIdentity.AsGuid, Order, CompactionUnavailableReason.None,
            isConjoined: true, scopeTenantId: "acme", EventTagStorage.None, skipping: false);

        plan.NeedsTenant.Should().BeFalse();
        plan.StreamTenantId.Should().Be("acme");
    }

    [Fact]
    public void A_read_failure_is_a_plan_that_says_so()
    {
        CompactionPlan plan = Plan(StreamState.Failed(StreamId, new EventDataError("connection refused", "08006", true)));

        plan.IsOffered.Should().BeFalse();
        plan.Error.Should().NotBeNull();
        plan.Explanation.Should().Contain("connection refused");
    }

    // ------------------------------------------------------------------------------------------------
    // The typed confirmation
    // ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(StreamId, true)]
    [InlineData("  " + StreamId + "  ", true)]
    [InlineData("11111111-1111-1111-1111-11111111111", false)]
    [InlineData("22222222-2222-2222-2222-222222222222", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_confirmation_is_the_stream_id_as_the_database_holds_it(string? typed, bool matches) =>
        StreamCompactionRules.ConfirmationMatches(typed, StreamId).Should().Be(matches);

    // ------------------------------------------------------------------------------------------------
    // Resolving the aggregate type
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_registered_single_stream_aggregate_resolves_from_its_alias_ignoring_case()
    {
        IReadOnlyList<CompactionAggregate> registered = Registered(options =>
            options.Projections.Snapshot<TimeTravelOrder>(SnapshotLifecycle.Inline));

        registered.Should().Contain(x => x.Type == typeof(TimeTravelOrder));

        string alias = registered.Single(x => x.Type == typeof(TimeTravelOrder)).Alias;

        (CompactionAggregate? found, CompactionUnavailableReason reason) =
            CompactionAggregates.Resolve(registered, alias.ToUpperInvariant());

        reason.Should().Be(CompactionUnavailableReason.None);
        found!.Type.Should().Be<TimeTravelOrder>();
    }

    /// <summary>
    /// A document type is not an aggregate Marten registers a fold for, so it is not offered - the time-travel
    /// picker's "try any type" fallback is not a thing compaction may try, because compaction deletes.
    /// </summary>
    [Fact]
    public void A_plain_document_type_is_not_something_to_compact_into()
    {
        IReadOnlyList<CompactionAggregate> registered = Registered(options => options.Schema.For<TimeTravelNote>());

        registered.Should().NotContain(x => x.Type == typeof(TimeTravelNote));
    }

    [Fact]
    public void An_untyped_stream_and_an_unknown_alias_are_two_different_answers()
    {
        IReadOnlyList<CompactionAggregate> registered = [Order];

        CompactionAggregates.Resolve(registered, null).Reason.Should().Be(CompactionUnavailableReason.Untyped);
        CompactionAggregates.Resolve(registered, "  ").Reason.Should().Be(CompactionUnavailableReason.Untyped);
        CompactionAggregates.Resolve(registered, "invoice").Reason.Should().Be(CompactionUnavailableReason.AggregateNotRegistered);
    }

    [Fact]
    public void Two_types_answering_to_one_alias_are_refused_rather_than_guessed()
    {
        IReadOnlyList<CompactionAggregate> registered =
        [
            Order,
            new CompactionAggregate(typeof(TimeTravelSummary), "time_travel_order"),
        ];

        CompactionAggregates.Resolve(registered, "time_travel_order").Reason
            .Should().Be(CompactionUnavailableReason.AggregateAmbiguous);
    }

    // ------------------------------------------------------------------------------------------------
    // What the dialog says
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Every fact the packet asks the dialog to state: what is replaced, what is deleted, that no archiver
    /// runs, what happens to the version and to async projections - each read from Marten's own source.
    /// </summary>
    [Fact]
    public void The_dialog_says_what_is_replaced_what_is_deleted_and_that_nothing_is_archived()
    {
        string said = string.Join(" ", StreamCompactionRules.WhatHappens(Plan(Stream(version: 12))));

        said.Should().Contain("one Compacted<TimeTravelOrder> event in place of the last one");
        said.Should().Contain("The 11 events before it");
        said.Should().Contain("deleted from mt_events");
        said.Should().Contain("No archiver runs");
        said.Should().Contain("gone, not moved");
        said.Should().Contain("version stays 12").And.Contain("compacted_version becomes 12");
        said.Should().Contain("Async projections");
        said.Should().Contain("A rebuild afterwards replays only the snapshot");
        said.Should().Contain("one transaction");
    }

    [Fact]
    public void The_dialog_names_the_tenant_the_tag_refusal_and_the_skipped_events_only_where_they_apply()
    {
        string plain = string.Join(" ", StreamCompactionRules.WhatHappens(Plan(Stream(version: 4))));

        plain.Should().NotContain("tenant").And.NotContain("DCB tags").And.NotContain("skip");

        CompactionPlan everything = StreamCompactionRules.Plan(
            Stream(version: 4, tenantId: "acme"), StreamIdentity.AsGuid, Order, CompactionUnavailableReason.None,
            isConjoined: true, scopeTenantId: "acme", EventTagStorage.TagTables, skipping: true);

        string said = string.Join(" ", StreamCompactionRules.WhatHappens(everything));

        said.Should().Contain("Only tenant acme's stream with this id is touched");
        said.Should().Contain("foreign key with no cascade");
        said.Should().Contain("neither folded nor deleted");
        said.Should().Contain("If the event at version 4 is one of them").And.Contain("compacted through that version instead");
    }

    [Fact]
    public void A_second_compaction_says_the_earlier_snapshot_is_folded_in()
    {
        string said = string.Join(" ", StreamCompactionRules.WhatHappens(Plan(Stream(version: 9, compacted: 7))));

        said.Should().Contain("including the Compacted<TimeTravelOrder> an earlier compaction left at version 7");
    }

    // ------------------------------------------------------------------------------------------------
    // EV-fix: the id Marten reads as "no stream filter"
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>EventStatement</c> emits its stream predicate only for a non-empty id, so compacting the empty GUID
    /// would fold and delete every event of the log. Refused before anything else about the stream.
    /// </summary>
    [Fact]
    public void A_stream_whose_id_is_the_empty_guid_is_never_offered()
    {
        CompactionPlan plan = Plan(Stream(version: 12, id: Guid.Empty.ToString()));

        plan.IsOffered.Should().BeFalse();
        plan.Reason.Should().Be(CompactionUnavailableReason.EmptyStreamId);
        plan.Explanation.Should().Contain("every event in the log");
    }

    [Fact]
    public void An_empty_key_on_a_string_store_is_refused_and_a_key_that_merely_looks_like_the_empty_guid_is_not()
    {
        StreamCompactionRules.IsWholeLogId(StreamIdentity.AsString, string.Empty).Should().BeTrue();
        StreamCompactionRules.IsWholeLogId(StreamIdentity.AsString, Guid.Empty.ToString()).Should().BeFalse(
            "on a string store that is an ordinary key, and Marten emits the predicate for it");
        StreamCompactionRules.IsWholeLogId(StreamIdentity.AsGuid, Guid.Empty.ToString()).Should().BeTrue();
        StreamCompactionRules.IsWholeLogId(StreamIdentity.AsGuid, StreamId).Should().BeFalse();

        StreamCompactionRules.IsWholeLogId(Guid.Empty).Should().BeTrue();
        StreamCompactionRules.IsWholeLogId(string.Empty).Should().BeTrue();
        StreamCompactionRules.IsWholeLogId(Guid.Parse(StreamId)).Should().BeFalse();
        StreamCompactionRules.IsWholeLogId("order-1").Should().BeFalse();
        StreamCompactionRules.IsWholeLogId(null).Should().BeTrue("an id of no known shape is not one to hand Marten");
    }

    /// <summary>The invoker refuses it too, before any session is touched: three lines, not one.</summary>
    [Fact]
    public async Task The_invoker_refuses_the_empty_guid_before_it_reaches_marten()
    {
        using DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = SqlTestStore.Schema;
        });

        await using IDocumentSession session = store.LightweightSession();

        Func<Task> compact = () => StreamCompactionInvoker.CompactAsync(
            session, typeof(TimeTravelOrder), Guid.Empty, version: 3, TestContext.Current.CancellationToken);

        await compact.Should().ThrowAsync<ArgumentException>().WithMessage("*whole event log*");
    }

    // ------------------------------------------------------------------------------------------------
    // EV-fix: hidden aggregates
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void An_aggregate_the_host_hides_is_refused_as_hidden_and_not_named()
    {
        (CompactionAggregate? found, CompactionUnavailableReason reason) = CompactionAggregates.Resolve(
            [Order], "time_travel_order", isVisible: static type => type != typeof(TimeTravelOrder));

        found.Should().BeNull();
        reason.Should().Be(CompactionUnavailableReason.AggregateHidden);
        StreamCompactionRules.Explain(reason).Should().NotContain(nameof(TimeTravelOrder));

        CompactionAggregates.Resolve([Order], "time_travel_order", isVisible: static _ => true).Reason
            .Should().Be(CompactionUnavailableReason.None);
    }

    // ------------------------------------------------------------------------------------------------
    // EV-fix: a single-tenanted store's *DEFAULT* is not a tenant the compaction is narrowed to
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_single_tenanted_stream_names_no_tenant_in_the_plan_or_the_dialog()
    {
        CompactionPlan plan = Plan(Stream(version: 4));

        plan.StreamTenantId.Should().BeNull("mt_streams.tenant_id is *DEFAULT* on every row of a single-tenanted store");
        string.Join(" ", StreamCompactionRules.WhatHappens(plan)).Should().NotContain("*DEFAULT*").And.NotContain("Only tenant");
    }

    // ------------------------------------------------------------------------------------------------
    // EV-fix: what runs is what the dialog described, and the audit says what happened
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void The_dialog_says_marten_stops_at_its_version()
    {
        string said = string.Join(" ", StreamCompactionRules.WhatHappens(Plan(Stream(version: 12))));

        said.Should().Contain("Marten is told to stop at version 12").And.Contain("appended after the dialog opened");
    }

    [Theory]
    [InlineData(3, 3, 0, null)]
    [InlineData(3, 5, 0, null)]
    [InlineData(5, 3, 0, "not at")]
    [InlineData(0, 3, 0, "not at")]
    [InlineData(3, 5, 3, "already been compacted")]
    [InlineData(1, 5, 0, "nothing to fold")]
    public void A_cutoff_is_a_version_the_stream_reached_and_one_past_its_last_compaction(
        long planned, long current, long compacted, string? problem)
    {
        string? found = StreamCompactionRules.CutoffProblem(planned, Stream(version: current, compacted: compacted));

        if (problem is null)
        {
            found.Should().BeNull();
        }
        else
        {
            found.Should().Contain(problem);
        }
    }

    [Fact]
    public void A_save_that_moved_the_watermark_to_the_dialogs_version_is_that_compaction()
    {
        (CompactionVerdict verdict, string outcome) =
            StreamCompactionRules.Verify(3, 0, Stream(version: 5, compacted: 3), "TimeTravelOrder", "acme", skipping: false);

        verdict.Should().Be(CompactionVerdict.Compacted);
        outcome.Should().Be("compacted through version 3 into one Compacted<TimeTravelOrder> event in tenant acme");
    }

    [Fact]
    public void A_save_that_did_not_move_the_watermark_compacted_nothing()
    {
        (CompactionVerdict archived, string archivedOutcome) =
            StreamCompactionRules.Verify(3, 0, Stream(version: 3, archived: true), "TimeTravelOrder", null, skipping: false);
        archived.Should().Be(CompactionVerdict.NothingCompacted, "Marten's fetch leaves archived events out, so it committed nothing");
        archivedOutcome.Should().Contain("Nothing was compacted").And.Contain("archived");

        (CompactionVerdict untouched, string untouchedOutcome) =
            StreamCompactionRules.Verify(3, 0, Stream(version: 3), "TimeTravelOrder", null, skipping: false);
        untouched.Should().Be(CompactionVerdict.NothingCompacted);
        untouchedOutcome.Should().Contain("compacted_version is still 0");

        // A retry after a compaction that stopped short: the watermark the first one left is where this one finds it.
        (CompactionVerdict retried, string retriedOutcome) =
            StreamCompactionRules.Verify(5, 4, Stream(version: 5, compacted: 4), "TimeTravelOrder", null, skipping: true);
        retried.Should().Be(CompactionVerdict.NothingCompacted);
        retriedOutcome.Should().Contain("still 4").And.Contain("skipped");
    }

    /// <summary>
    /// B1. Marten folds through the last event its fetch returned, and on a skipping store the fetch leaves the
    /// skipped ones out: the dialog's version 5 being a skipped event means a compaction through 4. That is a
    /// compaction - events 1 to 3 are gone and 4 is the snapshot - and it is said as one, with the reason.
    /// </summary>
    [Fact]
    public void A_compaction_that_stopped_short_of_a_skipped_event_is_a_compaction_through_the_version_marten_folded_to()
    {
        (CompactionVerdict one, string oneOutcome) =
            StreamCompactionRules.Verify(5, 0, Stream(version: 5, compacted: 4), "TimeTravelOrder", null, skipping: true);

        one.Should().Be(CompactionVerdict.Compacted);
        oneOutcome.Should().Be(
            "compacted through version 4 into one Compacted<TimeTravelOrder> event, not through version 5: version 5 is a skipped event, "
            + "which Marten's stream fetch leaves out, so it stays in the stream after the snapshot");

        (CompactionVerdict two, string twoOutcome) =
            StreamCompactionRules.Verify(6, 2, Stream(version: 6, compacted: 4), "TimeTravelOrder", null, skipping: true);

        two.Should().Be(CompactionVerdict.Compacted, "the watermark moved from 2, the read just before the save");
        twoOutcome.Should().Contain("through version 4").And.Contain("versions 5 to 6 are skipped events").And.Contain("they stay");

        StreamCompactionRules.Verify(5, 0, Stream(version: 5, compacted: 4), "TimeTravelOrder", null, skipping: false)
            .Outcome.Should().Contain("Marten's stream fetch did not return version 5").And.NotContain("skipped");
    }

    [Fact]
    public void A_save_whose_result_cannot_be_read_back_is_saved_and_not_confirmed()
    {
        (CompactionVerdict gone, string goneOutcome) =
            StreamCompactionRules.Verify(3, 0, StreamState.Missing(StreamId), "TimeTravelOrder", null, skipping: false);
        gone.Should().Be(CompactionVerdict.Unconfirmed);
        goneOutcome.Should().StartWith("Compaction was saved, not confirmed: ").And.Contain("no longer in this scope");

        (CompactionVerdict failed, string failedOutcome) = StreamCompactionRules.Verify(
            3, 0, StreamState.Failed(StreamId, new EventDataError("connection refused", null, true)), "TimeTravelOrder", null, skipping: false);
        failed.Should().Be(CompactionVerdict.Unconfirmed);
        failedOutcome.Should().StartWith("Compaction was saved, not confirmed: ").And.Contain("connection refused");

        // Npgsql's own messages end in a full stop; the sentence around them has one already.
        StreamCompactionRules.Unconfirmed("the stream could not be read back - No suitable host was found.")
            .Should().Be("Compaction was saved, not confirmed: the stream could not be read back - No suitable host was found. "
                + "Reload the page to see what the stream holds now.");
    }

    /// <summary>
    /// Marten never moves the watermark past the version it was handed, so one past the dialog's is another
    /// compaction's. It is not claimed as this one, and not called a failure either.
    /// </summary>
    [Fact]
    public void A_watermark_past_the_dialogs_version_is_not_claimed_as_this_compaction()
    {
        (CompactionVerdict verdict, string outcome) =
            StreamCompactionRules.Verify(3, 0, Stream(version: 7, compacted: 7), "TimeTravelOrder", null, skipping: false);

        verdict.Should().Be(CompactionVerdict.Unconfirmed);
        outcome.Should().Contain("compacted_version is now 7, past version 3").And.Contain("another compaction");
    }

    [Fact]
    public void A_tagged_stream_refusal_names_the_table_and_says_nothing_changed() =>
        StreamCompactionRules.TaggedStreamRefused("mt_event_tag_customer").Should()
            .Contain("mt_event_tag_customer").And.Contain("Nothing was changed");

    // ------------------------------------------------------------------------------------------------

    private static CompactionPlan Plan(StreamState stream) =>
        StreamCompactionRules.Plan(
            stream, StreamIdentity.AsGuid, Order, CompactionUnavailableReason.None, false, null, EventTagStorage.None, false);

    /// <summary>
    /// A stream row as Marten writes it. The default tenant is <c>*DEFAULT*</c>, not null: that is what
    /// <c>mt_streams.tenant_id</c> holds on every row of a single-tenanted store, and the plan must not read it
    /// as a tenant the compaction is narrowed to.
    /// </summary>
    private static StreamState Stream(
        long version,
        long compacted = 0,
        bool archived = false,
        string? tenantId = "*DEFAULT*",
        string id = StreamId) =>
        new(
            id,
            Exists: true,
            "time_travel_order",
            version,
            new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.Zero),
            archived,
            tenantId)
        {
            CompactedVersion = compacted,
        };

    private static IReadOnlyList<CompactionAggregate> Registered(Action<StoreOptions> configure)
    {
        IDocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(SqlTestStore.Unreachable);
            options.DatabaseSchemaName = SqlTestStore.Schema;
            configure(options);
        });

        return CompactionAggregates.Registered(store.Options);
    }
}
