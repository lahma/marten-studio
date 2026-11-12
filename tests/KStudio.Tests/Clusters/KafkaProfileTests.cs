using System.Reflection;

using Confluent.Kafka;

using KStudio.Core.Clusters;

namespace KStudio.Tests.Clusters;

/// <summary>
/// Profiles (AGENTS.md D47, D-M5 decision 4): the builder's word, the table of ceilings and its three properties - a profile only lowers, never
/// raises, and every limit of the public class has a row, so a limit added later fails by name until it is decided.
/// </summary>
public class KafkaProfileTests
{
    private const long KiB = 1024;
    private const long MiB = 1024 * KiB;
    private const long GiB = 1024 * MiB;

    private static readonly ClientConfig Client = new() { BootstrapServers = "fake:9092" };

    /// <summary>Every property of <see cref="KafkaScanLimits" /> at its lowest bound.</summary>
    private static KafkaScanLimits Lowest() => new()
    {
        MaxConcurrentScans = 1,
        MaxConcurrentScansPerOwner = 1,
        BytesPerSecond = 64 * KiB,
        OwnerBytesPerHour = MiB,
        MaxBytesPerScan = MiB,
        MaxRecordsPerScan = 1_000,
        MaxScanDuration = TimeSpan.FromSeconds(5),
        MaxMatches = 1,
        MaxMatchBytes = (int) KiB,
        MaxDecodeBytes = (int) (64 * KiB),
        ConfirmAboveBytes = 0,
        ConfirmAboveRecords = 0,
        QueueKilobytes = 1_024,
        FetchBytes = (int) MiB,
        CreateTimeMargin = TimeSpan.Zero,
        PartitionStallTimeout = TimeSpan.FromSeconds(1),
        MaxExportBytes = MiB,
        MaxTailMatches = 1,
        TailPauseAfter = TimeSpan.FromSeconds(10),
    };

    /// <summary>Every property of <see cref="KafkaScanLimits" /> at its highest bound.</summary>
    private static KafkaScanLimits Highest() => new()
    {
        MaxConcurrentScans = 8,
        MaxConcurrentScansPerOwner = 8,
        BytesPerSecond = GiB,
        OwnerBytesPerHour = 1024 * GiB,
        MaxBytesPerScan = 64 * GiB,
        MaxRecordsPerScan = 1_000_000_000,
        MaxScanDuration = TimeSpan.FromMinutes(30),
        MaxMatches = 10_000,
        MaxMatchBytes = (int) MiB,
        MaxDecodeBytes = (int) (16 * MiB),
        ConfirmAboveBytes = 64 * GiB,
        ConfirmAboveRecords = 1_000_000_000,
        QueueKilobytes = 65_536,
        FetchBytes = (int) (50 * MiB),
        CreateTimeMargin = TimeSpan.FromHours(1),
        PartitionStallTimeout = TimeSpan.FromMinutes(5),
        MaxExportBytes = 256 * MiB,
        MaxTailMatches = 10_000,
        TailPauseAfter = TimeSpan.FromMinutes(30),
    };

    public static TheoryData<string> HostSettings => ["defaults", "lowest", "highest"];

    public static TheoryData<KafkaProfile> Profiles => [KafkaProfile.Development, KafkaProfile.Test, KafkaProfile.Production];

    private static KafkaScanLimits Host(string name) => name switch
    {
        "lowest" => Lowest(),
        "highest" => Highest(),
        _ => new KafkaScanLimits(),
    };

    private static PropertyInfo[] LimitProperties() =>
        [.. typeof(KafkaScanLimits).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(static x => x.CanWrite && x.GetSetMethod() is { IsPublic: true })];

    [Fact]
    public void A_profile_is_set_by_the_builder_and_never_inferred()
    {
        KafkaEngineOptions options = new();
        options.AddCluster("prod", Client, static c => c.Profile(KafkaProfile.Production));
        options.AddCluster("dev", Client, static c => c.Profile(KafkaProfile.Development));
        options.AddCluster("shouting", Client, static c => c.Environment("production"));
        options.AddCluster("plain", Client);

        KafkaClusterRegistry registry = new(options);

        registry.Get("prod").Profile.Should().Be(KafkaProfile.Production);
        registry.Get("dev").Profile.Should().Be(KafkaProfile.Development);
        registry.Get("shouting").Profile.Should().BeNull("the environment label is text for a chip, never a profile (D47)");
        registry.Get("plain").Profile.Should().BeNull("no call, no profile: the host's limits as they are");
    }

