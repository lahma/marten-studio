using System.Text.RegularExpressions;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Replication;
using PostgresStudio.Services.Server;

namespace PostgresStudio.Tests.Conventions;

/// <summary>
/// Wave 6: every shared health check speaks Postgres Studio's words. A plain Postgres host has no store, no documents, no
/// events, no daemon and no Marten configuration, so nothing a shared check renders there - a finding's title, why, evidence,
/// caveat, remedy and link, a quiet note, a status row's reason, the check's own title - may say so. The theory evaluates
/// every shared check (<see cref="CheckWordCases" />) in Postgres Studio's words and scans what it renders; the coverage
/// fact holds the cases to every shared check, each with a finding and a quiet verdict, so a check added later is evaluated
/// too or fails here; the anti-vacuity fact proves a planted "store" fails the scan.
/// </summary>
public partial class PostgresStudioWordsTests
{
    /// <summary>What a plain Postgres host must never read, each with why.</summary>
    private static readonly (Regex Pattern, string Why)[] StoreWordsInChecks =
    [
        (Word(@"stores?|store's"), "a store is Marten's: a plain data source is a data source"),
        (new Regex("Marten", RegexOptions.CultureInvariant), "Postgres Studio names Marten only where an mt_ table is the subject"),
        (new Regex("StoreOptions", RegexOptions.CultureInvariant), "a store's configuration, which a data source does not have"),
        (Word("documents?|document type"), "documents are a Marten store's rows"),
        (new Regex("mt_", RegexOptions.CultureInvariant), "Marten's table prefix, never said of a table that does not carry it"),
        (Word("daemon|projections?"), "the async daemon and its projections are Marten's"),
        (new Regex("Weasel", RegexOptions.CultureInvariant), "Marten's schema library"),
        (new Regex(@"\bopts\.|IDocumentSession|LightweightSession|SaveChanges|ToListAsync|MartenStudioOptions|AddMartenStudio|StoreAuthorizationPolicy",
            RegexOptions.CultureInvariant), "Marten Studio's configuration, or Marten's API"),
        (new Regex(@"\b[DETCRSK][0-9]{2}\b", RegexOptions.CultureInvariant), "a check code - flagged only when Postgres Studio has no such check"),
    ];

    /// <summary>The legitimate uses, each with its reason: the spans removed before the scan.</summary>
    private static readonly (Regex Pattern, string Why)[] AllowedInChecks =
    [
        (Word("a leaf entry stores them"), "T25: the verb - a b-tree leaf stores an index's INCLUDE columns"),
        (Word("take a value and store nothing"), "T26: the verb - a rolled-back insert stores nothing"),
    ];

    /// <summary>
    /// The uses that are legitimate in a finding about a table named like Marten's (<see cref="CheckWordCase.AboutMartensTables" />),
    /// which Postgres Studio's table source classifies by its name: what the table is, said as Marten Studio says it.
    /// </summary>
    private static readonly (Regex Pattern, string Why)[] AllowedAboutMartensTables =
    [
        (new Regex(@"\bmt_\w+", RegexOptions.CultureInvariant), "the table's and its indexes' catalog names"),
        (Word("the document table"), "what Postgres Studio's table source calls an mt_doc_ table (StudioTableText.Describe)"),
        (Word("the event store's table"), "what Postgres Studio's table source calls an mt_events table (StudioTableText.Describe)"),
        (Word("managed by a Marten store; change it in that store's StoreOptions"), "T03: the origin of an index on a Marten table"),
        (Word("It is on a table a Marten store manages: that store's next apply decides it, so change it in that store's StoreOptions"),
            "T03: a Marten table's index is its store's to rebuild"),
        (Word("Every Marten upsert rewrites data"), "T06: what Marten writes to a document table, which no other table is judged on"),
        (Word("so no upsert to it can be a HOT update"), "T06: the same"),
        (Word("No StoreOptions line the studio can suggest"), "T07, T08: a Marten event or bookkeeping table's settings"),
        (Word("no Marten apply resets it"), "T07, T08: the same"),
        (new Regex(@"Marten Studio's Schema screen, over the store whose StoreOptions declare this table", RegexOptions.CultureInvariant),
            "T07, T08: Postgres Studio's MartenTablePartitionParameters, which points at Marten Studio on purpose"),
    ];

    /// <summary>Every shared check's code, for the code rule: a code Postgres Studio has is its own to name.</summary>
    private static readonly HashSet<string> SharedCodes =
        [.. HealthCheckRegistry.Discover(typeof(PostgresStudioText).Assembly).Select(static x => HealthCheckRegistry.Describe(x).Code)];

