using System.Text.Json;

using PostgresStudio.Services.Diagnostics;
using PostgresStudio.Services.Health;

using static PostgresStudio.Tests.Diagnostics.DiagnosticsTestFacts;

namespace PostgresStudio.Tests.Diagnostics;

/// <summary>
/// The compare's rules (1.2-H-B, D-HEALTH C6, P1 to P4): what the visitor's current gated view allows the compare to say, and
/// the proof that what it does not allow is counted and never described - sentinel subjects, texts and values from the earlier
/// file that must not appear in the comparison.
/// </summary>
public class SnapshotComparerTests
{
    private const string Sentinel = "SENTINEL-EARLIER-7c2e";

    private static PostgresDiagnosticsSnapshot Earlier() => SnapshotFileReaderTests.Everything() with { GeneratedAt = At.AddDays(-2) };

    private static PostgresDiagnosticsSnapshot Now() => SnapshotFileReaderTests.Everything() with { GeneratedAt = At.AddDays(0) };

    private static HealthCheckStatus Row(string code, HealthCheckOutcome outcome = HealthCheckOutcome.Checked, string? belowGate = null, bool withheld = false) =>
        new(code, "title", HealthArea.Tables, HealthCostTier.Catalog, outcome, 0, null, null, null, "docs", [], FindingsWithheld: withheld) { BelowGate = belowGate };

    private static VisitorView View(PostgresDiagnosticsSnapshot now, params HealthCheckStatus[] checks) =>
        new(now, new HealthReport { StoreKey = now.Database.Source, DatabaseId = now.Database.Database, Checks = checks }, now.GeneratedAt);

    private static SnapshotComparison Compare(PostgresDiagnosticsSnapshot earlier, PostgresDiagnosticsSnapshot now, bool hides = false, params HealthCheckStatus[] checks) =>
        SnapshotComparer.Compare(earlier, View(now, checks), "earlier.json", 0, hides);

    private static string Dump(SnapshotComparison comparison) => JsonSerializer.Serialize(comparison);

    private static PostgresFinding F(string code, PostgresSeverity severity, string title, string? subject = null, string? tenant = null, bool accepted = false) => new()
    {
        Code = code,
        Subject = subject,
        TenantId = tenant,
        Severity = severity,
        Category = PostgresFindingCategory.Tables,
        Title = title,
        Detail = "d",
        DocsUrl = "https://example.test",
        MeasuredAt = At,
        Accepted = accepted,
    };

    // -------------------------------------------------------------------------------------------------
    // Findings
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Every_kind_of_finding_change_is_named_and_titled_from_the_current_view()
    {
        PostgresDiagnosticsSnapshot earlier = Earlier() with
        {
            Findings =
            [
                F("A01", PostgresSeverity.Warning, "Earlier title A", "s"),
                F("B01", PostgresSeverity.Critical, "Earlier title B"),
                F("C01", PostgresSeverity.Info, "Earlier title C"),
                F("D01", PostgresSeverity.Info, "Earlier title D"),
                F("E01", PostgresSeverity.Warning, "Earlier title E", accepted: true),
                F("G01", PostgresSeverity.Warning, "Gone finding", "public.gone"),
            ],
        };

        PostgresDiagnosticsSnapshot now = Now() with
        {
            Findings =
            [
                F("A01", PostgresSeverity.Critical, "Now title A", "s"),
                F("B01", PostgresSeverity.Info, "Now title B"),
                F("C01", PostgresSeverity.Info, "Now title C", accepted: true),
                F("D01", PostgresSeverity.Info, "Now title D"),
                F("E01", PostgresSeverity.Warning, "Now title E"),
                F("N01", PostgresSeverity.Warning, "New finding"),
            ],
        };

        SnapshotComparison result = Compare(earlier, now, false, Row("G01"));

        result.Outcome.Should().Be(SnapshotCompareOutcome.Compared);
        Kind(result, "A01").Should().Be(SnapshotChangeKind.Worse);
        Kind(result, "B01").Should().Be(SnapshotChangeKind.Better);
        Kind(result, "C01").Should().Be(SnapshotChangeKind.NowAccepted);
        Kind(result, "D01").Should().Be(SnapshotChangeKind.Unchanged);
        Kind(result, "E01").Should().Be(SnapshotChangeKind.NoLongerAccepted);
        Kind(result, "N01").Should().Be(SnapshotChangeKind.New);
        Kind(result, "G01").Should().Be(SnapshotChangeKind.NoLongerReported, "its check's row is here, checked, shown and withholding nothing");

        result.Findings.Single(static x => x.Code == "A01").Title.Should().Be("Now title A");
        result.Findings.Single(static x => x.Code == "A01").Subject.Should().Be("s");
        result.Findings.Single(static x => x.Code == "A01").SeverityBefore.Should().Be(PostgresSeverity.Warning);
        result.Findings.Single(static x => x.Code == "A01").SeverityNow.Should().Be(PostgresSeverity.Critical);
        result.Findings.Single(static x => x.Code == "G01").Title.Should().Be("Gone finding", "the earlier title, because the finding is in no current view");
        result.Verdict.Should().Contain("1 new finding").And.Contain("1 worse").And.Contain("1 better").And.Contain("1 no longer reported");

        static SnapshotChangeKind Kind(SnapshotComparison comparison, string code) => comparison.Findings.Single(x => x.Code == code).Kind;
    }

