using System.Reflection;

using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;

using MartenStudio.Sample;

namespace MartenStudio.Integration.Tests;

/// <summary>
/// The Marten sample's throwaway container and this suite's reused one have different reuse identities.
/// </summary>
/// <remarks>
/// Testcontainers attaches a reusing start to whichever running container's configuration hashes alike. Both were built
/// from bare <c>postgres:17-alpine</c> defaults, so they hashed alike, and a <c>dotnet run</c> of the sample on a machine
/// that had run this suite opened the suite's reused container - month-old test data and all - as its demo database. The
/// sample's container now carries a label of its own, which is part of the hash.
/// </remarks>
public class EphemeralPostgresReuseTests
{
    /// <remarks>A <c>[PostgresFact]</c> only because Testcontainers' builder needs a Docker endpoint to build at all.</remarks>
    [PostgresFact]
    public async Task The_sample_and_the_suite_never_attach_to_each_others_container()
    {
        await using IContainer suite = PostgresFixture.CreateContainer(reuse: true);
        await using IContainer sample = EphemeralPostgres.CreateContainer(reuse: true);
        await using IContainer sampleUnreused = EphemeralPostgres.CreateContainer(reuse: false);

        ConfigurationOf(sample).Labels.Should().Contain(EphemeralPostgres.ReuseLabel, EphemeralPostgres.ReuseLabelValue);
        ConfigurationOf(sample).GetReuseHash().Should().NotBe(ConfigurationOf(suite).GetReuseHash(),
            "a reusing sample start must never open the integration suite's container as its demo database");
        ConfigurationOf(sampleUnreused).GetReuseHash().Should().Be(ConfigurationOf(sample).GetReuseHash(),
            "the reuse flag is not part of the identity, so the label is what tells them apart");
    }

    /// <summary>
    /// The configuration Testcontainers hashes, which <see cref="DockerContainer" /> holds in a private field - so a rename
    /// there fails here, by name.
    /// </summary>
    private static IContainerConfiguration ConfigurationOf(IContainer container)
    {
        FieldInfo field = typeof(DockerContainer).GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException(
                "Testcontainers' DockerContainer no longer has a private _configuration field; find where its "
                + "IContainerConfiguration (whose GetReuseHash() decides reuse) went.");

        return (IContainerConfiguration) field.GetValue(container)!;
    }
}
