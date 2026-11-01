using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Sources;

namespace MartenStudio.Tests.Query;

/// <summary>
/// 0.10-B (issue #28), security: what the Plan page may change for one plan is a list in code, and a name off it is refused before a
/// connection is opened; a value is a parameter, which Postgres - not the studio - reads. The same service answers the what-if in
/// place, which refuses an index its builder did not write, and a request that cannot be planned, before any connection too.
/// </summary>
/// <remarks>
/// No database: the store's connection string does not resolve, so a refusal that happened after a connection was tried would
/// come back as a connection error (SQLSTATE 08006) rather than as the studio's own refusal. The live half is in
/// <c>PlanSettingsLiveTests</c>.
/// </remarks>
public class PlanSettingsGateTests
{
    private const string ConnectionString = "Host=marten-studio-plan-settings.invalid;Database=shared;Username=none;Password=none;Timeout=2";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<string> NamesOffTheList() =>
    [
        "statement_timeout",
        "search_path",
        "session_authorization",
        "role",
        "log_statement",
        "WORK_MEM",
        "work_mem ",
        " work_mem",
        "work_mem;drop table x",
        "enable_",
        "pg_catalog.work_mem",
        string.Empty,
    ];

    [Theory]
    [MemberData(nameof(NamesOffTheList))]
    public async Task A_name_off_the_allow_list_is_refused_before_any_connection_and_audited(string name)
    {
        await using Harness harness = await Harness.CreateAsync();

        PlanRunResult result = await harness.Console.PlanAsync(
            harness.Scope,
            new PlanRunRequest("select 1", PlanMode.Plan) { SessionSettings = [new PlanSetting(name, "1")] },
            Token);

        result.Rejection.Should().NotBeNull();
        result.Rejection!.Reason.Should().Be(PlanRequests.SettingNotAllowedReason);
        result.Error.Should().BeNull("a refusal that came after a connection was tried would be a connection error");

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Succeeded.Should().BeFalse();
        entry.Action.Should().Be(SqlConsoleService.RunSqlAction);
    }

    [Fact]
    public async Task A_setting_on_the_list_is_not_refused_by_the_studio_whatever_its_value_says_and_reaches_the_connection()
    {
        await using Harness harness = await Harness.CreateAsync();

        // The value is text a visitor typed. It is Postgres's to read: the studio only bounds it, and the run goes on to the
        // connection - which does not resolve here, so the answer is the connection's error, never a refusal.
        PlanRunResult result = await harness.Console.PlanAsync(
            harness.Scope,
            new PlanRunRequest("select 1", PlanMode.Plan) { SessionSettings = [new PlanSetting("random_page_cost", "1'; drop table x; --")] },
            Token);

        result.Rejection.Should().BeNull();
        result.Error.Should().NotBeNull();
    }

    [Theory]
    [InlineData("work_mem", "2GB", "SettingAboveCeiling")]
    [InlineData("work_mem", "1049600kB", "SettingAboveCeiling")]
    [InlineData("work_mem", "64MB'; drop table x; --", "SettingValueInvalid")]
    [InlineData("hash_mem_multiplier", "9", "SettingAboveCeiling")]
    [InlineData("hash_mem_multiplier", "NaN", "SettingValueInvalid")]
    public async Task A_memory_setting_above_its_ceiling_or_not_readable_is_refused_before_any_connection_and_audited_with_no_value(string name, string value, string reason)
    {
        await using Harness harness = await Harness.CreateAsync();

        PlanRunResult result = await harness.Console.PlanAsync(
            harness.Scope,
            new PlanRunRequest("select 1", PlanMode.Analyze) { SessionSettings = [new PlanSetting(name, value)] },
            Token);

        result.Rejection!.Reason.Should().Be(reason);
        result.Error.Should().BeNull("a refusal that came after a connection was tried would be a connection error");

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().NotContain(value, "the visitor's value is not echoed into the audit");
    }

