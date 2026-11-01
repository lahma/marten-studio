using Microsoft.Extensions.DependencyInjection;

using PostgresStudio;

namespace MartenStudio.Tests.Services;

/// <summary>
/// The diagnostics engine is registered once per container (0.11-C): <c>AddPostgresDiagnostics()</c> beside
/// <c>AddMartenStudio()</c>, in either order, is refused with the reason, as it is beside <c>AddPostgresStudio()</c>.
/// </summary>
public class DiagnosticsRegistrationTests
{
    [Fact]
    public void The_engine_after_MartenStudio_is_refused_with_the_sentence()
    {
        ServiceCollection services = new();
        services.AddMartenStudio();

        Action add = () => services.AddPostgresDiagnostics();

        add.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("AddMartenStudio()").And.Contain("already registers the diagnostics engine").And.Contain("Remove AddPostgresDiagnostics()");
    }

    [Fact]
    public void MartenStudio_after_the_engine_is_refused_with_the_sentence_and_registers_nothing_of_its_own()
    {
        ServiceCollection services = new();
        services.AddPostgresDiagnostics();
        int before = services.Count;

        Action add = () => services.AddMartenStudio();

        add.Should().Throw<InvalidOperationException>()
            .Which.Message.Should().Contain("AddMartenStudio()").And.Contain("already registers the diagnostics engine").And.Contain("Remove AddPostgresDiagnostics()");
        services.Count.Should().Be(before, "refused before anything of the studio is registered");
    }

    [Fact]
    public void MartenStudio_alone_registers_the_diagnostics_api_once()
    {
        ServiceCollection services = new();
        services.AddMartenStudio();

        services.Where(static x => x.ServiceType == typeof(PostgresStudio.IPostgresDiagnostics)).Should().ContainSingle()
            .Which.Lifetime.Should().Be(ServiceLifetime.Singleton);
    }
}
