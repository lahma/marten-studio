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
}
