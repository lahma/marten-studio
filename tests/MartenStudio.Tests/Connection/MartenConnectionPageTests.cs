using Bunit;

using MartenStudio.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Components.Pages.Database;
using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Database;

namespace MartenStudio.Tests.Connection;

/// <summary>
/// "How Marten uses this connection" drawn on Database › Connection (1.1-NP4): the real contributor's contribution, arranged by the
/// real page builder and rendered by the real page - the section, Marten's sentence on the page's own hint, and no secret.
/// </summary>
public class MartenConnectionPageTests
{
    private const string Sentinel = "S3NT1NEL";

    [Fact]
    public async Task The_page_draws_the_section_below_its_own_and_marten_s_sentence_on_the_timeout_hint()
    {
        await using MartenConnectionInsightTests.Rig rig = MartenConnectionInsightTests.Rig.Create(static options =>
        {
            options.Connection("Host=marten-np4.invalid;Port=5432;Database=np4;Username=svc;Command Timeout=77");
            options.Advanced.MultiHostSettings.ReadSessionPreference = Npgsql.TargetSessionAttributes.PreferStandby;
        });

        ConnectionObserved observed = MartenConnectionInsightTests.Observed(statementTimeoutMs: 10_000);
        ConnectionContribution contribution = await rig.ContributeAsync(observed);

        ConnectionPageView view = ConnectionPageBuilder.Build(new ConnectionPageInputs
        {
            Facts = MartenConnectionInsightTests.Facts("Host=marten-np4.invalid;Port=5432;Database=np4;Username=svc;Password=" + Sentinel, observed),
            Contributions = [contribution],
            Activity = new SessionDetailsAccess(false, false, null, DatabaseRefusal.CapabilityOff, null),
            MeasuredAt = DateTimeOffset.UnixEpoch,
        });

        await using StudioComponentContext context = new(static services => services.AddSingleton(new SessionPoolObserver("MartenStudio.Tests.Np4." + Guid.NewGuid().ToString("N"))));
        context.Connection.View = view;
        await context.ReadyAsync();

        IRenderedComponent<ConnectionPage> page = context.Render<ConnectionPage>();
        page.WaitForAssertion(() => page.FindAll(".ms-conn-strip").Should().ContainSingle());

        AngleSharp.Dom.IElement section = page.Find("#conn-marten");
        section.QuerySelector("h2")!.TextContent.Should().Be("How Marten uses this connection");
        section.TextContent.Should().Contain("Command timeout").And.Contain("77 s")
            .And.Contain("Sticky connections").And.Contain("StoreOptions.UseStickyConnectionLifetimes, default off")
            .And.Contain("Async daemon").And.Contain("Marten's own");

        // The page's own CMDTIMEOUT card (server timeouts are set) carries Marten's sentence at its end.
        string.Join(' ', page.FindAll(".ms-conn-card").Select(static x => x.TextContent))
            .Should().Contain("the figure that counts is Marten's, 77 s").And.Contain("the server cancels a long Marten command first");

        // A section is below the page's own: after the settings.
        page.Markup.IndexOf("id=\"conn-marten\"", StringComparison.Ordinal)
            .Should().BeGreaterThan(page.Markup.IndexOf("id=\"conn-settings\"", StringComparison.Ordinal));

        // The sentinel stood in the connection strings; it is in no markup the page draws.
        page.Markup.Should().NotContain(Sentinel);
    }
}
