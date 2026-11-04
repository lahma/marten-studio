using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// 1.2-POLISH: a moment in health evidence, a check's sentence or a report has one spelling, <c>yyyy-MM-dd HH:mm:ss UTC</c>, and
/// one helper writes it (<c>EvidenceTime</c>). Before 1.2 the evidence said <c>2026-11-04 08:57 UTC</c> in one place and
/// <c>2026-11-04 08:57:03Z</c> (the "u" format) in thirty others, so two lines of one finding disagreed about the time.
/// </summary>
public class EvidenceTimeSpellingTests
{
    /// <summary>The helper is the only file that writes the spelling, so the scan skips it.</summary>
    private const string Helper = "EvidenceTime.cs";

    [Fact]
    public void A_moment_is_spelt_to_the_second_in_UTC_whatever_offset_it_was_read_at()
    {
        var at = new DateTimeOffset(2026, 11, 4, 10, 57, 3, TimeSpan.FromHours(2));

        EvidenceTime.Utc(at).Should().Be("2026-11-04 08:57:03 UTC");
        EvidenceTime.Utc(at.AddMilliseconds(999)).Should().Be("2026-11-04 08:57:03 UTC", "a millisecond is not rounded up");
    }

    [Fact]
    public void A_moment_that_is_not_there_is_never_or_what_the_caller_says()
    {
        EvidenceTime.Utc((DateTimeOffset?)null).Should().Be("never");
        EvidenceTime.Utc((DateTimeOffset?)null, "not yet").Should().Be("not yet");
        EvidenceTime.Utc((DateTimeOffset?)new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero)).Should().Be("2026-01-02 03:04:05 UTC");
    }

    [Fact]
    public void The_pages_helpers_that_spell_a_moment_for_evidence_say_the_same()
    {
        var at = new DateTimeOffset(2026, 11, 4, 8, 57, 3, TimeSpan.Zero);

        PostgresStudio.Services.Health.Checks.TableText.When(at).Should().Be("2026-11-04 08:57:03 UTC");
        PostgresStudio.Services.Health.Checks.TableText.When(null).Should().Be("never");
        MartenStudio.Services.Health.Checks.EventHealth.Time(at).Should().Be("2026-11-04 08:57:03 UTC");
        PostgresStudio.Services.Health.StatisticsWindow.When(at).Should().Be("2026-11-04 08:57:03 UTC");
    }

    /// <summary>
    /// No check, fact or report writes a time of its own: not the <c>"u"</c> format (which ends in a <c>Z</c>), and not a hand-written
    /// <c>'UTC'</c> format of any precision. The two helpers that remain forward to <c>EvidenceTime</c>.
    /// </summary>
    [Fact]
    public void No_source_file_but_the_helper_writes_a_time_of_its_own_into_evidence()
    {
        string[] files =
        [
            .. StudioProjects.Files("*.cs", SearchOption.AllDirectories, "Services"),
        ];

        files.Should().NotBeEmpty();
        files.Select(static x => SourceScanner.Relative(x)).Should().Contain(static x => x.StartsWith("src/PostgresStudio.Diagnostics/", StringComparison.Ordinal))
            .And.Contain(static x => x.StartsWith("src/MartenStudio/", StringComparison.Ordinal))
            .And.Contain(static x => x.StartsWith("src/PostgresStudio/", StringComparison.Ordinal));

        List<string> offenders = [];

        foreach (string file in files.Where(static x => !string.Equals(Path.GetFileName(x), Helper, StringComparison.Ordinal)))
        {
            string[] lines = File.ReadAllLines(file);

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart();

                if (line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.Contains("ToString(\"u\"", StringComparison.Ordinal) || line.Contains("'UTC'\"", StringComparison.Ordinal))
                {
                    offenders.Add(SourceScanner.Relative(file) + ":" + (i + 1));
                }
            }
        }

        offenders.Should().BeEmpty("evidence spells a moment with EvidenceTime.Utc, so two lines of one finding never disagree");
    }
}