    private static Regex Word(string words) =>
        new(@"\b(" + words + @")\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static TheoryData<string> CheckCases() => [.. CheckWordCases.All.Select(static x => x.ToString())];

    [Theory]
    [MemberData(nameof(CheckCases))]
    public void Every_shared_check_says_Postgres_Studios_words(string name)
    {
        CheckWordCase checkCase = CheckWordCases.All.Single(x => x.ToString() == name);
        HealthCheckInfo info = HealthCheckRegistry.Describe(checkCase.Check, text: PostgresStudioText.Instance);
        HealthVerdict verdict = ((IHealthCheck) Activator.CreateInstance(checkCase.Check)!).Evaluate(checkCase.Facts, checkCase.Evaluation);

        foreach (HealthFinding finding in verdict.Findings)
        {
            finding.Code.Should().Be(info.Code);
            HealthCheckRegistry.FindingProblems(info, finding).Should().BeEmpty();
        }

        Leaks(Rendered(info, verdict), checkCase.AboutMartensTables).Should().BeEmpty(
            "{0} is Postgres Studio's, and a plain Postgres host has no store; allow a use only with a reason (AllowedInChecks)", name);
    }

    /// <summary>
    /// The cases hold every shared check - one W5 added included - to a finding and to a quiet verdict, so that a check whose
    /// text the theory never sees is a failure here rather than a gap.
    /// </summary>
    [Fact]
    public void Every_shared_check_is_evaluated_to_a_finding_and_to_quiet()
    {
        IReadOnlyList<Type> shared = HealthCheckRegistry.Discover(typeof(PostgresStudioText).Assembly);
        shared.Should().HaveCountGreaterThanOrEqualTo(39, "every shared check of 0.4 and of wave 5 is in the PostgresStudio assembly");

        List<string> missing = [];

        foreach (Type check in shared)
        {
            IHealthCheck instance = (IHealthCheck) Activator.CreateInstance(check)!;
            HealthVerdict[] verdicts = [.. CheckWordCases.All.Where(x => x.Check == check).Select(x => instance.Evaluate(x.Facts, x.Evaluation))];
            string code = HealthCheckRegistry.Describe(check).Code;

            if (!verdicts.Any(static x => x.Findings.Count > 0))
            {
                missing.Add(code + " has no case that makes a finding");
            }

            if (!verdicts.Any(static x => x.Findings.Count == 0))
            {
                missing.Add(code + " has no case that is quiet");
            }
        }

        missing.Should().BeEmpty("every shared check's words are evaluated, findings and quiet notes both");
    }

    /// <summary>
    /// What a status row says when a check is not checked or not applicable - the reasons its gatherer throws - comes from
    /// these, in every platform's and every grant's wording.
    /// </summary>
    [Fact]
    public void Every_status_row_reason_says_Postgres_Studios_words()
    {
        PostgresStudio.Services.Settings.StudioText text = PostgresStudioText.Instance;
        List<(string Where, string Text)> reasons =
        [
            ("T02 not installed", StatementReads.NotInstalled),
            ("T02 not preloaded", StatementReads.NotPreloaded),
            ("T02 too old", StatementReads.TooOld(new StatementExtension(130000, "public", "1.7"))),
            ("T02 nothing recorded", text.NothingRecorded),
            ("T02 nothing recorded, the outcome", new StatementReadOutcome(StatementReadState.NothingRecorded, new StatementExtension(170011, "public", "1.11"), null).ReasonIn(text)!),
            ("C01 ambiguous pool", text.AmbiguousPoolNotChecked),
            ("tables, no source", HealthTables.NoSource),
            ("S06, declarations unread", PublicSchemaCreateCheck.DeclarationsUnread),
            ("S03, Aurora", ChecksumFailuresCheck.AuroraReason),
            ("R02-R04, Aurora", ReplicationPlatform.Aurora),
            ("the database gate", text.DatabaseGateReason),
            ("the export's description", new PostgresStudio.Services.Health.Export.HealthExportSummary { MeasuredAt = CheckWordCases.At, StoresNotReached = 2, Databases = 1 }.Describe(TimeSpan.FromSeconds(60), text)),
            ("a page with nothing selected", "Pick " + text.PickInTheHeader + " in the header to see its health."),
        ];

        reasons.Should().Contain(static x => x.Text.Contains("2 data sources could not be reached", StringComparison.Ordinal));

        foreach (PostgresPlatformKind kind in Enum.GetValues<PostgresPlatformKind>())
        {
            PostgresPlatform platform = new(kind, kind == PostgresPlatformKind.SelfHosted ? null : "a marker");
            string on = " on " + kind;

            foreach (PostgresGrant grant in PostgresGrant.Roles.Append(PostgresGrant.ColumnSelect("public.orders", "id")))
            {
                reasons.Add(("not checked, " + grant.Privilege + on, PostgresGrants.NotChecked(grant, platform, "studio_reader")));
                reasons.Add(("grantor, " + grant.Privilege + on, PostgresGrants.Grantor(grant, platform)));
            }

            reasons.Add(("refused" + on, PostgresGrants.Refused(platform, "studio_reader")));
            reasons.Add(("extension missing" + on, PostgresGrants.ExtensionNotInstalled("pg_stat_statements", false, platform)));
            reasons.Add(("trusted extension missing" + on, PostgresGrants.ExtensionNotInstalled("pgstattuple", true, platform)));
            reasons.Add(("not preloaded" + on, PostgresGrants.Preloaded("pg_stat_statements", platform)));
            reasons.Add(("R03 not checked" + on, StandbyLagCheck.NotChecked(new PostgresServerFacts(CheckWordCases.At, 170011, "17.11", platform), new PostgresGrants("app", false, []), 2)));

            foreach (ReplicationSection section in Enum.GetValues<ReplicationSection>())
            {
                if (ReplicationPlatform.NotApplicable(platform, section) is { } why)
                {
                    reasons.Add((section + " not applicable" + on, why));
                }
            }
        }

        Leaks(reasons, aboutMartensTables: false).Should().BeEmpty("a status row's reason is Postgres Studio's too");
    }

    /// <summary>
    /// S02's and S10's titles name a store in Marten Studio's words; Postgres Studio titles them its own way - in the registry
    /// its container builds, so the status rows and every finding's check title say so - while the docs link still finds the
    /// one heading the docs carry.
    /// </summary>
    [Theory]
    [InlineData(typeof(DangerousSettingsCheck), "Settings that blind or endanger the store", "Settings that blind or endanger the database")]
    [InlineData(typeof(ReadOnlyStoreCheck), "Store connections cannot write", "Connections cannot write")]
    public void A_check_titled_with_a_store_has_Postgres_Studios_own_title_and_the_same_docs_link(Type check, string marten, string postgres)
    {
        HealthCheckInfo attribute = HealthCheckRegistry.Describe(check);
        HealthCheckInfo words = HealthCheckRegistry.Describe(check, text: PostgresStudioText.Instance);

        attribute.Title.Should().Be(marten);
        words.Title.Should().Be(postgres);
        words.DocsTitle.Should().Be(marten, "the docs heading is the attribute's in every studio");
        words.DocsHeading.Should().Be(attribute.DocsHeading);
        words.DocsUrl.Should().Be(attribute.DocsUrl);

        HealthCheckRegistry registry = new(
            PostgresStudioDescriptor.Instance,
            runtime: null,
            PostgresStudioSettings.Of(new PostgresStudioOptions()));
        registry.All.Single(x => x.Info.CheckType == check).Info.Title.Should().Be(postgres, "the container hands the registry the studio's settings");
        registry.All.Should().OnlyContain(static x => !Word(@"stores?|store's").IsMatch(x.Info.Title), "no check of Postgres Studio's is titled with a store");
    }

    /// <summary>The scan's anti-vacuity half: a planted "store" fails it, and its allowed uses and plain words pass.</summary>
    [Fact]
    public void The_check_scan_catches_a_planted_store_and_passes_what_it_allows()
    {
        HealthVerdict planted = HealthVerdict.Of(new HealthFinding
        {
            Code = "T01",
            Severity = HealthSeverity.Warning,
            Title = "Sequential scans read the whole of public.orders",
            WhyItMatters = "This is where the store's own role spends its database time.",
            Remedy = new HealthRemedy("opts.Schema.For<Order>().Index(x => x.Status);"),
        });

        HealthCheckInfo info = HealthCheckRegistry.Describe(typeof(SequentialScansCheck), text: PostgresStudioText.Instance);
        IReadOnlyList<string> leaks = Leaks(Rendered(info, planted), aboutMartensTables: false);

        leaks.Should().Contain(static x => x.Contains("store's", StringComparison.Ordinal), "a planted store is the class this test exists to catch");
        leaks.Should().Contain(static x => x.Contains("opts.", StringComparison.Ordinal), "and so is a StoreOptions remedy");

        Leaks([("plain", "Restore it from a backup; the documented default; a leaf entry stores them; see S03 and T12")], aboutMartensTables: false)
            .Should().BeEmpty("words that merely contain a store noun, an allowed verb and shared codes are not leaks");
        Leaks([("code", "the async daemon is behind (D01, K08)")], aboutMartensTables: false)
            .Should().HaveCount(3, "the daemon, and two codes Postgres Studio has no check for");
        Leaks([("Marten table", "the document table public.mt_doc_order")], aboutMartensTables: true)
            .Should().BeEmpty("said of a table named like Marten's, its name and kind are what it is");
        Leaks([("Marten table", "the document table public.mt_doc_order")], aboutMartensTables: false)
            .Should().NotBeEmpty("but never of a plain table");

        foreach ((Regex pattern, string why) in AllowedInChecks.Concat(AllowedAboutMartensTables))
        {
            why.Should().NotBeNullOrWhiteSpace(pattern + " needs its reason");
        }
    }

    /// <summary>
    /// The theory's other anti-vacuity half: the very same cases, evaluated in Marten Studio's words, do say "store", "Marten"
    /// and the rest across many checks - so the cases reach the sentences each studio words its own way, and a pass above is
    /// Postgres Studio's words rather than facts that never get that far.
    /// </summary>
    [Fact]
    public void The_same_cases_in_Marten_Studios_words_do_speak_of_a_store()
    {
        HashSet<string> codes = new(StringComparer.Ordinal);

        foreach (CheckWordCase checkCase in CheckWordCases.All.Where(static x => !x.AboutMartensTables))
        {
            HealthCheckInfo info = HealthCheckRegistry.Describe(checkCase.Check, text: PostgresStudio.Services.Settings.StudioText.Marten);
            HealthVerdict verdict = ((IHealthCheck) Activator.CreateInstance(checkCase.Check)!)
                .Evaluate(checkCase.Facts, checkCase.Evaluation with { Text = PostgresStudio.Services.Settings.StudioText.Marten });

            if (Leaks(Rendered(info, verdict), aboutMartensTables: false).Count > 0)
            {
                codes.Add(checkCase.Code);
            }
        }

        codes.Should().Contain(["C01", "C03", "C04", "C05", "C07", "C08", "K07", "R01", "S02", "S03", "S05", "T02", "T03", "T07", "T14", "T15", "T23"],
            "each of these words a sentence its own way in each studio, and the cases make it appear");
    }

    /// <summary>Everything a check renders in a studio: its own title, its status note, and every part of every finding.</summary>
    private static IEnumerable<(string Where, string Text)> Rendered(HealthCheckInfo info, HealthVerdict verdict)
    {
        yield return ("check title", info.Title);

        if (verdict.Note is { } note)
        {
            yield return ("status note", note);
        }

        foreach (HealthFinding finding in verdict.Findings)
        {
            string at = finding.Key;

            yield return (at + " title", finding.Title);
            yield return (at + " why", finding.WhyItMatters);

            if (finding.Caveat is { } caveat)
            {
                yield return (at + " caveat", caveat);
            }

            foreach (HealthEvidence evidence in finding.Evidence)
            {
                yield return (at + " evidence label", evidence.Label);
                yield return (at + " evidence '" + evidence.Label + "'", evidence.Value);
            }

            if (finding.Remedy is { } remedy)
            {
                yield return (at + " remedy heading", remedy.Heading);
                yield return (at + " remedy", remedy.Code);

                if (remedy.Explanation is { } explanation)
                {
                    yield return (at + " remedy explanation", explanation);
                }
            }

            if (finding.Link is { } link)
            {
                yield return (at + " link", link.Label);
            }
        }
    }

    /// <summary>Every store word left in <paramref name="texts" /> once the allowed uses are taken out, with where and why.</summary>
    private static List<string> Leaks(IEnumerable<(string Where, string Text)> texts, bool aboutMartensTables)
    {
        List<string> leaks = [];

        foreach ((string where, string text) in texts)
        {
            string scanned = text;

            foreach ((Regex allowed, _) in aboutMartensTables ? AllowedInChecks.Concat(AllowedAboutMartensTables) : AllowedInChecks)
            {
                scanned = allowed.Replace(scanned, " ");
            }

            foreach ((Regex pattern, string why) in StoreWordsInChecks)
            {
                foreach (Match match in pattern.Matches(scanned))
                {
                    if (why.StartsWith("a check code", StringComparison.Ordinal) && SharedCodes.Contains(match.Value))
                    {
                        continue;
                    }

                    int from = Math.Max(0, match.Index - 40);
                    leaks.Add(where + ": '" + match.Value + "' (" + why + ") in \"…" + scanned[from..Math.Min(scanned.Length, match.Index + match.Length + 40)] + "…\"");
                }
            }
        }

        return leaks;
    }
}