    [Fact]
    public void A_finding_is_matched_by_its_recomputed_key_not_by_title()
    {
        PostgresDiagnosticsSnapshot earlier = Earlier() with { Findings = [F("T07", PostgresSeverity.Warning, "one title", "public.a"), F("T07", PostgresSeverity.Warning, "one title", "public.b")] };
        PostgresDiagnosticsSnapshot now = Now() with { Findings = [F("T07", PostgresSeverity.Warning, "other title", "public.b")] };

        SnapshotComparison result = Compare(earlier, now, false, Row("T07"));

        result.Findings.Should().ContainSingle(static x => x.Kind == SnapshotChangeKind.Unchanged && x.Subject == "public.b");
        result.Findings.Should().ContainSingle(static x => x.Kind == SnapshotChangeKind.NoLongerReported && x.Subject == "public.a");
    }

    public static TheoryData<string, string> NotGone => new()
    {
        { "no-row", "of checks with no result in your current view" },
        { "below-gate", "of checks whose findings are not shown to you now" },
        { "unavailable", "of checks that did not check this time" },
        { "withheld", "of checks some of whose findings are withheld from you now" },
        { "tenant", "about one tenant" },
        { "hides", "hides some document types from the studio" },
    };

    [Theory]
    [MemberData(nameof(NotGone))]
    public void A_finding_that_may_not_be_called_gone_is_counted_and_never_described(string scenario, string sentence)
    {
        PostgresFinding vanished = F("X01", PostgresSeverity.Warning, "Title " + Sentinel, "subject-" + Sentinel, tenant: scenario == "tenant" ? "tenant-" + Sentinel : null);
        PostgresDiagnosticsSnapshot earlier = Earlier() with { Findings = [vanished] };
        PostgresDiagnosticsSnapshot now = Now() with { Findings = [] };

        HealthCheckStatus[] rows = scenario switch
        {
            "no-row" => [],
            "below-gate" => [Row("X01", belowGate: "needs activity")],
            "unavailable" => [Row("X01", HealthCheckOutcome.Unavailable)],
            "withheld" => [Row("X01", withheld: true)],
            _ => [Row("X01")],
        };

        SnapshotComparison result = Compare(earlier, now, scenario == "hides", rows);

        result.Findings.Should().BeEmpty();
        result.NotCompared.Should().ContainSingle(x => x.What == "Earlier findings no longer in your view" && x.Count == 1 && x.Why.Contains(sentence, StringComparison.Ordinal));
        Dump(result).Should().NotContain(Sentinel, "an earlier finding the current view would not show is counted, never described");
        result.Verdict.Should().Contain("nothing you may see has changed");
    }