    [Fact]
    public void A_second_call_replaces_the_first_and_an_undefined_value_is_refused()
    {
        KafkaEngineOptions options = new();
        options.AddCluster("c", Client, static c => c.Profile(KafkaProfile.Production).Profile(KafkaProfile.Test));

        new KafkaClusterRegistry(options).Get("c").Profile.Should().Be(KafkaProfile.Test);

        KafkaEngineOptions bad = new();
        Action act = () => bad.AddCluster("c", Client, static c => c.Profile((KafkaProfile) 7));

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("profile");
    }

    [Fact]
    public void Every_limit_of_the_public_class_has_a_row_in_the_table()
    {
        string[] properties = [.. LimitProperties().Select(static x => x.Name)];

        properties.Except(KafkaProfileCeilings.DecidedProperties).Should().BeEmpty("a new limit needs a row in KafkaProfileCeilings, even if the row says the host's");
        KafkaProfileCeilings.DecidedProperties.Except(properties).Should().BeEmpty("a row names a limit that no longer exists");
    }

    [Theory]
    [MemberData(nameof(Profiles))]
    public void A_profile_only_lowers_whatever_the_host_set_and_the_result_is_valid(KafkaProfile profile)
    {
        foreach (string hostName in new[] { "defaults", "lowest", "highest" })
        {
            KafkaScanLimits host = Host(hostName);
            host.Validate().Should().BeEmpty("the host's own limits are inside their bounds: " + hostName);

            KafkaScanLimits capped = KafkaProfileCeilings.Cap(profile, host);

            capped.Validate().Should().BeEmpty("a capped set passes Validate: " + profile + " over " + hostName);

            foreach (PropertyInfo property in LimitProperties())
            {
                IComparable cappedValue = (IComparable) property.GetValue(capped)!;
                object hostValue = property.GetValue(host)!;

                cappedValue.CompareTo(hostValue).Should().BeLessThanOrEqualTo(0, $"{profile} over {hostName} must not raise {property.Name}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(HostSettings))]
    public void A_cluster_with_no_profile_and_a_development_cluster_get_the_hosts_limits_exactly(string hostName)
    {
        KafkaScanLimits host = Host(hostName);

        foreach (KafkaProfile? profile in new KafkaProfile?[] { null, KafkaProfile.Development })
        {
            KafkaScanLimits capped = KafkaProfileCeilings.Cap(profile, host);

            capped.Should().NotBeSameAs(host);

            foreach (PropertyInfo property in LimitProperties())
            {
                property.GetValue(capped).Should().Be(property.GetValue(host), $"{property.Name} under {profile?.ToString() ?? "no profile"}");
            }
        }
    }

    [Fact]
    public void Cap_does_not_write_to_the_limits_it_was_given()
    {
        KafkaScanLimits host = Highest();

        _ = KafkaProfileCeilings.Cap(KafkaProfile.Production, host);

        host.MaxBytesPerScan.Should().Be(64 * GiB);
        host.BytesPerSecond.Should().Be(GiB);
    }

    [Fact]
    public void The_test_table_caps_a_raised_host_at_kstudios_defaults()
    {
        KafkaScanLimits capped = KafkaProfileCeilings.Cap(KafkaProfile.Test, Highest());

        capped.MaxBytesPerScan.Should().Be(256 * MiB);
        capped.MaxRecordsPerScan.Should().Be(5_000_000);
        capped.MaxScanDuration.Should().Be(TimeSpan.FromMinutes(2));
        capped.MaxMatches.Should().Be(500);
        capped.MaxMatchBytes.Should().Be(64 * 1024);
        capped.MaxDecodeBytes.Should().Be(1024 * 1024);
        capped.ConfirmAboveBytes.Should().Be(64 * MiB);
        capped.ConfirmAboveRecords.Should().Be(1_000_000);
        capped.BytesPerSecond.Should().Be(10 * MiB);
        capped.OwnerBytesPerHour.Should().Be(2 * GiB);
        capped.MaxExportBytes.Should().Be(32 * MiB);
        capped.MaxTailMatches.Should().Be(200);
        capped.TailPauseAfter.Should().Be(TimeSpan.FromMinutes(5));

        // The process's memory and the scan's correctness stay the host's.
        capped.MaxConcurrentScans.Should().Be(8);
        capped.MaxConcurrentScansPerOwner.Should().Be(8);
        capped.QueueKilobytes.Should().Be(65_536);
        capped.FetchBytes.Should().Be(50 * (int) MiB);
        capped.CreateTimeMargin.Should().Be(TimeSpan.FromHours(1));
        capped.PartitionStallTimeout.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void The_production_table_is_lower_and_slower_and_forbids_export_and_tail()
    {
        KafkaScanLimits capped = KafkaProfileCeilings.Cap(KafkaProfile.Production, Highest());

        capped.MaxBytesPerScan.Should().Be(64 * MiB);
        capped.MaxRecordsPerScan.Should().Be(1_000_000);
        capped.MaxScanDuration.Should().Be(TimeSpan.FromMinutes(1));
        capped.MaxMatches.Should().Be(500);
        capped.MaxMatchBytes.Should().Be(64 * 1024);
        capped.MaxDecodeBytes.Should().Be(1024 * 1024);
        capped.ConfirmAboveBytes.Should().Be(16 * MiB);
        capped.ConfirmAboveRecords.Should().Be(250_000);
        capped.BytesPerSecond.Should().Be(5 * MiB);
        capped.OwnerBytesPerHour.Should().Be(512 * MiB);

        capped.MaxConcurrentScans.Should().Be(8, "the process's own limits are the host's");

        KafkaProfileCeilings.ForbidsExport(KafkaProfile.Production).Should().BeTrue();
        KafkaProfileCeilings.ForbidsTail(KafkaProfile.Production).Should().BeTrue();

        foreach (KafkaProfile? other in new KafkaProfile?[] { null, KafkaProfile.Development, KafkaProfile.Test })
        {
            KafkaProfileCeilings.ForbidsExport(other).Should().BeFalse();
            KafkaProfileCeilings.ForbidsTail(other).Should().BeFalse();
        }
    }

    [Fact]
    public void The_confirmations_are_capped_again_at_the_capped_budgets_so_the_result_validates()
    {
        // The host asks to confirm above 50 GiB of a budget it also left high: the Production cap lowers the budget, so the confirmation follows it.
        KafkaScanLimits host = new() { MaxBytesPerScan = 64 * GiB, ConfirmAboveBytes = 50 * GiB, MaxRecordsPerScan = 1_000_000_000, ConfirmAboveRecords = 900_000_000 };

        KafkaScanLimits capped = KafkaProfileCeilings.Cap(KafkaProfile.Production, host);

        capped.ConfirmAboveBytes.Should().BeLessThanOrEqualTo(capped.MaxBytesPerScan);
        capped.ConfirmAboveRecords.Should().BeLessThanOrEqualTo(capped.MaxRecordsPerScan);
        capped.Validate().Should().BeEmpty();
    }

    [Fact]
    public void A_host_with_a_lowered_match_limit_still_validates_whatever_the_tail_keeps()
    {
        // MaxTailMatches defaults to 200: a host that lowered MaxMatches below it before this limit existed must still start.
        KafkaScanLimits limits = new() { MaxMatches = 5 };

        limits.Validate().Should().BeEmpty();
        limits.TailCapacity.Should().Be(5, "a tail never keeps more than a search");
        new KafkaScanLimits { MaxMatches = 1_000, MaxTailMatches = 7 }.TailCapacity.Should().Be(7);
    }

    [Theory]
    [InlineData(nameof(KafkaScanLimits.MaxExportBytes), 1)]
    [InlineData(nameof(KafkaScanLimits.MaxExportBytes), 256 * MiB + 1)]
    [InlineData(nameof(KafkaScanLimits.MaxTailMatches), 0)]
    [InlineData(nameof(KafkaScanLimits.MaxTailMatches), 10_001)]
    public void The_new_numeric_limits_are_validated_by_name(string name, long value)
    {
        KafkaScanLimits limits = new();
        PropertyInfo property = typeof(KafkaScanLimits).GetProperty(name)!;
        property.SetValue(limits, Convert.ChangeType(value, property.PropertyType, System.Globalization.CultureInfo.InvariantCulture));

        limits.Validate().Should().ContainSingle().Which.Should().Contain("Limits." + name);
    }

    [Theory]
    [InlineData(9)]
    [InlineData(1_801)]
    public void The_tail_pause_is_validated_between_ten_seconds_and_thirty_minutes(int seconds)
    {
        KafkaScanLimits limits = new() { TailPauseAfter = TimeSpan.FromSeconds(seconds) };

        limits.Validate().Should().ContainSingle().Which.Should().Contain("Limits.TailPauseAfter");
    }

    [Fact]
    public void The_registry_refuses_a_new_limit_outside_its_bounds_like_the_others()
    {
        KafkaEngineOptions options = new();
        options.AddCluster("c", Client);
        options.Limits.MaxExportBytes = 0;

        Action act = () => _ = new KafkaClusterRegistry(options);

        act.Should().Throw<Exception>().Which.Message.Should().Contain("MaxExportBytes");
    }

    [Fact]
    public void The_defaults_of_the_new_limits_are_the_designed_ones()
    {
        KafkaScanLimits limits = new();

        limits.MaxExportBytes.Should().Be(32 * MiB);
        limits.MaxTailMatches.Should().Be(200);
        limits.TailPauseAfter.Should().Be(TimeSpan.FromMinutes(5));
        limits.Validate().Should().BeEmpty();
    }
}
