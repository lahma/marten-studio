using System.Text;
using System.Text.RegularExpressions;

using PostgresStudio.Internal.Sql;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// No read locks the sequences it lists: nothing in <c>src/</c> reads the <c>pg_sequences</c> view, and
/// <c>pg_sequence_last_value</c> is called in exactly one statement - <see cref="DatabaseCatalogQueries.SequenceValueSql" />,
/// one sequence at a time and behind <c>has_sequence_privilege</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why</b> (B0 §3 and §9, measured 2026-10-20 on 13.23 to 18.6): the <c>pg_sequences</c> view calls
/// <c>pg_sequence_last_value</c> for every row, which takes a <c>RowExclusiveLock</c> on each sequence and holds it to
/// the end of the transaction - on 13-17 whenever the reader may read the sequence, and on 18, whose view no longer checks
/// the privilege itself, for every reader, even when only catalog columns are selected (the volatile column stops the view
/// being flattened). A tenant-partitioned store has a sequence per tenant, so one list read takes thousands of locks from
/// the shared lock table, and queues behind any <c>ALTER SEQUENCE</c>. Lists read <c>pg_catalog.pg_sequence</c>; the one
/// value read checks <c>has_sequence_privilege</c> first, which on 18 is also what keeps the lock away.
/// </para>
/// <para>
/// <b>Comments stripped, strings kept</b> - the opposite of <see cref="SourceScanner" />'s pass, because here the SQL
/// <em>is</em> the strings and the prose that explains the rule is the comments. The pass understands regular, verbatim,
/// interpolated and raw string literals, so a comment marker inside SQL text is SQL, and
/// <see cref="The_scanner_catches_a_planted_read_and_ignores_one_in_prose" /> proves it can still fail.
/// </para>
/// </remarks>
public class NoSequenceLockingReadsTests
{
    // SQL names fold to lower case, so PG_SEQUENCES reads the same view: matched ignoring case.
    private static readonly Regex SequencesView = new(@"\bpg_sequences\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    private static readonly Regex LastValue = new(@"\bpg_sequence_last_value\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

    [Fact]
    public void Nothing_in_src_reads_the_pg_sequences_view()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        string[] offenders =
        [
            .. files
                .Where(x => SequencesView.IsMatch(StripComments(File.ReadAllText(x))))
                .Select(SourceScanner.Relative)
                .Order(StringComparer.Ordinal),
        ];

        offenders.Should().BeEmpty(
            "the pg_sequences view locks every sequence it lists (for every reader on PostgreSQL 18); read pg_catalog.pg_sequence. Offenders: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void Pg_sequence_last_value_is_called_only_by_the_one_sequence_value_read_behind_its_privilege_check()
    {
        string[] files = SourceScanner.ShippedSourceFiles();

        Dictionary<string, int> calls = files
            .Select(x => (File: SourceScanner.Relative(x), Count: LastValue.Count(StripComments(File.ReadAllText(x)))))
            .Where(static x => x.Count > 0)
            .ToDictionary(static x => x.File, static x => x.Count, StringComparer.Ordinal);

        calls.Should().Equal(
            new Dictionary<string, int> { ["src/PostgresStudio/Internal/Sql/DatabaseCatalogQueries.cs"] = LastValue.Count(DatabaseCatalogQueries.SequenceValueSql) },
            "pg_sequence_last_value takes a RowExclusiveLock on its sequence, so it is read for one sequence, on demand, in SequenceValueSql alone");

        string sql = DatabaseCatalogQueries.SequenceValueSql;
        LastValue.Count(sql).Should().Be(1);
        Regex.IsMatch(
                sql,
                @"when\s+pg_catalog\.has_sequence_privilege\([^)]*\)\s+then\s+pg_catalog\.pg_sequence_last_value\(",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(5))
            .Should().BeTrue("the value is read only once the privilege is proved, which on 18 is what keeps the lock away");
        sql.Should().Contain("c.relname::text = @exact", "one sequence, by exact name");
    }

    /// <summary>The anti-vacuity half: the pass has to be able to fail, and to tell SQL from prose.</summary>
    [Theory]
    [InlineData("class Q { const string Sql = \"select sequencename from pg_catalog.pg_sequences\"; }", true)]
    [InlineData("class Q { const string Sql = @\"select * from pg_sequences s\"; }", true)]
    [InlineData("class Q { const string Sql = \"\"\"\n    select last_value\n    from pg_catalog.pg_sequences\n    \"\"\"; }", true)]
    [InlineData("class Q { string S(string s) => $\"select * from {s}.pg_sequences\"; }", true)]
    [InlineData("class Q { const string Sql = \"-- a URL: https://example.org\\nselect 1 from pg_sequences\"; }", true)]
    [InlineData("class Q { const string Sql = \"\"\"\n    select 'a // b' from pg_sequences\n    \"\"\"; }", true)]
    [InlineData("class Q { const string Sql = \"SELECT LAST_VALUE FROM PG_CATALOG.PG_SEQUENCES\"; }", true)]
    [InlineData("// never read pg_sequences\nclass Q { }", false)]
    [InlineData("/// <summary>The pg_sequences view locks.</summary>\nclass Q { }", false)]
    [InlineData("/* pg_sequences */\nclass Q { }", false)]
    [InlineData("class Q { const string Sql = \"select seqmax from pg_catalog.pg_sequence\"; } // pg_sequences", false)]
    [InlineData("@* pg_sequences, in Razor prose *@\n<p>ok</p>", false)]
    public void The_scanner_catches_a_planted_read_and_ignores_one_in_prose(string source, bool expected)
    {
        SequencesView.IsMatch(StripComments(source)).Should().Be(expected);
    }

    [Theory]
    [InlineData("class Q { const string Sql = \"select pg_catalog.pg_sequence_last_value(c.oid) from pg_class c\"; }", 1)]
    [InlineData("class Q { const string Sql = \"SELECT PG_SEQUENCE_LAST_VALUE(C.OID) FROM PG_CLASS C\"; }", 1)]
    [InlineData("// pg_sequence_last_value takes a lock\nclass Q { }", 0)]
    public void The_scanner_counts_a_planted_value_read_and_not_one_in_prose(string source, int expected)
    {
        LastValue.Count(StripComments(source)).Should().Be(expected);
    }

    /// <summary>
    /// Every comment - <c>//</c>, <c>///</c>, <c>/* */</c> and Razor's <c>@* *@</c> - replaced with spaces, and every
    /// string and character literal kept, so that what is left is the code and the SQL it carries.
    /// </summary>
    internal static string StripComments(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        StringBuilder result = new(source.Length);
        int i = 0;

        while (i < source.Length)
        {
            char c = source[i];
            char next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n')
                {
                    result.Append(' ');
                    i++;
                }
            }
            else if ((c == '/' && next == '*') || (c == '@' && next == '*'))
            {
                string close = c == '/' ? "*/" : "*@";
                int end = source.IndexOf(close, i + 2, StringComparison.Ordinal);
                int stop = end < 0 ? source.Length : end + 2;

                for (; i < stop; i++)
                {
                    result.Append(source[i] == '\n' ? '\n' : ' ');
                }
            }
            else if (c == '"' || ((c == '@' || c == '$') && Opens(source, i)))
            {
                i = KeepString(source, result, i);
            }
            else if (c == '\'' && i + 2 < source.Length && (source[i + 2] == '\'' || (source[i + 1] == '\\' && i + 3 < source.Length && source[i + 3] == '\'')))
            {
                int length = source[i + 1] == '\\' ? 4 : 3;
                result.Append(source, i, length);
                i += length;
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        return result.ToString();
    }

    /// <summary>Whether a <c>@</c> or <c>$</c> at <paramref name="i" /> starts a string literal (<c>@"</c>, <c>$"</c>, <c>$@"</c>, <c>$$"""</c>).</summary>
    private static bool Opens(string source, int i)
    {
        int j = i;

        while (j < source.Length && source[j] is '@' or '$')
        {
            j++;
        }

        return j > i && j < source.Length && source[j] == '"';
    }

    /// <summary>Copies one string literal, of any kind, from <paramref name="start" />; returns the index after it.</summary>
    private static int KeepString(string source, StringBuilder result, int start)
    {
        int i = start;
        bool verbatim = false;

        while (source[i] is '@' or '$')
        {
            verbatim |= source[i] == '@';
            result.Append(source[i]);
            i++;
        }

        int quotes = 0;
        while (i + quotes < source.Length && source[i + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3)
        {
            // A raw string literal: closed by a run of exactly as many quotes.
            string delimiter = new('"', quotes);
            int end = source.IndexOf(delimiter, i + quotes, StringComparison.Ordinal);
            int stop = end < 0 ? source.Length : end + quotes;
            result.Append(source, i, stop - i);
            return stop;
        }

        if (quotes == 2)
        {
            // An empty string.
            result.Append("\"\"");
            return i + 2;
        }

        result.Append('"');
        i++;

        while (i < source.Length)
        {
            char c = source[i];

            if (!verbatim && c == '\\' && i + 1 < source.Length)
            {
                result.Append(source, i, 2);
                i += 2;
                continue;
            }

            if (c == '"')
            {
                if (verbatim && i + 1 < source.Length && source[i + 1] == '"')
                {
                    result.Append("\"\"");
                    i += 2;
                    continue;
                }

                result.Append('"');
                return i + 1;
            }

            if (!verbatim && c == '\n')
            {
                // An unterminated regular string ends at the line; carry on as code.
                return i;
            }

            result.Append(c);
            i++;
        }

        return i;
    }
}
