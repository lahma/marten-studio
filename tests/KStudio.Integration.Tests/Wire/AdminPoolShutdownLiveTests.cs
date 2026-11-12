using Confluent.Kafka;

using KStudio.Core.Wire;
using KStudio.Core.Wire.Confluent;
using KStudio.Tests.Contract;

using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Integration.Tests.Wire;

/// <summary>
/// The admin pool never destroys a client a call still holds (M2-HARDEN, hazard 2): a host that stops within the request timeout of a visitor
/// leaving a page, during a read against a cluster that does not answer, used to free the native client under a blocking
/// <c>GetMetadata</c>. No broker is needed: nothing listens on port 1, so the read blocks for the whole request timeout.
/// </summary>
public sealed class AdminPoolShutdownLiveTests
{
    [Fact]
    public async Task Disposing_the_engine_during_a_blocked_metadata_read_survives_and_waits_for_the_read()
    {
        CancellationToken test = TestContext.Current.CancellationToken;
        EngineHarness harness = EngineHarness.ForClient(
            new ClientConfig { BootstrapServers = "127.0.0.1:1" },
            configure: static options => options.RequestTimeout = TimeSpan.FromSeconds(5));

        KafkaAdminPool<IAdminClient> pool = harness.Provider.GetRequiredService<KafkaAdminPool<IAdminClient>>();
        using CancellationTokenSource caller = new();

        Task<WireResult<WireMetadata>> read = harness.Wire.GetMetadataAsync(caller.Token);

        // The pool creates the client and the call takes its lease before it blocks in GetMetadata: a wait on that condition.
        SpinWait.SpinUntil(() => pool.ClientCount == 1, TimeSpan.FromSeconds(30)).Should().BeTrue("the read rented the cluster's client");

        // The visitor leaves the page: the caller's wait ends, the call itself goes on holding its lease.
        await caller.CancelAsync();
        Func<Task> wait = () => read;
        await wait.Should().ThrowAsync<OperationCanceledException>();

        // The host stops while GetMetadata is still blocked on the native client.
        Task dispose = harness.DisposeAsync().AsTask();
        dispose.IsCompleted.Should().BeFalse("the pool waits for the client a call still holds");

        await dispose.WaitAsync(TimeSpan.FromSeconds(30), TimeProvider.System, test);

        harness.Logs.Entries.Should().NotContain(static x => x.EventId.Id == 9664, "the retired client was disposed cleanly");
    }
}
