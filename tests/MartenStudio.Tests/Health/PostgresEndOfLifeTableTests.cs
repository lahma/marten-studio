using System.Text.RegularExpressions;

using MartenStudio.Services.Health.Checks;
using MartenStudio.Tests.Conventions;

namespace MartenStudio.Tests.Health;

/// <summary>
/// S01's dated table cannot go stale quietly: it fails a year before its last row's end of life, and whenever a
/// Postgres image the repository runs is a major it has no row for.
/// </summary>
/// <remarks>
/// These read the real clock on purpose. A table of dates is only right until it is not, and the point of the test is
/// that the day it stops being right is a red build, not a Health page that calls a supported server unknown.
/// </remarks>
public class PostgresEndOfLifeTableTests
{
    [Fact]
    public void The_table_is_extended_a_year_before_its_last_row_runs_out()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);
        PostgresMajorSupport last = PostgresCommunitySupport.Last;

        today.Should().BeBefore(last.FinalRelease.AddYears(-1),
            "PostgreSQL {0}'s community support ends on {1:yyyy-MM-dd}: add the majors released since, from {2}",
            last.Name, last.FinalRelease, PostgresCommunitySupport.Source);
    }

    [Fact]
    public void Every_Postgres_image_the_repository_runs_is_a_major_the_table_has()
    {
        Regex image = new(@"""postgres:(\d+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        HashSet<int> majors = [];

        foreach (string root in new[] { RepositoryRoot.Combine("tests"), RepositoryRoot.Combine("samples") })
        {
            foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (Match match in image.Matches(File.ReadAllText(file)))
                {
                    majors.Add(int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        majors.Should().NotBeEmpty("the live suite and the sample run a pinned postgres image, which is what this reads");
        majors.Should().OnlyContain(static x => PostgresCommunitySupport.Find(x) != null,
            "the latest-tested Postgres must be a major S01 knows the end of life of");
    }

    [Fact]
    public void The_table_is_oldest_first_unique_and_five_years_per_major()
    {
        IReadOnlyList<PostgresMajorSupport> majors = PostgresCommunitySupport.Majors;

        majors.Select(static x => x.Major).Should().OnlyHaveUniqueItems();
        majors.Select(static x => PostgresCommunitySupport.Order(x.Major)).Should().BeInAscendingOrder();
        majors.Select(static x => x.FinalRelease).Should().BeInAscendingOrder();

        foreach (PostgresMajorSupport major in majors)
        {
            (major.FinalRelease.Year - major.FirstRelease.Year).Should().Be(5, "PostgreSQL {0} is supported for five years", major.Name);
            major.FinalRelease.Month.Should().Be(11, "a final release is that year's November minor release");
        }
    }

    [Theory]
    [InlineData(13, "2025-11-13")]
    [InlineData(14, "2026-11-12")]
    [InlineData(15, "2027-11-11")]
    [InlineData(16, "2028-11-09")]
    [InlineData(17, "2029-11-08")]
    [InlineData(18, "2030-11-14")]
    public void The_dates_are_the_versioning_pages(int major, string finalRelease)
    {
        PostgresCommunitySupport.Find(major)!.FinalRelease.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(finalRelease);
    }
}
