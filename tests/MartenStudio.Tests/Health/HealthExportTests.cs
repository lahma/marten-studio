using System.Text.Json;

using MartenStudio.Services.Health;
using MartenStudio.Services.Health.Checks;
using MartenStudio.Services.Health.Export;
using MartenStudio.Services.Projections;
using MartenStudio.Tests.Projections;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Npgsql;

using AspNetCoreHealthCheck = Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck;
using AspNetCoreHealthCheckContext = Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext;
using AspNetCoreHealthCheckResult = Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult;
using AspNetCoreHealthReport = Microsoft.Extensions.Diagnostics.HealthChecks.HealthReport;
using MartenCoordinator = Marten.Events.Daemon.Coordination.IProjectionCoordinator;

namespace MartenStudio.Tests.Health;

/// <summary>
/// The opt-in health check end to end, through ASP.NET Core's own <see cref="HealthCheckService" />: it is
/// registered only when asked for, maps findings to a status, says codes and counts and nothing else, runs one
/// sweep however many probes ask, and never makes a dynamic tenancy build anything.
/// </summary>
public class HealthExportTests
{
    private static readonly AsyncLocal<string?> Ambient = new();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // ---- Acceptance 1: nothing unless asked for ------------------------------------------------------

    [Fact]
    public void AddMartenStudio_alone_registers_no_health_check_and_no_registration()
    {
        ServiceCollection services = new();
        services.AddMartenStudio();

        services.Should().NotContain(static x => x.ServiceType == typeof(HealthCheckService), "AddMartenStudio() does not call AddHealthChecks()");
        services.Should().NotContain(static x => x.ServiceType == typeof(HealthExportRunner));
        services.Should().NotContain(static x => x.ServiceType == typeof(IConfigureOptions<HealthCheckServiceOptions>));
        services.Should().NotContain(
            static x => typeof(AspNetCoreHealthCheck).IsAssignableFrom(x.ServiceType)
                || (!x.IsKeyedService && x.ImplementationType != null && typeof(AspNetCoreHealthCheck).IsAssignableFrom(x.ImplementationType)),
            "a host that never opts in has no health check of the studio's");

        using ServiceProvider provider = services.BuildServiceProvider();

        provider.GetService<HealthCheckService>().Should().BeNull();
        provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.Should().BeEmpty();
    }

    /// <summary>
    /// What opting in adds, against a host that already has health checks of its own: the runner and the
    /// registration, and no hosted service - the check runs only when the endpoint is probed.
    /// </summary>
    [Fact]
    public void Opting_in_adds_one_runner_and_one_registration_and_no_hosted_service()
    {
        ServiceCollection plain = new();
        plain.AddHealthChecks();

        ServiceCollection opted = new();
        opted.AddHealthChecks().AddMartenStudio();

        ServiceDescriptor[] added = [.. opted.Skip(plain.Count)];

        added.Should().ContainSingle(static x => x.ServiceType == typeof(HealthExportRunner) && x.Lifetime == ServiceLifetime.Singleton);
        added.Should().Contain(static x => x.ServiceType == typeof(IConfigureOptions<HealthCheckServiceOptions>));
        added.Should().NotContain(static x => x.ServiceType == typeof(IHostedService), "no background work, ever");
        added.Should().OnlyContain(
            static x => x.ServiceType == typeof(HealthExportRunner) || x.ServiceType.Namespace == "Microsoft.Extensions.Options",
            "the runner and the registration's options plumbing, and nothing else");
    }

    [Fact]
    public void The_registration_carries_the_callers_name_failure_status_and_tags()
    {
        ServiceCollection services = new();
        services.AddHealthChecks().AddMartenStudio(name: "marten", failureStatus: HealthStatus.Degraded, tags: ["marten", "ready"]);

        using ServiceProvider provider = services.BuildServiceProvider();
        HealthCheckRegistration registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Should().ContainSingle().Subject;

        registration.Name.Should().Be("marten");
        registration.FailureStatus.Should().Be(HealthStatus.Degraded);
        registration.Tags.Should().BeEquivalentTo(["marten", "ready"]);
        registration.Factory(provider).Should().BeOfType<MartenStudioHealthCheck>();
    }

