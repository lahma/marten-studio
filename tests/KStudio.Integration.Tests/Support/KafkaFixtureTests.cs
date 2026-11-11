using Confluent.Kafka;
using Confluent.Kafka.Admin;

using Testcontainers.Kafka;

namespace KStudio.Integration.Tests.Support;

/// <summary>
/// The harness itself (decision 12.2): the reuse switch and each image's reuse identity, and that a broker the fixture disposes leaves none of
/// the six anonymous volumes the <c>apache/kafka</c> image creates.
/// </summary>
public class KafkaFixtureTests
{
    [Fact]
    public void The_switch_is_the_repositorys_own_variable_and_not_a_Testcontainers_one() =>
        KafkaFixture.ReuseEnvironmentVariable.Should().Be("MARTENSTUDIO_PG_REUSE", "one variable governs every container this repository starts");

    [Theory]
    [InlineData("false")]
    [InlineData("FALSE")]
    [InlineData("0")]
    [InlineData("  false  ")]
    public void False_and_zero_turn_reuse_off(string value) => KafkaFixture.IsReuseDisabled(value).Should().BeTrue();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("falsey")]
    public void Anything_else_keeps_the_default(string? value) => KafkaFixture.IsReuseDisabled(value).Should().BeFalse();

    [Fact]
    public void Continuous_integration_never_reuses()
    {
        if (DockerAvailability.OnContinuousIntegration)
        {
            KafkaFixture.ShouldReuse.Should().BeFalse("a CI agent is thrown away anyway");
        }
        else
        {
            KafkaFixture.ShouldReuse.Should().Be(!KafkaFixture.IsReuseDisabled(Environment.GetEnvironmentVariable(KafkaFixture.ReuseEnvironmentVariable)));
        }
    }

    [Fact]
    public void Each_image_has_a_reuse_label_of_its_own()
    {
        KafkaFixture.ReuseLabelValue(KafkaFixture.Image39).Should().Be("KStudio.Integration.Tests.3.9.2");
        KafkaFixture.ReuseLabelValue(KafkaFixture.Image4).Should().Be("KStudio.Integration.Tests.4.4.0");
    }

    /// <summary>A <c>[KafkaFact]</c> only because Testcontainers' builder needs a Docker endpoint to build at all.</summary>
    [KafkaFact]
    public async Task The_two_images_and_the_packets_hash_differently_and_the_reuse_flag_is_not_part_of_the_identity()
    {
        await using Containers containers = new(
            KafkaFixture.CreateContainer(KafkaFixture.Image39, reuse: true, packet: null),
            KafkaFixture.CreateContainer(KafkaFixture.Image4, reuse: true, packet: null),
            KafkaFixture.CreateContainer(KafkaFixture.Image4, reuse: true, packet: "ENG-A"),
            KafkaFixture.CreateContainer(KafkaFixture.Image4, reuse: true, packet: "ENG-B"),
            KafkaFixture.CreateContainer(KafkaFixture.Image4, reuse: false, packet: null));

        ContainerReuse.ConfigurationOf(containers.All[0]).Labels.Should().Contain(KafkaFixture.ReuseLabel, "KStudio.Integration.Tests.3.9.2");
        ContainerReuse.ConfigurationOf(containers.All[2]).Labels.Should().Contain("kstudio.packet", "ENG-A");

        string[] hashes = [.. containers.All.Take(4).Select(ContainerReuse.HashOf)];
        hashes.Should().OnlyHaveUniqueItems("two packets never attach to each other's broker, and neither does one image to another's");
        ContainerReuse.HashOf(containers.All[4]).Should().Be(hashes[1], "the reuse flag is not part of the identity");
    }

    /// <summary>
    /// A broker the fixture starts and disposes without reuse leaves none of its volumes: the image declares six anonymous ones, which Docker
    /// keeps unless they are removed with the container.
    /// </summary>
    [KafkaFact]
    public async Task A_disposed_broker_leaves_no_volume_of_ours()
    {
        // Reuse keeps the container, and its volumes with it, by design; this proof needs a broker that is really disposed.
        Assert.SkipWhen(KafkaFixture.ShouldReuse, "Container reuse is on (set MARTENSTUDIO_PG_REUSE=false): a reused broker keeps its volumes by design.");

        Kafka4Fixture throwaway = new();
        await throwaway.InitializeAsync();
        string[] volumes = [.. throwaway.Volumes];

        volumes.Should().NotBeEmpty("the apache/kafka image declares anonymous volumes; otherwise this test proves nothing");
        (await DockerCli.ExistingAsync(volumes)).Should().BeEquivalentTo(volumes, "they exist while the broker runs");

        await throwaway.DisposeAsync();

        (await DockerCli.ExistingAsync(volumes)).Should().BeEmpty("the fixture removes the broker's volumes with it");
    }

    private sealed class Containers(params KafkaContainer[] all) : IAsyncDisposable
    {
        public KafkaContainer[] All { get; } = all;

        public async ValueTask DisposeAsync()
        {
            foreach (KafkaContainer container in All)
            {
                await container.DisposeAsync();
            }
        }
    }
}

