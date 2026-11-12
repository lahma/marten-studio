using KStudio.Core.Wire;
using KStudio.Integration.Tests.Support;

namespace KStudio.Integration.Tests.Wire;

/// <summary>
/// The lag read as the first group request a pristine broker ever sees (librdkafka issue #5040, D36). A broker that has never hosted a consumer
/// group creates <c>__consumer_offsets</c> when the first group lookup arrives and answers <c>NOT_COORDINATOR</c> or
/// <c>COORDINATOR_NOT_AVAILABLE</c> until it has elected a coordinator. On an admin handle, which has no consumer group, librdkafka 2.16.0 and
/// earlier dereference a NULL <c>rk_cgrp</c> on that answer and the process dies (SIGSEGV, exit 139) - about once in ten fresh brokers with the
/// full suite around it, and not at all on Windows. The engine reads group offsets on a consumer-backed handle instead, so every iteration must
/// answer a value or a refusal and the test host must survive.
/// </summary>
/// <remarks>
/// Every iteration starts a broker of its own, which is what makes it the first group request: the loop runs <see cref="DefaultLoops" /> times
/// (about a minute and a half for both images, which run side by side), or <c>KSTUDIO_FRESH_BROKER_LOOPS</c> times when that is set. Before
/// the fix the first loops ended the test host with exit code 139 on Linux. A crash does not fail an assertion; it ends the test host, which is
/// how a failure shows.
/// </remarks>
public abstract class FreshBrokerLagReadLiveTests(string image)
{
    /// <summary>The variable that sets how many fresh brokers one run starts per image.</summary>
    public const string LoopsEnvironmentVariable = "KSTUDIO_FRESH_BROKER_LOOPS";

    /// <summary>The loops when the variable is not set.</summary>
    public const int DefaultLoops = 20;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [KafkaFact]
    public async Task The_first_group_request_a_pristine_broker_sees_is_answered_with_a_value_or_a_refusal_and_the_process_survives()
    {
        int loops = int.TryParse(Environment.GetEnvironmentVariable(LoopsEnvironmentVariable), out int parsed) && parsed > 0 ? parsed : DefaultLoops;
        List<string> outcomes = [];

        for (int i = 0; i < loops; i++)
        {
            await using FreshKafkaFixture broker = new(image);
            await broker.InitializeAsync();
            await using LiveContractCluster cluster = LiveContractCluster.Open(broker);

            string topic = cluster.NewTopic("fresh");
            await cluster.CreateTopicAsync(topic, 1);

            // Nothing has asked the broker about a group yet: this is the request that makes it create the coordinator's topic.
            WireResult<IReadOnlyList<WireGroupOffset>> read = await cluster.Wire.ListGroupOffsetsAsync(
                "kstudio-fresh-" + broker.RunId, [new WirePartition(topic, 0)], Token);

            // As admin, who may do everything, the broker either answers (a group nobody committed for has no offsets) or says it is not ready:
            // measured on both images, a pristine broker's coordinator is mostly "unavailable" (not elected yet) and sometimes
            // GroupLoadInProress (elected, still loading). Any other refusal would be the engine's own fault, and a crash never gets here.
            outcomes.Add(read.IsOk
                ? (read.Value.All(static x => x.Committed is null && x.Error is null) ? "value" : "value-with-offsets-or-errors")
                : read.Error switch
                {
                    WireError.Unavailable => "unavailable",
                    WireError.Failed { FailedCode: "GroupLoadInProress" } => "loading",
                    _ => "other:" + read.Error,
                });
        }

        outcomes.Should().HaveCount(loops);
        outcomes.Should().OnlyContain(static x => x == "value" || x == "unavailable" || x == "loading", "outcomes were: " + string.Join(", ", outcomes));
    }
}

/// <summary><see cref="FreshBrokerLagReadLiveTests" /> on Kafka 3.9.2.</summary>
public sealed class FreshBrokerLagReadLiveTests39() : FreshBrokerLagReadLiveTests(KafkaFixture.Image39);

/// <summary><see cref="FreshBrokerLagReadLiveTests" /> on Kafka 4.4.0.</summary>
public sealed class FreshBrokerLagReadLiveTests4() : FreshBrokerLagReadLiveTests(KafkaFixture.Image4);
