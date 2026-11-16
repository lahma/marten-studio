using Microsoft.Extensions.DependencyInjection;

using DataStudio.Postgres;

namespace DataStudio.Marten.Tests.Services;

/// <summary>
/// The diagnostics engine is registered once per container (0.11-C): <c>AddPostgresDiagnostics()</c> beside
/// <c>AddMarten()</c>, in either order, is refused with the reason, as it is beside <c>AddPostgres()</c>.
/// </summary>
public class DiagnosticsRegistrationTests
{
    [Fact]
    public void The_engine_after_MartenStudio_is_refused_with_the_sentence()
    {
        ServiceCollection services = new();
        services.AddDataStudio().AddMarten();

        Action add = () => services.AddPostgresDiagnostics();

        add.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("AddMarten()").And.Contain("already registers the diagnostics engine").And.Contain("Remove AddPostgresDiagnostics()");
    }

    [Fact]
    public void MartenStudio_after_the_engine_is_refused_with_the_sentence_and_registers_nothing_of_its_own()
    {
        ServiceCollection services = new();
        services.AddPostgresDiagnostics();
        DataStudioBuilder builder = services.AddDataStudio();
        int before = services.Count;

        Action add = () => builder.AddMarten();

        add.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("AddMarten()").And.Contain("already registers the diagnostics engine").And.Contain("Remove AddPostgresDiagnostics()");
        services.Count.Should().Be(before, "refused before anything of the module is registered");
    }

    [Fact]
    public void MartenStudio_alone_registers_the_diagnostics_api_once()
    {
        ServiceCollection services = new();
        services.AddDataStudio().AddMarten();

        services.Where(static x => x.ServiceType == typeof(DataStudio.Postgres.IPostgresDiagnostics)).Should().ContainSingle()
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }
}