    [Fact]
    public void Findings_withheld_from_the_visitor_now_are_counted_with_the_views_own_sentence()
    {
        PostgresDiagnosticsSnapshot now = Now() with
        {
            FindingsWithheld = 3,
            Withheld = new Dictionary<string, string> { ["findings"] = "3 findings are not shown to you." },
        };

        SnapshotComparison result = Compare(Earlier(), now, false);

        result.NotCompared.Should().Contain(new SnapshotNotCompared("Findings not shown to you now", 3, "3 findings are not shown to you."));
    }

    // -------------------------------------------------------------------------------------------------
    // A section the visitor is short of now
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void A_section_withheld_now_is_not_compared_and_nothing_of_the_files_content_for_it_is_drawn()
    {
        PostgresDiagnosticsSnapshot earlier = Earlier() with
        {
            Statements = new PostgresStatementsSection
            {
                Outcome = PostgresReadOutcome.Read,
                Window = TimeSpan.FromMinutes(10),
                Top = [new PostgresStatement { QueryId = 1, Text = "select " + Sentinel, Calls = 1, TotalMilliseconds = 1, MeanMilliseconds = 1, ShareOfTime = 0.5 }],
            },
            Storage = SnapshotFileReaderTests.Everything().Storage! with
            {
                LargestTables = [new PostgresTableStorage { Schema = "s", Name = "t-" + Sentinel, TotalBytes = 99 }],
            },
            Connection = SnapshotFileReaderTests.Everything().Connection! with
            {
                Settings = [new ConnectionSnapshotSetting("Host", "c", Sentinel, "connectionString", null, null, null)],
            },
        };

        PostgresDiagnosticsSnapshot now = Now() with
        {
            Statements = null,
            Storage = null,
            Settings = null,
            Server = null,
            Connections = null,
            Wal = null,
            Connection = null,
            Withheld = new Dictionary<string, string>
            {
                ["statements"] = "Statements need ViewDatabaseActivity.",
                ["storage"] = "The store policy does not allow you this database.",
                ["settings"] = "settings withheld",
                ["server"] = "server withheld",
                ["connections"] = "connections withheld",
                ["wal"] = "wal withheld",
                ["connection"] = "connection withheld",
            },
        };

        SnapshotComparison result = Compare(earlier, now);

        foreach ((string what, string why) in new[]
        {
            ("Statements", "Statements need ViewDatabaseActivity."),
            ("Storage", "The store policy does not allow you this database."),
            ("Server", "server withheld"),
            ("Settings and extensions", "settings withheld"),
            ("Connections", "connections withheld"),
            ("Write-ahead log", "wal withheld"),
            ("Connection settings", "connection withheld"),
        })
        {
            result.NotCompared.Should().Contain(new SnapshotNotCompared(what, 0, "not compared - " + why));
        }

        Dump(result).Should().NotContain(Sentinel);
        result.Statements.Should().BeEmpty();
        result.Tables.Should().BeEmpty();
    }

    [Fact]
    public void A_section_one_side_did_not_read_is_not_compared_and_says_so()
    {
        PostgresDiagnosticsSnapshot earlier = Earlier() with { Wal = new PostgresWalSection { Outcome = PostgresReadOutcome.Unavailable, Reason = "Sentinel " + Sentinel } };
        PostgresDiagnosticsSnapshot now = Now() with { Storage = Now().Storage! with { Outcome = PostgresReadOutcome.Failed, Reason = "the view's own sentence" } };

        SnapshotComparison result = Compare(earlier, now);

        result.NotCompared.Should().Contain(static x => x.What == "Write-ahead log" && x.Why.StartsWith("not compared - the earlier snapshot did not read it (unavailable)", StringComparison.Ordinal));
        result.NotCompared.Should().Contain(static x => x.What == "Storage" && x.Why == "not compared - not read now: the view's own sentence");
        Dump(result).Should().NotContain(Sentinel, "an earlier reason is the file's, not the view's");
    }

    // -------------------------------------------------------------------------------------------------
    // Statements
    // -------------------------------------------------------------------------------------------------

    private static PostgresStatementsSection Statements(TimeSpan? window, params PostgresStatement[] top) => new()
    {
        Outcome = PostgresReadOutcome.Read,
        Window = window,
        Top = top,
    };