/// <summary>The broker each image starts is the one the design describes: SASL/PLAIN, a super user, and the ACLs of three restricted principals.</summary>
public abstract class KafkaBrokerLiveTests(KafkaFixture fixture)
{
    [Fact]
    public async Task The_broker_starts_with_sasl_and_acls()
    {
        Assert.SkipUnless(DockerAvailability.IsAvailable, DockerAvailability.SkipReason);
        fixture.Started.Should().BeTrue();

        using IAdminClient admin = new AdminClientBuilder(fixture.Client("admin")).Build();
        admin.GetMetadata(TimeSpan.FromSeconds(10)).Brokers.Should().NotBeEmpty("SASL/PLAIN answers for admin");

        using IAdminClient wrong = new AdminClientBuilder(KafkaFixture.ClientFor(fixture.Bootstrap, "admin", "wrong")).Build();
        Action refused = () => wrong.GetMetadata(TimeSpan.FromSeconds(5));
        refused.Should().Throw<KafkaException>("a wrong password is refused");

        DescribeAclsResult result = await admin.DescribeAclsAsync(new AclBindingFilter
        {
            PatternFilter = new ResourcePatternFilter { Type = ResourceType.Any, Name = null, ResourcePatternType = ResourcePatternType.Any },
            EntryFilter = new AccessControlEntryFilter { Principal = null, Host = null, Operation = AclOperation.Any, PermissionType = AclPermissionType.Any },
        });

        List<AclBinding> acls = result.AclBindings;
        acls.Should().Contain(x => x.Entry.Principal == "User:reader" && x.Entry.Operation == AclOperation.Read && x.Pattern.Name == "ks." && x.Pattern.Type == ResourceType.Topic);
        acls.Should().Contain(x => x.Entry.Principal == "User:configreader" && x.Entry.Operation == AclOperation.DescribeConfigs);
        acls.Should().NotContain(x => x.Entry.Principal == "User:nogroup" && x.Pattern.Type == ResourceType.Group, "nogroup has no group ACL (SPIKE S1)");
    }
}

/// <summary><see cref="KafkaBrokerLiveTests" /> on Kafka 3.9.2.</summary>
[Collection(KafkaCollections.V39)]
public sealed class KafkaBrokerLiveTests39(Kafka39Fixture fixture) : KafkaBrokerLiveTests(fixture);

/// <summary><see cref="KafkaBrokerLiveTests" /> on Kafka 4.4.0.</summary>
[Collection(KafkaCollections.V4)]
public sealed class KafkaBrokerLiveTests4(Kafka4Fixture fixture) : KafkaBrokerLiveTests(fixture);
