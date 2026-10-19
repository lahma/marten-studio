using Marten;
using Marten.Services;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// JasperFx's DynamicQuery, reached by reflection: present exactly where the F1 probe says it is, and configured as the
/// strictest policy the feature can live with.
/// </summary>
/// <remarks>
/// The first test runs at every Marten, and is the one that would catch a JasperFx that renamed a member: the probe
/// would still find <c>DynamicQuery</c>, and the adapter - which resolves every member it needs - would not.
/// </remarks>
public class DynamicLinqAdapterTests
{
    [Fact]
    public void The_adapter_resolves_exactly_when_the_probe_finds_DynamicQuery()
    {
        bool probed = RuntimeFeature.DynamicLinqQuery.IsAvailable(MartenRuntime.Current);

        (DynamicLinqAdapter.Current is not null).Should().Be(probed,
            "every member the adapter needs is in JasperFx 2.84.0 to 2.89.0, and none is below it");
    }

    [Fact]
    public void Below_9_48_the_availability_names_the_Marten_it_needs()
    {
        MartenRuntime old = MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string>
        {
            [RuntimePackage.Marten] = "9.39.0",
            [RuntimePackage.JasperFx] = "2.74.0",
        });

        (LinqAvailability availability, string? message) = DynamicLinqAdapter.Availability(old);

        availability.Should().Be(LinqAvailability.MartenTooOld);
        message.Should().Be("LINQ mode needs Marten 9.48+; this host runs Marten 9.39.0 (JasperFx 2.74.0).");
    }

    [DynamicLinqFact]
    public void The_policy_is_tighter_than_JasperFxs_default_and_takes_no_arguments()
    {
        DynamicLinqAdapter adapter = DynamicLinqAdapter.Current!;
        LinqExpressionGuard guard = LinqTestStore.Guard<LinqOrder>();

        var where = adapter.DescribePolicy(LinqClause.Where, guard);
        var order = adapter.DescribePolicy(LinqClause.OrderBy, guard);

        where.Should().Be((LinqQueryLimits.MaxWhereLength, LinqQueryLimits.MaxNodes, 0, 2), "the guard, then SqlNullSemantics");
        order.Should().Be((LinqQueryLimits.MaxOrderByLength, LinqQueryLimits.MaxNodes, 0, 1), "the guard alone");
    }

    [DynamicLinqFact]
    public void The_shared_default_policy_is_never_changed()
    {
        DynamicLinqAdapter adapter = DynamicLinqAdapter.Current!;
        LinqExpressionGuard guard = LinqTestStore.Guard<LinqOrder>();

        adapter.DescribePolicy(LinqClause.Where, guard);

        Type policy = typeof(JasperFx.AutoCreate).Assembly.GetType("JasperFx.Linq.DynamicQueryPolicy", throwOnError: true)!;
        object shared = policy.GetProperty("Default")!.GetValue(null)!;

        policy.GetProperty("MaxTextLength")!.GetValue(shared).Should().Be(2_000, "WithRules copies Default; the caps are set on the copy");
        policy.GetProperty("MaxArguments")!.GetValue(shared).Should().Be(64);
    }

    [DynamicLinqFact]
    public void A_parse_failure_comes_back_as_a_refusal_with_JasperFxs_reason_and_position()
    {
        using DocumentStore store = LinqTestStore.Create();
        using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase(store.Storage.Database));

        DynamicLinqApplied<LinqOrder> applied = DynamicLinqAdapter.Current!.Apply(
            session.Query<LinqOrder>(), "Name ==", null, LinqTestStore.Guard<LinqOrder>());

        applied.Query.Should().BeNull();
        applied.Refusal!.Stage.Should().Be(LinqStage.Parse);
        applied.Refusal.Clause.Should().Be(LinqClause.Where);
        applied.Refusal.Message.Should().StartWith("The predicate ");
        applied.Refusal.Position.Should().BeGreaterThanOrEqualTo(0, "a syntax error carries the position the parser stopped at");
    }

    [DynamicLinqFact]
    public void The_studio_rule_refuses_inside_JasperFxs_parse()
    {
        using DocumentStore store = LinqTestStore.Create();
        using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase(store.Storage.Database));

        DynamicLinqApplied<LinqOrder> applied = DynamicLinqAdapter.Current!.Apply(
            session.Query<LinqOrder>(), "Name.ToLower().Contains(Notes)", null, LinqTestStore.Guard<LinqOrder>());

        applied.Refusal!.Stage.Should().Be(LinqStage.Parse, "the guard is the policy's first shape rule");
        applied.Refusal.Message.Should().Contain("literal");
    }

    [DynamicLinqFact]
    public void The_library_underneath_is_one_without_the_known_advisories()
    {
        DynamicLinqAdapter.Current!.DynamicLinqVersion.Should().BeGreaterThanOrEqualTo(LinqQueryLimits.MinimumDynamicLinq);
    }
}