    [Fact]
    public async Task More_than_eight_settings_a_setting_twice_and_a_value_that_is_not_bounded_text_are_refused()
    {
        await using Harness harness = await Harness.CreateAsync();

        PlanSetting[] nine = [.. PlanSettingsQueries.Allowed.Take(9).Select(static x => new PlanSetting(x, "1"))];
        string[] reasons =
        [
            (await Plan(harness, nine)).Rejection!.Reason,
            (await Plan(harness, new PlanSetting("jit", "off"), new PlanSetting("jit", "on"))).Rejection!.Reason,
            (await Plan(harness, new PlanSetting("jit", string.Empty))).Rejection!.Reason,
            (await Plan(harness, new PlanSetting("jit", new string('x', PlanSettingsQueries.MaxValueCharacters + 1)))).Rejection!.Reason,
            (await Plan(harness, new PlanSetting("jit", "off\r\nset role x"))).Rejection!.Reason,
        ];

        reasons.Should().Equal(
            PlanRequests.SettingsInvalidReason,
            PlanRequests.SettingsInvalidReason,
            PlanRequests.SettingValueInvalidReason,
            PlanRequests.SettingValueInvalidReason,
            PlanRequests.SettingValueInvalidReason);
    }

    [Fact]
    public async Task The_what_if_in_place_refuses_an_index_the_builder_did_not_write_and_a_request_that_cannot_be_planned_before_any_connection()
    {
        await using Harness harness = await Harness.CreateAsync();

        HypotheticalIndex typed = new()
        {
            Key = "typed",
            Definition = "create index on t (a)",
            Caption = "typed",
            Origin = HypotheticalIndexOrigin.Plan,
            Table = "t",
            Columns = ["a"],
        };

        HypotheticalIndex built = HypotheticalIndexCandidates.FromPlan(QueryPlanReader.Read(
            """[{"Plan":{"Node Type":"Seq Scan","Relation Name":"orders","Schema":"public","Alias":"o","Filter":"(o.customer_id = 42)","Total Cost":10}}]""").Plan!).Single();

        WhatIfResult notBuilt = await harness.Console.PlanWhatIfAsync(harness.Scope, new PlanRunRequest("select * from t where a = 1", PlanMode.Plan), typed, Token);
        WhatIfResult noValues = await harness.Console.PlanWhatIfAsync(harness.Scope, new PlanRunRequest("select * from t where a = $1", PlanMode.Plan), built, Token);
        WhatIfResult explainTyped = await harness.Console.PlanWhatIfAsync(harness.Scope, new PlanRunRequest("explain select 1", PlanMode.Plan), built, Token);

        notBuilt.Reason.Should().Be(SqlConsoleService.NotBuiltMessage);
        noValues.Reason.Should().Contain("placeholder");
        explainTyped.Reason.Should().Be(SqlConsoleService.AlreadyExplainMessage);

        harness.Ring.GetLatest().Should().HaveCount(3).And.OnlyContain(static x => !x.Succeeded && x.Action == SqlConsoleService.WhatIfAction);
    }

    private static Task<PlanRunResult> Plan(Harness harness, params PlanSetting[] settings) =>
        harness.Console.PlanAsync(harness.Scope, new PlanRunRequest("select 1", PlanMode.Plan) { SessionSettings = settings }, Token);

    /// <summary>A studio over a store that is never connected to.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private Harness(ServiceProvider provider, string database)
        {
            this.provider = provider;
            scope = provider.CreateScope();
            Scope = new StudioScope(MartenStoreRegistry.DefaultStoreKey, database, null);
        }

        public StudioScope Scope { get; }

        public ISqlConsoleService Console => scope.ServiceProvider.GetRequiredService<ISqlConsoleService>();

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        public static async Task<Harness> CreateAsync()
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMarten(options => options.Connection(ConnectionString));
            services.AddMartenStudio(options =>
            {
                options.Capabilities = MartenStudioCapabilities.All();
                options.StoreAuthorizationPolicy = "studio-store";
                options.WriteAuthorizationPolicy = "studio-write";
            });

            services.AddSingleton<IAuthorizationService>(policies);
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            ServiceProvider provider = services.BuildServiceProvider();
            IReadOnlyList<IMartenDatabase> databases = await provider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();

            return new Harness(provider, databases[0].Id.Identity);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }
}
