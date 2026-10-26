using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio;

namespace MartenStudio.Tests.Endpoints;

/// <summary>
/// Each studio's map call refuses an application that registered the other studio: the services it would map are the
/// registered studio's, so <c>MapPostgresStudio()</c> in a Marten Studio application would serve Marten Studio's pages
/// under PostgresStudio's name, and the reverse. The real methods, both ways - this project references both packages.
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
    public async Task MapPostgresStudio_in_a_Marten_Studio_application_is_refused_and_says_why()
    {
        await using WebApplication app = App(static services => services.AddMartenStudio());

        Action map = () => app.MapPostgresStudio();

        map.Should().Throw<InvalidOperationException>().WithMessage(
            "MapPostgresStudio() was called in an application that registered Marten Studio with AddMartenStudio(). "
            + "Map the studio the application registered - app.MapMartenStudio() - or register Postgres Studio with "
            + "AddPostgresStudio() in its place: one studio per application, and the services a map call serves are the "
            + "registered studio's.");
    }

    [Fact]
    public async Task MapMartenStudio_in_a_Postgres_Studio_application_is_refused_before_it_writes_a_path()
    {
        await using WebApplication app = App(static services =>
        {
            services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=app;Username=app"));
            services.AddPostgresStudio();
        });

        Action map = () => app.MapMartenStudio();
        Action pattern = () => app.MapMartenStudio("/ops/marten");

        map.Should().Throw<InvalidOperationException>().WithMessage(
            "MapMartenStudio() was called in an application that registered Postgres Studio with AddPostgresStudio(). "
            + "Map the studio the application registered - app.MapPostgresStudio() - *");
        pattern.Should().Throw<InvalidOperationException>().WithMessage("MapMartenStudio() was called*");
    }

    [Fact]
    public async Task Each_studio_maps_in_its_own_application()
    {
        await using WebApplication marten = App(static services => services.AddMartenStudio());
        ((Action) (() => marten.MapMartenStudio().AllowAnonymous())).Should().NotThrow();

        await using WebApplication postgres = App(static services =>
        {
            services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=app;Username=app"));
            services.AddPostgresStudio();
        });
        ((Action) (() => postgres.MapPostgresStudio().AllowAnonymous())).Should().NotThrow();
    }
}