    [Fact]
    public void Unless_told_otherwise_it_is_marten_studio_Unhealthy_and_untagged()
    {
        ServiceCollection services = new();
        services.AddHealthChecks().AddMartenStudio();

        using ServiceProvider provider = services.BuildServiceProvider();
        HealthCheckRegistration registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations.Should().ContainSingle().Subject;

        registration.Name.Should().Be(HealthExportHarness.DefaultName);
        registration.FailureStatus.Should().Be(HealthStatus.Unhealthy);
        registration.Tags.Should().BeEmpty();
    }

    [Fact]
    public void Two_registrations_share_one_runner()
    {
        ServiceCollection services = new();
        services.AddHealthChecks().AddMartenStudio("live").AddMartenStudio("ready", tags: ["ready"]);

        services.Count(static x => x.ServiceType == typeof(HealthExportRunner)).Should().Be(1);
    }

    [Fact]
    public void The_arguments_are_checked()
    {
        FluentActions.Invoking(static () => MartenStudioHealthChecksBuilderExtensions.AddMartenStudio(null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(static () => new ServiceCollection().AddHealthChecks().AddMartenStudio(" "))
            .Should().Throw<ArgumentException>();
    }

    /// <summary>The advisor's registry finds its checks by its own interface, which the export does not implement.</summary>
    [Fact]
    public void The_advisors_registry_does_not_take_the_export_for_one_of_its_checks()
    {
        HealthCheckRegistry.Discover(typeof(HealthCheckRegistry).Assembly).Should().NotContain(typeof(MartenStudioHealthCheck));
        typeof(MartenStudioHealthCheck).Should().Implement<AspNetCoreHealthCheck>()
            .And.NotImplement<MartenStudio.Services.Health.IHealthCheck>();
    }

    // ---- Acceptance 2: the mapping, through HealthCheckService --------------------------------------

    [Fact]
    public async Task A_quiet_store_is_Healthy_and_says_how_much_it_checked()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create();

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Status.Should().Be(HealthStatus.Healthy, entry.Description);
        entry.Description.Should().Be("No findings on 1 database. Refreshed after 60 s, served for at most 120 s.");
        entry.Data["databases"].Should().Be(1);
        entry.Data["critical"].Should().BeEquivalentTo(Array.Empty<string>());
        entry.Exception.Should().BeNull();

        harness.Script.Runs.Should().ContainKeys("K90", "K91", "T90", "C90")
            .And.NotContainKey("E90", "a probe runs tiers 0 and 1, never the deeper reads");
        harness.StudioWarnings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_critical_finding_reports_Unhealthy_and_names_its_code()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create();
        harness.Script.Finds("C90", HealthSeverity.Critical);

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Status.Should().Be(HealthStatus.Unhealthy);
        ((string[]) entry.Data["critical"]).Should().Equal("C90");
        entry.Description.Should().Be("1 critical (C90) on 1 database. Refreshed after 60 s, served for at most 120 s.");
    }

    [Fact]
    public async Task The_callers_failure_status_is_what_a_critical_reports()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(
            healthChecks: static builder => builder.AddMartenStudio(failureStatus: HealthStatus.Degraded));
        harness.Script.Finds("C90", HealthSeverity.Critical);

        (await harness.ProbeAsync()).Status.Should().Be(HealthStatus.Degraded);
    }

    /// <param name="severity">A <c>HealthSeverity</c> name: the enum is internal, so a public theory takes its name.</param>
    /// <param name="key">The data key it is listed under.</param>
    /// <param name="expected">The status it reports.</param>
    [Theory]
    [InlineData(nameof(HealthSeverity.Warning), "warning", HealthStatus.Degraded)]
    [InlineData(nameof(HealthSeverity.Info), "info", HealthStatus.Healthy)]
    public async Task A_warning_is_Degraded_and_an_info_is_not(string severity, string key, HealthStatus expected)
    {
        await using HealthExportHarness harness = HealthExportHarness.Create();
        harness.Script.Finds("K90", Enum.Parse<HealthSeverity>(severity));

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Status.Should().Be(expected);
        ((string[]) entry.Data[key]).Should().Equal("K90");
    }