    private static PostgresStatement S(long id, double mean, double share, long calls = 100, string? text = "select 1") => new()
    {
        QueryId = id,
        Text = text,
        Calls = calls,
        TotalMilliseconds = mean * calls,
        MeanMilliseconds = mean,
        ShareOfTime = share,
    };

    [Fact]
    public void Statements_are_matched_by_queryid_and_changed_only_past_the_thresholds()
    {
        PostgresDiagnosticsSnapshot earlier = Earlier() with
        {
            Statements = Statements(
                TimeSpan.FromHours(1),
                S(1, 10, 0.30),     // mean +50%, +5 ms: changed
                S(2, 10, 0.30),     // mean +5%: not
                S(3, 0.4, 0.10),    // mean +100% but under 1 ms: not
                S(4, 10, 0.10),     // share +10 points: changed
                S(5, 10, 0.10),     // share +4 points: not
                S(6, 8, 0.05, text: "select " + Sentinel)), // gone
        };

        PostgresDiagnosticsSnapshot now = Now() with
        {
            Statements = Statements(
                TimeSpan.FromHours(2),
                S(1, 15, 0.30, calls: 400),
                S(2, 10.5, 0.30),
                S(3, 0.8, 0.10),
                S(4, 10, 0.20),
                S(5, 10, 0.14),
                S(7, 2, 0.05, text: "select now_text")), // new
        };

        SnapshotComparison result = Compare(earlier, now);

        result.Statements.Where(static x => x.Kind == SnapshotStatementChangeKind.Changed).Select(static x => x.QueryId).Should().BeEquivalentTo([1L, 4L]);

        SnapshotStatementChange changed = result.Statements.Single(static x => x.QueryId == 1);
        changed.MeanBefore.Should().Be(10);
        changed.MeanNow.Should().Be(15);
        changed.CallsPerHourBefore.Should().BeApproximately(100, 0.001, "both windows are known, so calls per hour are shown");
        changed.CallsPerHourNow.Should().BeApproximately(200, 0.001);

        SnapshotStatementChange added = result.Statements.Single(static x => x.Kind == SnapshotStatementChangeKind.New);
        added.QueryId.Should().Be(7);
        added.Text.Should().Be("select now_text", "a new statement's text is the current view's, masked as the page masks it");

        SnapshotStatementChange gone = result.Statements.Single(static x => x.Kind == SnapshotStatementChangeKind.Gone);
        gone.QueryId.Should().Be(6);
        gone.Text.Should().BeNull("a statement that is gone is named by queryid and its earlier numbers only");
        gone.MeanBefore.Should().Be(8);
        Dump(result).Should().NotContain(Sentinel);
    }

    [Fact]
    public void Calls_per_hour_need_both_windows()
    {
        PostgresDiagnosticsSnapshot earlier = Earlier() with { Statements = Statements(null, S(1, 10, 0.3)) };
        PostgresDiagnosticsSnapshot now = Now() with { Statements = Statements(TimeSpan.FromHours(1), S(1, 20, 0.3)) };

        SnapshotStatementChange change = Compare(earlier, now).Statements.Single();

        change.CallsPerHourBefore.Should().BeNull();
        change.CallsPerHourNow.Should().BeNull();
    }

    [Fact]
    public void At_most_twenty_statements_of_each_kind_are_listed_and_the_rest_counted()
    {
        PostgresStatement[] many = [.. Enumerable.Range(1, 30).Select(static i => S(i, 10, 0.01))];
        PostgresStatement[] changedAll = [.. many.Select(static x => x with { MeanMilliseconds = 30 })];

        SnapshotComparison result = Compare(Earlier() with { Statements = Statements(null, many) }, Now() with { Statements = Statements(null, changedAll) });

        result.Statements.Should().HaveCount(SnapshotComparer.MaxListed);
        result.NotCompared.Should().Contain(static x => x.What == "Statements" && x.Count == 10);
    }

    // -------------------------------------------------------------------------------------------------
    // Storage
    // -------------------------------------------------------------------------------------------------

    private static PostgresStorageSection Storage(long database, DateTimeOffset at, params PostgresTableStorage[] tables) => new()
    {
        Outcome = PostgresReadOutcome.Read,
        MeasuredAt = at,
        DatabaseBytes = database,
        LargestTables = tables,
    };

