using KStudio.Internal;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using PostgresStudio;

namespace KStudio.Tests.Registration;

/// <summary>D35: a capability that is on, with the host studio's master switch off and no Kafka policy to answer for it, is refused at start - with no escape hatch.</summary>
public class KafkaStudioStartupGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task Start(Action<PostgresStudioOptions> studio, Action<KafkaStudioOptions> kafka)
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddPostgresStudio(studio);
        services.AddKafkaStudio(kafka);
        ServiceProvider provider = services.BuildServiceProvider();

        return provider.GetServices<IHostedService>().OfType<KafkaStudioStartupGuard>().Single().StartAsync(Ct);
    }

    [Fact]
    public async Task A_capability_with_no_policy_refuses_to_start()
    {
        Func<Task> start = () => Start(static _ => { }, static o => o.Capabilities.SearchRecords = true);

        (await start.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(
            "KStudio has SearchRecords enabled and no policy decides who may use them: every visitor admitted to the studio could read the records "
            + "of every topic this application's credentials can read. Set KafkaStudioOptions.CapabilityAuthorizationPolicy (asked for each "
            + "capability use, once per topic), or KafkaStudioOptions.ClusterAuthorizationPolicy (asked for every read, once per topic), or turn the "
            + "capabilities off; PostgresStudioOptions.ReadOnly = true turns them all off.");
    }

    [Theory]
    [InlineData("ReadPayloads")]
    [InlineData("ExportRecords")]
    [InlineData("LiveTail")]
    public async Task Each_capability_alone_with_no_policy_refuses_to_start(string capability)
    {
        Func<Task> start = () => Start(
            static _ => { },
            o =>
            {
                typeof(KafkaStudioCapabilities).GetProperty(capability)!.SetValue(o.Capabilities, true);

                // ReadPayloads on its own is refused by the options themselves (it needs SearchRecords); the two new flags are not.
                o.Capabilities.SearchRecords = capability == "ReadPayloads";
            });

        (await start.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain(capability + " enabled and no policy decides");
    }

    [Theory]
    [InlineData("ExportRecords")]
    [InlineData("LiveTail")]
    public async Task A_new_capability_without_SearchRecords_but_with_a_policy_starts(string capability)
    {
        await Start(
            static _ => { },
            o =>
            {
                typeof(KafkaStudioCapabilities).GetProperty(capability)!.SetValue(o.Capabilities, true);
                o.ClusterAuthorizationPolicy = "kafka-topics";
            });
    }

    [Fact]
    public async Task A_capability_under_ReadOnly_starts()
    {
        await Start(static o => o.ReadOnly = true, static o => o.Capabilities = KafkaStudioCapabilities.All());
    }

    [Theory]
    [InlineData("cluster")]
    [InlineData("capability")]
    public async Task A_capability_with_either_policy_starts(string which)
    {
        await Start(
            static _ => { },
            o =>
            {
                o.Capabilities = KafkaStudioCapabilities.All();
                if (which == "cluster")
                {
                    o.ClusterAuthorizationPolicy = "kafka-topics";
                }
                else
                {
                    o.CapabilityAuthorizationPolicy = "kafka-search";
                }
            });
    }

    [Fact]
    public async Task A_whitespace_policy_is_no_policy()
    {
        Func<Task> start = () => Start(static _ => { }, static o =>
        {
            o.Capabilities.SearchRecords = true;
            o.ClusterAuthorizationPolicy = "  ";
            o.CapabilityAuthorizationPolicy = string.Empty;
        });

        await start.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task No_capability_starts_without_a_policy()
    {
        await Start(static _ => { }, static _ => { });
    }
}
