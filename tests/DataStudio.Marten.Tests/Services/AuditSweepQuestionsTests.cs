using System.Security.Claims;

using DataStudio.Services;
using DataStudio.Marten.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

using DataStudio.Postgres.Services;

namespace DataStudio.Marten.Tests.Services;

/// <summary>
/// The exact sequence of policy questions the Activity ring's sweep asks, row by row, and how often it fetches the principal. A2c moved
/// the database's half of the sweep from <c>StudioActionLog</c> into the database's own audit gate; these literals were taken from the code
/// before the move and must not change with it.
/// </summary>
public class AuditSweepQuestionsTests
{
    private const string Wide = "default/localhost.marten/-";

    private sealed class FakeGate(string kind) : IStudioAuditScopeGate
    {
        public string Kind { get; } = kind;

        public IStudioAuditSweep BeginSweep(Func<ValueTask<ClaimsPrincipal>> principal) => new Sweep(principal);

        /// <summary>A module's gate, which always asks for the visitor: the sweep fetches the principal for the first entry and shares it.</summary>
        private sealed class Sweep(Func<ValueTask<ClaimsPrincipal>> principal) : IStudioAuditSweep
        {
            public async ValueTask<bool> IsVisibleAsync(StudioActionLogEntry entry, CancellationToken cancellationToken)
            {
                await principal();
                return true;
            }
        }
    }

    private static StudioComponentContext Context(bool storePolicy, bool writePolicy, StudioCapability[] on)
    {
        var context = new StudioComponentContext(services => services.AddSingleton<IStudioAuditScopeGate>(new FakeGate("kafka")));
        if (storePolicy)
        {
            context.Options.StoreAuthorizationPolicy = StudioComponentContext.StorePolicyName;
        }

        if (writePolicy)
        {
            context.Options.WriteAuthorizationPolicy = "MartenWrite";
        }

        foreach (StudioCapability capability in on)
        {
            switch (capability)
            {
                case StudioCapability.RunSql: context.Options.Capabilities.RunSql = true; break;
                case StudioCapability.BrowseDatabase: context.Options.Capabilities.BrowseDatabase = true; break;
                case StudioCapability.ViewDefinitions: context.Options.Capabilities.ViewDefinitions = true; break;
            }
        }

        context.AuthenticationState.SignIn("ops");
        return context;
    }

    private static void Entry(StudioComponentContext context, string action, StudioCapability? capability = null, StudioScope? scope = null) =>
        context.ActionLog.Record(action, "target", succeeded: true, "done", capability, scope ?? new StudioScope("default", "localhost.marten", null));

    private static async Task<(string Trace, string[] Kept)> SweepAsync(StudioComponentContext context)
    {
        context.AuthorizationService.Calls.Clear();
        int before = context.AuthenticationState.Reads;

        List<StudioActionLogEntry> visible = await Visible(context);

        string trace = string.Join(
            " | ",
            context.AuthorizationService.Calls.Select(static x =>
                $"{x.Resource.StoreName}/{x.Resource.DatabaseIdentifier}/{x.Resource.TenantId ?? "-"}:{x.Resource.Capability ?? "-"}"));
        return ($"{trace} ; reads={context.AuthenticationState.Reads - before}", [.. visible.Select(static x => x.Action)]);
    }

    private static ValueTask<List<StudioActionLogEntry>> Visible(StudioComponentContext context) =>
        context.ActivityRing.GetVisibleAsync(cancellationToken: Xunit.TestContext.Current.CancellationToken);

    [Fact]
    public async Task An_entry_with_no_store_policy_configured_asks_nothing_and_fetches_no_principal()
    {
        using var context = Context(storePolicy: false, writePolicy: false, on: []);
        Entry(context, "Plain");

        (string trace, string[] kept) = await SweepAsync(context);

        trace.Should().Be(" ; reads=0");
        kept.Should().Equal("Plain");
    }

    [Fact]
    public async Task An_entry_under_a_store_policy_asks_the_store_policy_once()
    {
        using var context = Context(storePolicy: true, writePolicy: false, on: []);
        Entry(context, "Plain");

        (string trace, string[] kept) = await SweepAsync(context);

        trace.Should().Be($"{Wide}:- ; reads=1");
        kept.Should().Equal("Plain");
    }

    [Fact]
    public async Task A_RunSql_entry_asks_the_store_policy_then_the_write_policy_with_the_capability_named()
    {
        using var context = Context(storePolicy: true, writePolicy: true, on: [StudioCapability.RunSql]);
        Entry(context, "Run SQL", StudioCapability.RunSql);

        (string trace, string[] kept) = await SweepAsync(context);

        trace.Should().Be($"{Wide}:- | {Wide}:RunSql ; reads=1");
        kept.Should().Equal("Run SQL");
    }

    [Fact]
    public async Task A_RunSql_entry_with_the_capability_off_asks_only_the_store_policy_and_is_hidden()
    {
        using var context = Context(storePolicy: true, writePolicy: true, on: []);
        Entry(context, "Run SQL", StudioCapability.RunSql);

        (string trace, string[] kept) = await SweepAsync(context);

        trace.Should().Be($"{Wide}:- ; reads=1");
        kept.Should().BeEmpty();
    }

    [Fact]
    public async Task A_RunSql_entry_with_only_a_store_policy_is_asked_again_with_the_capability_named_because_the_write_policy_falls_back_to_it()
    {
        using var context = Context(storePolicy: true, writePolicy: false, on: [StudioCapability.RunSql]);
        Entry(context, "Run SQL", StudioCapability.RunSql);

        (string trace, string[] kept) = await SweepAsync(context);

        trace.Should().Be($"{Wide}:- | {Wide}:RunSql ; reads=1");
        kept.Should().Equal("Run SQL");
    }

