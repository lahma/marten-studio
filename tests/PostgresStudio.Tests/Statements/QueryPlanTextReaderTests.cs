using System.Globalization;
using System.Text;

using MartenStudio.Tests.Conventions;

using PostgresStudio.Services.Query;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// The text-format plan reader (1.0-P3A, issue #28) offline: every text fixture the matrix parity test saved
/// (<c>PlanCorpus/text-*.txt</c>, PostgreSQL 14, 17 and 18) reads as its JSON twin does (<c>pair-text-*.json</c>), the way people
/// paste a plan (psql's framing, CRLF, a log's prefix) reads the same, the reader's bounds hold, and no input makes it throw.
/// </summary>
public class QueryPlanTextReaderTests
{
    private static string Corpus => RepositoryRoot.Combine("tests", "PostgresStudio.Tests", "Statements", "PlanCorpus");

    public static TheoryData<string> TextFixtures()
    {
        TheoryData<string> data = [];

        foreach (string file in Directory.GetFiles(Corpus, "text-*.txt").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(file));
        }

        return data;
    }

    public static TheoryData<string> PlainPairs()
    {
        TheoryData<string> data = [];

        foreach (string file in Directory.GetFiles(Corpus, "pair-text-*.json").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(file)["pair-".Length..]);
        }

        return data;
    }

    private static string Text(string name) => File.ReadAllText(Path.Combine(Corpus, name + ".txt"));

    private static QueryPlan Read(string text)
    {
        QueryPlanRead read = QueryPlanTextReader.Read(text);
        read.Failure.Should().BeNull();
        return read.Plan!;
    }

    [Fact]
    public void The_corpus_has_text_fixtures_for_three_majors_and_a_json_twin_for_every_plain_one()
    {
        TextFixtures().Count.Should().BeGreaterThan(60);
        PlainPairs().Count.Should().Be(Directory.GetFiles(Corpus, "text-*-plain-*.txt").Length);
    }

    [Theory]
    [MemberData(nameof(PlainPairs))]
    public void A_plain_text_plan_reads_as_its_JSON_twin(string name)
    {
        QueryPlan json = QueryPlanReader.Read(File.ReadAllText(Path.Combine(Corpus, "pair-" + name + ".json"))).Plan!;
        QueryPlan text = Read(Text(name));

        PlanParity.Compare(json, text, actuals: false, timed: false, name).Should().BeEmpty();
        text.FromText.Should().BeTrue();
        json.FromText.Should().BeFalse();
    }

    [Theory]
    [MemberData(nameof(TextFixtures))]
    public void Every_text_fixture_reads_whole_with_nothing_left_unread(string name)
    {
        QueryPlan plan = Read(Text(name));

        plan.Truncated.Should().BeFalse();
        plan.NodeCount.Should().BeGreaterThan(0);
        plan.UnreadLines.Should().BeEmpty();
        plan.Nodes.SelectMany(static x => x.UnreadLines).Should().BeEmpty();

        if (name.Contains("-analyze-", StringComparison.Ordinal))
        {
            plan.Analyzed.Should().BeTrue();
            plan.Timed.Should().BeTrue();
        }
        else if (name.Contains("-notiming-", StringComparison.Ordinal))
        {
            plan.Analyzed.Should().BeTrue();
            plan.Timed.Should().BeFalse();
        }
        else
        {
            plan.Analyzed.Should().BeFalse();
        }
    }

    [Fact]
    public void Subplans_and_initplans_hang_under_their_parent_with_their_names()
    {
        QueryPlan plan = Read(Text("text-subplans-analyze-pg17"));

        PlanNode[] nodes = [.. plan.Nodes];
        nodes.Should().Contain(static x => x.ParentRelationship == "InitPlan" && x.SubplanName == "InitPlan 2" && x.Depth == 1);
        nodes.Should().Contain(static x => x.ParentRelationship == "SubPlan" && x.SubplanName == "SubPlan 1" && x.Depth == 1);
        plan.PlanningMs.Should().BeApproximately(0.088, 0.0001);
        plan.ExecutionMs.Should().BeApproximately(0.156, 0.0001);
    }

    [Fact]
    public void Psql_framing_crlf_and_a_common_indent_read_as_the_bare_plan()
    {
        string bare = Text("text-hashjoin-analyze-pg17").TrimEnd('\n');
        string[] lines = bare.Split('\n');

        StringBuilder framed = new();
        framed.Append("                                    QUERY PLAN\r\n");
        framed.Append("-----------------------------------------------------------------------------------\r\n");

        foreach (string line in lines)
        {
            framed.Append(' ').Append(line).Append("\r\n");
        }

        framed.Append('(').Append(lines.Length.ToString(CultureInfo.InvariantCulture)).Append(" rows)\r\n");

        QueryPlan expected = Read(bare);
        QueryPlan actual = Read(framed.ToString());

        PlanParity.Compare(expected, actual, actuals: true, timed: true, "framed").Should().BeEmpty();
        actual.ExecutionMs.Should().Be(expected.ExecutionMs);
    }

    [Fact]
    public void A_log_line_before_the_plan_is_skipped()
    {
        string bare = Text("text-seqscan-plain-pg18");
        string logged = "2026-11-02 08:00:00.123 UTC [42] LOG:  duration: 12.345 ms  plan:\nQuery Text: select * from public.orders where note = 'note 5'\n" + bare;

        PlanParity.Compare(Read(bare), Read(logged), actuals: false, timed: false, "logged").Should().BeEmpty();
    }

    [Fact]
    public void Quoted_names_are_unquoted_as_JSON_gives_them()
    {
        QueryPlan plan = Read("Seq Scan on public.\"My \"\"Big\"\" Table\" \"t 1\"  (cost=0.00..1.01 rows=1 width=4)\n  Filter: (x > 1)");

        plan.Root.Schema.Should().Be("public");
        plan.Root.RelationName.Should().Be("My \"Big\" Table");
        plan.Root.Alias.Should().Be("t 1");
        plan.Root.Filter.Should().Be("(x > 1)");
    }

    [Fact]
    public void A_line_the_reader_does_not_know_is_kept_verbatim_and_masked_reads_keep_none()
    {
        const string Plan = "Seq Scan on public.orders  (cost=0.00..1.01 rows=1 width=4)\n  Filter: (note = 'secret')\n  this is not a key line\n";

        QueryPlan plan = Read(Plan);
        plan.Root.UnreadLines.Should().Equal("this is not a key line");

        QueryPlan masked = QueryPlanTextReader.Read(Plan, static x => x.Replace("secret", "‹masked›", StringComparison.Ordinal)).Plan!;
        masked.Root.Filter.Should().NotContain("secret");
        masked.Root.UnreadLines.Should().BeEmpty();
        masked.UnreadLines.Should().BeEmpty();
        masked.Root.Properties.Should().BeEmpty();
    }

    [Fact]
    public void Never_executed_nodes_read_as_not_executed()
    {
        QueryPlan plan = Read(
            "Append  (cost=0.00..2.02 rows=2 width=4) (actual time=0.010..0.011 rows=1 loops=1)\n"
            + "  ->  Seq Scan on public.a  (cost=0.00..1.01 rows=1 width=4) (actual time=0.009..0.009 rows=1 loops=1)\n"
            + "  ->  Seq Scan on public.b  (cost=0.00..1.01 rows=1 width=4) (never executed)\n"
            + "Planning Time: 0.050 ms\nExecution Time: 0.020 ms");

        PlanNode b = plan.Nodes.Single(static x => x.RelationName == "b");
        b.ActualLoops.Should().Be(0);
        b.ParentRelationship.Should().Be("Member");
    }

    [Fact]
    public void Text_with_no_node_is_refused_and_says_so()
    {
        QueryPlanTextReader.Read("hello\nworld").Failure.Should().Be(QueryPlanTextReader.NotAPlan);
        QueryPlanTextReader.Read("   ").Failure.Should().NotBeNull();
        QueryPlanTextReader.Read(null).Failure.Should().NotBeNull();
    }

    [Fact]
    public void Text_past_the_character_bound_is_refused_before_it_is_read()
    {
        string huge = "Result  (cost=0.00..0.01 rows=1 width=4)\n" + new string(' ', QueryPlanReader.MaxCharacters);

        QueryPlanTextReader.Read(huge).Failure.Should().Be(QueryPlanReader.TooLarge(huge.Length));
    }

    [Fact]
    public void A_plan_past_the_node_bound_is_drawn_as_far_as_it_was_read_and_says_so()
    {
        StringBuilder text = new("Append  (cost=0.00..1.00 rows=1 width=4)\n");

        for (int i = 0; i < QueryPlanReader.MaxNodes + 50; i++)
        {
            text.Append("  ->  Seq Scan on public.t").Append(i.ToString(CultureInfo.InvariantCulture)).Append("  (cost=0.00..1.00 rows=1 width=4)\n");
        }

        QueryPlan plan = Read(text.ToString());
        plan.Truncated.Should().BeTrue();
        plan.NodeCount.Should().BeLessThanOrEqualTo(QueryPlanReader.MaxNodes);
    }

    [Fact]
    public void A_plan_past_the_depth_bound_is_drawn_as_far_as_it_was_read_and_says_so()
    {
        StringBuilder text = new("Limit  (cost=0.00..1.00 rows=1 width=4)\n");

        for (int i = 0; i < QueryPlanReader.MaxDepth + 20; i++)
        {
            text.Append(' ', 2 + (6 * i)).Append("->  Limit  (cost=0.00..1.00 rows=1 width=4)\n");
        }

        QueryPlan plan = Read(text.ToString());
        plan.Truncated.Should().BeTrue();
        plan.Nodes.Max(static x => x.Depth).Should().BeLessThanOrEqualTo(QueryPlanReader.MaxDepth);
    }

    [Fact]
    public void Paste_detects_the_format_and_names_the_one_it_tried()
    {
        string json = File.ReadAllText(Path.Combine(Corpus, "pair-text-seqscan-plain-pg18.json"));

        QueryPlanReader.ReadAny(json).Plan!.FromText.Should().BeFalse();
        QueryPlanReader.ReadAny(Text("text-seqscan-plain-pg18")).Plan!.FromText.Should().BeTrue();
        QueryPlanReader.ReadAny("[ not json").Failure.Should().StartWith("Tried it as JSON");
        QueryPlanReader.ReadAny("not a plan").Failure.Should().StartWith("Tried it as the text format of EXPLAIN");
    }

    /// <summary>
    /// Truncations, dropped and swapped lines, flipped characters and noise over every analyzed fixture: the reader answers a plan or
    /// a refusal, within its bounds, and never throws. The seed is fixed so a failure reproduces.
    /// </summary>
    [Fact]
    public void No_mangling_of_a_real_plan_makes_the_reader_throw()
    {
        Random random = new(20261102);
        const string Noise = " ->()=.:'\"\t\r\n-0123456789abcxyzSeq ScanHash JoinBuffers: shared hit=";
        int reads = 0;

        foreach (string file in Directory.GetFiles(Corpus, "text-*-analyze-pg17.txt"))
        {
            string text = File.ReadAllText(file);
            List<string> mangled = [];

            for (int cut = 0; cut < text.Length; cut += 37)
            {
                mangled.Add(text[..cut]);
            }

            for (int i = 0; i < 40; i++)
            {
                List<string> lines = [.. text.Split('\n')];
                int a = random.Next(lines.Count);
                int b = random.Next(lines.Count);
                (lines[a], lines[b]) = (lines[b], lines[a]);
                lines.RemoveAt(random.Next(lines.Count));
                mangled.Add(string.Join('\n', lines));

                char[] chars = text.ToCharArray();

                for (int f = 0; f < 10; f++)
                {
                    chars[random.Next(chars.Length)] = Noise[random.Next(Noise.Length)];
                }

                mangled.Add(new string(chars));
            }

            foreach (string input in mangled)
            {
                Assert(input);
                reads++;
            }
        }

        for (int i = 0; i < 300; i++)
        {
            StringBuilder noise = new();
            int length = random.Next(400);

            for (int c = 0; c < length; c++)
            {
                noise.Append(Noise[random.Next(Noise.Length)]);
            }

            Assert(noise.ToString());
            Assert("Seq Scan on x  (cost=" + noise);
            reads += 2;
        }

        reads.Should().BeGreaterThan(1000);

        static void Assert(string input)
        {
            // The unguarded entry: the guarded one would turn a parser defect into a refusal this test accepts.
            QueryPlanRead read = QueryPlanTextReader.ReadUnguarded(input);

            (read.Plan is null).Should().Be(read.Failure is not null, "a read is a plan or a refusal: {0}", input);

            if (read.Plan is { } plan)
            {
                plan.NodeCount.Should().BeLessThanOrEqualTo(QueryPlanReader.MaxNodes);
                plan.Nodes.Should().OnlyContain(static x => x.Depth <= QueryPlanReader.MaxDepth);
            }
        }
    }
}
