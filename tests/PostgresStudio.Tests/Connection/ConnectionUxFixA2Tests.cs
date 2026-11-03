using MartenStudio.Tests.Conventions;

using PostgresStudio.Services.Connection;
using PostgresStudio.Services.Server;

using static PostgresStudio.Tests.Connection.ConnectionTestFacts;

namespace PostgresStudio.Tests.Connection;

/// <summary>
/// UX-FIX-A2: the review's follow-ups to the Connection page's UX fixes, held at the model and the stylesheet - one rule for "the
/// server's peer is local", the container guess kept for a host with no pooler in front, and the stylesheet's tablet step.
/// </summary>
public sealed class ConnectionUxFixA2Tests
{
    // -------------------------------------------------------------------------------------------------
    // One rule, one place
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("127.0.0.1")]
    [InlineData(" 127.0.0.1 ")]
    [InlineData("127.1.2.3")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("172.17.0.1")]
    [InlineData("10.0.0.9")]
    [InlineData("192.168.65.1")]
    [InlineData("2001:db8::1")]
    [InlineData("not-an-address")]
    public void The_observed_peer_is_local_by_the_one_rule_the_server_page_uses(string? address) =>
        Observed(ssl: false, clientAddress: address).PeerIsLocal.Should().Be(
            ServerConnectionPosture.KindOf(address) != ServerConnectionKind.Network,
            "ConnectionObserved asks ServerConnectionPosture.KindOf, it does not keep a rule of its own");

    // -------------------------------------------------------------------------------------------------
    // The container guess
    // -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Host=localhost;SSL Mode=Prefer", "not encrypted · the server sees a TCP peer (likely a container network)")]
    [InlineData("Host=localhost;Port=6432;SSL Mode=Prefer", "not encrypted · the server sees a TCP peer")]
    [InlineData("Host=127.0.0.1;Port=6543;SSL Mode=Prefer", "not encrypted · the server sees a TCP peer")]
    public void A_container_network_is_not_guessed_when_a_pooler_sits_on_the_loopback_host(string connectionString, string text)
    {
        ConnectionPageView view = View(Facts(connectionString, Observed(ssl: false, clientAddress: "172.17.0.1")));

        view.Tls.State.Should().Be(ConnectionTlsState.NotEncrypted);
        view.Tls.Text.Should().Be(text);

        if (!text.Contains("container", StringComparison.Ordinal))
        {
            view.Tls.Detail.Should().NotContain("container or NAT", "a local pooler is the likelier reason, and the page does not guess another");
        }
    }

    // -------------------------------------------------------------------------------------------------
    // The stylesheet
    // -------------------------------------------------------------------------------------------------

    [Fact]
    public void The_settings_tables_hold_a_tablet_without_scrolling_sideways()
    {
        string css = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "wwwroot", "css", "studio.css"));
        string section = css[css.IndexOf("/* ===== 1.1-UX-FIX-A ", StringComparison.Ordinal)..];

        section.Should().Contain(".ms-conn-settings-table,\n.ms-conn-compare-table {\n    table-layout: fixed;\n    min-width: 36rem;");
        section.Should().Contain("@media (max-width: 719px) {\n    .ms-conn-table,", "the stacked cards start where the stylesheet's own 720 px step does");
        section.Should().Contain(".ms-health-remedy-explanation {", "the steps of a remedy are on lines of their own wherever the explanation is drawn");
    }
}