    private static PostgresTableStorage T(string name, long bytes) => new() { Schema = "public", Name = name, TotalBytes = bytes };

    [Fact]
    public void Database_size_change_is_per_day_only_when_the_two_are_an_hour_apart()
    {
        const long gb = 1024L * 1024 * 1024;

        SnapshotComparison apart = Compare(
            Earlier() with { Storage = Storage(10 * gb, At.AddDays(-2)) },
            Now() with { Storage = Storage(12 * gb, At) });

        apart.Storage.Single().Note.Should().Contain("+2.0 GiB").And.Contain("+1.0 GiB a day");

        SnapshotComparison close = Compare(
            Earlier() with { Storage = Storage(10 * gb, At.AddMinutes(-10)) },
            Now() with { Storage = Storage(12 * gb, At) });

        close.Storage.Single().Note.Should().NotContain("a day");
    }

    [Fact]
    public void Tables_are_changed_new_and_gone_by_name_when_the_view_lists_every_table_it_would()
    {
        const long mb = 1024 * 1024;

        PostgresDiagnosticsSnapshot earlier = Earlier() with
        {
            Storage = Storage(1, At, T("grew", 100 * mb), T("tiny-change", 500 * mb), T("small-big-ratio", 5 * mb), T("gone", 7 * mb)),
        };

        PostgresDiagnosticsSnapshot now = Now() with
        {
            Storage = Storage(1, At, T("grew", 150 * mb), T("tiny-change", 500 * mb + 10), T("small-big-ratio", 6 * mb), T("fresh", 9 * mb)),
            Withheld = new Dictionary<string, string>(),
        };

        SnapshotComparison result = Compare(earlier, now);

        result.Tables.Select(static x => (x.Kind, x.Table)).Should().BeEquivalentTo(
        [
            (SnapshotTableChangeKind.Changed, "public.grew"),
            (SnapshotTableChangeKind.Changed, "public.small-big-ratio"),
            (SnapshotTableChangeKind.New, "public.fresh"),
            (SnapshotTableChangeKind.Gone, "public.gone"),
        ]);
    }

    [Fact]
    public void A_table_is_named_gone_only_without_the_withheld_key_and_outside_p4()
    {
        PostgresDiagnosticsSnapshot earlier = Earlier() with { Storage = Storage(1, At, T("t-" + Sentinel, 1000)) };
        PostgresDiagnosticsSnapshot now = Now() with { Storage = Storage(1, At) };

        SnapshotComparison withheld = Compare(earlier, now with { Withheld = new Dictionary<string, string> { ["storage.largestTables"] = "1 table is in a schema you may not browse." } });
        SnapshotComparison hidden = Compare(earlier, now, hides: true);

        foreach (SnapshotComparison result in new[] { withheld, hidden })
        {
            result.Tables.Should().BeEmpty();
            result.NotCompared.Should().ContainSingle(static x => x.What == "Earlier largest tables no longer listed" && x.Count == 1);
            Dump(result).Should().NotContain(Sentinel);
        }
    }

    // -------------------------------------------------------------------------------------------------
    // WAL, checksums, settings, server
    // -------------------------------------------------------------------------------------------------

    private static PostgresWalSection Wal(long records, long images, long bytes, DateTimeOffset reset, DateTimeOffset at) => new()
    {
        Outcome = PostgresReadOutcome.Read,
        MeasuredAt = at,
        Records = records,
        FullPageImages = images,
        Bytes = bytes,
        StatsReset = reset,
    };

    [Fact]
    public void The_wal_written_between_is_shown_with_a_rate_when_the_counters_were_not_reset()
    {
        DateTimeOffset reset = At.AddDays(-30);

        SnapshotComparison result = Compare(
            Earlier() with { Wal = Wal(1000, 100, 2_000_000, reset, At.AddHours(-10)) },
            Now() with { Wal = Wal(3000, 150, 12_000_000, reset, At) });

        result.Wal.Should().HaveCount(3);
        result.Wal.Single(static x => x.Label == "WAL records written").Now.Should().Be("2,000");
        result.Wal.Single(static x => x.Label == "WAL records written").Note.Should().Be("200 an hour");
        result.Wal.Single(static x => x.Label == "WAL bytes written").Now.Should().Be("9.5 MiB");
    }

