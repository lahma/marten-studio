using System.Globalization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;

namespace DataStudio.Marten.Tests.Conventions;

/// <summary>The part of <see cref="SuiteCultureTests" /> that needs a <c>TestServer</c>, which the shell's and the Wolverine adapter's suites do not reference.</summary>
public partial class SuiteCultureTests
{
    /// <summary>
    /// What a request renders, a page's prerendered markup and the shell's golden files included, runs in the suite's culture: <c>TestServer</c>
    /// does not preserve the test's execution context, so a culture set only on the test's flow (xunit's own <c>culture</c> setting) would leave
    /// it in the machine's.
    /// </summary>
    [Fact]
    public async Task A_request_a_test_server_serves_runs_in_the_suite_culture()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        await using var app = builder.Build();
        app.MapGet("/culture", static () => CultureInfo.CurrentCulture.Name + " " + (-1_234).ToString("N0", CultureInfo.CurrentCulture));

        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = app.GetTestClient();

            (await client.GetStringAsync(new Uri("/culture", UriKind.Relative), TestContext.Current.CancellationToken))
                .Should().Be("fi-FI \u22121\u00A0234");
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
