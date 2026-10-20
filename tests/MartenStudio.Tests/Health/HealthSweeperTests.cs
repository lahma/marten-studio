using MartenStudio.Internal.Sql;
using MartenStudio.Services.Configuration;
using MartenStudio.Services.Health;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Tests.Health;

/// <summary>
/// A sweep: gather, sample, judge - each check on its own, and no failure out of it but cancellation.
/// </summary>
public class HealthSweeperTests
{
    [Fact]
    public async Task A_regular_sweep_runs_tiers_zero_and_one_and_nothing_costlier()
    {
        await using HealthHarness harness = HealthHarness.Create();

        HealthSweep sweep = await harness.SweepAsync();

        sweep.Results.Select(static x => x.Check.Code).Should().BeEquivalentTo(["K90", "K91", "T90", "C90", "D90"]);
        harness.Script.Runs.Should().NotContainKey("E90", "tier 2 runs only behind Run deeper checks");
    }

    [Fact]
    public async Task A_deeper_sweep_runs_tier_two_alone()
    {
        await using HealthHarness harness = HealthHarness.Create();

        HealthSweep sweep = await harness.SweepAsync(HealthSweepKind.Deeper);

        sweep.Results.Select(static x => x.Check.Code).Should().Equal("E90");
    }

    /// <summary>
    /// Per-check isolation, the requirement in so many words: one check's gatherer throwing is that check's
    /// row, and the check after it still runs and still finds what it finds.
    /// </summary>
    [Fact]
    public async Task One_failing_check_never_hides_the_others()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Throws("K90", new InvalidOperationException("a bug in a check"));
        harness.Script.Finds("K91", HealthSeverity.Warning);

        HealthSweep sweep = await harness.SweepAsync();

        HealthCheckResult failed = sweep.Results.Single(static x => x.Check.Code == "K90");
        failed.Outcome.Should().Be(HealthCheckOutcome.Failed);
        failed.Problem!.Reason.Should().Contain("InvalidOperationException");
        failed.Findings.Should().BeEmpty();

