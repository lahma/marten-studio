using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;
using PostgresStudio.Services.Statements;
using PostgresStudio.Tests.Components;

using static PostgresStudio.Tests.Statements.StatementsTestFacts;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// 1.2-INDEX-A: the index advisor's service is the Statements page's gate asked again, in the page's order, before anything is read
/// or planned: a refusal is the page's own (never audited, never a statement sweep); one run per database in the process, refused and
/// never joined, and audited; a failure is a value, audited once as failed, naming no table and no connection detail.
/// </summary>
public class IndexAdviceServiceTests
{
    private sealed class CountingFacts(StatementFacts facts) : IStatementFacts
    {
        public int Reads { get; private set; }

        public Task<StatementFacts> ReadAsync(ResolvedSource resolved, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(facts);
        }

        public StatementFacts? Latest(ResolvedSource resolved) => facts;
    }

    private static StatementsService Service(PostgresStudioComponentContext context, IStatementFacts facts, IndexAdvisorFlights? flights = null)
    {
        IServiceProvider services = context.Services;

        return new StatementsService(
            services.GetRequiredService<IStudioSettings>(),
            services.GetRequiredService<StudioCapabilityGuard>(),
            services.GetRequiredService<StudioAuthorization>(),
            ActivatorUtilities.CreateInstance<StudioSourceResolver>(services),
            facts,
            services.GetRequiredService<StudioActionLog>(),
            new StudioLogThrottle(TimeProvider.System),
            NullLogger<StatementsService>.Instance,
            flights: flights);
    }

    private static CountingFacts Some() => new(Facts(Read(null, Statement(1, "select * from public.orders where id = $1", 10, 100, [Orders]))));

    private static readonly StudioScope Scope = new("default", "default", null);

    private static IReadOnlyList<StudioActionLogEntry> Entries(PostgresStudioComponentContext context) =>
        [.. context.Services.GetRequiredService<StudioActionLogService>().GetLatest().Where(static x => x.Action == StatementsService.IndexAdviceAction)];

    [Fact]
    public async Task With_ViewDatabaseActivity_off_nothing_is_read_and_the_refusal_is_not_audited()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();
        CountingFacts facts = Some();

        IndexAdviceView view = await Service(context, facts).AdviseIndexesAsync(Scope, new IndexAdviceRequest(), TestContext.Current.CancellationToken);

        view.Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        view.Reason.Should().Be(PostgresStudioText.Instance.StatementsCapabilityDenial);
        facts.Reads.Should().Be(0);
        Entries(context).Should().BeEmpty("the page's own refusal is not an audit entry");
    }

    [Fact]
    public async Task Under_ReadOnly_the_advisor_says_ReadOnly_turned_it_off()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        context.Options.ReadOnly = true;
        await context.ReadyAsync();

        IndexAdviceView view = await Service(context, Some()).AdviseIndexesAsync(Scope, new IndexAdviceRequest(), TestContext.Current.CancellationToken);

        view.Refusal.Should().Be(DatabaseRefusal.ReadOnly);
        Entries(context).Should().BeEmpty();
    }

    [Fact]
    public async Task The_write_policy_is_asked_with_the_capability_named_and_no_tenant_and_its_refusal_runs_nothing()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        context.Options.SourceAuthorizationPolicy = "source";
        context.Options.WriteAuthorizationPolicy = "write";
        context.AuthorizationService.Allow(static resource => resource.Capability is null);
        await context.ReadyAsync();
        CountingFacts facts = Some();
        IndexAdvisorFlights flights = new();

        IndexAdviceView view = await Service(context, facts, flights)
            .AdviseIndexesAsync(Scope with { TenantId = "acme" }, new IndexAdviceRequest(), TestContext.Current.CancellationToken);

        view.Refusal.Should().Be(DatabaseRefusal.WritePolicy);
        facts.Reads.Should().Be(0);
        flights.Count.Should().Be(0);
        context.AuthorizationService.Calls.Should().Contain(static x => x.Policy == "write" && x.Resource.Capability == nameof(StudioCapability.ViewDatabaseActivity));
        Entries(context).Should().BeEmpty();
    }

    [Fact]
    public async Task A_database_another_run_holds_is_refused_not_joined_and_the_refusal_is_audited_once()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();
        CountingFacts facts = Some();
        IndexAdvisorFlights flights = new();
        flights.TryEnter("default\u001fdefault").Should().BeTrue("another visitor's run holds the database");

        IndexAdviceView view = await Service(context, facts, flights).AdviseIndexesAsync(Scope, new IndexAdviceRequest(), TestContext.Current.CancellationToken);

        view.Reason.Should().Be(IndexAdvisor.AlreadyRunningForDatabase);
        facts.Reads.Should().Be(0);
        flights.Count.Should().Be(1, "the other run still holds it: this one released nothing it did not take");

        StudioActionLogEntry entry = Entries(context).Should().ContainSingle().Subject;
        entry.Succeeded.Should().BeFalse();
        entry.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));
    }

    [Fact]
    public async Task A_run_that_cannot_reach_the_database_is_a_value_audited_once_as_failed_and_releases_the_database()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();
        IndexAdvisorFlights flights = new();

        IndexAdviceView view = await Service(context, Some(), flights)
            .AdviseIndexesAsync(Scope, new IndexAdviceRequest { Table = "public.orders" }, TestContext.Current.CancellationToken);

        view.Reason.Should().StartWith("Index advice failed");
        view.Recommended.Should().BeEmpty();
        flights.Count.Should().Be(0, "the database is free again");

        StudioActionLogEntry entry = Entries(context).Should().ContainSingle().Subject;
        entry.Succeeded.Should().BeFalse();
        entry.Target.Should().Be("statements on public.orders");
        entry.Message.Should().NotContain("127.0.0.1").And.NotContain("secret");
    }

    [Fact]
    public async Task A_caller_that_records_the_press_itself_leaves_the_advisor_without_an_entry()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();

        IndexAdviceView view = await Service(context, Some())
            .AdviseIndexesAsync(Scope, new IndexAdviceRequest(), recordAudit: false, TestContext.Current.CancellationToken);

        view.AuditOutcome.Should().NotBeNullOrEmpty("the runner's entry carries it");
        Entries(context).Should().BeEmpty();
    }
}
