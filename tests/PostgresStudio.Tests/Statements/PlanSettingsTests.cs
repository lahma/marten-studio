using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// 0.10-B (issue #28): the settings what-if's allow-list and the checks on a run's settings and timeout that need no database, and
/// the page's own check of a value by the setting's type and range.
/// </summary>
public class PlanSettingsTests
{
    private static readonly string[] Brief =
    [
        "work_mem", "hash_mem_multiplier", "random_page_cost", "effective_io_concurrency", "jit", "max_parallel_workers_per_gather",
        "enable_async_append", "enable_bitmapscan", "enable_gathermerge", "enable_hashagg", "enable_hashjoin", "enable_incremental_sort",
        "enable_indexonlyscan", "enable_indexscan", "enable_material", "enable_memoize", "enable_mergejoin", "enable_nestloop",
        "enable_parallel_append", "enable_parallel_hash", "enable_partition_pruning", "enable_partitionwise_aggregate",
        "enable_partitionwise_join", "enable_presorted_aggregate", "enable_seqscan", "enable_sort", "enable_tidscan",
        "enable_group_by_reordering", "enable_distinct_reordering",
    ];

    [Fact]
    public void The_allow_list_is_the_one_the_design_names_and_nothing_else()
    {
        PlanSettingsQueries.Allowed.Should().BeEquivalentTo(Brief).And.OnlyHaveUniqueItems();
        PlanSettingsQueries.MaxSettings.Should().Be(8);
    }