    [Fact]
    public void A_reset_or_a_fallen_wal_counter_says_it_is_not_known()
    {
        DateTimeOffset reset = At.AddDays(-30);

        SnapshotComparison differentReset = Compare(Earlier() with { Wal = Wal(1, 1, 1, reset, At.AddHours(-2)) }, Now() with { Wal = Wal(9, 9, 9, reset.AddDays(1), At) });
        SnapshotComparison fallen = Compare(Earlier() with { Wal = Wal(100, 1, 1, reset, At.AddHours(-2)) }, Now() with { Wal = Wal(50, 9, 9, reset, At) });

        foreach (SnapshotComparison result in new[] { differentReset, fallen })
        {
            result.Wal.Should().ContainSingle().Which.Note.Should().Be("the WAL counters were reset between the two, so what was written between them is not known");
        }
    }

    [Fact]
    public void Checksum_failures_that_rose_say_how_many_and_when_and_ones_that_fell_say_reset()
    {
        PostgresSettingsSection settings = Now().Settings!;

        SnapshotComparison rose = Compare(
            Earlier() with { Settings = settings with { ChecksumFailures = 1 } },
            Now() with { Settings = settings with { ChecksumFailures = 4, LastChecksumFailure = At.AddHours(-1) } });
        SnapshotComparison fell = Compare(
            Earlier() with { Settings = settings with { ChecksumFailures = 5 } },
            Now() with { Settings = settings with { ChecksumFailures = 0 } });

        rose.Server.Single(static x => x.Label == "Checksum failures").Note.Should().Be("3 new, the last at 2026-11-02 05:00:01 UTC");
        fell.Server.Single(static x => x.Label == "Checksum failures").Note.Should().Be("reset");
    }

    [Fact]
    public void Settings_and_extensions_are_compared_by_name_with_added_and_removed()
    {
        PostgresSettingsSection settings = Now().Settings!;

        PostgresDiagnosticsSnapshot earlier = Earlier() with
        {
            Settings = settings with
            {
                Settings =
                [
                    new PostgresSetting { Name = "shared_buffers", Value = "16384", Unit = "8kB", Source = "default" },
                    new PostgresSetting { Name = "removed_setting", Value = "1" },
                ],
                Extensions = [new PostgresExtension { Name = "pgstattuple", InstalledVersion = "1.4" }, new PostgresExtension { Name = "old_ext", InstalledVersion = "1" }],
            },
        };

        PostgresDiagnosticsSnapshot now = Now() with
        {
            Settings = settings with
            {
                Settings =
                [
                    new PostgresSetting { Name = "shared_buffers", Value = "32768", Unit = "8kB", Source = "configuration file" },
                    new PostgresSetting { Name = "added_setting", Value = "on", PendingRestart = true },
                ],
                Extensions = [new PostgresExtension { Name = "pgstattuple", InstalledVersion = "1.5" }, new PostgresExtension { Name = "new_ext", InstalledVersion = "2" }],
            },
        };

        SnapshotComparison result = Compare(earlier, now);

        result.Settings.Should().Contain(new SnapshotValueChange("shared_buffers", "16384 8kB", "32768 8kB"));
        result.Settings.Should().Contain(new SnapshotValueChange("added_setting", null, "on", "added"));
        result.Settings.Should().Contain(new SnapshotValueChange("removed_setting", "1", null, "no longer listed"));
        result.Extensions.Should().Contain(new SnapshotValueChange("pgstattuple", "1.4", "1.5"));
        result.Extensions.Should().Contain(new SnapshotValueChange("new_ext", null, "2", "added"));
        result.Extensions.Should().Contain(new SnapshotValueChange("old_ext", "1", null, "no longer listed"));
    }

