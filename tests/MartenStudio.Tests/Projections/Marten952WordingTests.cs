using AngleSharp.Dom;

using Bunit;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Components;

using Page = MartenStudio.Components.Pages.Projections.Projections;

namespace MartenStudio.Tests.Projections;

/// <summary>An event the member projections below start from.</summary>
/// <param name="Name">Anything.</param>
public sealed record MemberStarted(string Name);

/// <summary>The first member's document.</summary>
public sealed class FirstMemberDoc
{
    /// <summary>The stream id.</summary>
    public Guid Id { get; set; }
}

/// <summary>The second member's document.</summary>
public sealed class SecondMemberDoc
{
    /// <summary>The stream id.</summary>
    public Guid Id { get; set; }
}

/// <summary>A plain projection's document.</summary>
public sealed class SoloDoc
{
    /// <summary>The stream id.</summary>
    public Guid Id { get; set; }
}

/// <summary>A composite's first-stage member.</summary>
public sealed partial class FirstMemberProjection : global::Marten.Events.Aggregation.SingleStreamProjection<FirstMemberDoc, Guid>
{
    /// <summary>Named.</summary>
    public FirstMemberProjection() => Name = "FirstMember";

    /// <summary>Starts one.</summary>
    public static FirstMemberDoc Create(MemberStarted started) => new();
}

/// <summary>A composite's second-stage member.</summary>
public sealed partial class SecondMemberProjection : global::Marten.Events.Aggregation.SingleStreamProjection<SecondMemberDoc, Guid>
{
    /// <summary>Named.</summary>
    public SecondMemberProjection() => Name = "SecondMember";

    /// <summary>Starts one.</summary>
    public static SecondMemberDoc Create(MemberStarted started) => new();
}

/// <summary>A projection that is nobody's member.</summary>
public sealed partial class SoloProjection : global::Marten.Events.Aggregation.SingleStreamProjection<SoloDoc, Guid>
{
    /// <summary>Named.</summary>
    public SoloProjection() => Name = "Solo";

    /// <summary>Starts one.</summary>
    public static SoloDoc Create(MemberStarted started) => new();
}

