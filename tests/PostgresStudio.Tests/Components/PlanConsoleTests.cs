using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Settings;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// <c>ISqlConsoleService.PlanAsync</c>, the Plan page's door into the console (X3): the gate in the order that is the security
/// property, every refusal audited as a <c>RunSql</c> run with what would have been sent, and no database - each refusal
/// happens before a connection is opened, so a gate that ran late would hang on the connection string that goes nowhere.
/// </summary>
public class PlanConsoleTests
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private static SqlConsoleService Console(PostgresStudioComponentContext context) =>
        new(
            ActivatorUtilities.CreateInstance<StudioSourceResolver>(context.Services),
            context.Services.GetRequiredService<StudioCapabilityGuard>(),
            context.Services.GetRequiredService<StudioActionLog>(),
            context.Services.GetRequiredService<IStudioSettings>(),
            NullLogger<SqlConsoleService>.Instance,
            context.Services.GetRequiredService<AuthenticationStateProvider>());

    private static IReadOnlyList<StudioActionLogEntry> Ring(PostgresStudioComponentContext context) =>
        context.Services.GetRequiredService<StudioActionLogService>().GetLatest();

    [Fact]
    public async Task With_RunSql_off_a_plan_is_refused_before_anything_and_audited_with_what_it_would_have_sent()
    {
        await using PostgresStudioComponentContext context = await new PostgresStudioComponentContext().ReadyAsync();

        Func<Task> run = () => Console(context).PlanAsync(Scope, new PlanRunRequest("select 1", PlanMode.Analyze), Token);

        await run.Should().ThrowAsync<StudioCapabilityDeniedException>();

        StudioActionLogEntry entry = Ring(context).Should().ContainSingle().Subject;
        entry.Action.Should().Be(SqlConsoleService.RunSqlAction);
        entry.Succeeded.Should().BeFalse();
        entry.Target.Should().Be("explain (analyze, buffers, format json) select 1");
        entry.Capability.Should().Be(nameof(StudioCapability.RunSql));
    }

    [Fact]
    public async Task ReadOnly_refuses_a_plan_as_it_refuses_the_console()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        context.Options.ReadOnly = true;
        await context.ReadyAsync();

        Func<Task> run = () => Console(context).PlanAsync(Scope, new PlanRunRequest("select 1", PlanMode.Plan), Token);

        await run.Should().ThrowAsync<StudioCapabilityDeniedException>();
        Ring(context).Should().ContainSingle(static x => !x.Succeeded && x.Action == SqlConsoleService.RunSqlAction);
    }

    [Fact]
    public async Task A_write_policy_that_refuses_the_visitor_refuses_a_plan_for_the_database_as_a_whole()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        context.Options.WriteAuthorizationPolicy = "write-policy";
        await context.ReadyAsync();

        // The source policy passes and the write policy - the one asked with RunSql on the resource - does not.
        context.AuthorizationService.Allow(static resource => resource.Capability is null);

        Func<Task> run = () => Console(context).PlanAsync(Scope with { TenantId = "acme" }, new PlanRunRequest("select 1", PlanMode.Plan), Token);

        StudioNotAuthorizedException refused = (await run.Should().ThrowAsync<StudioNotAuthorizedException>()).Which;

        refused.Scope.TenantId.Should().BeNull("nothing narrows a statement to a tenant: the page asks for the database as a whole");

        StudioActionLogEntry entry = Ring(context).Should().ContainSingle().Subject;
        entry.Action.Should().Be(SqlConsoleService.RunSqlAction);
        entry.Succeeded.Should().BeFalse();
    }

    [Theory]
    [InlineData("Plan", "explain (format json) delete from t where id = 1")]
    [InlineData("Analyze", "explain (analyze, buffers, format json) delete from t where id = 1")]
    public async Task A_write_is_refused_by_the_guard_in_every_mode_and_never_analyzed(string mode, string sent)
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();

        PlanRunResult result = await Console(context).PlanAsync(Scope, new PlanRunRequest("delete from t where id = 1", Enum.Parse<PlanMode>(mode)), Token);

        result.Rejection!.Reason.Should().Be(nameof(SqlRejectionReason.DisallowedStatement));
        result.Rejection.Position.Should().Be(0, "the guard read the statement as typed, not behind the prefix");
        result.PlanJson.Should().BeNull();
        result.Succeeded.Should().BeFalse();

        StudioActionLogEntry entry = Ring(context).Should().ContainSingle().Subject;
        entry.Succeeded.Should().BeFalse();
        entry.Target.Should().Be(sent, "what would have been sent is what is recorded");
    }

    [Fact]
    public async Task A_generic_plan_of_a_write_is_refused_by_the_guard_too()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();

        PlanRunResult result = await Console(context).PlanAsync(Scope, new PlanRunRequest("delete from t where id = $1", PlanMode.Generic), Token);

        result.Rejection!.Reason.Should().Be(nameof(SqlRejectionReason.DisallowedStatement));
        Ring(context).Should().ContainSingle().Which.Target.Should().StartWith("prepare \"marten_studio_plan\" as delete from t");
    }

    [Theory]
    [InlineData("explain select 1")]
    [InlineData("  EXPLAIN (format json) select 1")]
    [InlineData("/* why */ explain analyze select 1")]
    public async Task A_statement_that_is_an_EXPLAIN_already_is_refused_and_audited(string statement)
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();

        PlanRunResult result = await Console(context).PlanAsync(Scope, new PlanRunRequest(statement, PlanMode.Plan), Token);

        result.Rejection!.Reason.Should().Be(SqlConsoleService.AlreadyExplainReason);
        result.Rejection.Message.Should().Be(SqlConsoleService.AlreadyExplainMessage);
        Ring(context).Should().ContainSingle(static x => !x.Succeeded && x.Action == SqlConsoleService.RunSqlAction);
    }

    [Fact]
    public async Task A_request_that_cannot_be_planned_is_refused_after_the_gate_and_audited()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();

        PlanRunResult generic = await Console(context).PlanAsync(Scope, new PlanRunRequest("select 1", PlanMode.Generic), Token);
        generic.Rejection!.Reason.Should().Be(PlanRequests.NoPlaceholdersReason);

        PlanRunResult missing = await Console(context).PlanAsync(Scope, new PlanRunRequest("select $1", PlanMode.Plan), Token);
        missing.Rejection!.Reason.Should().Be(PlanRequests.ValuesMissingReason);
        missing.Placeholders.Should().Be(1);

        Ring(context).Should().HaveCount(2).And.OnlyContain(static x => !x.Succeeded && x.Action == SqlConsoleService.RunSqlAction && x.Capability == nameof(StudioCapability.RunSql));
    }

    [Fact]
    public async Task An_empty_statement_is_the_guards_refusal()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();

        PlanRunResult result = await Console(context).PlanAsync(Scope, new PlanRunRequest("   ", PlanMode.Plan), Token);

        result.Rejection!.Reason.Should().Be(nameof(SqlRejectionReason.Empty));
    }

    [Fact]
    public async Task The_value_of_a_placeholder_is_never_in_the_audit_ring()
    {
        await using PostgresStudioComponentContext context = new();
        context.WithAllCapabilities();
        await context.ReadyAsync();

        // Refused after the gate (a value is missing), so no connection - and what was recorded is the statement, with $1 in it.
        await Console(context).PlanAsync(Scope, new PlanRunRequest("select $1, $2", PlanMode.Plan) { Values = ["very secret value"] }, Token);

        Ring(context).Single().Target.Should().Contain("$1").And.NotContain("very secret value");
    }
}
