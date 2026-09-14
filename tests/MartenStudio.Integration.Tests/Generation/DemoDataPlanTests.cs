using MartenStudio.SampleDomain.Generation;

namespace MartenStudio.Integration.Tests.Generation;

/// <summary>
/// The plan and the generator's pure parts: no Postgres, no Docker, no clock.
/// </summary>
/// <remarks>
/// They live beside the live tests rather than in the fast suite because they are about the sample
/// domain's generator, and keeping the whole feature's tests in one folder is what makes it obvious
/// which of them the large switch turns on.
/// </remarks>
public class DemoDataPlanTests
{
    /// <summary>The presets are the sizes the packet and the panel both promise.</summary>
    [Theory]
    [InlineData(DemoDataSize.Small, 50, 50)]
    [InlineData(DemoDataSize.Medium, 90_000, 90_000)]
    [InlineData(DemoDataSize.Large, 1_000_000, 1_000_000)]
    public void Every_preset_is_at_least_the_size_it_advertises(DemoDataSize size, long documents, long events)
    {
        DemoDataPlan plan = DemoDataPlan.For(size);

        plan.Size.Should().Be(size);
        plan.DocumentTarget.Should().BeGreaterThanOrEqualTo(documents);
        plan.EventTarget.Should().BeGreaterThanOrEqualTo(events);
        plan.Validate().Should().BeNull();
    }

    /// <summary>The large set is the one the responsiveness budgets are about, so its size is pinned.</summary>
    [Fact]
    public void The_large_preset_is_over_a_million_of_each()
    {
        DemoDataPlan plan = DemoDataPlan.For(DemoDataSize.Large);

        plan.DocumentTarget.Should().BeGreaterThan(1_000_000);
        plan.EventTarget.Should().BeGreaterThan(1_000_000);

        // One ~2 MB document per hundred thousand, and a poisoned stream per ten thousand.
        plan.MediaAssets.Should().Be(12);
        plan.PoisonedStreams.Should().Be(24);
    }

    /// <summary>A plan that would fill a disk is refused on the way in, not discovered on the way out.</summary>
    [Fact]
    public void A_plan_beyond_the_ceiling_is_refused()
    {
        DemoDataPlan plan = new() { Size = DemoDataSize.Custom, Customers = 2_000_000_000 };

        plan.Validate().Should().NotBeNull().And.Contain("refuses");
    }

    /// <summary>Bad numbers are named, one complaint at a time.</summary>
    [Theory]
    [InlineData(-1, 0, 0, 4, 6, "negative")]
    [InlineData(0, 10, 20, 4, 6, "soft-deleted")]
    [InlineData(0, 0, 0, 0, 6, "at least one")]
    [InlineData(0, 0, 0, 6, 4, "at least one")]
    public void A_plan_that_cannot_be_run_says_why(
        int customers, int orders, int softDeleted, int min, int max, string expected)
    {
        DemoDataPlan plan = new()
        {
            Size = DemoDataSize.Custom,
            Customers = customers,
            Orders = orders,
            SoftDeletedOrders = softDeleted,
            MinEventsPerStream = min,
            MaxEventsPerStream = max,
        };

        plan.Validate().Should().NotBeNull().And.Contain(expected);
    }

    /// <summary>An unknown size name is the smallest one, never the largest.</summary>
    [Theory]
    [InlineData(null, DemoDataSize.Small)]
    [InlineData("", DemoDataSize.Small)]
    [InlineData("nonsense", DemoDataSize.Small)]
    [InlineData("LARGE", DemoDataSize.Large)]
    [InlineData("custom", DemoDataSize.Custom)]
    public void An_unparseable_size_falls_back_to_the_smallest(string? value, DemoDataSize expected) =>
        DemoDataPlan.ParseSize(value).Should().Be(expected);

    /// <summary>
    /// Two runs get different ids, which is what lets a second run append rather than collide with the
    /// first on <c>customer.email</c>'s unique index.
    /// </summary>
    [Fact]
    public void Two_runs_get_different_ids_and_therefore_different_identities()
    {
        var first = DemoDataGenerator.NewRunId();
        var second = DemoDataGenerator.NewRunId();

        first.Should().NotBe(second);
        DemoDataGenerator.CustomerId(first, 7).Should().NotBe(DemoDataGenerator.CustomerId(second, 7));
        DemoDataGenerator.StreamId(first, 7).Should().NotBe(DemoDataGenerator.StreamId(second, 7));

        // ... and within one run the ids are stable and distinct per kind and index.
        DemoDataGenerator.CustomerId(first, 7).Should().Be(DemoDataGenerator.CustomerId(first, 7));
        DemoDataGenerator.CustomerId(first, 7).Should().NotBe(DemoDataGenerator.CustomerId(first, 8));
        DemoDataGenerator.CustomerId(first, 7).Should().NotBe(DemoDataGenerator.StreamId(first, 7));
    }

    /// <summary>
    /// Content is a pure function of the seed, so a screenshot of the demo stays true across runs.
    /// </summary>
    [Fact]
    public void Content_is_reproducible_from_the_seed_alone()
    {
        DemoDataWords.PersonName(123, 4).Should().Be(DemoDataWords.PersonName(123, 4));
        DemoDataWords.PersonName(123, 4).Should().NotBe(DemoDataWords.PersonName(124, 4));
        DemoDataWords.Timestamp(123, 4, 1).Should().Be(DemoDataWords.Timestamp(123, 4, 1));

        // The distribution has to actually spread: a "hash" that returned the same bucket for every
        // index would pass every test above and produce a million identical customers.
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < 500; i++)
        {
            names.Add(DemoDataWords.PersonName(20260914, i));
        }

        names.Count.Should().BeGreaterThan(200);
    }

    /// <summary>Identifiers are quoted and embedded quotes doubled, even though nothing untrusted reaches here.</summary>
    [Fact]
    public void Identifiers_are_quoted()
    {
        DemoDataSql.QuoteQualified("studio_sample", "mt_doc_customer")
            .Should().Be("\"studio_sample\".\"mt_doc_customer\"");

        DemoDataSql.Quote("weird\"name").Should().Be("\"weird\"\"name\"");
    }
}
