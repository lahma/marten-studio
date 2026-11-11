using KStudio.Core.Catalog;

using PostgresStudio.Tests.Support;

namespace KStudio.Tests.Catalog;

/// <summary>Throughput from the overview reads that happen anyway (decision 5.2): the change over the newest two samples at least ten seconds apart within ten minutes.</summary>
public class ThroughputSamplesTests
{
    [Fact]
    public void There_is_no_rate_until_two_samples_ten_seconds_apart_exist()
    {
        ManualTimeProvider clock = new();
        KafkaThroughputSamples samples = new(clock);

        samples.Rate("c", "t").Should().BeNull();
        samples.Record("c", "t", 100);
        samples.Rate("c", "t").Should().BeNull("one sample is no rate");

        clock.Advance(TimeSpan.FromSeconds(5));
        samples.Record("c", "t", 150);
        samples.Rate("c", "t").Should().BeNull("the two samples are only five seconds apart");
    }

    [Fact]
    public void The_rate_is_offsets_per_second_between_the_newest_sample_and_the_newest_one_ten_seconds_before_it()
    {
        ManualTimeProvider clock = new();
        KafkaThroughputSamples samples = new(clock);

        samples.Record("c", "t", 100);
        clock.Advance(TimeSpan.FromSeconds(20));
        samples.Record("c", "t", 300);

        samples.Rate("c", "t").Should().BeApproximately(10.0, 0.0001);
    }

    [Fact]
    public void Samples_older_than_ten_minutes_are_forgotten()
    {
        ManualTimeProvider clock = new();
        KafkaThroughputSamples samples = new(clock);

        samples.Record("c", "t", 100);
        clock.Advance(TimeSpan.FromMinutes(11));
        samples.Record("c", "t", 200);

        samples.Rate("c", "t").Should().BeNull();
    }

    [Fact]
    public void Topics_and_clusters_are_separate()
    {
        ManualTimeProvider clock = new();
        KafkaThroughputSamples samples = new(clock);

        samples.Record("c", "a", 0);
        samples.Record("c", "b", 0);
        clock.Advance(TimeSpan.FromSeconds(10));
        samples.Record("c", "a", 100);
        samples.Record("c", "b", 10);

        samples.Rate("c", "a").Should().BeApproximately(10.0, 0.0001);
        samples.Rate("c", "b").Should().BeApproximately(1.0, 0.0001);
        samples.Rate("other", "a").Should().BeNull();
    }
}