    /// <summary>A failing check is a "could not check" entry counted as Degraded - never an exception out of the probe.</summary>
    [Fact]
    public async Task A_check_that_fails_is_Degraded_by_code_and_its_words_stay_home()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create();
        harness.Script.Throws("K91", new InvalidOperationException("relation \"secret_schema.mt_doc_orders\" does not exist"));

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Status.Should().Be(HealthStatus.Degraded);
        ((string[]) entry.Data["failed"]).Should().Equal("K91");
        entry.Description.Should().Be("No findings on 1 database; 1 check could not run (K91). Refreshed after 60 s, served for at most 120 s.");
        entry.Exception.Should().BeNull();
    }

    [Fact]
    public async Task A_host_that_added_the_check_and_not_the_studio_is_told_what_to_call()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(registerStudio: false);

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Status.Should().Be(HealthStatus.Degraded);
        entry.Description.Should().Contain("services.AddMartenStudio()");
    }

    /// <summary>
    /// A fault in the run itself - here, options that fail validation - is Degraded with the exception's type and
    /// none of its words; the words go to the log as event 9270, a Warning once and Debug while it repeats (F8).
    /// </summary>
    [Fact]
    public async Task A_run_that_fails_in_the_studio_is_Degraded_names_only_the_exception_type_and_warns_once()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(studio: static options => options.Path = "no-leading-slash");

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Status.Should().Be(HealthStatus.Degraded);
        entry.Description.Should().Be(
            "Marten Studio could not check: its health export failed with OptionsValidationException, logged as event 9270. Refreshed after 60 s, served for at most 120 s.");
        entry.Description.Should().NotContain("Path", "the validation message is the log's, not the endpoint's");
        harness.StudioWarnings.Should().ContainSingle(static x => x.EventId.Id == 9270);

        harness.Clock.Advance(TimeSpan.FromMinutes(2));
        await harness.ProbeAsync();
        await harness.Runner.WhenIdleAsync();

        harness.Runner.RunCount.Should().Be(2);
        harness.StudioWarnings.Should().ContainSingle(static x => x.EventId.Id == 9270, "throttled: an orchestrator probes every few seconds");
        harness.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9270 && x.Level == LogLevel.Debug);
    }

    // ---- Acceptance 3: no visitor-scoped data -------------------------------------------------------

    /// <summary>
    /// Tenant names, session details, statements, table names, Postgres' words and a hidden document type are all
    /// present in the sweep; none of them reaches the report, and the hidden type's finding is not even counted.
    /// </summary>
    [Fact]
    public async Task No_tenant_session_statement_table_or_hidden_type_reaches_the_report()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(
            studio: static options => options.IsDocumentTypeVisible = static type => type != typeof(HiddenHealthThing));

        harness.Script
            .Finds("K90", HealthSeverity.Warning, "Tenant acme-secret-tenant is behind", static finding =>
            {
                finding.TenantId = "acme-secret-tenant";
                finding.Subject = "acme-secret-tenant";
                finding.Evidence.Add(new HealthEvidence("Tenant", "acme-secret-tenant", HealthGate.Activity));
            })
            .Finds("C90", HealthSeverity.Critical, "Session 4242 of secret_role from 10.9.8.7 is idle in transaction", static finding =>
            {
                finding.Subject = "4242";
                finding.Evidence.Add(new HealthEvidence("Process id", "4242", HealthGate.Activity));
                finding.Evidence.Add(new HealthEvidence("Client address", "10.9.8.7", HealthGate.Activity));
                finding.Evidence.Add(new HealthEvidence("Query", "select * from secret_schema.mt_doc_orders where name = 'secret-literal'", HealthGate.Activity));
            })
            .Finds("T90", HealthSeverity.Critical, "mt_doc_hiddenhealththing has an invalid index", static finding =>
            {
                finding.Subject = "secret_schema.mt_doc_hiddenhealththing";
                finding.DocumentTypes.Add(typeof(HiddenHealthThing));
            })
            .Throws("K91", new InvalidOperationException("relation \"secret_schema.mt_doc_hiddenhealththing\" does not exist"));

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Status.Should().Be(HealthStatus.Unhealthy, "C90 is Critical");
        ((string[]) entry.Data["critical"]).Should().Equal(["C90"], "the hidden type's Critical is dropped by the sweep before anything counts it");
        ((string[]) entry.Data["warning"]).Should().Equal("K90");
        ((string[]) entry.Data["failed"]).Should().Equal("K91");
        entry.Description.Should().Be("1 critical (C90), 1 warning (K90) on 1 database; 1 check could not run (K91). Refreshed after 60 s, served for at most 120 s.");
        entry.Exception.Should().BeNull();

        string json = JsonSerializer.Serialize(new { entry.Status, entry.Description, entry.Data, entry.Tags });

        json.Should().NotContainAny(
            "secret", "acme", "4242", "10.9.8.7", "mt_doc", "select", "HiddenHealthThing", "hiddenhealththing", "127.0.0.1", "health_export");
    }

    // ---- Acceptance 4: one run, however many probes --------------------------------------------------

    [Fact]
    public async Task Concurrent_callers_share_one_run()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(checks: [typeof(ScriptedConfigurationCheck)]);
        (TaskCompletionSource entered, TaskCompletionSource release) = Block(harness);

        Task<HealthExportSummary>[] waiting = [.. Enumerable.Range(0, 4).Select(_ => harness.Runner.GetAsync(Token))];
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        waiting.Should().OnlyContain(static x => !x.IsCompleted, "all four are waiting on the one run in flight");
        release.SetResult();

        HealthExportSummary[] summaries = await Task.WhenAll(waiting);

        summaries.Should().OnlyContain(x => ReferenceEquals(x, summaries[0]));
        harness.Runner.RunCount.Should().Be(1);
        harness.Script.Runs["K90"].Should().Be(1);
    }

    /// <summary>Two registrations - a liveness and a readiness check - probed together still cost one run.</summary>
    [Fact]
    public async Task Concurrent_probes_of_two_registrations_cost_one_run()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(
            checks: [typeof(ScriptedConfigurationCheck)],
            healthChecks: static builder => builder.AddMartenStudio("live").AddMartenStudio("ready"));

        AspNetCoreHealthReport[] reports = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => harness.ProbeAllAsync()));

        reports.SelectMany(static x => x.Entries.Values).Should().HaveCount(6).And.OnlyContain(static x => x.Status == HealthStatus.Healthy);
        harness.Runner.RunCount.Should().Be(1);
        harness.Script.Runs["K90"].Should().Be(1);
    }

    [Fact]
    public async Task A_result_is_held_for_the_interval_and_the_first_probe_after_it_starts_the_next_run()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create();

        HealthReportEntry first = await harness.ProbeAsync();

        harness.Clock.Advance(TimeSpan.FromSeconds(59));
        await harness.ProbeAsync();
        await harness.Runner.WhenIdleAsync();
        harness.Runner.RunCount.Should().Be(1, "an orchestrator probing every ten seconds is served the result in hand");

        harness.Clock.Advance(TimeSpan.FromSeconds(2));
        HealthReportEntry stale = await harness.ProbeAsync();

        stale.Data["measuredAt"].Should().Be(first.Data["measuredAt"], "the probe is answered with the result in hand and starts the next run as it goes");
        await harness.Runner.WhenIdleAsync();
        harness.Runner.RunCount.Should().Be(2);

        (await harness.ProbeAsync()).Data["measuredAt"].Should().NotBe(first.Data["measuredAt"], "the probes after it get the new run");
    }

    [Fact]
    public async Task HealthInterval_is_the_cadence_and_the_description_says_so()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(studio: static options => options.HealthInterval = TimeSpan.FromSeconds(30));

        (await harness.ProbeAsync()).Description.Should().EndWith("Refreshed after 30 s, served for at most 60 s.");

        harness.Clock.Advance(TimeSpan.FromSeconds(31));
        await harness.ProbeAsync();
        await harness.Runner.WhenIdleAsync();

        harness.Runner.RunCount.Should().Be(2);
    }

    /// <summary>
    /// H8-fix F1: once a run has completed, no probe waits for the next one - only the process's first probe ever
    /// waits for the checks - and probes during a refresh neither wait nor start a second run.
    /// </summary>
    [Fact]
    public async Task Once_a_result_exists_no_probe_waits_for_the_next_run()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(checks: [typeof(ScriptedConfigurationCheck)]);
        await harness.ProbeAsync();

        (TaskCompletionSource entered, TaskCompletionSource release) = Block(harness);
        harness.Clock.Advance(TimeSpan.FromSeconds(90));

        HealthReportEntry served = await harness.ProbeAsync().WaitAsync(TimeSpan.FromSeconds(30), Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        served.Status.Should().Be(HealthStatus.Healthy, "the result in hand, while the next run is blocked");
        harness.Runner.RunCount.Should().Be(2, "the stale result started the next run");

        await harness.ProbeAsync().WaitAsync(TimeSpan.FromSeconds(30), Token);
        harness.Runner.RunCount.Should().Be(2, "a probe during a refresh joins nothing and starts nothing");

        release.SetResult();
        await harness.Runner.WhenIdleAsync();
    }

    /// <summary>
    /// H8-fix2: a result two intervals old is not served. The probe that finds it - the first after a quiet spell -
    /// waits for a fresh run, as the process's first probe does.
    /// </summary>
    [Fact]
    public async Task A_result_two_intervals_old_is_not_served_and_the_probe_waits_for_a_fresh_one()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(checks: [typeof(ScriptedConfigurationCheck)]);
        HealthReportEntry first = await harness.ProbeAsync();

        (TaskCompletionSource entered, TaskCompletionSource release) = Block(harness);
        harness.Clock.Advance(TimeSpan.FromSeconds(120));

        Task<HealthReportEntry> waiting = harness.ProbeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        waiting.IsCompleted.Should().BeFalse("a result two intervals old is no answer; the probe waits for the run it started");
        release.SetResult();

        HealthReportEntry fresh = await waiting.WaitAsync(TimeSpan.FromSeconds(30), Token);
        fresh.Data["measuredAt"].Should().NotBe(first.Data["measuredAt"]);
        harness.Runner.RunCount.Should().Be(2);
    }

    /// <summary>
    /// A probe that waits - the first, or the first after a quiet spell - waits no longer than the registration's
    /// <c>timeout</c>, which ASP.NET Core reports at the registration's failure status; the run it started carries
    /// on, and the probes after it are served that run.
    /// </summary>
    [Fact]
    public async Task A_waiting_probe_is_bounded_by_the_registrations_timeout_and_the_run_is_not()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(
            checks: [typeof(ScriptedConfigurationCheck)],
            healthChecks: static builder => builder.AddMartenStudio(timeout: TimeSpan.FromMilliseconds(200)));
        (TaskCompletionSource entered, TaskCompletionSource release) = Block(harness);

        HealthReportEntry timedOut = await harness.ProbeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        timedOut.Status.Should().Be(HealthStatus.Unhealthy, "ASP.NET Core reports a check past its timeout at its failure status");
        timedOut.Description.Should().Contain("timeout");

        release.SetResult();
        await harness.Runner.WhenIdleAsync();

        (await harness.ProbeAsync()).Status.Should().Be(HealthStatus.Healthy, "the run the timed-out probe left behind finished, and is served");
        harness.Runner.RunCount.Should().Be(1);
    }

    [Fact]
    public void The_registration_takes_ASP_NET_Cores_usual_timeout()
    {
        ServiceCollection services = new();
        services.AddHealthChecks().AddMartenStudio("timed", timeout: TimeSpan.FromSeconds(5)).AddMartenStudio("untimed");

        using ServiceProvider provider = services.BuildServiceProvider();
        Dictionary<string, HealthCheckRegistration> registrations = provider
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.ToDictionary(static x => x.Name);

        registrations["timed"].Timeout.Should().Be(TimeSpan.FromSeconds(5));
        registrations["untimed"].Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    /// <summary>H8-fix F6: the run is no probe's child - nothing ambient in the first probe's request reaches it.</summary>
    [Fact]
    public async Task The_run_does_not_carry_the_first_probes_execution_context()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(checks: [typeof(ScriptedConfigurationCheck)]);
        string? seen = "not run";
        harness.Script.Gather["K90"] = (_, _) =>
        {
            seen = Ambient.Value;
            return Task.FromResult(new ScriptedFacts("K90", [], null));
        };

        Ambient.Value = "the first probe's request";
        await harness.Runner.GetAsync(Token);

        seen.Should().BeNull("the run is started with the caller's execution context suppressed");
    }

    /// <summary>
    /// H8-fix F7: the application's stopping ends a run in flight, which then says so, and no run starts after it -
    /// the runner is never left holding a task that will not finish.
    /// </summary>
    [Fact]
    public async Task Stopping_the_application_ends_the_run_and_starts_no_other()
    {
        using StoppableLifetime lifetime = new();
        await using HealthExportHarness harness = HealthExportHarness.Create(checks: [typeof(ScriptedConfigurationCheck)], lifetime: lifetime);

        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Script.Gather["K90"] = async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ScriptedFacts("K90", [], null);
        };

        Task<HealthReportEntry> probe = harness.ProbeAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        lifetime.StopApplication();
        HealthReportEntry entry = await probe.WaitAsync(TimeSpan.FromSeconds(30), Token);

        entry.Status.Should().Be(HealthStatus.Degraded);
        entry.Description.Should().StartWith("The application is stopping");
        await harness.Runner.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30), Token);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        await harness.ProbeAsync();
        harness.Runner.RunCount.Should().Be(1, "a stopping application starts no new run");
    }

    /// <summary>
    /// H8-fix2: however many checks ask about the daemon - here C09 and two scripted checks - a sweep asks the
    /// coordinator once. On Marten's own coordinator that ask is an enumeration of the store.
    /// </summary>
    [Fact]
    public async Task A_sweep_asks_the_projection_coordinator_once_however_many_checks_ask()
    {
        WolverineShapedCoordinator coordinator = new();
        await using HealthExportHarness harness = HealthExportHarness.Create(
            checks: [typeof(ScriptedConfigurationCheck), typeof(AnotherScriptedConfigurationCheck), typeof(DaemonPoolCheck)],
            hostServices: services => services.AddSingleton<MartenCoordinator>(coordinator));

        foreach (string code in new[] { "K90", "K91" })
        {
            harness.Script.Gather[code] = async (context, token) =>
            {
                DaemonHosting hosting = await SweptDaemon.HostingAsync(context, context.Services.GetRequiredService<DaemonAccessor>());
                token.ThrowIfCancellationRequested();
                return new ScriptedFacts(code + ":" + hosting.State, [], null);
            };
        }

        await harness.ProbeAsync();

        coordinator.Calls.Should().Be(1, "three askers, one sweep, one lookup");
    }

    // ---- H8-fix F2: running out of time is not the host's choice -------------------------------------

    /// <param name="sqlState">What Postgres answers the check's statement with.</param>
    /// <param name="key">The data key the check is listed under.</param>
    /// <param name="expected">The status it reports.</param>
    [Theory]
    [InlineData(PostgresErrorCodes.QueryCanceled, "timedOut", HealthStatus.Degraded)]
    [InlineData(PostgresErrorCodes.LockNotAvailable, "timedOut", HealthStatus.Degraded)]
    [InlineData(PostgresErrorCodes.InsufficientPrivilege, "unavailable", HealthStatus.Healthy)]
    public async Task A_statement_out_of_time_is_Degraded_and_a_missing_privilege_is_not(string sqlState, string key, HealthStatus expected)
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(checks: [typeof(ScriptedConfigurationCheck)]);
        harness.Script.Throws("K90", new PostgresException("canceling statement", "ERROR", "ERROR", sqlState));

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Status.Should().Be(expected, entry.Description);
        ((string[]) entry.Data[key]).Should().Equal("K90");
    }

    [Fact]
    public async Task What_a_check_says_is_absent_here_is_the_hosts_choice_and_Healthy()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(checks: [typeof(ScriptedConfigurationCheck)]);
        harness.Script.Throws("K90", new HealthCheckUnavailableException("pg_stat_statements is not installed."));

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Status.Should().Be(HealthStatus.Healthy);
        ((string[]) entry.Data["unavailable"]).Should().Equal("K90");
        ((string[]) entry.Data["timedOut"]).Should().BeEmpty();
    }

    /// <summary>
    /// A check that runs out of its own budget, and the checks whose turn comes after the sweep's budget has run
    /// out, are all "ran out of time".
    /// </summary>
    [Fact]
    public async Task A_check_past_its_budget_and_the_checks_the_sweep_had_no_time_for_are_Degraded()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(
            checks: [typeof(ScriptedConfigurationCheck), typeof(AnotherScriptedConfigurationCheck)]);
        harness.Services.GetRequiredService<HealthSweeper>().SweepBudget = TimeSpan.FromMilliseconds(200);
        harness.Script.Gather["K90"] = static async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ScriptedFacts("K90", [], null);
        };

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Status.Should().Be(HealthStatus.Degraded, entry.Description);
        ((string[]) entry.Data["timedOut"]).Should().Equal("K90", "K91");
        entry.Description.Should().Contain("2 checks ran out of time (K90, K91)");
    }

    /// <summary>
    /// H1's review finding, for the export's own cache: a run still going when the interval has passed is
    /// joined, never doubled.
    /// </summary>
    [Fact]
    public async Task A_run_slower_than_the_interval_is_joined_and_never_doubled()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(checks: [typeof(ScriptedConfigurationCheck)]);
        (TaskCompletionSource entered, TaskCompletionSource release) = Block(harness);

        Task<HealthExportSummary> first = harness.Runner.GetAsync(Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        harness.Clock.Advance(TimeSpan.FromMinutes(10));
        Task<HealthExportSummary> second = harness.Runner.GetAsync(Token);

        second.IsCompleted.Should().BeFalse("the second probe waits for the run in flight");
        release.SetResult();

        (await second).Should().BeSameAs(await first);
        harness.Runner.RunCount.Should().Be(1);
        harness.Script.Runs["K90"].Should().Be(1);
    }

    [Fact]
    public async Task A_probe_that_gives_up_cancels_its_own_wait_and_not_the_run()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(checks: [typeof(ScriptedConfigurationCheck)]);
        (TaskCompletionSource entered, TaskCompletionSource release) = Block(harness);
        MartenStudioHealthCheck check = new(harness.Runner);

        using CancellationTokenSource gaveUp = CancellationTokenSource.CreateLinkedTokenSource(Token);
        Task<AspNetCoreHealthCheckResult> impatient = check.CheckHealthAsync(Context(check), gaveUp.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        await gaveUp.CancelAsync();
        await FluentActions.Awaiting(() => impatient).Should().ThrowAsync<OperationCanceledException>(
            "the caller's own cancellation is the one thing the health check does not catch");

        release.SetResult();
        AspNetCoreHealthCheckResult patient = await check.CheckHealthAsync(Context(check), Token);

        patient.Status.Should().Be(HealthStatus.Healthy);
        harness.Runner.RunCount.Should().Be(1, "the run belongs to no probe");
    }

    // ---- Many databases, and the tenancies that build them -------------------------------------------

    [Fact]
    public async Task Every_database_of_a_static_tenancy_is_checked_and_nothing_connects()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(
            checks: [typeof(ScriptedConfigurationCheck)],
            tenancy: static options => options.MultiTenantedDatabases(static tenancy =>
            {
                tenancy.AddSingleTenantDatabase(HealthExportHarness.Refused("export_tenant_a"), "a");
                tenancy.AddSingleTenantDatabase(HealthExportHarness.Refused("export_tenant_b"), "b");
            }));

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Status.Should().Be(HealthStatus.Healthy, entry.Description);
        entry.Data["databases"].Should().Be(2);
        harness.Script.Runs["K90"].Should().Be(2);
        harness.StudioWarnings.Should().BeEmpty("a static tenancy's databases are configuration, and no connection was opened");
    }

    /// <summary>
    /// A single-server tenancy's first enumeration runs <c>CREATE DATABASE</c> for every tenant it was configured
    /// with. A probe is never that first enumeration: with nothing built yet the store is left out, said so, and
    /// nothing connected - the master connection is refused, so an attempt would have been a Warning.
    /// </summary>
    [Fact]
    public async Task A_single_server_tenancy_that_has_built_nothing_is_left_alone()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(
            checks: [typeof(ScriptedConfigurationCheck)],
            tenancy: static options => options.MultiTenantedWithSingleServer(
                HealthExportHarness.Refused("export_master"), static tenancy => tenancy.WithTenants("a", "b")));

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Status.Should().Be(HealthStatus.Healthy, "a tenancy that has not loaded its databases yet is an expected state");
        entry.Data["databases"].Should().Be(0);
        entry.Data["storesNotEnumerated"].Should().Be(1);
        entry.Description.Should().Contain("1 store not enumerated");
        harness.Script.Runs.Should().BeEmpty();
        harness.StudioWarnings.Should().BeEmpty("nothing reached for the refused master connection");
    }

    /// <summary>A master-table tenancy applies its tenant table on its first enumeration; a probe is not that either.</summary>
    [Fact]
    public async Task A_master_table_tenancy_that_holds_nothing_is_left_alone()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(
            checks: [typeof(ScriptedConfigurationCheck)],
            tenancy: static options => options.MultiTenantedDatabasesWithMasterDatabaseTable(HealthExportHarness.Refused("export_master"), "tenants"));

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Status.Should().Be(HealthStatus.Healthy);
        entry.Data["storesNotEnumerated"].Should().Be(1);
        harness.Script.Runs.Should().BeEmpty();
        harness.StudioWarnings.Should().BeEmpty();
    }

    [Fact]
    public async Task A_run_checks_at_most_its_limit_and_says_how_many_it_skipped()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(
            checks: [typeof(ScriptedConfigurationCheck)],
            limits: new HealthExportLimits(MaxDatabases: 2),
            tenancy: static options => options.MultiTenantedDatabases(static tenancy =>
            {
                tenancy.AddSingleTenantDatabase(HealthExportHarness.Refused("export_tenant_a"), "a");
                tenancy.AddSingleTenantDatabase(HealthExportHarness.Refused("export_tenant_b"), "b");
                tenancy.AddSingleTenantDatabase(HealthExportHarness.Refused("export_tenant_c"), "c");
            }));

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Status.Should().Be(HealthStatus.Healthy, "a limit the studio chose is not a fault of the host's");
        entry.Data["databases"].Should().Be(2);
        entry.Data["databasesSkipped"].Should().Be(1);
        entry.Description.Should().Contain("1 database skipped");
    }

    /// <summary>
    /// Each sweep has a budget of one interval of its own, so the run's default is never shorter than that: a run
    /// must not cut short a sweep the Health page would let finish.
    /// </summary>
    [Fact]
    public void The_default_run_budget_is_two_minutes_or_one_interval_whichever_is_longer()
    {
        HealthExportLimits.Default.BudgetFor(TimeSpan.FromSeconds(60)).Should().Be(TimeSpan.FromMinutes(2));
        HealthExportLimits.Default.BudgetFor(TimeSpan.FromMinutes(10)).Should().Be(TimeSpan.FromMinutes(10));
        new HealthExportLimits(25, TimeSpan.FromSeconds(1)).BudgetFor(TimeSpan.FromMinutes(10)).Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_run_that_runs_out_of_time_is_Degraded()
    {
        await using HealthExportHarness harness = HealthExportHarness.Create(
            checks: [typeof(ScriptedConfigurationCheck)],
            limits: new HealthExportLimits(MaxDatabases: 25, Budget: TimeSpan.FromMilliseconds(200)),
            tenancy: static options => options.MultiTenantedDatabases(static tenancy =>
            {
                tenancy.AddSingleTenantDatabase(HealthExportHarness.Refused("export_tenant_a"), "a");
                tenancy.AddSingleTenantDatabase(HealthExportHarness.Refused("export_tenant_b"), "b");
            }));

        harness.Script.Gather["K90"] = static async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new ScriptedFacts("K90", [], null);
        };

        HealthReportEntry entry = await harness.ProbeAsync();

        entry.Status.Should().Be(HealthStatus.Degraded, entry.Description);
        entry.Data["databases"].Should().Be(0);
        entry.Data["databasesTimedOut"].Should().Be(2);
    }

    // ---- Helpers -------------------------------------------------------------------------------------

    /// <summary>Holds K90's gatherer until the test releases it, and says when it has started.</summary>
    private static (TaskCompletionSource Entered, TaskCompletionSource Release) Block(HealthExportHarness harness)
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        harness.Script.Gather["K90"] = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return new ScriptedFacts("K90", [], null);
        };

        return (entered, release);
    }

    private static AspNetCoreHealthCheckContext Context(MartenStudioHealthCheck check) => new()
    {
        Registration = new HealthCheckRegistration(HealthExportHarness.DefaultName, check, failureStatus: null, tags: null),
    };
}
