using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;
using Marten.Subscriptions;

using MartenStudio.Services.Projections;
using MartenStudio.Tests.Health;

using PostgresStudio.Internal.Sql;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// M4: what a row of <c>mt_event_progression</c> is, as the store's configuration answers it - a registered shard, a
/// composite's member, a tenant's row of one under per-tenant partitioning, Marten's own bookkeeping, or an orphan with a
/// reason. Pure, from plain data; and over a real store's configuration, never connected to.
/// </summary>
public class ProgressionRowClassifierTests
{
    /// <summary>A store that registers Trips (async, version 2), Orders (async), Ledger (inline) and the composite Bundle.</summary>
    private static ProgressionRowClassifier Classifier(bool tenantPartitioned = false, bool gate = false) => new(
        [
            new RegisteredShard("Trips:V2:All", "Trips", gate),
            new RegisteredShard("Orders:All", "Orders"),
            new RegisteredShard("Bundle:All", "Bundle"),
        ],
        [
            new RegisteredSource("Trips", ProjectionLifecycle.Async, 2),
            new RegisteredSource("Orders", ProjectionLifecycle.Async, 1),
            new RegisteredSource("Ledger", ProjectionLifecycle.Inline, 1),
            new RegisteredSource("Bundle", ProjectionLifecycle.Async, 1),
            new RegisteredSource("BundleMember", ProjectionLifecycle.Async, 1),
        ],
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["BundleMember:All"] = "Bundle" },
        tenantPartitioned);

    [Theory]
    [InlineData("HighWaterMark")]
    [InlineData("HighWaterMark:acme")]
    [InlineData("HighWaterAllocationFence")]
    [InlineData("HighWaterStuckGap")]
    public void Martens_bookkeeping_rows_are_never_shards_and_never_orphans(string name)
    {
        Classifier(tenantPartitioned: true).Classify(name).Kind.Should().Be(ProgressionRowKind.Bookkeeping);
    }

    [Fact]
    public void A_registered_shard_is_its_projections_whatever_its_case()
    {
        ProgressionRowClass row = Classifier().Classify("orders:all");

        row.Kind.Should().Be(ProgressionRowKind.Shard);
        row.Projection.Should().Be("Orders");
        row.IsRegistered.Should().BeTrue();
    }

    [Fact]
    public void A_composites_member_is_the_composites()
    {
        ProgressionRowClass row = Classifier().Classify("BundleMember:All");

        row.Kind.Should().Be(ProgressionRowKind.CompositeMember);
        row.Projection.Should().Be("Bundle");
    }

    /// <summary>
    /// Under per-tenant partitioning each tenant's row of a registered shard is that shard's, and of a member the
    /// composite's - and on any other store the same row is an orphan, because the daemon runs the store-global agent only.
    /// </summary>
    [Fact]
    public void A_tenants_row_of_a_registered_shard_is_the_shards_only_under_per_tenant_partitioning()
    {
        ProgressionRowClass shard = Classifier(tenantPartitioned: true).Classify("Orders:All:acme");
        shard.Kind.Should().Be(ProgressionRowKind.TenantShard);
        shard.Projection.Should().Be("Orders");
        shard.TenantId.Should().Be("acme");
        shard.OfCompositeMember.Should().BeFalse();

        ProgressionRowClass versioned = Classifier(tenantPartitioned: true).Classify("Trips:V2:All:acme");
        versioned.Kind.Should().Be(ProgressionRowKind.TenantShard);
        versioned.Projection.Should().Be("Trips");

        ProgressionRowClass member = Classifier(tenantPartitioned: true).Classify("BundleMember:All:acme");
        member.Kind.Should().Be(ProgressionRowKind.TenantShard);
        member.Projection.Should().Be("Bundle");
        member.OfCompositeMember.Should().BeTrue();

        ProgressionRowClass leftOver = Classifier(tenantPartitioned: false).Classify("Orders:All:acme");
        leftOver.Kind.Should().Be(ProgressionRowKind.Orphan);
        leftOver.Reason.Should().Be(OrphanReason.TenantRowWithoutPartitioning);
        leftOver.TenantId.Should().Be("acme");
    }

    [Fact]
    public void A_renamed_or_removed_projections_row_names_no_registration()
    {
        ProgressionRowClass row = Classifier().Classify("OrdersBeforeRename:All");

        row.Kind.Should().Be(ProgressionRowKind.Orphan);
        row.Reason.Should().Be(OrphanReason.NoSuchProjection);
        row.Projection.Should().BeNull();

        ProgressionRowClass tenant = Classifier(tenantPartitioned: true).Classify("Gone:All:acme");
        tenant.Reason.Should().Be(OrphanReason.NoSuchProjection, "a tenant's row of nothing registered is an orphan too");
        tenant.TenantId.Should().Be("acme");
    }

    /// <summary>
    /// A version bump leaves the prior version's row, which nothing advances - and which the daemon still reads when the
    /// registered version gates its side effects behind the prior version.
    /// </summary>
    [Fact]
    public void A_prior_versions_row_is_an_orphan_the_daemon_reads_only_behind_the_side_effect_gate()
    {
        ProgressionRowClass plain = Classifier().Classify("Trips:All");
        plain.Kind.Should().Be(ProgressionRowKind.Orphan);
        plain.Reason.Should().Be(OrphanReason.OtherVersion);
        plain.RegisteredVersion.Should().Be(2u);
        plain.RowVersion.Should().Be(1u);
        plain.ReadByDaemon.Should().BeFalse();

        Classifier(gate: true).Classify("Trips:All").ReadByDaemon.Should().BeTrue();

        ProgressionRowClass later = Classifier(gate: true).Classify("Trips:V3:All");
        later.Reason.Should().Be(OrphanReason.OtherVersion);
        later.ReadByDaemon.Should().BeFalse("the daemon reads a prior version only, never a later one");

        ProgressionRowClass tenant = Classifier(tenantPartitioned: true).Classify("Trips:All:acme");
        tenant.Reason.Should().Be(OrphanReason.OtherVersion, "a tenant's row of the prior version is the prior version's");
        tenant.TenantId.Should().Be("acme");
    }

    [Fact]
    public void A_shard_the_projection_no_longer_runs_and_an_inline_projections_row_are_orphans_with_their_reasons()
    {
        Classifier().Classify("Orders:EU").Reason.Should().Be(OrphanReason.ShardNotRegistered);

        ProgressionRowClass inline = Classifier().Classify("Ledger:All");
        inline.Reason.Should().Be(OrphanReason.NotAsync);
        inline.Projection.Should().Be("Ledger");
    }

    [Fact]
    public void A_name_no_shard_name_parses_is_an_orphan()
    {
        Classifier().Classify("a:b:c:d:e").Reason.Should().Be(OrphanReason.NotAShardName);
    }

    [Fact]
    public void The_bound_on_the_health_read_names_every_registered_identity_and_the_tenant_prefixes_only_when_partitioned()
    {
        Classifier().RegisteredIdentities.Should().BeEquivalentTo(["Bundle:All", "BundleMember:All", "Orders:All", "Trips:V2:All"]);
        Classifier().TenantPrefixes.Should().BeEmpty();
        Classifier(tenantPartitioned: true).TenantPrefixes.Should().BeEquivalentTo(["Bundle:All:", "BundleMember:All:", "Orders:All:", "Trips:V2:All:"]);
    }

    [Fact]
    public void The_explanations_name_the_projection_and_the_reason()
    {
        OrphanWording.Explain("Trips:All", Classifier().Classify("Trips:All"))
            .Should().Be("Trips is registered at version 2, and this row is version 1's, which nothing advances any more.");

        OrphanWording.Explain("Trips:All", Classifier(gate: true).Classify("Trips:All"))
            .Should().Contain("GateSideEffectsBehindPriorVersion").And.Contain("not offered for removal");

        OrphanWording.Explain("Orders:All:acme", Classifier().Classify("Orders:All:acme"))
            .Should().StartWith("One tenant's row of Orders, and this store does not number its events per tenant");

        OrphanWording.Count(1).Should().Be("1 orphaned progression row");
        OrphanWording.Count(3).Should().Be("3 orphaned progression rows");
    }

    // ------------------------------------------------------------------------------------------------
    // Over a real store's configuration, never connected to
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A subscription registered with <c>Events.Subscribe(...)</c> is in the daemon's <c>AllShards()</c> and not in
    /// <c>Projections()</c>: 0.5 drew its rows as "Unregistered shards". It is listed, and its rows are its own.
    /// </summary>
    [Fact]
    public void A_subscription_only_the_daemon_lists_is_registered_and_listed()
    {
        using DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(HealthHarness.RefusedConnectionString);
            options.Projections.Add(new SoloProjection(), ProjectionLifecycle.Async);
            options.Events.Subscribe(new AuditSubscription());
        });

        ((IDocumentStore) store).Options.Events.Projections().Select(static x => x.Name)
            .Should().NotContain("Audit", "the premise: Projections() does not list it");

        List<ProjectionInfo> projections = ProjectionDataService.RegisteredProjectionsOf(store);

        ProjectionInfo audit = projections.Should().ContainSingle(static x => x.Name == "Audit").Subject;
        audit.IsSubscription.Should().BeTrue();
        audit.IsAsync.Should().BeTrue();
        audit.ShardNames.Should().Equal("Audit:All");
        projections.Single(static x => x.Name == "Solo").IsSubscription.Should().BeFalse();

        ProgressionRowClassifier classifier = ProgressionRowClassifier.For(store, projections);
        classifier.Classify("Audit:All").Kind.Should().Be(ProgressionRowKind.Shard);
        classifier.Classify("Solo:All").Kind.Should().Be(ProgressionRowKind.Shard);
        classifier.Classify("Retired:All").Kind.Should().Be(ProgressionRowKind.Orphan);
    }

    /// <summary>The side-effect gate is read off the daemon's own shard list, as JasperFx reads it on a start.</summary>
    [Fact]
    public void The_side_effect_gate_comes_from_the_daemons_own_shard_options()
    {
        using DocumentStore store = DocumentStore.For(options =>
        {
            options.Connection(HealthHarness.RefusedConnectionString);

            var solo = new SoloProjection { Version = 3 };
            solo.Options.GateSideEffectsBehindPriorVersion = true;
            options.Projections.Add(solo, ProjectionLifecycle.Async);
        });

        ProgressionRowClassifier classifier = ProgressionRowClassifier.For(store, ProjectionDataService.RegisteredProjectionsOf(store));

        classifier.Classify("Solo:V3:All").Kind.Should().Be(ProgressionRowKind.Shard);

        ProgressionRowClass prior = classifier.Classify("Solo:V2:All");
        prior.Reason.Should().Be(OrphanReason.OtherVersion);
        prior.ReadByDaemon.Should().BeTrue();
    }

    /// <summary>The removal statement is the host's to run: every name quoted, the row's key a literal, never a parameter.</summary>
    [Fact]
    public void The_removal_statement_quotes_every_name_and_refuses_a_value_it_cannot_print_honestly()
    {
        MigrationSqlText.DeleteRowWhereEquals("events", "mt_event_progression", "name", "Retired:All")
            .Should().Be("""DELETE FROM "events"."mt_event_progression" WHERE "name" = 'Retired:All';""");

        MigrationSqlText.DeleteRowWhereEquals("events", "mt_event_progression", "name", "O'Brien:All")
            .Should().Be("""DELETE FROM "events"."mt_event_progression" WHERE "name" = 'O''Brien:All';""");

        MigrationSqlText.DeleteRowWhereEquals("events", "mt_event_progression", "name", "a\\b:All")
            .Should().Be("""DELETE FROM "events"."mt_event_progression" WHERE "name" = E'a\\b:All';""");

        MigrationSqlText.DeleteRowWhereEquals("events", "mt_event_progression", "name", "x\n; drop table y;").Should().BeNull();
        MigrationSqlText.DeleteRowWhereEquals("events", "mt_event_progression", "name", "x‮y").Should().BeNull();
        MigrationSqlText.DeleteRowWhereEquals("ev\"ents", "mt_event_progression", "name", "Retired:All").Should().BeNull();
    }

    /// <summary>A subscription the store registers with <c>Events.Subscribe(...)</c>.</summary>
    private sealed class AuditSubscription : SubscriptionBase
    {
        public AuditSubscription() => Name = "Audit";

        public override Task<IChangeListener> ProcessEventsAsync(
            EventRange page,
            ISubscriptionController controller,
            IDocumentOperations operations,
            CancellationToken cancellationToken) =>
            Task.FromResult(NullChangeListener.Instance);
    }
}
