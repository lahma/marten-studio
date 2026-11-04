using System.Reflection;

using MartenStudio.Tests.Health;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using PostgresStudio.Services;
using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Sources;

namespace PostgresStudio.Tests.Components;

/// <summary>
/// The finding-action seam's runner (1.2-H-C): the action found by id, one press at a time in a tab and one run per database, the
/// gate re-asked on every press by the report's own implementation, the capability and write policy of an action that declares one,
/// the audit of every press and the log of a failure - over a test action, so no database is read.
/// </summary>
public sealed class HealthActionServiceTests : IAsyncDisposable
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    private readonly PostgresStudioComponentContext context = new();
    private readonly HealthActionFlights flights = new();
    private readonly CapturingLoggerProvider logs = new();

    public ValueTask DisposeAsync() => context.DisposeAsync();

    // ---------------------------------------------------------------------------------------------------
    // Which action, on which finding
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_finding_offers_the_actions_that_apply_to_it_and_none_that_do_not()
    {
        TestAction applies = new("a.applies", "T01");
        TestAction other = new("a.other", "T02");
        HealthActionService service = Service(applies, other);

        service.OffersFor(Finding("T01")).Should().ContainSingle().Which.Should().Be(new HealthActionOffer("a.applies", applies.Label, applies.Description));
        service.OffersFor(Finding("T03")).Should().BeEmpty();
        applies.Runs.Should().Be(0, "offering reads nothing");
        await Task.CompletedTask;
    }

    [Fact]
    public void A_finding_about_a_gated_schema_offers_nothing_whatever_applies()
    {
        HealthActionService service = Service(new TestAction("a.applies", "T01"));

        service.OffersFor(Finding("T01") with { Finding = Finding("T01").Finding with { GatedSchemas = ["legacy"] } }).Should().BeEmpty();
    }

    [Fact]
    public async Task An_unknown_action_and_one_the_finding_does_not_offer_are_refused_and_not_audited()
    {
        TestAction action = new("a.applies", "T01");
        HealthActionService service = Service(action);

        HealthActionResult unknown = await service.RunAsync(Scope, Finding("T01"), "a.nope", Token);
        HealthActionResult wrongFinding = await service.RunAsync(Scope, Finding("T02"), "a.applies", Token);
        HealthActionResult wrongCase = await service.RunAsync(Scope, Finding("T01"), "A.APPLIES", Token);

        foreach (HealthActionResult result in new[] { unknown, wrongFinding, wrongCase })
        {
            result.Outcome.Should().Be(HealthActionOutcome.Refused);
            result.Reason.Should().Be("This finding offers no such action.");
        }

        action.Runs.Should().Be(0);
        Audited().Should().BeEmpty("a press the page could not have offered read nothing");
    }

    [Fact]
    public async Task A_press_on_a_finding_about_a_gated_schema_is_refused()
    {
        TestAction action = new("a.applies", "T01");
        HealthActionService service = Service(action);

        HealthActionResult result = await service.RunAsync(
            Scope, Finding("T01") with { Finding = Finding("T01").Finding with { GatedSchemas = ["legacy"] } }, "a.applies", Token);

        result.Outcome.Should().Be(HealthActionOutcome.Refused);
        action.Runs.Should().Be(0);
    }

    [Fact]
    public async Task A_finding_from_another_database_is_refused_before_any_read_and_is_not_audited()
    {
        // The page presses with the scope selected now and the finding of the report on screen; a scope change in between leaves the
        // two apart. The finding below was read for database "elsewhere"; the press is scoped to "default".
        TestAction action = new("a.applies", "T01") { Gate = HealthGate.None };
        HealthReportFinding fromA = Finding("T01") with { MuteKey = HealthService.MuteKeyOf("default", "elsewhere", Finding("T01").Finding) };

        HealthActionResult result = await Service(action).RunAsync(Scope, fromA, "a.applies", Token);

        result.Outcome.Should().Be(HealthActionOutcome.Refused);
        result.Reason.Should().Be("This finding is from another database; run the checks again.");
        action.Runs.Should().Be(0, "the action never ran");
        action.Seen.Should().BeNull();
        Audited().Should().BeEmpty("a refusal before any gate or read is not audited, like the runner's other pre-flight refusals");

        // The same finding, keyed to this database, runs.
        (await Service(action).RunAsync(Scope, Finding("T01"), "a.applies", Token)).Outcome.Should().Be(HealthActionOutcome.Done);
        action.Runs.Should().Be(1);
    }

    // ---------------------------------------------------------------------------------------------------
    // The gate, asked now
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_visitor_short_of_the_database_gate_is_refused_audited_and_the_action_never_runs()
    {
        context.Options.SourceAuthorizationPolicy = "source";

        // The resolver's own questions pass; the gate's, which come after them, do not. (A Postgres Studio source has no tenant, so
        // the store policy's answer to the gate is the resolver's - the order of the questions is what tells them apart.)
        int before = context.AuthorizationService.Calls.Count;
        await ResolveAsync();
        int resolverQuestions = context.AuthorizationService.Calls.Count - before;
        resolverQuestions.Should().BeGreaterThan(0);

        int asked = 0;
        context.AuthorizationService.Allow(_ => ++asked <= resolverQuestions);

        TestAction action = new("a.applies", "T01") { Gate = HealthGate.Database };

        HealthActionResult result = await Service(action).RunAsync(Scope, Finding("T01"), "a.applies", Token);

        result.Outcome.Should().Be(HealthActionOutcome.Refused);
        result.Reason.Should().StartWith("This action needs ").And.Contain("source policy");
        action.Runs.Should().Be(0);
        Audited().Should().ContainSingle().Which.Should().Match<StudioActionLogEntry>(x =>
            !x.Succeeded && x.Action == action.AuditAction && x.Target == action.AuditTarget);
    }

    [Fact]
    public async Task A_finding_whose_own_gate_is_stricter_than_the_actions_is_held_to_it()
    {
        // The action asks nothing; the finding's check is Activity-gated, and the capability is off.
        TestAction action = new("a.applies", "T01") { Gate = HealthGate.None };
        HealthReportFinding finding = Finding("T01") with { Finding = Finding("T01").Finding with { Gate = HealthGate.Activity } };

        HealthActionResult result = await Service(action).RunAsync(Scope, finding, "a.applies", Token);

        result.Outcome.Should().Be(HealthActionOutcome.Refused);
        result.Reason.Should().Contain("ViewDatabaseActivity");
        action.Runs.Should().Be(0);
        Audited().Should().ContainSingle().Which.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task A_finding_about_a_tenant_needs_the_activity_gate_because_it_names_the_tenant()
    {
        // A tenant's own store-policy question is MartenStudio's (a Postgres Studio source has no tenant); what holds for both is
        // that a finding naming a tenant is Activity-gated whatever the action asks, exactly as the report gates it.
        TestAction action = new("a.applies", "T01") { Gate = HealthGate.None };
        HealthReportFinding about = Finding("T01") with { Finding = Finding("T01").Finding with { TenantId = "acme" } };

        HealthActionResult refused = await Service(action).RunAsync(Scope, about, "a.applies", Token);

        refused.Outcome.Should().Be(HealthActionOutcome.Refused);
        refused.Reason.Should().Contain("ViewDatabaseActivity");
        action.Runs.Should().Be(0);

        context.Options.Capabilities.ViewDatabaseActivity = true;
        (await Service(action).RunAsync(Scope, about, "a.applies", Token)).Outcome.Should().Be(HealthActionOutcome.Done);
        Audited().Should().HaveCount(2);
    }

    [Fact]
    public async Task The_gates_are_asked_on_every_press_never_remembered()
    {
        context.Options.SourceAuthorizationPolicy = "source";
        bool open = true;
        context.AuthorizationService.Allow(_ => open);

        TestAction action = new("a.applies", "T01") { Gate = HealthGate.Database };
        HealthActionService service = Service(action);

        (await service.RunAsync(Scope, Finding("T01"), "a.applies", Token)).Outcome.Should().Be(HealthActionOutcome.Done);

        open = false;
        (await service.RunAsync(Scope, Finding("T01"), "a.applies", Token)).Outcome.Should().Be(HealthActionOutcome.Refused);
    }

    [Fact]
    public async Task A_scope_the_store_policy_refuses_is_refused_audited_by_scope_and_never_run()
    {
        context.Options.SourceAuthorizationPolicy = "source";
        context.AuthorizationService.Allow(static _ => false);

        TestAction action = new("a.applies", "T01");
        HealthActionResult result = await Service(action).RunAsync(Scope, Finding("T01"), "a.applies", Token);

        result.Outcome.Should().Be(HealthActionOutcome.Refused);
        action.Runs.Should().Be(0);
        Audited().Should().ContainSingle().Which.Succeeded.Should().BeFalse();
    }

    // ---------------------------------------------------------------------------------------------------
    // A capability, when an action declares one
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_action_that_declares_a_capability_needs_it_on_first_and_the_refusal_is_audited()
    {
        TestAction action = new("a.applies", "T01") { Capability = StudioCapability.ViewDatabaseActivity };

        HealthActionResult result = await Service(action).RunAsync(Scope, Finding("T01"), "a.applies", Token);

        result.Outcome.Should().Be(HealthActionOutcome.Refused);
        result.Reason.Should().Contain("ViewDatabaseActivity");
        action.Runs.Should().Be(0);
        Audited().Should().ContainSingle().Which.Should().Match<StudioActionLogEntry>(x =>
            !x.Succeeded && x.Capability == nameof(StudioCapability.ViewDatabaseActivity));
    }

    [Fact]
    public async Task With_the_capability_on_the_write_policy_is_asked_with_the_capability_named_and_a_refusal_is_audited()
    {
        context.Options.Capabilities.ViewDatabaseActivity = true;
        context.Options.SourceAuthorizationPolicy = "source";
        context.Options.WriteAuthorizationPolicy = "write";
        context.AuthorizationService.Allow(static x => x.Capability is null);

        TestAction action = new("a.applies", "T01") { Capability = StudioCapability.ViewDatabaseActivity };

        HealthActionResult result = await Service(action).RunAsync(Scope, Finding("T01"), "a.applies", Token);

        result.Outcome.Should().Be(HealthActionOutcome.Refused);
        result.Reason.Should().Contain("write policy").And.Contain("ViewDatabaseActivity");
        action.Runs.Should().Be(0);
        context.AuthorizationService.Calls.Should().Contain(x => x.Policy == "write" && x.Resource.Capability == nameof(StudioCapability.ViewDatabaseActivity));
        Audited().Should().ContainSingle().Which.Capability.Should().Be(nameof(StudioCapability.ViewDatabaseActivity));
    }

    [Fact]
    public async Task With_the_capability_on_and_the_policy_passing_the_action_runs_and_is_audited_under_the_capability()
    {
        context.Options.Capabilities.ViewDatabaseActivity = true;

        TestAction action = new("a.applies", "T01") { Capability = StudioCapability.ViewDatabaseActivity };

        HealthActionResult result = await Service(action).RunAsync(Scope, Finding("T01"), "a.applies", Token);

        result.Outcome.Should().Be(HealthActionOutcome.Done);
        Audited().Should().ContainSingle().Which.Should().Match<StudioActionLogEntry>(x =>
            x.Succeeded && x.Capability == nameof(StudioCapability.ViewDatabaseActivity));
    }

    [Fact]
    public async Task ReadOnly_turns_a_declared_capability_off()
    {
        context.Options.Capabilities.ViewDatabaseActivity = true;
        context.Options.ReadOnly = true;

        TestAction action = new("a.applies", "T01") { Capability = StudioCapability.ViewDatabaseActivity };

        (await Service(action).RunAsync(Scope, Finding("T01"), "a.applies", Token)).Outcome.Should().Be(HealthActionOutcome.Refused);
        action.Runs.Should().Be(0);
    }

    // ---------------------------------------------------------------------------------------------------
    // One at a time
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_second_press_in_the_same_tab_is_refused_not_audited_and_the_tab_is_free_again_afterwards()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestAction action = new("a.applies", "T01") { Started = started, Release = release.Task };
        HealthActionService service = Service(action);

        Task<HealthActionResult> first = service.RunAsync(Scope, Finding("T01"), "a.applies", Token);
        await started.Task.WaitAsync(Token);

        HealthActionResult second = await service.RunAsync(Scope, Finding("T01"), "a.applies", Token);

        second.Outcome.Should().Be(HealthActionOutcome.Refused);
        second.Reason.Should().Contain("Another action is running in this tab");
        Audited().Should().BeEmpty("nothing was asked of the database, and the first press has not finished");

        release.SetResult();
        (await first).Outcome.Should().Be(HealthActionOutcome.Done);

        action.Release = Task.CompletedTask;
        (await service.RunAsync(Scope, Finding("T01"), "a.applies", Token)).Outcome.Should().Be(HealthActionOutcome.Done);
        action.Runs.Should().Be(2);
        flights.Count.Should().Be(0);
    }

    [Fact]
    public async Task One_run_of_an_action_per_database_across_circuits_the_second_is_refused_never_joined()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestAction action = new("a.applies", "T01") { Started = started, Release = release.Task, Summary = "first visitor's result" };

        HealthActionService circuitA = Service(action);
        HealthActionService circuitB = Service(action);

        Task<HealthActionResult> first = circuitA.RunAsync(Scope, Finding("T01"), "a.applies", Token);
        await started.Task.WaitAsync(Token);

        HealthActionResult second = await circuitB.RunAsync(Scope, Finding("T01"), "a.applies", Token);

        second.Outcome.Should().Be(HealthActionOutcome.Refused);
        second.Reason.Should().Contain("already running on this database").And.NotContain("first visitor");
        second.Summary.Should().BeNull("the second visitor is never handed the first's result");
        Audited().Should().ContainSingle("the refused press is audited; the first is still running").Which.Succeeded.Should().BeFalse();

        release.SetResult();
        HealthActionResult done = await first;

        done.Summary.Should().Be("first visitor's result");
        action.Runs.Should().Be(1, "the refused press never ran the action");
        flights.Count.Should().Be(0, "the flight is released");
    }

    [Fact]
    public async Task Two_different_actions_on_one_database_do_not_block_each_other()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestAction slow = new("a.slow", "T01") { Started = started, Release = release.Task };
        TestAction quick = new("a.quick", "T01");

        HealthActionService circuitA = Service(slow, quick);
        HealthActionService circuitB = Service(slow, quick);

        Task<HealthActionResult> first = circuitA.RunAsync(Scope, Finding("T01"), "a.slow", Token);
        await started.Task.WaitAsync(Token);

        (await circuitB.RunAsync(Scope, Finding("T01"), "a.quick", Token)).Outcome.Should().Be(HealthActionOutcome.Done);

        release.SetResult();
        await first;
    }

    // ---------------------------------------------------------------------------------------------------
    // What a run does
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_run_is_handed_the_scope_as_selected_the_database_as_a_whole_and_the_cost_bounds()
    {
        context.Options.QueryTimeout = TimeSpan.FromSeconds(12.2);
        context.Options.SqlConsoleRole = "studio_reader";

        TestAction action = new("a.applies", "T01");
        HealthActionResult result = await Service(action).RunAsync(Scope, Finding("T01"), "a.applies", Token);

        result.Outcome.Should().Be(HealthActionOutcome.Done);
        HealthActionContext seen = action.Seen!;
        seen.Whole.Should().Be(new StudioScope("default", seen.Resolved.Target.Identity, null));
        seen.Resolved.Scope.StoreKey.Should().Be("default");
        seen.Finding.Code.Should().Be("T01");
        seen.Session.StatementTimeout.Should().Be(TimeSpan.FromSeconds(12.2));
        seen.Session.Role.Should().Be("studio_reader");
        seen.CommandTimeoutSeconds.Should().Be(13 + 5, "the statement timeout rounded up, and five seconds");
    }

    [Fact]
    public async Task A_success_is_audited_with_the_actions_name_target_and_a_count_and_stamped_with_its_time()
    {
        TestAction action = new("a.applies", "T01") { AuditMessage = "Listed 3 of the longest live streams." };

        HealthActionResult result = await Service(action).RunAsync(Scope, Finding("T01"), "a.applies", Token);

        result.Outcome.Should().Be(HealthActionOutcome.Done);
        result.Took.Should().NotBeNull();
        result.RanAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-1));
        Audited().Should().ContainSingle().Which.Should().Match<StudioActionLogEntry>(x =>
            x.Succeeded
            && x.Action == "Find the test rows"
            && x.Target == "the test table"
            && x.Message == "Listed 3 of the longest live streams."
            && x.Capability == null);
    }

    [Fact]
    public async Task A_summary_is_what_the_action_read_and_never_reaches_the_audit_ring_or_a_log_line()
    {
        context.Services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        const string Sentinel = "SENTINEL-STREAM-ID-5f8b3a52";

        TestAction action = new("a.applies", "T01") { Summary = "Found " + Sentinel };

        HealthActionResult result = await Service(action).RunAsync(Scope, Finding("T01"), "a.applies", Token);

        result.Summary.Should().Contain(Sentinel, "the page is given what was found");
        Audited().Should().ContainSingle().Which.Message.Should().Be("Done.");
        logs.Entries.Should().NotBeEmpty().And.OnlyContain(x => !x.Message.Contains(Sentinel, StringComparison.Ordinal));
        logs.Entries.Should().Contain(x => x.EventId.Id == 9200, "the press was written to the application's log too");
    }

    [Fact]
    public async Task A_policy_that_throws_in_the_resolver_is_a_failed_press_logged_audited_and_holds_no_flight()
    {
        context.Options.SourceAuthorizationPolicy = "source";
        context.AuthorizationService.Allow(static _ => throw new InvalidOperationException("host policy bug"));

        TestAction action = new("a.applies", "T01");

        HealthActionResult result = await Service(action).RunAsync(Scope, Finding("T01"), "a.applies", Token);

        AssertContained(result, action);
    }

    [Fact]
    public async Task A_policy_that_throws_in_the_gate_is_a_failed_press_logged_audited_and_holds_no_flight()
    {
        context.Options.SourceAuthorizationPolicy = "source";

        // The resolver's questions pass; the gate's, which come after them, throw.
        int before = context.AuthorizationService.Calls.Count;
        await ResolveAsync();
        int resolverQuestions = context.AuthorizationService.Calls.Count - before;
        int asked = 0;
        context.AuthorizationService.Allow(_ => ++asked <= resolverQuestions ? true : throw new InvalidOperationException("host policy bug"));

        TestAction action = new("a.applies", "T01") { Gate = HealthGate.Database };

        HealthActionResult result = await Service(action).RunAsync(Scope, Finding("T01"), "a.applies", Token);

        AssertContained(result, action);
    }

    [Fact]
    public async Task A_policy_that_throws_on_a_cancelled_press_is_the_cancellation_not_a_failure()
    {
        context.Options.SourceAuthorizationPolicy = "source";
        using CancellationTokenSource cancel = new();
        context.AuthorizationService.Allow(_ =>
        {
            cancel.Cancel();
            throw new OperationCanceledException(cancel.Token);
        });

        await FluentActions.Awaiting(() => Service(new TestAction("a.applies", "T01")).RunAsync(Scope, Finding("T01"), "a.applies", cancel.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        Audited().Should().BeEmpty();
        logs.Entries.Should().NotContain(x => x.EventId.Id == 9223);
    }

    private void AssertContained(HealthActionResult result, TestAction action)
    {
        result.Outcome.Should().Be(HealthActionOutcome.Failed);
        result.Reason.Should().Be("The action failed (InvalidOperationException); the server log has the details.").And.NotContain("host policy bug");
        action.Runs.Should().Be(0);
        Audited().Should().ContainSingle().Which.Should().Match<StudioActionLogEntry>(x => !x.Succeeded && !x.Message!.Contains("host policy bug"));
        logs.Entries.Should().ContainSingle(x => x.EventId.Id == 9223 && x.Level == LogLevel.Warning);
        flights.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_result_is_cut_at_the_row_cap_and_says_so()
    {
        TestAction action = new("a.applies", "T01") { RowCount = HealthActionResult.MaxRows + 25 };

        HealthActionResult result = await Service(action).RunAsync(Scope, Finding("T01"), "a.applies", Token);

        result.Rows.Should().HaveCount(HealthActionResult.MaxRows);
        result.Notes.Should().Contain("Only the first 100 rows are shown.");
    }

    [Fact]
    public async Task A_refusal_the_action_returns_is_audited_and_logged_at_Debug()
    {
        TestAction action = new("a.applies", "T01") { Return = HealthActionResult.Refused("Not on this store.", DateTimeOffset.UtcNow) };

        HealthActionResult result = await Service(action).RunAsync(Scope, Finding("T01"), "a.applies", Token);

        result.Outcome.Should().Be(HealthActionOutcome.Refused);
        Audited().Should().ContainSingle().Which.Should().Match<StudioActionLogEntry>(x => !x.Succeeded && x.Message == "Not on this store.");
        logs.Entries.Should().ContainSingle(x => x.EventId.Id == 9224 && x.Level == LogLevel.Debug).Which.Message.Should().Contain("Not on this store.");
        logs.Entries.Should().NotContain(x => x.EventId.Id == 9223);
    }

    [Fact]
    public async Task An_exception_is_a_failed_result_logged_once_as_9223_audited_and_frees_the_flight()
    {
        // An id with a line break in it stands for any value a log line must not be forged with: it is escaped and bounded.
        TestAction action = new("a.applies\nforged line", "T01") { Throw = new InvalidOperationException("secret server words") };

        HealthActionResult result = await Service(action).RunAsync(Scope, Finding("T01"), "a.applies\nforged line", Token);

        result.Outcome.Should().Be(HealthActionOutcome.Failed);
        result.Reason.Should().Be("The action failed (InvalidOperationException); the server log has the details.");
        result.Reason.Should().NotContain("secret");

        CapturedLogEntry logged = logs.Entries.Should().ContainSingle(x => x.EventId.Id == 9223).Subject;
        logged.Level.Should().Be(LogLevel.Warning);
        logged.Message.Should().Contain("a.applies").And.Contain("\\n").And.NotContain("\n", "a value is escaped, never a second line");

        Audited().Should().ContainSingle().Which.Should().Match<StudioActionLogEntry>(x => !x.Succeeded && !x.Message!.Contains("secret"));
        flights.Count.Should().Be(0);
    }

    [Fact]
    public async Task A_cancelled_press_throws_audits_nothing_and_frees_the_tab_and_the_database()
    {
        using CancellationTokenSource cancel = new();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TestAction action = new("a.applies", "T01") { Started = started, Release = new TaskCompletionSource().Task };
        HealthActionService service = Service(action);

        Task<HealthActionResult> pressed = service.RunAsync(Scope, Finding("T01"), "a.applies", cancel.Token);
        await started.Task.WaitAsync(Token);
        await cancel.CancelAsync();

        await FluentActions.Awaiting(() => pressed).Should().ThrowAsync<OperationCanceledException>();

        Audited().Should().BeEmpty("the page left");
        flights.Count.Should().Be(0);

        action.Release = Task.CompletedTask;
        (await service.RunAsync(Scope, Finding("T01"), "a.applies", Token)).Outcome.Should().Be(HealthActionOutcome.Done);
    }

    // ---------------------------------------------------------------------------------------------------
    // Holds nothing
    // ---------------------------------------------------------------------------------------------------

    [Fact]
    public void The_runner_holds_services_and_one_flag_and_no_result_finding_row_or_scope()
    {
        FieldInfo[] fields = typeof(HealthActionService).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        fields.Should().OnlyContain(static x => x.IsInitOnly || x.Name == "running", "every field is readonly but the one flag");
        fields.Where(static x => !x.IsInitOnly).Select(static x => x.FieldType).Should().Equal(typeof(int));

        foreach (FieldInfo field in fields)
        {
            field.FieldType.Should().NotBe<HealthActionResult>()
                .And.NotBe<HealthReportFinding>()
                .And.NotBe<StudioScope>()
                .And.NotBe<HealthActionRow>();
        }

        fields.Select(static x => x.FieldType).Where(static x => x.IsGenericType).Should().BeEquivalentTo(
            [typeof(IReadOnlyList<IHealthFindingAction>), typeof(ILogger<HealthActionService>)],
            "the registry and the logger are the only generic fields: no list or dictionary of results");
    }

    [Fact]
    public void The_report_and_the_snapshot_take_hold_and_return_no_action_type()
    {
        static bool IsActionType(Type type) =>
            type == typeof(IHealthFindingAction) || type == typeof(IHealthActionService) || type == typeof(HealthActionService)
            || type == typeof(HealthActionContext) || type == typeof(HealthActionResult) || type == typeof(HealthActionFlights)
            || type == typeof(HealthActionOffer) || type == typeof(HealthActionRow) || type == typeof(HealthActionCell)
            || (type.IsGenericType && type.GetGenericArguments().Any(IsActionType));

        foreach (Type type in new[] { typeof(HealthService), typeof(VisitorSnapshotBuilder), typeof(HealthSweepCache), typeof(HealthSweeper) })
        {
            BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            type.GetConstructors(all).SelectMany(static x => x.GetParameters()).Select(static x => x.ParameterType)
                .Where(IsActionType).Should().BeEmpty($"{type.Name}'s constructor");
            type.GetFields(all).Select(static x => x.FieldType).Where(IsActionType).Should().BeEmpty($"{type.Name}'s fields");
            type.GetProperties(all).Select(static x => x.PropertyType).Where(IsActionType).Should().BeEmpty($"{type.Name}'s properties");
            type.GetMethods(all).Select(static x => x.ReturnType).Where(IsActionType).Should().BeEmpty($"{type.Name}'s methods' results");
            type.GetMethods(all).SelectMany(static x => x.GetParameters()).Select(static x => x.ParameterType)
                .Where(IsActionType).Should().BeEmpty($"{type.Name}'s methods' parameters");
        }
    }

    [Fact]
    public void The_health_services_gate_is_the_one_implementation_the_report_uses()
    {
        typeof(HealthService).GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
            .Should().NotContain(static x => x.Name == "VisitorGate", "the report asks HealthVisitorGate, as an action does");
        typeof(HealthActionService).Assembly.GetType("PostgresStudio.Services.Health.HealthVisitorGate").Should().NotBeNull();
    }

    // ---------------------------------------------------------------------------------------------------

    private HealthActionService Service(params TestAction[] actions)
    {
        ILoggerFactory factory = logs.CreateFactory();

        return ActivatorUtilities.CreateInstance<HealthActionService>(
            context.Services,
            Resolver(),
            flights,
            (IEnumerable<IHealthFindingAction>) actions,
            factory.CreateLogger<HealthActionService>());
    }

    private StudioSourceResolver Resolver() => ActivatorUtilities.CreateInstance<StudioSourceResolver>(context.Services);

    private async Task<ResolvedSource> ResolveAsync() => await Resolver().ResolveAsync(Scope, null, Token);

    private StudioActionLogEntry[] Audited() =>
        [.. context.ActionLog.GetLatest().Where(static x => x.Action == "Find the test rows")];

    private static HealthReportFinding Finding(string code)
    {
        HealthFinding finding = FakeHealthService.Finding(code, HealthSeverity.Info, "A finding");

        return new HealthReportFinding(finding, finding.Evidence, HealthService.MuteKeyOf("default", "default", finding), "https://example.invalid/", true, null);
    }

    /// <summary>An action that counts its runs, can be held open, and can answer, throw or declare a capability.</summary>
    private sealed class TestAction(string id, string code) : IHealthFindingAction
    {
        public int Runs { get; private set; }

        public HealthActionContext? Seen { get; private set; }

        public TaskCompletionSource? Started { get; init; }

        public Task? Release { get; set; }

        public Exception? Throw { get; init; }

        public HealthActionResult? Return { get; init; }

        public string? Summary { get; init; }

        public string? AuditMessage { get; init; }

        public int RowCount { get; init; }

        public string Id => id;

        public string Code => code;

        public string Label => "Find the test rows";

        public string Description => "Reads one table once.";

        public string AuditAction => "Find the test rows";

        public string AuditTarget => "the test table";

        public HealthGate Gate { get; init; } = HealthGate.None;

        public StudioCapability? Capability { get; init; }

        public bool AppliesTo(HealthFinding finding) => string.Equals(finding.Code, code, StringComparison.Ordinal);

        public async Task<HealthActionResult> RunAsync(HealthActionContext context, CancellationToken cancellationToken)
        {
            Runs++;
            Seen = context;
            Started?.TrySetResult();

            if (Release is { } release)
            {
                await release.WaitAsync(cancellationToken);
            }

            if (Throw is not null)
            {
                throw Throw;
            }

            return Return ?? new HealthActionResult
            {
                Outcome = HealthActionOutcome.Done,
                RanAt = DateTimeOffset.UtcNow,
                Summary = Summary ?? "Found rows.",
                AuditMessage = AuditMessage,
                Rows = [.. Enumerable.Range(0, RowCount).Select(static i => new HealthActionRow([new HealthActionCell("row " + i)]))],
            };
        }
    }
}