        HealthCheckResult next = sweep.Results.Single(static x => x.Check.Code == "K91");
        next.Outcome.Should().Be(HealthCheckOutcome.Checked);
        next.Findings.Should().ContainSingle();
    }

    [Fact]
    public async Task An_evaluator_that_throws_is_its_own_checks_failure_and_nobody_elses()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Judge["K90"] = static (_, _) => throw new InvalidOperationException("judged badly");
        harness.Script.Finds("K91", HealthSeverity.Info);

        HealthSweep sweep = await harness.SweepAsync();

        sweep.Results.Single(static x => x.Check.Code == "K90").Outcome.Should().Be(HealthCheckOutcome.Failed);
        sweep.Results.Single(static x => x.Check.Code == "K91").Findings.Should().ContainSingle();
    }

    /// <summary>A remedy is a StoreOptions line: an evaluator that tried to suggest DDL fails its check.</summary>
    [Fact]
    public async Task An_evaluator_that_suggests_DDL_fails_its_check_rather_than_showing_it()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Finds("K90", HealthSeverity.Warning, shape: static x => x.Remedy = new HealthRemedy("create index ix on mt_doc_x (id);"));

        HealthSweep sweep = await harness.SweepAsync();

        HealthCheckResult result = sweep.Results.Single(static x => x.Check.Code == "K90");
        result.Outcome.Should().Be(HealthCheckOutcome.Failed);
        result.Findings.Should().BeEmpty();
    }

    [Theory]
    [InlineData("create index ix on t (a);")]
    [InlineData("opts.X = 1;\nALTER TABLE t SET (fillfactor = 70);")]
    [InlineData("opts.X = 1; drop index ix;")]
    [InlineData("VACUUM ANALYZE t;")]
    public void A_remedy_that_is_DDL_is_refused(string code)
    {
        Action remedy = () => _ = new HealthRemedy(code);

        remedy.Should().Throw<ArgumentException>().WithMessage("*never DDL*");
    }

    [Theory]
    [InlineData("opts.Schema.For<Order>().FillFactor(80);")]
    [InlineData("opts.AutoCreateSchemaObjects = AutoCreate.None;\n// then: dotnet run -- db-apply")]
    [InlineData("opts.Events.EnableAdvancedAsyncTracking = true; // creates mt_high_water_skips on the next apply")]
    public void A_remedy_that_is_a_StoreOptions_line_is_accepted(string code)
    {
        new HealthRemedy(code).Code.Should().Be(code);
    }

    [Fact]
    public async Task A_finding_coded_for_another_check_fails_the_check_that_made_it()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Judge["K90"] = static (_, _) => HealthVerdict.Of(new HealthFinding
        {
            Code = "K01",
            Severity = HealthSeverity.Critical,
            Title = "Not mine to say",
            WhyItMatters = "It is somebody else's code.",
        });

        HealthSweep sweep = await harness.SweepAsync();

        sweep.Results.Single(static x => x.Check.Code == "K90").Outcome.Should().Be(HealthCheckOutcome.Failed);
        sweep.Findings.Should().BeEmpty();
    }

    /// <summary>
    /// Expected states are values (the memory rule): a gatherer's own "not here", a privilege Postgres
    /// refused, a statement that ran out of its timeout - a row and a Debug line, never a Warning.
    /// </summary>
    [Fact]
    public async Task Expected_states_are_unavailable_rows_and_Debug_lines()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Throws("K90", new HealthCheckUnavailableException("pg_stat_statements is not installed"));
        harness.Script.Throws("K91", new PostgresException("permission denied for view pg_stat_activity", "ERROR", "ERROR", PostgresErrorCodes.InsufficientPrivilege));
        harness.Script.Throws("T90", new PostgresException("canceling statement due to statement timeout", "ERROR", "ERROR", PostgresErrorCodes.QueryCanceled));
        harness.Script.Throws("C90", new PostgresException("canceling statement due to lock timeout", "ERROR", "ERROR", PostgresErrorCodes.LockNotAvailable));

        HealthSweep sweep = await harness.SweepAsync();

        foreach (string code in new[] { "K90", "K91", "T90", "C90" })
        {
            sweep.Results.Single(x => x.Check.Code == code).Outcome.Should().Be(HealthCheckOutcome.Unavailable, code);
        }

        sweep.Results.Single(static x => x.Check.Code == "K90").Problem!.Reason.Should().Be("pg_stat_statements is not installed");
        sweep.Results.Single(static x => x.Check.Code == "K91").Problem!.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        harness.Warnings.Should().BeEmpty("an expected state is a value the page draws, Debug at most");
        harness.Logs.Entries.Should().Contain(static x => x.Level == LogLevel.Debug && x.Message.Contains("K90", StringComparison.Ordinal));
    }

    /// <summary>
    /// A real anomaly on a polled path: event 9222 at Warning the first time, and Debug for the same check,
    /// database and kind of failure for the rest of the throttle's window.
    /// </summary>
    [Fact]
    public async Task An_unexpected_failure_is_event_9222_at_Warning_once_then_Debug()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Throws("K90", new PostgresException("relation does not exist", "ERROR", "ERROR", PostgresErrorCodes.UndefinedTable));

        await harness.SweepAsync();
        await harness.SweepAsync();
        await harness.SweepAsync();

        List<CapturedLogEntryView> nineTwentyTwo =
        [
            .. harness.Logs.Entries.Where(static x => x.EventId.Id == 9222).Select(static x => new CapturedLogEntryView(x.Level)),
        ];

        nineTwentyTwo.Should().HaveCount(3);
        nineTwentyTwo.Count(static x => x.Level == LogLevel.Warning).Should().Be(1, "throttled: once per check, database and kind of failure per window");
    }

    /// <summary>
    /// A database that will not take a connection is one failure told once - not one Warning per check, and
    /// not one connect timeout per check either: the second check is told the same thing at once.
    /// </summary>
    [Fact]
    public async Task A_database_that_refuses_the_connection_is_one_warning_however_many_checks_need_it()
    {
        await using HealthHarness harness = HealthHarness.Create();

        harness.Script.Gather["T90"] = static async (context, token) =>
        {
            await context.Sql.ScalarAsync<int>(new HealthQuery("select 1"), token);
            return new ScriptedFacts("T90", [], null);
        };

        harness.Script.Gather["C90"] = static async (context, token) =>
        {
            await context.Sql.ScalarAsync<int>(new HealthQuery("select 2"), token);
            return new ScriptedFacts("C90", [], null);
        };

        HealthSweep sweep = await harness.SweepAsync();

        sweep.Results.Single(static x => x.Check.Code == "T90").Outcome.Should().Be(HealthCheckOutcome.Failed);
        sweep.Results.Single(static x => x.Check.Code == "C90").Outcome.Should().Be(HealthCheckOutcome.Failed);
        sweep.Results.Single(static x => x.Check.Code == "T90").Statements.Should().ContainSingle()
            .Which.Sql.Should().Be("select 1", "a statement is recorded for Show SQL even when it never ran");

        harness.Warnings.Where(static x => x.EventId.Id == 9222).Should().ContainSingle(
            "the connection is one problem, not one per check");

        HealthCheckProblem problem = sweep.Results.Single(static x => x.Check.Code == "T90").Problem!;
        problem.Reason.Should().Be("The studio could not connect to this database.");
        problem.Detail.Should().NotBeNullOrWhiteSpace("a connection error's own words are carried, apart");
        problem.PostgresText.Should().BeNull("they are Npgsql's or the socket's, not Postgres'");

        sweep.Results.Single(static x => x.Check.Code == "K90").Outcome.Should().Be(HealthCheckOutcome.Checked,
            "a check that needs no SQL is unaffected by a database that is down");
    }

    /// <summary>
    /// A check whose feature this host's Marten lacks is listed as not applicable, and never run - asked of a made-up
    /// host on the floor, Marten 9.39.0 with JasperFx.Events 2.74.0, so the answer is the same whichever Marten this run
    /// resolved: D90's classifier is in every Marten from 9.50.3, the latest tested among them.
    /// </summary>
    [Fact]
    public async Task A_check_that_needs_a_newer_Marten_is_not_applicable_and_never_runs()
    {
        await using HealthHarness harness = HealthHarness.Create(runtime: FloorHost);

        HealthSweep sweep = await harness.SweepAsync();

        HealthCheckResult result = sweep.Results.Single(static x => x.Check.Code == "D90");
        result.Outcome.Should().Be(HealthCheckOutcome.NotApplicable);
        result.Note.Should().Contain("Needs Marten 9.50.3+",
            "the classifier is JasperFx.Events 2.89's, and the first Marten that brings it is a patch release, named in full");
        result.Note.Should().Contain("this host runs Marten 9.39.0 (JasperFx.Events 2.74.0)",
            "review item 9: the host's Marten first, the package the feature is in after it");
        harness.Script.Runs.Should().NotContainKey("D90");
    }

    /// <summary>The other half: on a host that has the feature, the same check runs like any other.</summary>
    [Fact]
    public async Task A_check_whose_feature_the_host_has_runs()
    {
        await using HealthHarness harness = HealthHarness.Create(runtime: MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string>
        {
            [RuntimePackage.Marten] = "9.50.3",
            [RuntimePackage.JasperFxEvents] = "2.90.1",
        }));

        HealthSweep sweep = await harness.SweepAsync();

        sweep.Results.Single(static x => x.Check.Code == "D90").Outcome.Should().Be(HealthCheckOutcome.Checked);
        harness.Script.Runs.Should().ContainKey("D90");
    }

    /// <summary>A host on the floor, as a made-up runtime: every light-up absent.</summary>
    private static MartenRuntime FloorHost => MartenRuntime.ForVersions(new Dictionary<RuntimePackage, string>
    {
        [RuntimePackage.Marten] = "9.39.0",
        [RuntimePackage.JasperFx] = "2.74.0",
        [RuntimePackage.JasperFxEvents] = "2.74.0",
    });

    /// <summary>
    /// Review item 4: two findings under one key would be two elements under one Blazor @key. H1-fix item 5: the
    /// row says so with the code and a count - the key holds the Subject, which sits at the finding's gate and
    /// may be stricter than the check's - and does not call the studio's sentence Postgres'.
    /// </summary>
    [Fact]
    public async Task Two_findings_with_one_key_fail_the_check_that_made_them_and_the_row_names_no_subject()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Judge["K90"] = static (_, _) => HealthVerdict.Of(
            new HealthFinding { Code = "K90", Severity = HealthSeverity.Info, Title = "One", WhyItMatters = "x", Subject = "acme-orders" },
            new HealthFinding { Code = "K90", Severity = HealthSeverity.Info, Title = "Two", WhyItMatters = "x", Subject = "acme-orders" },
            new HealthFinding { Code = "K90", Severity = HealthSeverity.Info, Title = "Three", WhyItMatters = "x", Subject = "globex" });
        harness.Script.Finds("K91", HealthSeverity.Info);

        HealthSweep sweep = await harness.SweepAsync();

        HealthCheckResult result = sweep.Results.Single(static x => x.Check.Code == "K90");
        result.Outcome.Should().Be(HealthCheckOutcome.Failed);
        result.Problem!.Reason.Should().Contain("2 of K90's 3 findings share a key").And.NotContain("acme").And.NotContain("globex");
        result.Problem.PostgresText.Should().BeNull("these are the studio's words, not Postgres'");
        result.Problem.Detail.Should().BeNull();
        sweep.Results.Single(static x => x.Check.Code == "K91").Findings.Should().ContainSingle();

        harness.Logs.Entries.Should().Contain(static x => x.EventId.Id == 9222 && x.Message.Contains("K90", StringComparison.Ordinal),
            "the log carries the exception, whose message is where the duplicated key is named");
    }

    [Fact]
    public async Task Two_pieces_of_evidence_under_one_label_fail_the_check_and_the_row_names_no_label()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Finds("K90", HealthSeverity.Info, shape: static x =>
        {
            x.Evidence.Add(new HealthEvidence("Rows in acme", "1"));
            x.Evidence.Add(new HealthEvidence("Rows in acme", "2"));
        });

        HealthSweep sweep = await harness.SweepAsync();

        HealthCheckResult result = sweep.Results.Single(static x => x.Check.Code == "K90");
        result.Outcome.Should().Be(HealthCheckOutcome.Failed);
        result.Problem!.Reason.Should().Contain("2 pieces of evidence under one label").And.NotContain("acme");
        result.Problem.PostgresText.Should().BeNull();
    }

    /// <summary>
    /// H1-fix item 5: a failure that is not Postgres' carries no words of its own to the page - its type, and a
    /// pointer to the log, where its message is - and nothing about it is labelled as Postgres'.
    /// </summary>
    [Fact]
    public async Task A_failure_that_is_not_Postgres_shows_its_type_and_leaves_its_message_to_the_log()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Throws("K90", new InvalidOperationException("table acme.secret_orders is odd"));
        harness.Script.Judge["K91"] = static (_, _) => throw new KeyNotFoundException("The given key 'globex' was not present");

        HealthSweep sweep = await harness.SweepAsync();

        foreach ((string code, string type) in new[] { ("K90", "InvalidOperationException"), ("K91", "KeyNotFoundException") })
        {
            HealthCheckProblem problem = sweep.Results.Single(x => x.Check.Code == code).Problem!;

            problem.Reason.Should().Contain(type).And.Contain("event 9222").And.NotContain("acme").And.NotContain("globex");
            problem.PostgresText.Should().BeNull("{0}'s exception is not Postgres'", code);
            problem.Detail.Should().BeNull("only a connection failure's words are carried");
        }
    }

    [Fact]
    public async Task Only_a_PostgresException_carries_Postgres_words()
    {
        PostgresException postgres = new("relation \"acme.mt_doc_x\" does not exist", "ERROR", "ERROR", PostgresErrorCodes.UndefinedTable);

        (HealthCheckOutcome outcome, HealthCheckProblem problem) = HealthSweeper.Classify(postgres, budgetExpired: false);

        outcome.Should().Be(HealthCheckOutcome.Failed);
        problem.PostgresText.Should().Be("relation \"acme.mt_doc_x\" does not exist");
        problem.SqlState.Should().Be(PostgresErrorCodes.UndefinedTable);
        problem.Reason.Should().NotContain("acme", "the studio's sentence names the SQLSTATE, and Postgres' words go where they are masked");
    }

    /// <summary>
    /// Review item 2: the sweep has a budget of its own - one interval - and a check whose turn comes after it
    /// has run out is not started, but listed as not checked.
    /// </summary>
    [Fact]
    public async Task A_check_whose_turn_comes_after_the_sweep_budget_is_not_started()
    {
        await using HealthHarness harness = HealthHarness.Create(runtime: FloorHost);
        harness.Sweeper.SweepBudget = TimeSpan.FromMilliseconds(300);

        // Tier by tier (H10): the in-process K90 and K91 first, then the catalog's D90 (not applicable on the floor host
        // this harness is told it runs, at every Marten the suite runs at), T90 and C90.
        // T90 outlives the budget, so C90 - whose turn comes after it - is the one not started.
        harness.Script.Gather["T90"] = static async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new ScriptedFacts("T90", [], null);
        };

        HealthSweep sweep = await harness.SweepAsync();

        sweep.Results.Single(static x => x.Check.Code == "T90").Outcome.Should().Be(HealthCheckOutcome.Unavailable);
        HealthCheckResult skipped = sweep.Results.Single(static x => x.Check.Code == "C90");
        skipped.Outcome.Should().Be(HealthCheckOutcome.Unavailable);
        skipped.Problem!.Reason.Should().Contain("ran out of its budget");
        harness.Script.Runs.Should().NotContainKey("C90", "it was never started");
        harness.Warnings.Should().BeEmpty("running out of the budget is the budget working");

        // The configuration checks cost nothing and ran before any catalog read could use the budget up.
        sweep.Results.Where(static x => x.Check.Tier == HealthCostTier.InProcess)
            .Should().OnlyContain(static x => x.Outcome == HealthCheckOutcome.Checked);
        harness.Script.Runs.Should().ContainKeys("K90", "K91");

        // ...and the results are still in the registry's area and code order, the order the page draws.
        sweep.Results.Select(static x => x.Check.Code).Should().Equal(harness.Services.GetRequiredService<HealthCheckRegistry>().All
            .Where(static x => x.Info.Tier is HealthCostTier.InProcess or HealthCostTier.Catalog).Select(static x => x.Info.Code));
    }

    /// <summary>Review item 6: a deeper sweep adds nothing to the history an ungated evaluator reads.</summary>
    [Fact]
    public async Task Only_a_regular_sweep_adds_to_the_history()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Gather["E90"] = static (_, _) => Task.FromResult(new ScriptedFacts("E90", [new HealthSamplePart("events.secret", 42)], null));

        await harness.SweepAsync(HealthSweepKind.Deeper);

        harness.History.Read("default", harness.Resolved.Database.Id.Identity).Samples.Should().BeEmpty();
    }

    /// <summary>
    /// Review item 12: a read several checks need runs once per sweep, its answer is every asker's, and the
    /// next sweep reads afresh.
    /// </summary>
    [Fact]
    public async Task A_shared_read_runs_once_per_sweep_and_every_asker_gets_its_answer()
    {
        await using HealthHarness harness = HealthHarness.Create();

        int reads = 0;
        List<int> answers = [];

        Func<HealthCheckContext, CancellationToken, Task<ScriptedFacts>> asker(string code) => async (context, _) =>
        {
            answers.Add(await context.Shared("tables.stats", _ => Task.FromResult(Interlocked.Increment(ref reads))));
            return new ScriptedFacts(code, [], null);
        };

        harness.Script.Gather["K90"] = asker("K90");
        harness.Script.Gather["K91"] = asker("K91");

        await harness.SweepAsync();
        reads.Should().Be(1);
        answers.Should().Equal(1, 1);

        await harness.SweepAsync();
        reads.Should().Be(2, "the memo is the sweep's, and dies with it");
    }

    /// <summary>A shared read that fails fails every check that asked for it, and its SQL is in every one's Show SQL.</summary>
    [Fact]
    public async Task A_shared_reads_statements_and_failure_belong_to_every_asker()
    {
        await using HealthHarness harness = HealthHarness.Create();

        Func<HealthCheckContext, CancellationToken, Task<ScriptedFacts>> asker(string code) => async (context, _) =>
        {
            await context.Shared("catalog", token => context.Sql.ScalarAsync<int>(new HealthQuery("select 7"), token));
            return new ScriptedFacts(code, [], null);
        };

        harness.Script.Gather["K90"] = asker("K90");
        harness.Script.Gather["K91"] = asker("K91");

        HealthSweep sweep = await harness.SweepAsync();

        foreach (string code in new[] { "K90", "K91" })
        {
            HealthCheckResult result = sweep.Results.Single(x => x.Check.Code == code);
            result.Outcome.Should().Be(HealthCheckOutcome.Failed, "{0} needed the read, and the database refused the connection", code);
            result.Statements.Should().ContainSingle().Which.Sql.Should().Be("select 7");
        }
    }

    [Fact]
    public async Task A_shared_key_asked_for_with_another_type_is_a_bug()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Gather["K90"] = static async (context, _) =>
        {
            await context.Shared("k", static _ => Task.FromResult(1));
            return new ScriptedFacts("K90", [], null);
        };
        harness.Script.Gather["K91"] = static async (context, _) =>
        {
            await context.Shared("k", static _ => Task.FromResult("one"));
            return new ScriptedFacts("K91", [], null);
        };

        HealthSweep sweep = await harness.SweepAsync();

        sweep.Results.Single(static x => x.Check.Code == "K91").Outcome.Should().Be(HealthCheckOutcome.Failed);
    }

    [Fact]
    public async Task A_finding_about_a_hidden_document_type_never_leaves_the_sweep()
    {
        await using HealthHarness harness = HealthHarness.Create(
            studio: static options => options.IsDocumentTypeVisible = static type => type != typeof(HiddenHealthThing));

        harness.Script.Finds("K90", HealthSeverity.Critical, shape: static x => x.DocumentTypes.Add(typeof(HiddenHealthThing)));
        harness.Script.Finds("K91", HealthSeverity.Warning, shape: static x => x.DocumentTypes.Add(typeof(string)));

        HealthSweep sweep = await harness.SweepAsync();

        sweep.Findings.Select(static x => x.Code).Should().Equal("K91");
    }

    [Fact]
    public async Task The_sweep_stamps_what_only_the_check_knows()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Finds("T90", HealthSeverity.Warning);

        HealthSweep sweep = await harness.SweepAsync();

        HealthFinding finding = sweep.Findings.Single();
        finding.Area.Should().Be(HealthArea.Tables);
        finding.Gate.Should().Be(HealthGate.Database, "the attribute's default, the conservative one");
        finding.CheckTitle.Should().Be("Scripted catalog");
        finding.MeasuredAt.Should().Be(harness.Clock.GetUtcNow());
    }

    /// <summary>
    /// The history is an extensible bag: each check contributes keyed parts, the sweep makes one sample of
    /// them, and an evaluator reads the trend - this sweep's sample included.
    /// </summary>
    [Fact]
    public async Task Sample_parts_become_history_that_the_next_evaluation_reads_as_a_rate()
    {
        await using HealthHarness harness = HealthHarness.Create();

        double sequence = 1_000;
        harness.Script.Gather["K90"] = (_, _) => Task.FromResult(
            new ScriptedFacts("K90", [new HealthSamplePart("daemon.highest-sequence", sequence)], null));

        double? rate = null;
        harness.Script.Judge["K90"] = (_, evaluation) =>
        {
            rate = evaluation.History.Trend("daemon.highest-sequence").PerSecond;
            return HealthVerdict.Quiet();
        };

        await harness.SweepAsync();
        rate.Should().BeNull("one sample is no rate");

        harness.Clock.Advance(TimeSpan.FromSeconds(60));
        sequence = 7_000;
        await harness.SweepAsync();

        rate.Should().Be(100, "6,000 events over 60 seconds");
        harness.History.Read("default", harness.Resolved.Database.Id.Identity).Samples.Should().HaveCount(2);
    }

    [Fact]
    public async Task Two_checks_writing_one_series_in_one_sweep_fail_the_second()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Gather["K90"] = static (_, _) => Task.FromResult(new ScriptedFacts("K90", [new HealthSamplePart("x.y", 1)], null));
        harness.Script.Gather["K91"] = static (_, _) => Task.FromResult(new ScriptedFacts("K91", [new HealthSamplePart("x.y", 2)], null));

        HealthSweep sweep = await harness.SweepAsync();

        sweep.Results.Single(static x => x.Check.Code == "K90").Outcome.Should().Be(HealthCheckOutcome.Checked);
        sweep.Results.Single(static x => x.Check.Code == "K91").Outcome.Should().Be(HealthCheckOutcome.Failed);
        sweep.Results.Single(static x => x.Check.Code == "K91").Problem!.Reason.Should().NotContain("x.y",
            "a series name can carry a qualifier - a projection, a tenant - so the row names the code only");
    }

    /// <summary>A check that breaks the one-writer rule leaves none of its parts behind, not only the duplicate.</summary>
    [Fact]
    public async Task A_check_that_writes_a_series_twice_leaves_none_of_its_parts_in_the_sample()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Gather["K90"] = static (_, _) => Task.FromResult(new ScriptedFacts("K90", [new HealthSamplePart("x.y", 1)], null));
        harness.Script.Gather["K91"] = static (_, _) => Task.FromResult(new ScriptedFacts("K91",
            [new HealthSamplePart("k91.own", 5), new HealthSamplePart("x.y", 2)], null));

        await harness.SweepAsync();

        HealthSample sample = harness.History.Read("default", harness.Resolved.Database.Id.Identity).Samples.Single();
        sample.Values.Should().ContainKey("x.y").WhoseValue.Should().Be(1);
        sample.Values.Should().NotContainKey("k91.own");
    }

    /// <summary>
    /// H1-fix item 3: a number crosses from one check to another only through the history, so that is where the
    /// gates are kept. Every series carries its writer's gate - the check's, or a stricter one the part names -
    /// and an evaluator reads only the series at or below its own check's gate.
    /// </summary>
    [Fact]
    public async Task An_evaluator_reads_only_the_series_written_at_or_below_its_own_gate()
    {
        await using HealthHarness harness = HealthHarness.Create();

        // C90 is Activity-gated, T90 Database-gated, K90 and K91 at None; K91 marks one of its own parts Activity.
        harness.Script.Gather["C90"] = static (_, _) => Task.FromResult(new ScriptedFacts("C90", [new HealthSamplePart("connections.by-role:reporting", 3)], null));
        harness.Script.Gather["T90"] = static (_, _) => Task.FromResult(new ScriptedFacts("T90", [new HealthSamplePart("tables.rows", 1_000)], null));
        harness.Script.Gather["K91"] = static (_, _) => Task.FromResult(new ScriptedFacts("K91",
        [
            new HealthSamplePart("configuration.count", 7),
            new HealthSamplePart("configuration.names", 2) { Gate = HealthGate.Activity },
        ], null));

        Dictionary<string, IReadOnlyCollection<string>> seen = new(StringComparer.Ordinal);
        foreach (string code in new[] { "K90", "T90", "C90" })
        {
            harness.Script.Judge[code] = (_, evaluation) =>
            {
                seen[code] = [.. evaluation.History.Samples.SelectMany(static x => x.Values.Keys)];
                return HealthVerdict.Quiet();
            };
        }

        await harness.SweepAsync();

        seen["K90"].Should().BeEquivalentTo(["configuration.count"],
            "an ungated check sees neither the activity count, the database-wide number, nor the part K91 marked Activity");
        seen["T90"].Should().BeEquivalentTo(["configuration.count", "tables.rows"]);
        seen["C90"].Should().BeEquivalentTo(["configuration.count", "configuration.names", "tables.rows", "connections.by-role:reporting"],
            "the strictest gate reads everything");

        HealthSample stored = harness.History.Read("default", harness.Resolved.Database.Id.Identity).Samples.Single();
        stored.GateOf("connections.by-role:reporting").Should().Be(HealthGate.Activity, "the writer's gate is stamped by the sweep");
        stored.GateOf("tables.rows").Should().Be(HealthGate.Database);
        stored.GateOf("configuration.names").Should().Be(HealthGate.Activity, "a part may be stricter than its check");
        stored.GateOf("configuration.count").Should().Be(HealthGate.None);
    }

    /// <summary>A part cannot loosen its writer's gate: an Activity check's part marked None is still Activity.</summary>
    [Fact]
    public async Task A_part_cannot_be_looser_than_the_check_that_wrote_it()
    {
        await using HealthHarness harness = HealthHarness.Create();
        harness.Script.Gather["C90"] = static (_, _) => Task.FromResult(new ScriptedFacts("C90",
            [new HealthSamplePart("connections.by-role:reporting", 3) { Gate = HealthGate.None }], null));

        await harness.SweepAsync();

        harness.History.Read("default", harness.Resolved.Database.Id.Identity).Samples.Single()
            .GateOf("connections.by-role:reporting").Should().Be(HealthGate.Activity);
    }

    /// <summary>
    /// The real tier-0 checks need no connection, so a sweep of them opens none. Tier 0 only: a catalog check a
    /// later packet adds would fail, correctly, against the harness's refused connection.
    /// </summary>
    [Fact]
    public async Task A_sweep_of_the_configuration_checks_opens_no_connection_and_logs_no_warning()
    {
        await using HealthHarness harness = HealthHarness.Create(checks: new HealthCheckRegistry().All
            .Where(static x => x.Info.Tier == HealthCostTier.InProcess)
            .Select(static x => x.Info.CheckType));

        HealthSweep sweep = await harness.SweepAsync();

        sweep.Results.Should().OnlyContain(static x => x.Outcome == HealthCheckOutcome.Checked,
            "a refused connection would have failed any check that tried one");
        sweep.Results.Should().OnlyContain(static x => x.Statements.Count == 0);
        harness.Warnings.Should().BeEmpty();
    }

    private sealed record CapturedLogEntryView(LogLevel Level);
}
