using Marten;
using Marten.Services;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// JasperFx's DynamicQuery, reached by reflection, and configured as the strictest policy the feature can live with.
/// </summary>
/// <remarks>
/// The first test is the one that would catch a JasperFx that renamed a member: the adapter resolves every member it needs
/// by name and signature, and is absent otherwise.
/// </remarks>
public class DynamicLinqAdapterTests
{
    [Fact]
    public void The_adapter_resolves_against_the_JasperFx_this_run_resolved()
    {
        DynamicLinqAdapter.Current.Should().NotBeNull(
            "every member the adapter needs is in JasperFx 2.84.0 and later, and the floor's JasperFx is {0}",
            MartenRuntime.Current.VersionOf(RuntimePackage.JasperFx));
    }

    /// <summary>
    /// L6: the studio's date correction is idle: JasperFx reads an offset-less date as UTC itself (jasperfx#1024, 2.91.0),
    /// on every server - so the assertion is zone-free and holds on a UTC CI runner too. A JasperFx that read server time
    /// again, on any server east or west of Greenwich, fails it - and so does a probe that stopped being able to read its
    /// date at all, which answers "true" on every server.
    /// </summary>
    [DynamicLinqFact]
    public void The_date_correction_is_idle_because_this_JasperFx_reads_dates_as_UTC()
    {
        DynamicLinqAdapter adapter = DynamicLinqAdapter.Current!;

        adapter.ReadsDatesInServerTime.Should().BeFalse(
            "JasperFx {0} reads an offset-less date as UTC itself, so the studio's correction must be idle",
            MartenRuntime.Current.VersionOf(RuntimePackage.JasperFx));
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
