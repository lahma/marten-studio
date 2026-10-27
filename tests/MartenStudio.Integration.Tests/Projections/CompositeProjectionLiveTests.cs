using Marten.Events.Aggregation;

using MartenStudio.SampleDomain.Events;
using MartenStudio.Services.Projections;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>What one composite member keeps: how many items an order has.</summary>
public sealed class CompositeOrderTally
{
    /// <summary>The order stream's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Items added.</summary>
    public int Items { get; set; }
}

/// <summary>The other member: whether the order has shipped.</summary>
public sealed class CompositeOrderStatus
{
    /// <summary>The order stream's id.</summary>
    public Guid Id { get; set; }

    /// <summary>Whether it shipped.</summary>
    public bool Shipped { get; set; }
}

/// <summary>A composite's first-stage member.</summary>
public sealed partial class CompositeOrderTallyProjection : SingleStreamProjection<CompositeOrderTally, Guid>
{
    /// <summary>Named, so its progression row is predictable.</summary>
    public CompositeOrderTallyProjection()
    {
        Name = "CompositeOrderTally";
    }

    /// <summary>A new order.</summary>
    public static CompositeOrderTally Create(OrderPlaced placed) => new();

    /// <summary>An item.</summary>
    public static void Apply(ItemAdded added, CompositeOrderTally view) => view.Items += added.Quantity;
}

/// <summary>A composite's second-stage member.</summary>
public sealed partial class CompositeOrderStatusProjection : SingleStreamProjection<CompositeOrderStatus, Guid>
{
    /// <summary>Named, so its progression row is predictable.</summary>
    public CompositeOrderStatusProjection()
    {
        Name = "CompositeOrderStatus";
    }

    /// <summary>A new order.</summary>
    public static CompositeOrderStatus Create(OrderPlaced placed) => new();

    /// <summary>Shipped.</summary>
    public static void Apply(OrderShipped shipped, CompositeOrderStatus view) => view.Shipped = true;
}

/// <summary>
/// F: a composite projection's members, live. The composite runs as one shard (<c>OrderComposite:All</c>) and each member
/// records its own progression row in the same batch (<c>ExecutionStage.ExecuteDownstreamAsync</c>, JasperFx 2.74.0 and
/// 2.95.1) - rows no <c>ISubscriptionSource.ShardNames()</c> lists, because <c>CompositeProjection</c>'s returns only its
/// own. The projections screen drew them under "Unregistered shards", with "Nothing advances them", while the composite
/// advanced them on every page.
/// </summary>
public class CompositeProjectionLiveTests(PostgresFixture postgres)
{
    private const string Composite = "OrderComposite";

    private static readonly string[] Members = ["CompositeOrderTally:All", "CompositeOrderStatus:All"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task A_composites_members_are_its_own_rows_and_not_unregistered_shards()
    {
        await using ProjectionsFixture fixture = await ProjectionsFixture.StartAsync(
            postgres,
            "projections_composite",
            configureStore: static options => options.Projections.CompositeProjectionFor(Composite, static composite =>
            {
                composite.Add(new CompositeOrderTallyProjection());
                composite.Add(new CompositeOrderStatusProjection(), 2);
            }),
            cancellationToken: Token);

        // The premise: the members wrote progression rows of their own, beside the composite's.
        ProjectionsView view = await WaitForMembersAsync(fixture);

        view.Projections.Should().Contain(x => x.Name == Composite, "the composite is the registered projection");
        view.Projections.Should().NotContain(x => Members.Contains(x.Name + ":All"), "its members are not projections of the store");

        // The finding, as the screen judged it before: a progression row is registered when some projection's ShardNames()
        // lists it, and the composite's lists only its own shard - so its members' rows were "Unregistered shards".
        view.Projections.SelectMany(static x => x.ShardNames).Should().NotIntersectWith(
            Members, "CompositeProjection.ShardNames() returns [Name:All] alone, at JasperFx 2.74.0 and 2.95.1");

        view.Orphans.Rows.Select(static x => x.Name).Should().NotIntersectWith(
            Members, "a composite advances its members in its own batch: they are registered, through it");

        // M4: a composite store shows no orphaned progression row at all - its own shard and its members' are its rows.
        view.Orphans.Count.Should().Be(0, "every row a composite and its members write is registered");

        foreach (string member in Members)
        {
            ShardProgress row = view.Progress.Should().ContainSingle(x => x.ShardName == member).Subject;

            row.IsRegistered.Should().BeTrue();
            row.ProjectionName.Should().Be(Composite, "a member is drawn under the composite that runs it");
            row.IsCompositeMember.Should().BeTrue("it is not an agent of its own, so the screen offers it no Start or Stop");
            row.HasProgressRow.Should().BeTrue();
        }

        ShardProgress shard = view.Progress.Should().ContainSingle(x => x.ShardName == Composite + ":All").Subject;
        shard.IsCompositeMember.Should().BeFalse("the composite's own shard is the agent");

        view.Projections.Single(x => x.Name == Composite).MemberShardNames.Should().BeEquivalentTo(Members);
    }

    private static async Task<ProjectionsView> WaitForMembersAsync(ProjectionsFixture fixture)
    {
        ProjectionsView? latest = null;

        await ProjectionsFixture.WaitForAsync(
            async () =>
            {
                latest = await fixture.UseAsync(service => service.GetProjectionsAsync(fixture.Scope, Token));
                return Members.All(member => latest.Progress.Any(x => x.ShardName == member && x.HasProgressRow && x.Sequence > 0));
            },
            "the composite's members to write their progression rows",
            TimeSpan.FromSeconds(60),
            Token);

        return latest!;
    }
}