    [Fact]
    public void A_server_that_started_again_says_so()
    {
        PostgresServerInfo server = Now().Server!;

        SnapshotComparison result = Compare(
            Earlier() with { Server = server with { StartedAt = At.AddDays(-10), Version = "14.13" } },
            Now() with { Server = server with { StartedAt = At.AddHours(-1), Version = "14.14" } });

        result.Server.Should().Contain(static x => x.Label == "Version" && x.Before == "14.13" && x.Now == "14.14");
        result.Server.Single(static x => x.Label == "Server started").Note.Should().Be("the server started again at 2026-11-02 05:00:01 UTC");
    }

    [Fact]
    public void Connection_limits_and_counts_are_compared()
    {
        PostgresConnectionsSection connections = Now().Connections!;

        SnapshotComparison result = Compare(
            Earlier() with { Connections = connections with { MaxConnections = 100, Database = connections.Database with { Total = 5 } } },
            Now() with { Connections = connections with { MaxConnections = 200, Database = connections.Database with { Total = 12 } } });

        result.Connections.Should().Contain(new SnapshotValueChange("max_connections", "100", "200"));
        result.Connections.Should().Contain(new SnapshotValueChange("Connections to this database", "5", "12"));
    }

    // -------------------------------------------------------------------------------------------------
    // The Connection page's settings (D33)
    // -------------------------------------------------------------------------------------------------

    private static ConnectionSnapshotSetting C(string keyword, string value) => new(keyword, "c", value, "connectionString", null, null, null);

    [Fact]
    public void Connection_keywords_are_compared_as_the_page_exposes_each_and_a_secret_or_option_value_never_appears()
    {
        ConnectionSnapshotSection basis = Now().Connection!;

        PostgresDiagnosticsSnapshot earlier = Earlier() with
        {
            Connection = basis with
            {
                Settings =
                [
                    C("Maximum Pool Size", "20"),
                    C("Password", "set"),
                    C("SSL Password", "not set"),
                    C("Options", "-c app.secret=" + Sentinel + "-opt"),
                    C("Some Future Keyword", Sentinel + "-future"),
                    C("Gone Future Keyword", Sentinel + "-gone"),
                    C("Host", "old.example"),
                ],
            },
        };

        PostgresDiagnosticsSnapshot now = Now() with
        {
            Connection = basis with
            {
                Settings =
                [
                    C("Maximum Pool Size", "50"),
                    C("Password", Sentinel + "-now-password-text"),
                    C("SSL Password", "set"),
                    C("Options", "-c app.secret=" + Sentinel + "-opt2"),
                    C("Some Future Keyword", Sentinel + "-future2"),
                    C("Host", "old.example"),
                ],
            },
        };

        SnapshotComparison result = Compare(earlier, now);

        result.Connection.Should().Contain(new SnapshotValueChange("Maximum Pool Size", "20", "50"));
        result.Connection.Should().Contain(static x => x.Label == "SSL Password" && x.Before == "not set" && x.Now == "set", "a secret is set or not set and nothing else");
        result.Connection.Should().NotContain(static x => x.Label == "Password", "an earlier 'set' and any text now are both 'set'");
        result.Connection.Single(static x => x.Label == "Options").Note.Should().Contain("not shown");
        result.Connection.Single(static x => x.Label == "Some Future Keyword").Note.Should().Contain("not shown");
        result.NotCompared.Should().Contain(static x => x.What == "Earlier connection keywords" && x.Count == 1);
        result.Connection.Should().NotContain(static x => x.Label == "Host");

        // The sentinel is in the file's Options, future keyword and gone keyword, and in the current view's own secret: only the
        // current view's own words may be drawn, and a secret's text never is.
        Dump(result).Should().NotContain(Sentinel);
    }

    [Fact]
    public void Connection_hints_are_new_or_gone_by_id_and_a_known_keyword_only()
    {
        ConnectionSnapshotSection basis = Now().Connection!;

        SnapshotComparison result = Compare(
            Earlier() with { Connection = basis with { Hints = [new ConnectionSnapshotHint("S11", "SSL Mode", "t", null, null), new ConnectionSnapshotHint(Sentinel + " bad id", Sentinel, "t", null, null)] } },
            Now() with { Connection = basis with { Hints = [new ConnectionSnapshotHint("C16", "Minimum Pool Size", "t", null, null)] } });

        result.Connection.Should().Contain(new SnapshotValueChange("Hint C16", null, null, "new (Minimum Pool Size)"));
        result.Connection.Should().Contain(new SnapshotValueChange("Hint S11", null, null, "gone (SSL Mode)"));
        result.Connection.Should().Contain(new SnapshotValueChange("Hint (unnamed)", null, null, "gone"));
        Dump(result).Should().NotContain(Sentinel);
    }

