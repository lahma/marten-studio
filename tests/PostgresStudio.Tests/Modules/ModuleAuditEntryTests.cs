using System.Security.Claims;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Services;
using PostgresStudio.Tests.Components;

namespace PostgresStudio.Tests.Modules;

/// <summary>
/// D34: a module's audit entry is judged only by its kind's gate - never by the host's store policy - and hidden when no gate answers for its
/// kind; a host entry is judged as before, and the principal is fetched once per sweep.
/// </summary>
public class ModuleAuditEntryTests
{
    private sealed class FakeGate(string kind, Func<StudioActionLogEntry, bool> visible) : IStudioAuditScopeGate
    {
        public int Sweeps { get; private set; }

        public string Kind { get; } = kind;

        public IStudioAuditSweep BeginSweep(ClaimsPrincipal user)
        {
            Sweeps++;
            return new Sweep(visible);
        }

        private sealed class Sweep(Func<StudioActionLogEntry, bool> visible) : IStudioAuditSweep
        {
            public ValueTask<bool> IsVisibleAsync(StudioActionLogEntry entry, CancellationToken cancellationToken) => ValueTask.FromResult(visible(entry));
        }
    }

    private static PostgresStudioComponentContext Context(params IStudioAuditScopeGate[] gates) =>
        new(services =>
        {
            foreach (IStudioAuditScopeGate gate in gates)
            {
                services.AddSingleton(gate);
            }
        });

    private static ValueTask<List<StudioActionLogEntry>> Visible(PostgresStudioComponentContext context) =>
        context.ActionLog.GetVisibleAsync(
            context.Services.GetRequiredService<StudioAuthorization>(),
            context.Services.GetRequiredService<StudioCapabilityGuard>(),
            cancellationToken: TestContext.Current.CancellationToken);

    private static void RecordModule(PostgresStudioComponentContext context, string kind = "fake") =>
        context.ActionLog.RecordModule(new StudioModuleAuditScope(kind, "label", new object()), "Act", "target", succeeded: true, "done", "Cap");

    [Fact]
    public async Task A_module_entry_is_never_judged_by_the_store_policy()
    {
        FakeGate gate = new("fake", static _ => true);
        await using PostgresStudioComponentContext context = Context(gate);
        context.Options.SourceAuthorizationPolicy = "host-source";
        context.AuthorizationService.Allow(static _ => false);
        RecordModule(context);

        StudioActionLogEntry entry = (await Visible(context)).Should().ContainSingle("the host's policy refuses every source and is not asked").Which;

        entry.ScopeKind.Should().Be("fake");
        entry.ScopeLabel.Should().Be("label");
        entry.StoreKey.Should().Be("(module)");
        context.AuthorizationService.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task A_module_entry_without_a_gate_is_hidden()
    {
        await using PostgresStudioComponentContext context = Context(new FakeGate("another", static _ => true));
        RecordModule(context);

        (await Visible(context)).Should().BeEmpty("fail closed: no gate answers for the kind");
    }

    [Fact]
    public async Task A_module_entry_the_gate_refuses_is_hidden()
    {
        await using PostgresStudioComponentContext context = Context(new FakeGate("fake", static _ => false));
        RecordModule(context);

        (await Visible(context)).Should().BeEmpty();
    }

    [Fact]
    public async Task A_host_entry_is_judged_as_before()
    {
        await using PostgresStudioComponentContext context = Context(new FakeGate("fake", static _ => true));
        context.Options.SourceAuthorizationPolicy = "host-source";
        context.AuthorizationService.Allow(static _ => false);
        context.ActionLog.Record("OpenRow", "public.orders", succeeded: true);

        (await Visible(context)).Should().BeEmpty("the store policy refuses the host's scope");
        context.AuthorizationService.Calls.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_principal_is_fetched_once_per_sweep_and_one_gate_sweep_serves_every_entry_of_its_kind()
    {
        FakeGate gate = new("fake", static _ => true);
        await using PostgresStudioComponentContext context = Context(gate);
        for (int i = 0; i < 5; i++)
        {
            RecordModule(context);
        }

        int before = context.AuthenticationState.Reads;

        (await Visible(context)).Should().HaveCount(5);

        gate.Sweeps.Should().Be(1);
        (context.AuthenticationState.Reads - before).Should().Be(1, "the principal is fetched exactly once for the sweep, whatever the number of entries");
    }

    [Fact]
    public async Task The_take_bound_counts_kept_entries_of_both_kinds()
    {
        await using PostgresStudioComponentContext context = Context(new FakeGate("fake", static _ => true));
        context.ActionLog.Record("OpenRow", "a", succeeded: true);
        RecordModule(context);
        RecordModule(context);

        List<StudioActionLogEntry> two = await context.ActionLog.GetVisibleAsync(
            context.Services.GetRequiredService<StudioAuthorization>(),
            context.Services.GetRequiredService<StudioCapabilityGuard>(),
            take: 2,
            cancellationToken: TestContext.Current.CancellationToken);

        two.Should().HaveCount(2);
    }
}
