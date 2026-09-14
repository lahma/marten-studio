using MartenStudio.Services.Projections;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MartenStudio.Tests.Projections;

/// <summary>
/// The per-shard replay budget a rebuild is given, and where it comes from.
/// </summary>
/// <remarks>
/// <para>
/// <c>IProjectionDaemon.RebuildProjectionAsync(name, token)</c> is not the "no timeout" overload it looks
/// like: JasperFx 2.69.3 forwards it to <c>RebuildProjectionAsync(name, 5.Minutes(), token)</c>, and the
/// teardown of the projection's tables runs <em>before</em> that budget starts applying to the replay. On
/// a production-sized store the five minutes expire with the tables already emptied and the rebuild
/// recorded as failed - which is the one outcome a rebuild screen must never produce silently.
/// </para>
/// <para>
/// So the studio never calls it. It names its own budget, the host can change it, and the dialog states
/// it before anything is emptied.
/// </para>
/// </remarks>
public class RebuildShardTimeoutTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_rebuild_is_asked_for_with_an_explicit_per_shard_timeout()
    {
        using var daemon = new FakeProjectionDaemon();

        await ProjectionDataService.RebuildWithTimeoutAsync(daemon, "DailySales", TimeSpan.FromHours(3), Token);

        daemon.UsedTimeoutlessOverload.Should().BeFalse(
            "that overload hides a five minute per-shard timeout applied after the tables are emptied");

        daemon.LastShardTimeout.Should().Be(TimeSpan.FromHours(3));
        daemon.Rebuilds.Should().ContainSingle().Which.Should().Be("DailySales/03:00:00");
    }

    [Fact]
    public async Task The_configured_option_is_the_budget_that_is_handed_over()
    {
        using var daemon = new FakeProjectionDaemon();

        var options = new MartenStudioOptions { RebuildShardTimeout = TimeSpan.FromMinutes(90) };

        await ProjectionDataService.RebuildWithTimeoutAsync(
            daemon, "DailySales", options.RebuildShardTimeout, Token);

        daemon.LastShardTimeout.Should().Be(TimeSpan.FromMinutes(90));
    }

    /// <summary>
    /// The default is the studio's, not Marten's. One hour is generous on purpose: the cost of being
    /// wrong the other way is an emptied projection.
    /// </summary>
    [Fact]
    public void The_default_budget_is_an_hour_rather_than_Martens_five_minutes()
    {
        new MartenStudioOptions().RebuildShardTimeout.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public void A_per_shard_timeout_under_a_minute_is_refused_at_startup()
    {
        var services = new ServiceCollection();
        services.AddMartenStudio(static options => options.RebuildShardTimeout = TimeSpan.FromSeconds(30));

        using ServiceProvider provider = services.BuildServiceProvider();

        Action resolving = () => _ = provider.GetRequiredService<IOptions<MartenStudioOptions>>().Value;

        resolving.Should().Throw<OptionsValidationException>().WithMessage("*RebuildShardTimeout*");
    }
}
