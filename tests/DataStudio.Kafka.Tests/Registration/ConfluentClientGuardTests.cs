using DataStudio.Kafka.Core.Clusters;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Kafka.Tests.Registration;

/// <summary>
/// <c>AddKafka</c> refuses Confluent.Kafka 3 and later with one sentence that names the way out (#103). The refusal is driven through the guard's seam,
/// because this test host runs the one Confluent.Kafka the build restored, which is 2.
/// </summary>
public class ConfluentClientGuardTests
{
    private const string Own = "2.0.2";

    [Fact]
    public void The_Confluent_Kafka_this_build_restored_passes_and_so_does_the_registration()
    {
        typeof(global::Confluent.Kafka.IProducer<,>).Assembly.GetName().Version!.Major.Should().BeLessThan(ConfluentClientGuard.FirstUnsupportedMajor);

        FluentActions.Invoking(ConfluentClientGuard.Ensure).Should().NotThrow();
        FluentActions.Invoking(() => new ServiceCollection().AddDataStudio().AddKafka()).Should().NotThrow();
    }

    [Theory]
    [InlineData("2.14.0.0")]
    [InlineData("2.16.0.0")]
    [InlineData("2.99.0.0")]
    [InlineData("1.9.2.0")]
    public void Confluent_Kafka_2_and_below_pass(string loaded)
    {
        FluentActions.Invoking(() => ConfluentClientGuard.Ensure(Version.Parse(loaded), Own)).Should().NotThrow();
    }

    [Fact]
    public void An_assembly_with_no_version_is_not_refused()
    {
        FluentActions.Invoking(() => ConfluentClientGuard.Ensure(null, Own)).Should().NotThrow();
    }

    [Theory]
    [InlineData("3.0.0.0", "3.0.0")]
    [InlineData("4.4.0.0", "4.4.0")]
    [InlineData("4.4.1.2", "4.4.1")]
    public void Confluent_Kafka_3_and_later_are_refused_with_the_sentence(string loaded, string shown)
    {
        FluentActions.Invoking(() => ConfluentClientGuard.Ensure(Version.Parse(loaded), Own))
            .Should().Throw<InvalidOperationException>()
            .WithMessage(
                $"Data Studio {Own} reads Kafka with Confluent.Kafka 2, and this application uses Confluent.Kafka {shown}, a different client under the same package id. " +
                "Keep Confluent.Kafka 2 for Data Studio 2.");
    }

    [Fact]
    public void The_guard_runs_first_in_the_modules_registration_body()
    {
        System.Reflection.MethodInfo body = typeof(KafkaDataStudioBuilderExtensions).GetMethod(
            "AddKafkaServices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        System.Reflection.MethodInfo guard = typeof(ConfluentClientGuard).GetMethod(nameof(ConfluentClientGuard.Ensure), Type.EmptyTypes)!;
        byte[] il = body.GetMethodBody()!.GetILAsByteArray()!;

        // The first instruction that is not a nop (a Debug build starts with one) is a call (0x28) whose operand is the guard's method token.
        int first = Array.FindIndex(il, static x => x != 0x00);

        il[first].Should().Be(0x28, "the first thing the body does is call");
        body.Module.ResolveMethod(BitConverter.ToInt32(il, first + 1)).Should().Be(guard, "and what it calls is the guard");
    }
}