    [Theory]
    [InlineData("work_mem", true)]
    [InlineData("enable_seqscan", true)]
    [InlineData("WORK_MEM", false)]
    [InlineData("work_mem ", false)]
    [InlineData("statement_timeout", false)]
    [InlineData("search_path", false)]
    [InlineData("role", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_name_is_on_the_list_exactly_or_not(string? name, bool allowed) =>
        PlanSettingsQueries.IsAllowed(name).Should().Be(allowed);

    [Fact]
    public void The_offered_settings_are_read_by_a_bound_array_of_the_allow_list_from_pg_settings()
    {
        PlanSettingsQueries.OffersSql.Should().Contain("pg_catalog.pg_settings").And.Contain("name = any(@names)").And.NotContain("'");
    }

    // ---- The timeout -----------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, 30, 30)]
    [InlineData(5, 30, 5)]
    [InlineData(30, 30, 30)]
    [InlineData(31, 30, 30)]
    [InlineData(100_000, 30, 30)]
    [InlineData(int.MaxValue, 30, 30)]
    public void A_runs_timeout_is_never_above_the_hosts(int? asked, int host, int held)
    {
        PlanRunRequest request = new("select 1", PlanMode.Plan) { TimeoutSeconds = asked };

        PlanRequests.EffectiveTimeout(request, TimeSpan.FromSeconds(host)).Should().Be(TimeSpan.FromSeconds(held));
    }

    [Fact]
    public void A_hosts_timeout_below_a_second_is_still_the_ceiling()
    {
        PlanRequests.EffectiveTimeout(new PlanRunRequest("select 1", PlanMode.Plan) { TimeoutSeconds = 5 }, TimeSpan.FromMilliseconds(500))
            .Should().Be(TimeSpan.FromMilliseconds(500));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_timeout_below_a_second_is_refused_rather_than_read_as_no_timeout(int seconds)
    {
        SqlRejection? refusal = PlanRequests.Refusal(new PlanRunRequest("select 1", PlanMode.Plan) { TimeoutSeconds = seconds }, 0);

        refusal.Should().NotBeNull();
        refusal!.Reason.Should().Be(PlanRequests.TimeoutInvalidReason);
    }

    // ---- What a request that names settings is refused for -----------------------------------------------------------------

    [Fact]
    public void A_request_with_no_settings_and_one_with_settings_on_the_list_are_not_refused()
    {
        PlanRequests.Refusal(new PlanRunRequest("select 1", PlanMode.Plan), 0).Should().BeNull();
        PlanRequests.Refusal(
            new PlanRunRequest("select 1", PlanMode.Plan) { SessionSettings = [new PlanSetting("work_mem", "64MB"), new PlanSetting("jit", "off")] }, 0)
            .Should().BeNull();
    }

    [Fact]
    public void A_value_that_is_an_attack_is_not_refused_here_when_the_setting_is_one_Postgres_reads_it_is_a_parameter()
    {
        // Bounded text. What it would do as SQL it never is.
        PlanRequests.Refusal(new PlanRunRequest("select 1", PlanMode.Plan) { SessionSettings = [new PlanSetting("random_page_cost", "1'; drop table x; --")] }, 0)
            .Should().BeNull();
    }

    // ---- The memory ceilings: refused before a connection, never reduced -----------------------------------------------------

    private static SqlRejection? Refusal(string name, string value) =>
        PlanRequests.Refusal(new PlanRunRequest("select 1", PlanMode.Plan) { SessionSettings = [new PlanSetting(name, value)] }, 0);

    [Theory]
    [InlineData("1GB")]
    [InlineData("1 GB")]
    [InlineData("1024MB")]
    [InlineData("1048576")]
    [InlineData("1048576kB")]
    [InlineData("1073741824B")]
    [InlineData("64MB")]
    [InlineData("64")]
    [InlineData("1")]
    [InlineData("0")]
    public void Work_mem_at_or_below_a_gigabyte_is_not_refused_in_any_unit(string value) =>
        Refusal("work_mem", value).Should().BeNull();

    [Theory]
    [InlineData("1025MB")]
    [InlineData("1048577")]
    [InlineData("1048577kB")]
    [InlineData("1073741825B")]
    [InlineData("2GB")]
    [InlineData("1TB")]
    [InlineData("2 GB")]
    [InlineData("999999999999999TB")]
    [InlineData("999999999999999")]
    public void Work_mem_above_a_gigabyte_is_refused_in_every_unit_with_the_ceiling_named_and_the_value_never_echoed(string value)
    {
        SqlRejection? refusal = Refusal("work_mem", value);

        refusal.Should().NotBeNull();
        refusal!.Reason.Should().Be(PlanRequests.SettingAboveCeilingReason);
        refusal.Message.Should().Contain("1GB").And.Contain("refused, not reduced").And.NotContain(value.Trim());
    }

    [Theory]
    [InlineData("1gb")]
    [InlineData("1Gb")]
    [InlineData("1.5GB")]
    [InlineData("1GB'; drop table x; --")]
    [InlineData("lots")]
    [InlineData("-1")]
    [InlineData("1 PB")]
    [InlineData("0x40")]
    [InlineData("1e3")]
    [InlineData("1000000000000000000000")]
    public void Work_mem_the_studio_cannot_read_is_refused_rather_than_passed_on(string value)
    {
        SqlRejection? refusal = Refusal("work_mem", value);

        refusal.Should().NotBeNull();
        refusal!.Reason.Should().Be(PlanRequests.SettingValueInvalidReason);
        refusal.Message.Should().Contain("1GB");
    }

    [Theory]
    [InlineData("8")]
    [InlineData("8.0")]
    [InlineData("1")]
    [InlineData("1.5")]
    [InlineData("8e0")]
    [InlineData("0.5")]
    public void Hash_mem_multiplier_at_or_below_eight_is_not_refused(string value) =>
        Refusal("hash_mem_multiplier", value).Should().BeNull();

    [Theory]
    [InlineData("8.01")]
    [InlineData("9")]
    [InlineData("1000")]
    [InlineData("1e1")]
    public void Hash_mem_multiplier_above_eight_is_refused_with_the_ceiling_named(string value)
    {
        SqlRejection? refusal = Refusal("hash_mem_multiplier", value);

        refusal.Should().NotBeNull();
        refusal!.Reason.Should().Be(PlanRequests.SettingAboveCeilingReason);
        refusal.Message.Should().Contain("8").And.Contain("refused, not reduced").And.NotContain(value);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("abc")]
    [InlineData("8; drop table x")]
    public void Hash_mem_multiplier_the_studio_cannot_read_is_refused_rather_than_passed_on(string value) =>
        Refusal("hash_mem_multiplier", value)!.Reason.Should().Be(PlanRequests.SettingValueInvalidReason);

    [Fact]
    public void A_ceiling_holds_whatever_else_is_asked_and_binds_no_other_setting()
    {
        // Every other setting is Postgres's to read, whatever its value looks like.
        foreach (string name in PlanSettingsQueries.Allowed.Except(["work_mem", "hash_mem_multiplier"]))
        {
            PlanSettingsQueries.Ceiling(name, "99999999999").Should().BeNull(name);
        }

        // The ceiling is the first thing a plan's settings meet, in any position among them.
        PlanRequests.Refusal(
            new PlanRunRequest("select 1", PlanMode.Plan) { SessionSettings = [new PlanSetting("jit", "off"), new PlanSetting("work_mem", "2GB")] }, 0)!
            .Reason.Should().Be(PlanRequests.SettingAboveCeilingReason);
    }

    [Fact]
    public void The_page_says_the_ceiling_before_anything_is_sent()
    {
        PlanSettingRow workMem = Row("work_mem", "integer", "kB", "64", "2147483647");

        PlanSettingValues.ValidateWithCeiling(workMem, "1GB").Should().BeNull();
        PlanSettingValues.ValidateWithCeiling(workMem, "2GB").Should().Contain("1GB");
        PlanSettingValues.ValidateWithCeiling(Row("hash_mem_multiplier", "real", null, "1", "1000"), "9").Should().Contain("8");
        PlanSettingValues.ValidateWithCeiling(Row("hash_mem_multiplier", "real", null, "1", "1000"), "8").Should().BeNull();
    }

    // ---- The audit's words ---------------------------------------------------------------------------------------------------

    [Fact]
    public void What_a_run_changed_is_named_in_the_audit_and_a_value_is_escaped()
    {
        PlanRunRequest request = new("select 1", PlanMode.Plan) { SessionSettings = [new PlanSetting("work_mem", "64MB"), new PlanSetting("jit", "off")] };

        PlanRunner.Changes(request).Should().Be(" with work_mem=64MB, jit=off");
        PlanRunner.Changes(request, TimeSpan.FromSeconds(5)).Should().Be(" with work_mem=64MB, jit=off, statement_timeout=5s");
        PlanRunner.Changes(new PlanRunRequest("select 1", PlanMode.Plan)).Should().BeEmpty();
        PlanRunner.Outcome(PlanMode.Plan, TimeSpan.FromMilliseconds(3), request).Should().Be("planned in 3 ms with work_mem=64MB, jit=off");

        PlanRunner.Changes(new PlanRunRequest("select 1", PlanMode.Plan) { SessionSettings = [new PlanSetting("jit", "off\u001b[2J")] })
            .Should().NotContain("\u001b");
    }

    // ---- The page's own check of a value -----------------------------------------------------------------------------------------

    private static PlanSettingRow Row(string name, string type, string? unit = null, string? min = null, string? max = null) =>
        new(name, "x", unit, type, min, max);

    [Theory]
    [InlineData("on", true)]
    [InlineData("OFF", true)]
    [InlineData("true", true)]
    [InlineData("0", true)]
    [InlineData("maybe", false)]
    [InlineData("", false)]
    public void A_boolean_takes_on_or_off(string value, bool valid) =>
        (PlanSettingValues.Validate(Row("jit", "bool"), value) is null).Should().Be(valid);

    [Theory]
    [InlineData("64MB", true)]
    [InlineData("512kB", true)]
    [InlineData("1GB", true)]
    [InlineData("4096", true)]
    [InlineData("64 MB", true)]
    [InlineData("64MB'; drop table x; --", false)]
    [InlineData("-1", false)]
    [InlineData("1.5MB", false)]
    [InlineData("64 megabytes", false)]
    [InlineData("1kB", false)]
    [InlineData("4TB", false)]
    public void Memory_takes_a_size_within_its_range(string value, bool valid)
    {
        // work_mem: kB, at least 64, at most 2,147,483,647.
        PlanSettingRow workMem = Row("work_mem", "integer", "kB", "64", "2147483647");

        (PlanSettingValues.Validate(workMem, value) is null).Should().Be(valid);
    }

    [Theory]
    [InlineData("integer", "0", "1000", "200", true)]
    [InlineData("integer", "0", "1000", "1001", false)]
    [InlineData("integer", "0", "1000", "-1", false)]
    [InlineData("integer", "0", "1000", "1.5", false)]
    [InlineData("real", "0", "1.79769e+308", "1.1", true)]
    [InlineData("real", "0", "1.79769e+308", "-0.5", false)]
    [InlineData("real", "0", "1.79769e+308", "NaN", false)]
    [InlineData("real", "0", "1.79769e+308", "abc", false)]
    [InlineData("enum", null, null, "on", false)]
    public void A_number_is_checked_against_the_servers_range(string type, string? min, string? max, string value, bool valid) =>
        (PlanSettingValues.Validate(Row("random_page_cost", type, null, min, max), value) is null).Should().Be(valid);
}