    [Fact]
    public async Task A_RunSql_entry_with_no_store_policy_and_no_write_policy_asks_nothing_and_fetches_no_principal()
    {
        using var context = Context(storePolicy: false, writePolicy: false, on: [StudioCapability.RunSql]);
        Entry(context, "Run SQL", StudioCapability.RunSql);

        (string trace, string[] kept) = await SweepAsync(context);

        trace.Should().Be(" ; reads=0");
        kept.Should().Equal("Run SQL");
    }

    [Fact]
    public async Task A_ViewDefinitions_entry_with_BrowseDatabase_on_asks_the_implying_capability()
    {
        using var context = Context(storePolicy: true, writePolicy: true, on: [StudioCapability.BrowseDatabase]);
        Entry(context, "Read definition", StudioCapability.ViewDefinitions);

        (string trace, string[] kept) = await SweepAsync(context);

        trace.Should().Be($"{Wide}:- | {Wide}:BrowseDatabase ; reads=1");
        kept.Should().Equal("Read definition");
    }

    [Fact]
    public async Task A_ViewDefinitions_entry_with_both_on_asks_its_own_capability_first_and_the_implying_one_when_that_is_refused()
    {
        using var context = Context(storePolicy: true, writePolicy: true, on: [StudioCapability.BrowseDatabase, StudioCapability.ViewDefinitions]);
        context.AuthorizationService.Allow(static resource => resource.Capability != nameof(StudioCapability.ViewDefinitions));
        Entry(context, "Read definition", StudioCapability.ViewDefinitions);

        (string trace, string[] kept) = await SweepAsync(context);

        trace.Should().Be($"{Wide}:- | {Wide}:ViewDefinitions | {Wide}:BrowseDatabase ; reads=1");
        kept.Should().Equal("Read definition");
    }

    [Fact]
    public async Task Two_entries_of_one_scope_ask_each_question_once()
    {
        using var context = Context(storePolicy: true, writePolicy: true, on: [StudioCapability.RunSql]);
        Entry(context, "Run SQL", StudioCapability.RunSql);
        Entry(context, "Run SQL again", StudioCapability.RunSql);
        Entry(context, "Plain");

        (string trace, string[] kept) = await SweepAsync(context);

        trace.Should().Be($"{Wide}:- | {Wide}:RunSql ; reads=1");
        kept.Should().Equal("Plain", "Run SQL again", "Run SQL");
    }

    [Fact]
    public async Task A_placeholder_scope_entry_is_asked_about_the_placeholder_and_never_recognised()
    {
        using var context = Context(storePolicy: true, writePolicy: false, on: []);
        context.AuthorizationService.AllowStores("default");
        context.ActionLog.Record("Unsettled", "target", succeeded: true);

        (string trace, string[] kept) = await SweepAsync(context);

        trace.Should().Be("(unknown)/(unknown)/-:- ; reads=1");
        kept.Should().BeEmpty();
    }

    [Fact]
    public async Task A_module_entry_asks_the_hosts_policies_nothing_and_fetches_the_principal_once_for_its_gate()
    {
        using var context = Context(storePolicy: true, writePolicy: true, on: []);
        context.ActivityRing.Record(new StudioAuditScope("kafka", ["cluster"], "cluster", new object()), "Search", "topic", succeeded: true);

        (string trace, string[] kept) = await SweepAsync(context);

        trace.Should().Be(" ; reads=1");
        kept.Should().Equal("Search");
    }

    [Fact]
    public async Task A_mixed_ring_asks_the_database_questions_for_the_database_entries_only_and_shares_one_principal()
    {
        using var context = Context(storePolicy: true, writePolicy: true, on: [StudioCapability.RunSql]);
        Entry(context, "Plain");
        context.ActivityRing.Record(new StudioAuditScope("kafka", ["cluster"], "cluster", new object()), "Search", "topic", succeeded: true);
        Entry(context, "Run SQL", StudioCapability.RunSql);

        (string trace, string[] kept) = await SweepAsync(context);

        trace.Should().Be($"{Wide}:- | {Wide}:RunSql ; reads=1");
        kept.Should().Equal("Run SQL", "Search", "Plain");
    }

    [Fact]
    public async Task A_database_entry_whose_value_is_not_a_database_scope_is_hidden_and_asks_no_policy_question()
    {
        // The fail-closed branch of the database's gate (REVIEW-A2c finding 2): the entry is of the database's kind, so the database's gate judges it,
        // and what it carries is not a DatabaseAuditScope, so there is nothing to ask a policy about and nothing to recognise it by.
        using var context = Context(storePolicy: true, writePolicy: true, on: [StudioCapability.RunSql]);
        context.ActivityRing.Record(new StudioAuditScope(DataStudio.Marten.Services.MartenDescriptor.Key, ["x"], "x", new object()), "Foreign", "target", succeeded: true);

        (string trace, string[] kept) = await SweepAsync(context);

        trace.Should().Be(" ; reads=0");
        kept.Should().BeEmpty();
    }

    [Fact]
    public async Task The_take_bound_stops_the_sweep_before_it_asks_about_older_entries()
    {
        using var context = Context(storePolicy: true, writePolicy: false, on: []);
        Entry(context, "Old", scope: new StudioScope("other", "localhost.marten", "t1"));
        Entry(context, "New");

        context.AuthorizationService.Calls.Clear();
        List<StudioActionLogEntry> visible = await context.ActivityRing.GetVisibleAsync(take: 1,
            cancellationToken: Xunit.TestContext.Current.CancellationToken);

        visible.Select(static x => x.Action).Should().Equal("New");
        context.AuthorizationService.Calls.Select(static x => x.Resource.StoreName).Should().Equal("default");
    }
}
