using MartenStudio.Services;
using MartenStudio.Services.Projections;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// The same store, registered without <c>AddAsyncDaemon</c>: the daemon runs somewhere else, or nowhere.
/// </summary>
/// <remarks>
/// This is a supported deployment and not a fault, so the page still renders every progress row out of
/// the database and only the controls go away. It is also the state that proves the studio never builds
/// a daemon of its own - if it did, this class would find one (AGENTS.md hard rule 11).
/// </remarks>
public class DaemonNotHostedLiveTests(PostgresFixture postgres) : ProjectionsTestBase(postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    protected override bool WithDaemon => false;

    [PostgresFact]
    public async Task The_card_says_not_hosted_in_this_process_and_explains_what_to_do()
    {
        ProjectionsView view = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));

        view.Daemon.Hosting.Should().Be(DaemonHostingState.NotHostedInThisProcess);
        view.Daemon.IsHostedHere.Should().BeFalse();
        view.Daemon.Explanation.Should().Contain("AddAsyncDaemon");
        view.Daemon.Agents.Should().BeEmpty();
    }

    /// <summary>
    /// The page is still a page: the static model and the progression table are read from the database,
    /// which is where they live whether or not anything here is running.
    /// </summary>
    [PostgresFact]
    public async Task Progress_still_comes_out_of_the_database()
    {
        ProjectionsView view = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));

        view.Projections.Should().HaveCount(3);
        view.HighWaterMark.Should().BeGreaterThan(0, "the seeder appended events even though no daemon ran");
        view.Progress.Should().NotBeEmpty("every async projection has a row, whether or not it has ever run");

        // Nothing has advanced and no daemon is hosted here, which is exactly the banner's condition.
        view.NoDaemonAnywhere.Should().BeTrue();
        view.Progress.Should().AllSatisfy(shard => shard.HasProgressRow.Should().BeFalse());
    }

    /// <summary>
    /// There is nothing here to ask, and the service says so rather than quietly doing nothing - the
    /// refusal lives where the operation does, not in the markup.
    /// </summary>
    [PostgresFact]
    public async Task Controlling_a_daemon_that_is_not_here_is_refused_with_a_reason()
    {
        Func<Task> starting = () => Fixture.UseAsync(service => service.StartAllAsync(Fixture.Scope, Token));

        (await starting.Should().ThrowAsync<StudioDaemonNotHostedException>())
            .WithMessage("*AddAsyncDaemon*");

        Fixture.ActionLog.GetLatest().Should().Contain(x => x.Action == "StartAllAgents" && !x.Succeeded);
    }

    [PostgresFact]
    public async Task A_rebuild_that_has_no_daemon_to_run_it_is_refused_before_anything_starts()
    {
        Func<Task> rebuilding = () => Fixture.UseAsync(service => service.RebuildAsync(Fixture.Scope, "DailySales", Token));

        await rebuilding.Should().ThrowAsync<StudioDaemonNotHostedException>();

        Fixture.Operations.All().Should().BeEmpty("nothing was started");
    }

    /// <summary>
    /// Progression corrections are writes against the progression table rather than daemon calls, so they
    /// are still offered - and still gated on <c>CorrectProgression</c>.
    /// </summary>
    [PostgresFact]
    public async Task Correcting_progression_needs_no_daemon()
    {
        await Fixture.UseAsync(service => service.CorrectProgressionAsync(Fixture.Scope, Token));

        Fixture.ActionLog.GetLatest().Should().Contain(x => x.Action == "CorrectProgression" && x.Succeeded);
    }

    /// <summary>With no daemon there is no tracker to watch, and asking for a lease is still answered.</summary>
    [PostgresFact]
    public async Task Subscribing_to_live_state_is_answered_with_a_lease_that_watches_nothing()
    {
        using IDisposable lease = await Fixture.UseAsync(service => service.SubscribeToLiveStateAsync(Fixture.Scope, Token));

        lease.Should().NotBeNull();
        Fixture.LiveState.WatchCount.Should().Be(0);
    }
}

/// <summary>
/// The same host with the capabilities a fresh mapping has: none.
/// </summary>
/// <remarks>
/// The gating is tested here rather than only in the component suite because hiding a button is a UI
/// convenience and the refusal has to live where the operation does (AGENTS.md hard rule 5). A Blazor
/// circuit is a long-lived object a client can drive.
/// </remarks>
public class ProjectionCapabilityGatingLiveTests(PostgresFixture postgres) : ProjectionsTestBase(postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private protected override MartenStudioCapabilities? Capabilities => new();

    [PostgresFact]
    public async Task Reading_needs_no_capability_at_all()
    {
        ProjectionsView view = await Fixture.UseAsync(service => service.GetProjectionsAsync(Fixture.Scope, Token));

        view.Projections.Should().NotBeEmpty();
    }

    [PostgresFact]
    public async Task Controlling_the_daemon_is_refused_and_names_the_option()
    {
        Func<Task> starting = () => Fixture.UseAsync(service => service.StartAllAsync(Fixture.Scope, Token));

        (await starting.Should().ThrowAsync<StudioCapabilityDeniedException>())
            .WithMessage("*MartenStudioOptions.Capabilities.ControlDaemon*");
    }

    [PostgresFact]
    public async Task Rebuilding_is_refused_and_names_the_option()
    {
        Func<Task> rebuilding = () => Fixture.UseAsync(service => service.RebuildAsync(Fixture.Scope, "DailySales", Token));

        (await rebuilding.Should().ThrowAsync<StudioCapabilityDeniedException>())
            .WithMessage("*MartenStudioOptions.Capabilities.RebuildProjections*");

        Fixture.Operations.All().Should().BeEmpty();
    }

    [PostgresFact]
    public async Task Correcting_progression_is_refused_and_names_the_option()
    {
        Func<Task> correcting = () => Fixture.UseAsync(service => service.CorrectProgressionAsync(Fixture.Scope, Token));

        (await correcting.Should().ThrowAsync<StudioCapabilityDeniedException>())
            .WithMessage("*MartenStudioOptions.Capabilities.CorrectProgression*");
    }

    /// <summary>Every refusal is in the ring, which is what the Activity page reads.</summary>
    [PostgresFact]
    public async Task Every_refusal_is_audited()
    {
        Func<Task> starting = () => Fixture.UseAsync(service => service.StopAllAsync(Fixture.Scope, Token));

        await starting.Should().ThrowAsync<StudioCapabilityDeniedException>();

        Fixture.ActionLog.GetLatest().Should().Contain(x =>
            x.Action == "StopAllAgents"
            && !x.Succeeded
            && x.Capability == nameof(StudioCapability.ControlDaemon));
    }
}
