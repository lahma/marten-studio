using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace DataStudio.Marten.Tests.Endpoints;

/// <summary>
/// One <c>MapDataStudio()</c> serves whichever database module was added (v2 step 4): the refusals between two studios' map calls are gone with the
/// calls, and each module maps in an application of its own. The real methods - this project references both packages.
/// </summary>
public class MapTheRegisteredStudioTests
{
    private static WebApplication App(Action<IServiceCollection> register)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        register(builder.Services);
        return builder.Build();
    }

    [Fact]
    public async Task Each_database_module_maps_in_an_application_of_its_own()
    {
        await using WebApplication marten = App(static services => services.AddDataStudio().AddMarten());
        ((Action) (() => marten.MapDataStudio().AllowAnonymous())).Should().NotThrow();

        await using WebApplication postgres = App(static services =>
        {
            services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=app;Username=app"));
            services.AddDataStudio().AddPostgres();
        });
        ((Action) (() => postgres.MapDataStudio().AllowAnonymous())).Should().NotThrow();
    }

    /// <summary>
    /// In a Marten host the module's home is Marten's, so the front address and the Postgres module's own root address (<c>{mount}/postgres</c>,
    /// which must not be a second copy of the store overview) both lead there: with a path, whole, never an absolute URL built from the request's
    /// scheme and host.
    /// </summary>
    [Theory]
    [InlineData("/studio", null, "/studio", "/studio/marten")]
    [InlineData("/studio", null, "/studio?store=orders", "/studio/marten?store=orders")]
    [InlineData("/studio", null, "/studio/postgres", "/studio/marten")]
    [InlineData("/studio", null, "/studio/postgres?store=orders&db=main", "/studio/marten?store=orders&db=main")]
    [InlineData("/ops/data", null, "/ops/data/postgres?store=orders", "/ops/data/marten?store=orders")]
    [InlineData("/studio", "/app", "/app/studio?store=orders", "/app/studio/marten?store=orders")]
    [InlineData("/studio", "/app", "/app/studio/postgres?store=orders", "/app/studio/marten?store=orders")]
    [InlineData("/ops/data", "/app", "/app/ops/data/postgres?store=orders", "/app/ops/data/marten?store=orders")]
    public async Task In_a_marten_host_the_front_address_and_the_postgres_root_lead_to_the_marten_home_as_a_path(string mount, string? pathBase, string request, string location)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDataStudio(options => options.Path = mount).AddMarten();
        await using WebApplication app = builder.Build();
        if (pathBase is not null)
        {
            app.UsePathBase(pathBase);
            app.UseRouting();
        }

        app.MapDataStudio().AllowAnonymous();
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage response = await client.GetAsync(new Uri(request, UriKind.Relative), TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.Found);
            response.Headers.Location!.OriginalString.Should().Be(location);
            response.Headers.Location.IsAbsoluteUri.Should().BeFalse();
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task In_a_postgres_host_the_postgres_root_is_the_home_and_is_served_not_redirected()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=app;Username=app"));
        builder.Services.AddDataStudio().AddPostgres();
        await using WebApplication app = builder.Build();
        app.MapDataStudio().AllowAnonymous();
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using HttpClient client = app.GetTestClient();

            using HttpResponseMessage front = await client.GetAsync(new Uri("/studio", UriKind.Relative), TestContext.Current.CancellationToken);
            using HttpResponseMessage home = await client.GetAsync(new Uri("/studio/postgres", UriKind.Relative), TestContext.Current.CancellationToken);

            front.StatusCode.Should().Be(HttpStatusCode.Found);
            front.Headers.Location!.OriginalString.Should().Be("/studio/postgres");
            home.StatusCode.Should().Be(HttpStatusCode.OK, "the module's own home is a page, not a redirect to itself");
        }
        finally
        {
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
    }
}