    [Fact]
    public void The_tls_state_is_drawn_only_when_it_is_one_this_studio_knows()
    {
        ConnectionSnapshotSection basis = Now().Connection!;

        SnapshotComparison result = Compare(
            Earlier() with { Connection = basis with { Tls = Sentinel } },
            Now() with { Connection = basis with { Tls = "verified" } });

        result.Connection.Should().ContainSingle(static x => x.Label == "TLS" && x.Before == "(not a state this studio knows)" && x.Now == "verified");
        Dump(result).Should().NotContain(Sentinel);
    }

    // -------------------------------------------------------------------------------------------------
    // Notices
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void Another_database_another_product_and_a_file_from_the_future_are_notices()
    {
        PostgresDiagnosticsSnapshot earlier = Earlier() with { Database = new PostgresDatabaseRef("other", "elsewhere"), Producer = "SomethingElse 9.9.9", GeneratedAt = At.AddDays(1) };

        SnapshotComparison result = Compare(earlier, Now());

        result.Notices.Should().Contain("The earlier file is of other / elsewhere; you are looking at tenants / acme.");
        result.Notices.Should().Contain(static x => x.StartsWith("The earlier file was written by SomethingElse; this is PostgresStudio.", StringComparison.Ordinal));
        result.Notices.Should().Contain(static x => x.Contains("dated after now", StringComparison.Ordinal));
        result.EarlierProducer.Should().Be("SomethingElse 9.9.9");
    }

    [Fact]
    public void An_earlier_snapshot_that_read_nothing_says_so_and_compares_nothing()
    {
        PostgresDiagnosticsSnapshot earlier = Earlier() with { Outcome = PostgresReadOutcome.Withheld, Reason = Sentinel };

        SnapshotComparison result = Compare(earlier, Now());

        result.Notices.Should().Contain("The earlier snapshot holds nothing about the database (withheld).");
        result.Findings.Should().BeEmpty();
        result.Verdict.Should().StartWith("Nothing to compare");
        Dump(result).Should().NotContain(Sentinel);
    }

    [Fact]
    public void File_text_in_the_comparison_is_capped_and_free_of_control_characters()
    {
        string longName = new string('n', 400) + "\u0007";
        PostgresDiagnosticsSnapshot earlier = Earlier() with { Database = new PostgresDatabaseRef(longName, "d"), Producer = new string('p', 1000) };

        SnapshotComparison result = SnapshotComparer.Compare(earlier, View(Now()), new string('f', 500), 0, false);

        result.FileName.Should().HaveLength(120);
        result.EarlierProducer.Should().HaveLength(SnapshotComparer.MaxFileText);
        result.Notices.Single(static x => x.StartsWith("The earlier file is of", StringComparison.Ordinal)).Should().NotContain("\u0007");
    }

    [Fact]
    public void Comparing_a_snapshot_with_itself_changes_nothing()
    {
        PostgresDiagnosticsSnapshot snapshot = SnapshotFileReaderTests.Everything();

        SnapshotComparison result = Compare(snapshot, snapshot, false, Row("T07"));

        result.Findings.Should().OnlyContain(static x => x.Kind == SnapshotChangeKind.Unchanged);
        result.Server.Should().BeEmpty();
        result.Settings.Should().BeEmpty();
        result.Storage.Should().BeEmpty();
        result.Statements.Should().BeEmpty();
        result.Tables.Should().BeEmpty();
        result.Wal.Should().BeEmpty();
        result.Connections.Should().BeEmpty();
        result.Connection.Should().BeEmpty();
        result.Verdict.Should().Contain("nothing you may see has changed");
    }
}