/// <summary>
/// What the projections screen says from Marten 9.52 about two operations whose behaviour moved: a rebuild that replays
/// from the projection's start floor (JasperFx.Events 2.93, #5740), and a high-water advance that ensures the event
/// store's storage and, under per-tenant partitioning, moves each tenant's own mark (#5738). Each version's words are
/// judged against a made-up runtime, so both ends run at the floor and at the latest alike.
/// </summary>
public class Marten952WordingTests
{
    private static readonly MartenRuntime Below = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string>
    {
        [RuntimePackage.Marten] = "9.51.0",
        [RuntimePackage.JasperFxEvents] = "2.91.0",
    });

    private static readonly MartenRuntime From = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string>
    {
        [RuntimePackage.Marten] = "9.52.0",
        [RuntimePackage.JasperFxEvents] = "2.93.1",
    });

    // ---- The rebuild: start floors ---------------------------------------------------------------------------------

    /// <summary>Below 9.52 there is no floor, and the dialog's words are the ones it always had.</summary>
    [Fact]
    public void Below_9_52_a_rebuild_replays_from_the_beginning_with_no_row_or_note_of_its_own()
    {
        RebuildWording.ReplayPhrase(null).Should().Be("from the beginning");
        RebuildWording.ReplaysFrom(null).Should().BeNull();
        RebuildWording.FloorNote(null).Should().BeNull();
    }

    [Fact]
    public void A_stored_floor_is_where_the_replay_starts_and_what_it_takes_off_the_count()
    {
        RebuildFloor floor = RebuildFloor.From([new ProjectionFloorRow(string.Empty, 1_200)]);

        floor.Stored.Should().Be(1_200);
        floor.EventsToReplay(5_000).Should().Be(3_800);
        floor.EventsToReplay(1_000).Should().Be(0, "a floor above the mark leaves nothing to replay, never a negative count");

        RebuildWording.ReplayPhrase(floor).Should().Be("from its stored start floor, after sequence 1,200 - not from the beginning");
        RebuildWording.ReplaysFrom(floor).Should().Be("after sequence 1,200: the start floor stored for it");
        RebuildWording.FloorNote(floor).Should().Contain("deleted and not rebuilt").And.Contain("subscribes from the present");
    }

    /// <summary>Per-tenant floors are counted, never named: their keys are the tenant list.</summary>
    [Fact]
    public void Per_tenant_floors_are_counted_and_never_named()
    {
        RebuildFloor floor = RebuildFloor.From(
            [new ProjectionFloorRow("acme", 40), new ProjectionFloorRow("globex", 90), new ProjectionFloorRow(string.Empty, 7)],
            tenantPartitionedEvents: true);

        floor.Stored.Should().BeNull("under per-tenant partitioning every rebuild agent reads its own tenant's floor, never the global row");
        floor.TenantFloorCount.Should().Be(2);
        floor.EventsToReplay(500).Should().Be(500, "each tenant draws its own sequence, so no one floor comes off the mark");

        string text = RebuildWording.ReplaysFrom(floor)!;
        text.Should().Be("each tenant's own start floor: 2 tenants have one, after sequences 40 to 90");
        text.Should().NotContain("acme").And.NotContain("globex");
        RebuildWording.ReplayPhrase(floor).Should().Be("from each tenant's stored start floor, where it has one");

        RebuildWording.ReplaysFrom(RebuildFloor.From([new ProjectionFloorRow("acme", 40)], tenantPartitionedEvents: true))
            .Should().Be("each tenant's own start floor: 1 tenant has one, after sequence 40");

        RebuildFloor.From([new ProjectionFloorRow("acme", 40)]).Should().Be(RebuildFloor.None,
            "without per-tenant partitioning the rebuild reads the store-global row alone");
    }

    /// <summary>No floor stored: a floor the code configures still applies, and the studio cannot see it, so it says so.</summary>
    [Fact]
    public void With_no_floor_stored_the_dialog_says_a_floor_in_code_may_still_apply()
    {
        RebuildFloor none = RebuildFloor.From([]);

        none.Should().Be(RebuildFloor.None);
        RebuildWording.ReplayPhrase(none).Should().Be("from the beginning, unless its code configures a start floor");
        RebuildWording.ReplaysFrom(none).Should().Be("the first event, unless its code configures a start floor");
        RebuildWording.FloorNote(none).Should().Contain("SubscribeFromSequence").And.Contain("cannot see");
    }

    [Fact]
    public void A_database_without_the_floors_table_stores_none()
    {
        RebuildFloor missing = RebuildFloor.From(null);

        missing.TableExists.Should().BeFalse();
        missing.Stored.Should().BeNull();
        RebuildWording.ReplaysFrom(missing).Should().Contain("no mt_event_projection_floors").And.Contain("unless its code configures");
        RebuildWording.ReplayPhrase(missing).Should().Be("from the beginning, unless its code configures a start floor");
    }

    [Fact]
    public async Task The_rebuild_dialog_names_a_stored_floor_in_its_summary_and_its_own_row()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        context.ProjectionData.HighWaterMark = 5_000;
        context.ProjectionData.RebuildFloor = new RebuildFloor(1_200);
        await context.ReadyAsync();

        var page = context.Render<Page>();
        page.Find(".ms-projection-rebuild").Click();

        IElement dialog = page.WaitForElement(".ms-confirm-dialog");

        dialog.TextContent.Should().Contain("replays 3,800 events into it from its stored start floor, after sequence 1,200 - not from the beginning");
        dialog.QuerySelector(".ms-rebuild-floor")!.TextContent.Should().Contain("after sequence 1,200");
        dialog.QuerySelector(".ms-rebuild-floor-note")!.TextContent.Should().Contain("deleted and not rebuilt");
    }

    [Fact]
    public async Task Below_9_52_the_rebuild_dialog_is_the_one_it_always_was()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        context.ProjectionData.HighWaterMark = 5_000;
        await context.ReadyAsync();

        var page = context.Render<Page>();
        page.Find(".ms-projection-rebuild").Click();

        IElement dialog = page.WaitForElement(".ms-confirm-dialog");

        dialog.TextContent.Should().Contain("replays 5,000 events into it from the beginning. It cannot be undone.");
        dialog.QuerySelectorAll(".ms-rebuild-floor, .ms-rebuild-floor-note").Should().BeEmpty();
    }

    // ---- A composite's members (pre-existing, every version) -----------------------------------------------------

    /// <summary>
    /// A composite's member rows are the composite's: drawn under it, marked as members, offered no Start or Stop - its
    /// own shard is the agent - and not listed as unregistered.
    /// </summary>
    [Fact]
    public async Task A_composites_member_rows_are_drawn_under_it_with_no_agent_controls()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData
            .WithStudioPause()
            .WithProjection("OrderComposite")
            .WithCompositeMember("OrderComposite", "CompositeOrderTally:All");
        await context.ReadyAsync();

        var page = context.Render<Page>();

        IElement member = page.WaitForElement("[data-shard='CompositeOrderTally:All']");
        member.QuerySelector(".ms-shard-composite-member").Should().NotBeNull();
        member.QuerySelectorAll(".ms-shard-start, .ms-shard-stop").Should().BeEmpty("a member has no agent of its own");

        page.Find("[data-shard='OrderComposite:All']").QuerySelectorAll(".ms-shard-start, .ms-shard-stop")
            .Should().HaveCount(2, "the composite's own shard is the agent");
        page.FindAll("[data-unregistered-shard]").Should().BeEmpty();
    }

    /// <summary>
    /// Every shard name once, because the names are <c>@key</c>s and two siblings with one key end the circuit (issue #1).
    /// Neither JasperFx nor Marten refuses a composite member named like a top-level projection, and here the composite
    /// sorts first: the top-level projection's own shard wins, the member is not drawn a second time, and a member two
    /// composites share is drawn once.
    /// </summary>
    [Fact]
    public void A_composite_member_named_like_a_projection_that_sorts_after_it_is_drawn_once_as_that_projections_own()
    {
        ProjectionInfo alpha = new("Alpha", JasperFx.Events.Projections.ProjectionLifecycle.Async, "CompositeProjection", 1, "CompositeProjection", ["Alpha:All"])
        {
            MemberShardNames = ["Zed:All", "Shared:All"],
        };
        ProjectionInfo beta = new("Beta", JasperFx.Events.Projections.ProjectionLifecycle.Async, "CompositeProjection", 1, "CompositeProjection", ["Beta:All"])
        {
            MemberShardNames = ["shared:all"],
        };
        ProjectionInfo zed = new("Zed", JasperFx.Events.Projections.ProjectionLifecycle.Async, "Projection", 1, "ZedProjection", ["Zed:All"]);

        JasperFx.Events.Projections.ShardState[] stored =
        [
            new("Alpha:All", 10), new("Beta:All", 10), new("Zed:All", 20), new("Shared:All", 30), new("Orphan:All", 5),
        ];

        List<ShardProgress> rows = ProjectionDataService.MergeProgress(
            [alpha, beta, zed], stored, new Dictionary<string, JasperFx.Events.Projections.ShardState>(), 100, null);

        rows.Select(static x => x.ShardName.ToUpperInvariant()).Should().OnlyHaveUniqueItems("a key is compared as the page compares names");
        rows.Should().HaveCount(5);

        ShardProgress own = rows.Single(static x => x.ShardName == "Zed:All");
        own.ProjectionName.Should().Be("Zed", "the top-level projection is the agent that row belongs to");
        own.IsCompositeMember.Should().BeFalse();
        own.IsRegistered.Should().BeTrue();

        ShardProgress shared = rows.Single(static x => string.Equals(x.ShardName, "Shared:All", StringComparison.OrdinalIgnoreCase));
        shared.IsCompositeMember.Should().BeTrue();
        shared.ProjectionName.Should().Be("Alpha", "the first composite to claim it");

        rows.Single(static x => x.ShardName == "Orphan:All").IsRegistered.Should().BeFalse();
    }

    /// <summary>The member identities follow the composite's own rule, at the floor's JasperFx and the latest's alike.</summary>
    [Fact]
    public void A_composites_member_identities_are_its_stages_projections_by_name_and_version()
    {
        var options = new global::Marten.StoreOptions();
        options.Projections.CompositeProjectionFor("OrderComposite", static composite =>
        {
            composite.Add(new FirstMemberProjection());
            composite.Add(new SecondMemberProjection(), 2);
        });
        options.Projections.Add(new SoloProjection(), JasperFx.Events.Projections.ProjectionLifecycle.Async);

        IReadOnlyList<JasperFx.Events.Subscriptions.ISubscriptionSource> sources =
            ((global::Marten.IReadOnlyStoreOptions) options).Events.Projections();
        JasperFx.Events.Subscriptions.ISubscriptionSource composite = sources.Single(static x => x.Name == "OrderComposite");

        ProjectionDataService.CompositeMemberIdentities(composite).Should().BeEquivalentTo(["FirstMember:All", "SecondMember:All"]);
        composite.ShardNames().Select(static x => x.Identity).Should().Equal(["OrderComposite:All"],
            "the composite lists only its own shard, which is why its members' rows read as unregistered before");

        ProjectionDataService.CompositeMemberIdentities(sources.Single(static x => x.Name == "Solo"))
            .Should().BeEmpty("a projection that is not a composite has no members");
    }

    // ---- The high-water advance ----------------------------------------------------------------------------------

    /// <summary>
    /// At every version: the mark moves to the highest committed sequence, and what is lost is the sequence numbers below
    /// it with no committed event - not the events a projection has still to process.
    /// </summary>
    [Fact]
    public void The_advance_says_what_it_skips_at_every_version()
    {
        foreach (MartenRuntime runtime in new[] { Below, From })
        {
            string message = HighWaterAdvanceWording.Message(runtime, tenantPartitionedEvents: false, tenantId: null, "CreateOrUpdate");

            message.Should().StartWith("This moves the high water mark to the highest committed event sequence.");
            message.Should().Contain("no committed event is skipped for good").And.Contain("in flight");
            message.Should().NotContain("treats the events it has not processed as already gone");
        }
    }

    [Fact]
    public void Below_9_52_the_advance_creates_nothing_and_says_nothing_about_storage()
    {
        HighWaterAdvanceWording.Message(Below, false, null, "CreateOrUpdate").Should().NotContain("ensures");
        HighWaterAdvanceWording.ButtonNote(Below, "CreateOrUpdate").Should().BeEmpty();
    }

    /// <summary>
    /// With a tenant selected the advance runs in that tenant's database alone, so this database's mode is the one that
    /// applies, and under None it creates nothing.
    /// </summary>
    [Fact]
    public void From_9_52_a_tenants_advance_says_it_may_create_or_migrate_under_this_databases_AutoCreate()
    {
        string message = HighWaterAdvanceWording.Message(From, false, "acme", "CreateOrUpdate");

        message.Should().Contain("Marten first ensures the event store's storage under this database's AutoCreate (CreateOrUpdate)");
        message.Should().Contain("may create the event store's tables, or migrate them");
        message.Should().NotContainEquivalentOf("additive", "hard rule 14: nothing may call CreateOrUpdate additive");

        HighWaterAdvanceWording.ButtonNote(From, "All", "acme").Should().StartWith("Marten first ensures").And.Contain("(All)");
        HighWaterAdvanceWording.ButtonNote(From, "None", "acme").Should().Contain("creates nothing");
        HighWaterAdvanceWording.Message(From, false, "acme", "None").Should().Contain("(None) creates nothing");
    }

    /// <summary>
    /// Without a tenant the advance runs its ensure in every database of the store, each under its own AutoCreate: the
    /// selected database's mode is quoted as this one's, and "creates nothing" is never said for all of them.
    /// </summary>
    [Fact]
    public void From_9_52_a_tenantless_advance_says_each_database_ensures_under_its_own_AutoCreate()
    {
        foreach (string mode in new[] { "CreateOrUpdate", "None" })
        {
            string message = HighWaterAdvanceWording.Message(From, false, null, mode);

            message.Should().Contain("in each database it reaches, under each database's own AutoCreate (this one's is " + mode + ")");
            message.Should().Contain("wherever that is not None, it may create the event store's tables, or migrate them");
            message.Should().NotContain("creates nothing", "another database of the store may not be None");
            message.Should().NotContainEquivalentOf("additive");

            HighWaterAdvanceWording.ButtonNote(From, mode).Should().StartWith("Marten first ensures the event store's storage in each database");
        }
    }

    [Fact]
    public void Below_9_52_an_advance_on_a_partitioned_store_moves_no_tenants_mark()
    {
        HighWaterAdvanceWording.Message(Below, tenantPartitionedEvents: true, "acme", "CreateOrUpdate")
            .Should().Contain("no tenant's mark moves");
        HighWaterAdvanceWording.TenantReach(Below, true, "acme").Should().BeNull("below 9.52 the shared correction sentence applies");
    }

    [Fact]
    public void From_9_52_an_advance_on_a_partitioned_store_moves_the_selected_tenants_mark_or_every_tenants()
    {
        HighWaterAdvanceWording.Message(From, true, "acme", "CreateOrUpdate").Should().Contain("moves tenant acme's own mark, and no other tenant's");
        HighWaterAdvanceWording.Message(From, true, null, "CreateOrUpdate").Should().Contain("moves every tenant's own mark");

        string reach = HighWaterAdvanceWording.TenantReach(From, true, "acme")!;
        reach.Should().Contain("moves only that tenant's own mark");
        reach.Should().Contain("refused unless you may change that database as a whole", "the authorization does not narrow with it");

        HighWaterAdvanceWording.TenantReach(From, false, "acme").Should().BeNull("one mark per database: every tenant's");
        HighWaterAdvanceWording.TenantReach(From, true, null).Should().BeNull();
    }

    /// <summary>The page, at whichever Marten this run resolved: the storage sentence is there exactly from 9.52.</summary>
    [Fact]
    public async Task The_advance_dialog_words_the_storage_check_for_the_Marten_this_host_runs()
    {
        await using var context = new StudioComponentContext().WithAllCapabilities();
        context.ProjectionData.WithProjection("DailySales");
        await context.ReadyAsync();

        var page = context.Render<Page>();
        IElement button = page.Find(".ms-daemon-advance-high-water");
        bool from952 = RuntimeFeature.PerDatabaseHighWaterAdvance.IsAvailable(MartenRuntime.Current);

        (button.GetAttribute("title") ?? string.Empty).Contains("ensures the event store's storage", StringComparison.Ordinal)
            .Should().Be(from952);

        button.Click();
        string text = page.WaitForElement(".ms-confirm-dialog").TextContent;

        text.Should().Contain("highest committed event sequence");
        text.Contains("Marten first ensures the event store's storage", StringComparison.Ordinal).Should().Be(from952);
    }
}
